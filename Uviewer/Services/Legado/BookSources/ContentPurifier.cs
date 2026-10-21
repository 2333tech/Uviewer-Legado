using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阶段 3 正文净化管线（对标 reader frontend <c>stores/reader.ts</c> 的 <c>applyReplaceRules</c>，
/// 逐分支同构移植；legado <c>ContentProcessor</c> 为第二参照）。
/// <para>reader 的应用顺序（本类严格遵循）：</para>
/// ① 空文本 → 空串；② 书籍关闭 useReplaceRule → 原文返回；
/// ③ 过滤 isEnabled 且 scope 匹配当前书籍的规则；④ 按 order **稳定排序**；
/// ⑤ 逐规则应用：isRegex → global+multiline 正则替换；否则字面替换；
/// ⑥ 单条规则异常（非法正则/替换超时）→ 跳过该规则，不影响其余（reader try/catch per rule）。
/// <para>与 reader 的有意偏差（均记录在案）：</para>
/// <list type="bullet">
/// <item>空 pattern 规则跳过（legado 语义）——reader(JS) 不跳过，空正则会向每处插入 replacement，
/// 而 .NET 字面替换对空 oldValue 的行为与 JS 不一致，跳过是唯一确定行为；</item>
/// <item>正则带 3000ms 匹配超时（legado getValidTimeoutMillisecond 默认值，reader/JS 无此保护）——
/// 防止灾难性回溯把 UI 线程挂死；超时按"跳过该规则"处理。</item>
/// <item><b>入口统一换行归一</b>（CRLF/CR → LF，见 <see cref="TextNormalization"/>）：
/// reader/legado 的正文天生是 LF，而我们读到的 TXT 常常是 CRLF（Windows 编辑器 / 下载落盘）。
/// 不归一的话 <c>$</c> 收尾的分支全部静默失效、<c>[^\n…]</c> 类又会吃掉 <c>\r</c>——
/// 同一份规则在两端表现不同，是"规则在 legado 正常、在这里不对"的头号原因。</item>
/// </list>
/// <para><b>@js: 替换（legado 语义，阶段 3 补齐）</b>：isRegex 规则的 replacement 以 <c>@js:</c> 开头时，
/// 对正则的<b>每个匹配</b>用 Jint 求值脚本（<c>result</c> = 匹配串），completion 值作为替换内容
/// （对标 legado <c>RegexExtensions.replace</c> 的 isJs 分支）。求值异常 ⇒ 整条规则跳过。
/// 字面（isRegex=false）规则不评估 <c>@js:</c>——legado 字面分支走 stdlib replace 同样不评估，
/// reader 的 <c>replaceAll</c> 亦然；把 <c>@js:</c> 当字面量插入是两端的一致行为。</para>
/// <para>性能架构（优化后，务必保持）：</para>
/// <list type="bullet">
/// <item>编译快照不可变：<see cref="Rules"/> 指向新列表才重建（引用比较），之后零分配读；
/// 并行批量正文时所有章节共享同一快照，无线程间可变状态；</item>
/// <item>正则编译走有界缓存（Multiline + 超时选项），同一 pattern 跨章节只编译一次；</item>
/// <item>快路径：无规则 / 未启用 / 空文本 → 原样返回（零分配）。</item>
/// </list>
/// </summary>
public sealed class ContentPurifier
{
    /// <summary>legado 默认单规则超时（毫秒）；reader/JS 无超时保护，这里作为安全网引入。</summary>
    private const int DefaultTimeoutMillisecond = 3000;

    /// <summary>
    /// <b>底层内置净化规则</b>（用户不可见、不可关，先于用户规则与总开关执行）：
    /// 剔除整行形如「分卷阅读」「分卷阅读001」的行（全角数字也认）。
    /// <para><b>为什么是内置而不是默认用户规则</b>：这类行是站点分页脚注/灌水标记，
    /// 出现即噪音——不该出现在目录标题里（标题通道先于分章识别生效 → 该行被剔成空行，
    /// 永远不可能被当成章名），也不该留在正文里。用户规则列表为空时也要生效，
    /// 因此挂在 <see cref="Apply"/>/<see cref="ApplyToTitle"/> 入口而不是规则快照里。</para>
    /// <para><b>必须整行匹配</b>：只吃「分卷阅读±数字」独占一行的形态，
    /// 正文中恰好包含"分卷阅读"字样的正常句子不受影响。</para>
    /// </summary>
    private static readonly Regex BuiltinNoiseLineRegex = new(
        "^[ \\t\\u3000]*分卷阅读[0-9０-９]{0,6}[ \\t\\u3000]*\\r?\\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>应用底层内置净化（快速路径：文本不含关键词时零正则开销）。</summary>
    internal static string StripBuiltinNoise(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("分卷阅读", StringComparison.Ordinal))
        {
            return text;
        }

        // 先归一换行再匹配：本正则用 Multiline ^ 锚点，只认 \n 后边界
        // （CR-only 换行的文本从第二行起 ^ 不成立，会静默漏剥）。
        // ToLf 幂等且对无 \r 文本零分配，放在这里不影响调用方的零开销契约。
        return BuiltinNoiseLineRegex.Replace(TextNormalization.ToLf(text), string.Empty);
    }

    /// <summary>
    /// 正则编译缓存上限。与 <see cref="RuleAnalyzer.GetRegex"/> 的正则缓存**分开**：
    /// 那里是 legado 行内 ## 规则（Singleline），这里是用户替换规则（Multiline + 超时），
    /// 选项不同不能共用缓存。
    /// </summary>
    private const int RegexCacheLimit = 512;

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache =
        new(StringComparer.Ordinal);

    /// <summary>最近一次编译的源列表引用（引用比较判重，省掉逐条比对）。</summary>
    /// <remarks>volatile：与 <see cref="_compiled"/> 的读写序共同保证"读到新源引用时
    /// 必读到新快照"（详见 <see cref="EnsureCompiled"/> 的发布协议注释）。</remarks>
    private volatile IReadOnlyList<ReplaceRule>? _compiledFrom;

    /// <summary>不可变编译快照：已过滤禁用/空 pattern、已按 order 稳定排序、正则已编译。</summary>
    private volatile CompiledRule[]? _compiled;

    /// <summary>重建快照的短锁（仅列表引用变化时参与，读路径无锁）。</summary>
    private readonly object _buildGate = new();

    /// <summary>宿主注入的规则列表（通常是 <see cref="ReplaceRuleManager.GetAll"/> 的快照）。</summary>
    private volatile IReadOnlyList<ReplaceRule>? _rules;

    /// <summary>
    /// 宿主注入的规则列表（通常是 <see cref="ReplaceRuleManager.GetAll"/> 的快照）。
    /// 指向新列表实例时下次 Apply 自动重建编译快照；置 null = 无规则（管线空转）。
    /// </summary>
    public IReadOnlyList<ReplaceRule>? Rules
    {
        get => _rules;
        set => _rules = value;
    }

    /// <summary>是否存在可用规则（不含重建开销；无规则时批量路径可整体跳过净化）。</summary>
    public bool HasRules
    {
        get
        {
            var compiled = EnsureCompiled();
            return compiled is { Length: > 0 };
        }
    }

    /// <summary>
    /// 应用净化管线（对标 applyReplaceRules，步骤见类型注释）。
    /// </summary>
    /// <param name="text">待净化正文（HTML 文本）。</param>
    /// <param name="ctx">书籍上下文（scope 匹配 + useReplaceRule 开关）。</param>
    public string Apply(string text, in PurifyContext ctx)
    {
        // ⓪ 底层内置净化：先于一切用户规则/开关（见 StripBuiltinNoise 注释）
        text = StripBuiltinNoise(text);

        // ① 空文本 → 空串（reader: if (!text) return ''）
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // ② 书籍显式关闭净化 → 原文返回（reader: useReplaceRule === false 早退；
        // null/true = 启用，可空语义见 PurifyContext 注释）
        if (ctx.UseReplaceRule is false)
        {
            return text;
        }

        // ③+④ 取编译快照（已过滤启用/非空 pattern、已按 order 稳定排序）
        var rules = EnsureCompiled();
        if (rules is null || rules.Length == 0)
        {
            // 快路径：无规则 → 零分配返回
            return text;
        }

        // ③.5 换行符归一（CRLF/CR → LF），**必须先于任何规则**。
        // 原因：替换规则的作者（legado / reader 生态）一律假设文本是 LF——
        //   · .NET 的 `$` 只在 `\n` 之前成立，`^ab$` 对 "ab\r\n" **匹配不到**
        //     ⇒ 所有"...$"收尾的分支在本库上会静默失效；
        //   · 否定字符类 `[^\n…]` 会把 `\r` 当普通字符吃掉
        //     ⇒ 替换后行尾结构被改写（典型表现：行首缩进被整块替换成装饰符）。
        // 归一放在快照之后：无规则时保持"原样返回"的零开销契约。
        text = TextNormalization.ToLf(text);

        // ⑤+⑥ 逐规则应用：scope 过滤 + 替换 + 单规则异常跳过
        foreach (var rule in rules)
        {
            // 正文通道：只作用于标题的规则（legado scopeTitle-only）不参与正文净化
            if (!rule.Rule.ScopeContent || !ScopeMatches(rule, in ctx))
            {
                continue;
            }

            try
            {
                text = ApplyRule(rule, text);
            }
            catch (RegexMatchTimeoutException)
            {
                // 灾难性回溯：跳过该规则（legado 超时停用 + 报错，这里不停用只跳过，
                // 停用需要持久化，属于 UI 层决策）
            }
            catch (ArgumentException)
            {
                // 替换串非法等：跳过该规则（reader 的 catch 分支等价行为）
            }
            catch (Exception)
            {
                // @js: 求值异常（脚本语法错误 / 运行期错误）：整条规则跳过、文本不被部分改写
                // （legado：异常使整个 replace 调用失败，mContent 保持原值；reader：try/catch 包住整条规则）
            }
        }

        return text;
    }

    // EN: Returns the ids of rules that actually change the text (used by the rule manager's "active here" badge).
    /// <summary>
    /// 探测<b>真正生效</b>的规则：按管线顺序逐条应用，只有改变了文本的规则才算命中。
    /// <para>给"替换净化管理"界面打「本章生效」标记用，因此判定口径与 <see cref="Apply"/> 完全一致：
    /// 作用域不匹配、正则不命中、<c>@js:</c> 异常、替换结果与原文相同（空替换等）都不算生效。</para>
    /// <para>顺序很关键：后面的规则是在前面规则改写过的文本上匹配的，所以这里必须真实地滚一遍文本，
    /// 不能对每条规则都拿原文单独 test。</para>
    /// </summary>
    /// <param name="text">探测文本（通常是当前章节的原文）。</param>
    /// <param name="ctx">书籍上下文（scope 匹配 + 净化开关）。</param>
    /// <returns>命中规则的主键（不会重复；按执行顺序）。</returns>
    public IReadOnlyList<long> FindEffectiveRuleIds(string text, in PurifyContext ctx)
    {
        var hits = new List<long>();
        if (string.IsNullOrEmpty(text) || ctx.UseReplaceRule is false)
        {
            return hits;
        }

        var rules = EnsureCompiled();
        if (rules is null || rules.Length == 0)
        {
            return hits;
        }

        // 与 Apply 管线对齐：内置噪音行先剥掉（否则针对"分卷阅读"行的用户规则
        // 会出现「生效标记亮着但实际不生效」的错位）
        text = StripBuiltinNoise(text);
        text = TextNormalization.ToLf(text);

        foreach (var rule in rules)
        {
            // 探针与正文管线同口径：只看作用于正文的规则
            if (!rule.Rule.ScopeContent || !ScopeMatches(rule, in ctx))
            {
                continue;
            }
            var before = text;
            try
            {
                text = ApplyRule(rule, text);
            }
            catch
            {
                // 与 Apply 一致：单条规则异常 → 跳过该规则，后续规则继续在未改动的文本上跑
                continue;
            }

            if (!string.Equals(before, text, StringComparison.Ordinal))
            {
                hits.Add(rule.Rule.Id);
            }
        }

        return hits;
    }

    // EN: Title channel: applies only scopeTitle rules, before title rules split the text into chapters.
    /// <summary>
    /// <b>标题通道</b>：只跑"作用于标题"的规则（legado <c>scopeTitle</c>）。
    /// <para>调用时机在"标题/分章规则识别章节行<b>之前</b>"——不少 TXT 的标题要先去掉前缀/装饰符
    /// 才能被标题规则匹配（例如"【玄幻】第12章 风起"要先去掉【玄幻】）。</para>
    /// <para>与正文通道共用同一份编译快照与 scope 匹配，差别只有 <c>ScopeTitle</c> 过滤；
    /// 标题是单行文本，逐条替换代价可忽略。</para>
    /// </summary>
    public string ApplyToTitle(string titleText, in PurifyContext ctx)
    {
        // ⓪ 底层内置净化：标题行同样剔除（先于标题识别 → "分卷阅读001" 永远不可能成为目录标题）
        titleText = StripBuiltinNoise(titleText);

        if (string.IsNullOrEmpty(titleText) || ctx.UseReplaceRule is false)
        {
            return titleText;
        }

        var rules = EnsureCompiled();
        if (rules is null || rules.Length == 0)
        {
            return titleText;
        }

        var text = TextNormalization.ToLf(titleText);
        foreach (var rule in rules)
        {
            if (!rule.Rule.ScopeTitle || !ScopeMatches(rule, in ctx))
            {
                continue;
            }

            try
            {
                text = ApplyRule(rule, text);
            }
            catch
            {
                // 与正文通道一致：单条异常跳过，不影响其余规则
            }
        }

        return text;
    }

    /// <summary>单条规则的实际替换（正则 / <c>@js:</c> / 字面三分支）。</summary>
    private static string ApplyRule(CompiledRule rule, string text) =>
        rule.JsScript is { } js
            // @js: 形态：逐匹配求值（MatchEvaluator 的返回值按字面量使用，
            // 不经 $1/$& 展开——legado 用 quoteReplacementJs 达到同样效果）
            ? rule.Regex!.Replace(text, m => ReplaceJsEvaluator.Evaluate(js, m.Value))
            : rule.Regex is not null
                ? rule.Regex.Replace(text, rule.Replacement)
                : text.Replace(rule.Pattern, rule.Replacement);

    /// <summary>
    /// 取编译快照（惰性重建 + 短锁双重检查）。
    /// <para>失效判定用**列表引用比较**（宿主每次变更都换新列表实例，如 ReplaceRuleManager
    /// 的快照语义）；重建步骤：① 过滤 isEnabled 且 pattern 非空（legado：空 pattern 跳过）；
    /// ② 按 order 稳定排序（LINQ OrderBy 稳定，同值保持存储顺序 = reader V8 sort 稳定性）；
    /// ③ 逐条编译：isRegex → 进正则缓存（非法 pattern → 丢弃该规则，reader 的
    /// try/catch 等价于"永不生效"）；字面 → 记录 pattern。</para>
    /// </summary>
    private CompiledRule[] EnsureCompiled()
    {
        // 发布协议（防"新源引用 + 旧快照"组合）：
        //   写：_compiled = built（volatile）→ _compiledFrom = source（volatile）
        //   读：_compiledFrom（volatile）→ _compiled（volatile）
        // 读侧先读源引用：若看到的是新 source，则随后对 _compiled 的读按 volatile
        // 规则必见到新 built；若看到旧源引用，说明快照尚未发布，走锁重建。
        // 旧实现写序反了（先源引用后快照）且 _compiledFrom 非 volatile——读方可能
        // 拿着新源引用配上旧快照返回，用户改了规则却应用上一版，静默不一致。
        var source = Rules;
        var compiledFrom = _compiledFrom;
        var compiled = _compiled;
        if (compiled is not null && ReferenceEquals(compiledFrom, source))
        {
            return compiled;
        }

        lock (_buildGate)
        {
            // 双重检查：等锁期间可能已被其他线程重建
            var sourceNow = Rules;
            var compiledFromNow = _compiledFrom;
            var compiledNow = _compiled;
            if (compiledNow is not null && ReferenceEquals(compiledFromNow, sourceNow))
            {
                return compiledNow;
            }

            CompiledRule[] built;
            if (source is null || source.Count == 0)
            {
                // 无规则：记空数组（管线空转的快路径），同时记录源引用以便下次换列表能识别
                built = Array.Empty<CompiledRule>();
            }
            else
            {
                var list = new List<CompiledRule>(source.Count);
                foreach (var rule in source
                    .Where(r => r.IsEnabled && !string.IsNullOrEmpty(r.Pattern))
                    .OrderBy(r => r.Order))
                {
                    if (rule.IsRegex)
                    {
                        var regex = GetRegex(rule.Pattern);
                        if (regex is null)
                        {
                            // 非法正则：丢弃（reader 在应用时 try/catch 跳过，效果等价）
                            continue;
                        }

                        list.Add(new CompiledRule(
                            rule,
                            regex,
                            null,
                            rule.Replacement,
                            // @js: 形态：预取脚本文本（去前缀）。仅正则分支评估（legado 语义）
                            ReplaceJsEvaluator.IsJs(rule.Replacement) ? ReplaceJsEvaluator.ScriptOf(rule.Replacement) : null));
                    }
                    else
                    {
                        list.Add(new CompiledRule(rule, null, rule.Pattern, rule.Replacement));
                    }
                }

                built = list.ToArray();
            }

            // 先发布快照再发布源引用（见方法头的发布协议）
            _compiled = built;
            _compiledFrom = source;
            return built;
        }
    }

    /// <summary>
    /// scope 匹配（对标 reader matchRuleScope，逐分支一致）：
    /// ① 空或 '*' → 全部书籍命中；
    /// ② "source:&lt;x&gt;" → x 等于书源 URL；
    /// ③ "book:&lt;x&gt;" → x 等于书籍 URL；
    /// ④ "&lt;name&gt;[;&lt;bookUrl&gt;]" → 第一段为 '*' 或书名，第二段（存在则）为书籍 URL。
    /// 无当前书籍（ctx 标识为空）时，②③④ 自然不命中（与 reader <c>!currentBook → false</c> 一致）。
    /// </summary>
    private static bool ScopeMatches(CompiledRule rule, in PurifyContext ctx)
    {
        var scope = (rule.Scope ?? string.Empty).Trim();
        if (scope.Length == 0 || scope == "*")
        {
            return true;
        }

        if (scope.StartsWith("source:", StringComparison.Ordinal))
        {
            return string.Equals(scope.Substring(7), ctx.Origin, StringComparison.Ordinal);
        }

        if (scope.StartsWith("book:", StringComparison.Ordinal))
        {
            return string.Equals(scope.Substring(5), ctx.BookUrl, StringComparison.Ordinal);
        }

        // "name[;bookUrl]"：第一段管书名，第二段（可选）管书籍 URL
        var parts = scope.Split(';');
        if (parts[0] != "*" && !string.Equals(parts[0], ctx.BookName, StringComparison.Ordinal))
        {
            return false;
        }

        return parts.Length == 1 || string.Equals(parts[1], ctx.BookUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取（缓存）用户替换规则的正则：global+multiline（reader 的 'gm' 标志，
    /// Multiline 让 ^/$ 匹配行边界——JS 'm' 同义）+ 3000ms 匹配超时。
    /// 非法 pattern 返回 null（调用方丢弃该规则）。
    /// </summary>
    private static Regex? GetRegex(string pattern)
    {
        if (RegexCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        Regex? compiled;
        try
        {
            compiled = new Regex(
                pattern,
                RegexOptions.Multiline,
                TimeSpan.FromMilliseconds(DefaultTimeoutMillisecond));
        }
        catch
        {
            // 非法正则：缓存 null 防止反复编译同一坏 pattern
            compiled = null;
        }

        if (RegexCache.Count < RegexCacheLimit)
        {
            RegexCache[pattern] = compiled;
        }

        return compiled;
    }

    /// <summary>
    /// 编译后的规则（不可变）：正则与字面二选一（<see cref="Regex"/> 非空 = 正则规则）。
    /// scope 匹配在应用时按 ctx 逐次判定（依赖书籍上下文，无法预编译）。
    /// </summary>
    private sealed class CompiledRule
    {
        public CompiledRule(
            ReplaceRule rule, Regex? regex, string? literalPattern, string replacement, string? jsScript = null)
        {
            Rule = rule;
            Regex = regex;
            LiteralPattern = literalPattern;
            Replacement = replacement;
            JsScript = jsScript;
        }

        /// <summary>源规则（取 Scope）。</summary>
        public ReplaceRule Rule { get; }

        /// <summary>正则形态的编译产物（null = 字面规则）。</summary>
        public Regex? Regex { get; }

        /// <summary>字面形态的 pattern（null = 正则规则）。</summary>
        public string? LiteralPattern { get; }

        /// <summary>替换内容。</summary>
        public string Replacement { get; }

        /// <summary>
        /// <c>@js:</c> 形态的脚本（已去前缀；null = 普通替换）。
        /// 非 null 时 <see cref="Apply"/> 走逐匹配 JS 求值分支（见 <see cref="ReplaceJsEvaluator"/>）。
        /// </summary>
        public string? JsScript { get; }

        /// <summary>作用域原文（大小写敏感比较，见 ScopeMatches）。</summary>
        public string? Scope => Rule.Scope;

        /// <summary>字面规则的匹配 pattern（<see cref="Apply"/> 直接用）。</summary>
        public string Pattern => LiteralPattern ?? string.Empty;
    }
}
