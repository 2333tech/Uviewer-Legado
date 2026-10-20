using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ReplaceRule = BookSource.Core.Models.ReplaceRule;
using TitleRule = BookSource.Core.Models.TitleRule;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 规则编辑表单的<b>纯逻辑</b>元数据（字段顺序 / 中文标签 / 克隆 / 新建默认值 / 保存校验）。
    ///
    /// <para><b>为什么单独拆一个文件</b>：编辑界面（<see cref="RuleManagerDialog{T}"/>）依赖 WinUI，
    /// 无法被集成测试链接；而"克隆是否漏字段""新规则默认值是否合法""非法正则能否被拦住"
    /// 这些逻辑恰恰是最值得测的。按项目既有约定（见 <c>TextTocBuilder</c> 的拆法），
    /// 把纯逻辑放进本文件，测试工程直接链接。</para>
    ///
    /// <para><b>与书源管理的对应关系</b>：书源管理的表单由
    /// <see cref="BookSourceManagerDialog.RuleGroup{T}"/> 反射生成；规则表单同理——
    /// 字段顺序、标签、多行/只读判定全部收敛到此处，两个规则类型（净化 / 标题）共用一套，
    /// 模型加字段时只需改本文件的标签表（顺序表缺省自动补在尾部）。</para>
    /// </summary>
    public static class RuleEditSpec
    {
        /// <summary>
        /// 字段展示顺序（两类规则字段的并集；类型上没有的字段自动跳过，
        /// 未列出的字段按名字典序补在尾部——不依赖 <see cref="Type.GetProperties"/>
        /// 的返回顺序，保证表单布局确定）。
        /// </summary>
        private static readonly string[] CanonicalOrder =
        {
            "Name", "Group", "Pattern", "Replacement", "Scope",
            "TitleGroup", "IsRegex", "IsVolume", "IsBookTitle", "Level",
            "IsEnabled", "Order", "Id",
        };

        private static readonly Dictionary<string, string> ReplaceRuleLabels = new(StringComparer.Ordinal)
        {
            ["Name"] = "规则名称",
            ["Group"] = "分组（仅 UI 归类，不参与匹配）",
            ["Pattern"] = "匹配串（正则或文本，依下方开关）",
            ["Replacement"] = "替换为（正则形态支持 $1 / $& 分组引用）",
            ["Scope"] = "作用范围（空或 * = 全部书籍；source:书源URL；book:书籍URL；书名;书籍URL）",
            ["IsRegex"] = "正则替换（关 = 字面替换）",
            ["IsEnabled"] = "启用该规则",
            ["Order"] = "执行顺序（升序，越小越先执行）",
            ["Id"] = "规则 ID（主键，保存时分配）",
        };

        private static readonly Dictionary<string, string> TitleRuleLabels = new(StringComparer.Ordinal)
        {
            ["Name"] = "规则名称",
            ["Group"] = "分组（仅 UI 归类）",
            ["Pattern"] = "章节标题正则（整行匹配；留空 = 书名行）",
            ["TitleGroup"] = "取第几个捕获组作为章名（0 = 整行）",
            ["IsVolume"] = "卷标题（进目录但不跳章）",
            ["IsBookTitle"] = "书名行（提取书名，不计入章节）",
            ["Level"] = "目录层级（1 = 章，2 = 节）",
            ["IsEnabled"] = "启用该规则",
            ["Order"] = "执行顺序（升序，第一条命中的规则生效）",
            ["Id"] = "规则 ID（主键，保存时分配）",
        };

        /// <summary>表单可编辑的属性类型（string / int / long 主键 / bool；其余类型不进表单）。</summary>
        private static bool IsFormable(Type t) =>
            t == typeof(string) || t == typeof(int) || t == typeof(long) || t == typeof(bool);

        /// <summary>
        /// 导入专用别名属性（legado 旧键名的接收字段，统一 <c>Legacy*</c> 前缀）不进任何反射表单：
        /// 反序列化后由 <c>LegacyAlias.Migrate</c> 迁走并置 null，对用户是不可见的内部管道，
        /// 显示成表单字段只会得到一堆永远为空的未翻译行。
        /// <para>书源表单（<c>BookSourceManagerDialog</c>）与规则表单（<see cref="FieldOrder"/>）共用本判定，
        /// 同一份约定不写两遍。</para>
        /// </summary>
        public static bool IsLegacyAliasProperty(string propertyName) =>
            propertyName.StartsWith("Legacy", StringComparison.Ordinal);

        /// <summary>
        /// 该规则类型的字段展示顺序（确定性：规范顺序 + 未列出的按名字补尾）。
        /// </summary>
        public static IReadOnlyList<string> FieldOrder(Type ruleType)
        {
            var names = ruleType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && IsFormable(p.PropertyType) && !IsLegacyAliasProperty(p.Name))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            var ordered = new List<string>(names.Count);
            foreach (var name in CanonicalOrder)
            {
                if (names.Remove(name))
                {
                    ordered.Add(name);
                }
            }

            ordered.AddRange(names.OrderBy(n => n, StringComparer.Ordinal));
            return ordered;
        }

        /// <summary>字段中文标签（未列出的字段退回属性名）。</summary>
        public static string Label(Type ruleType, string propertyName)
        {
            var table = ruleType == typeof(TitleRule) ? TitleRuleLabels : ReplaceRuleLabels;
            return table.TryGetValue(propertyName, out var label) ? label : propertyName;
        }

        /// <summary>长文本字段用多行输入框（匹配串/替换串/作用范围/分组都是会越过 80 列的内容）。</summary>
        public static bool IsLongText(string propertyName) =>
            propertyName is "Pattern" or "Replacement" or "Scope" or "Group";

        /// <summary>主键字段只读展示（Id 由存储层分配，界面改它会造出孤儿规则）。</summary>
        public static bool IsReadOnlyKey(string propertyName) => propertyName == "Id";

        // ============================================================
        // 列表 / 表单共用的展示文本（纯字符串处理，无 WinUI 依赖）
        // ============================================================

        /// <summary>规则名（空名兜底为"(未命名规则)"，列表与详情标题共用）。</summary>
        public static string ReplaceRuleName(ReplaceRule rule) =>
            string.IsNullOrWhiteSpace(rule.Name) ? "(未命名规则)" : rule.Name;

        /// <summary>净化规则副标题：分组 / 正则类型 / 匹配串摘要 / 作用范围。</summary>
        public static string ReplaceRuleDetail(ReplaceRule rule)
        {
            var kind = rule.IsRegex ? "正则" : "文本";
            var scope = string.IsNullOrWhiteSpace(rule.Scope) || rule.Scope == "*" ? "全部书籍" : rule.Scope;
            var group = string.IsNullOrWhiteSpace(rule.Group) ? null : $"[{rule.Group}]";
            var pattern = rule.Pattern.Length > 40 ? rule.Pattern[..40] + "…" : rule.Pattern;

            return string.Join("　", new[] { group, $"{kind}：{pattern}", scope }
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        /// <summary>标题规则名（空名兜底同上）。</summary>
        public static string TitleRuleName(TitleRule rule) =>
            string.IsNullOrWhiteSpace(rule.Name) ? "(未命名规则)" : rule.Name;

        /// <summary>标题规则副标题：分组 / 规则种类 / 匹配串摘要。</summary>
        public static string TitleRuleDetail(TitleRule rule)
        {
            var kind = rule.IsBookTitle ? "书名行" : rule.IsVolume ? "卷标题" : $"层级 {rule.Level}";
            var pattern = rule.Pattern.Length > 40 ? rule.Pattern[..40] + "…" : rule.Pattern;
            var group = string.IsNullOrWhiteSpace(rule.Group) ? null : $"[{rule.Group}]";

            return string.Join("　", new[] { group, kind, pattern }
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        // ============================================================
        // 克隆（进编辑时用：表单改的是副本，不能污染正在净化/切分的内存实例）
        // ============================================================

        /// <summary>
        /// 深拷贝净化规则。逐字段显式赋值——模型加字段时编译器不会提醒，
        /// 故 <see cref="RuleEditSpecTests"/> 用反射核对"两边属性完全一致"兜底。
        /// </summary>
        public static ReplaceRule Clone(ReplaceRule r) => new()
        {
            Id = r.Id,
            Name = r.Name,
            Group = r.Group,
            Pattern = r.Pattern,
            Replacement = r.Replacement,
            Scope = r.Scope,
            IsEnabled = r.IsEnabled,
            IsRegex = r.IsRegex,
            Order = r.Order,
        };

        /// <summary>深拷贝标题规则（字段比净化规则多：TitleGroup / IsVolume / IsBookTitle / Level）。</summary>
        public static TitleRule Clone(TitleRule r) => new()
        {
            Id = r.Id,
            Name = r.Name,
            Group = r.Group,
            Pattern = r.Pattern,
            TitleGroup = r.TitleGroup,
            IsVolume = r.IsVolume,
            IsBookTitle = r.IsBookTitle,
            Level = r.Level,
            IsEnabled = r.IsEnabled,
            Order = r.Order,
        };

        // ============================================================
        // 新建默认值（对齐 legado UI 与模型文档注释）
        // ============================================================

        /// <summary>
        /// 新建净化规则的默认值：scope='*'（全部书籍）/ isEnabled=true / isRegex=false /
        /// order=现有条数+1（追加到队尾执行，与 legado 新建规则语义一致）。
        /// </summary>
        public static ReplaceRule NewReplaceRule(int existingCount) => new()
        {
            Name = "新净化规则",
            Scope = "*",
            IsEnabled = true,
            IsRegex = false,
            Order = existingCount + 1,
        };

        /// <summary>
        /// 新建标题规则的默认值：预填常见中文写法正则（"第一章 xxx / 第12章"），
        /// Level=1（章）、order=现有条数+1。用户可随手改成正则，不必从零写起。
        /// </summary>
        public static TitleRule NewTitleRule(int existingCount) => new()
        {
            Name = "新标题规则",
            Pattern = @"^\s*第[0-9一二三四五六七八九十百千]+[章节卷回].*$",
            TitleGroup = 0,
            IsVolume = false,
            IsBookTitle = false,
            Level = 1,
            IsEnabled = true,
            Order = existingCount + 1,
        };

        // ============================================================
        // 保存校验（与导入语义对齐：非法/空规则不入库，但这里选择拦住并告知原因）
        // ============================================================

        /// <summary>
        /// 校验待保存的净化规则。返回 null = 可保存；否则为给用户看的原因。
        /// <para>空匹配串的规则永不命中（<see cref="ContentPurifier"/> 过滤空 pattern），
        /// 存下来只会让列表里多一条"永远不生效"的垃圾，故直接拦住。</para>
        /// </summary>
        public static string? Validate(ReplaceRule rule)
        {
            if (rule is null)
            {
                return "规则不能为空";
            }

            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                return "匹配串不能为空：空匹配串的规则永不命中，保存它没有意义。";
            }

            if (rule.IsRegex && !RegexIsValid(rule.Pattern, out var error))
            {
                return $"正则语法错误：{error}";
            }

            return null;
        }

        /// <summary>
        /// 校验待保存的标题规则。返回 null = 可保存；否则为原因。
        /// <para>与 <see cref="TitleRuleManager.ImportFromFileAsync"/> 的入库条件一致：
        /// 正则非空，或"书名行"规则（IsBookTitle，靠 IsBookTitle 标记而非匹配串工作）。</para>
        /// </summary>
        public static string? Validate(TitleRule rule)
        {
            if (rule is null)
            {
                return "规则不能为空";
            }

            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                if (!rule.IsBookTitle)
                {
                    return "章节标题正则不能为空（仅“书名行”规则允许留空）。";
                }

                return null;
            }

            if (!RegexIsValid(rule.Pattern, out var error))
            {
                return $"正则语法错误：{error}";
            }

            if (rule.Level < 1)
            {
                return "目录层级至少为 1（1 = 章，2 = 节）。";
            }

            if (rule.TitleGroup < 0)
            {
                return "捕获组序号不能为负（0 = 整行作为章名）。";
            }

            return null;
        }

        /// <summary>
        /// 正则语法预检。净化器/切分器对非法正则的策略是"静默跳过该规则"，
        /// 但保存界面应该当场告知——否则用户看到的是"规则明明启用了却毫无效果"。
        /// </summary>
        private static bool RegexIsValid(string pattern, out string? error)
        {
            try
            {
                _ = new Regex(pattern);
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
