using System;
using System.Collections.Generic;
using System.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// legado 内联 JS / 脚本段执行宿主（基于 Jint）。
/// 暴露 <c>result</c> / <c>baseUrl</c> / <c>java.*</c> 以及（阶段 4-2 起）<c>source</c>/<c>book</c>/
/// <c>chapter</c>/<c>cache</c>/<c>cookie</c>/<c>html</c> 全局对象（见 <see cref="JsContextObjects"/>）。
/// <c>java.getString(rule)</c> 回灌规则引擎，作用于当前 result 内容（由 RuleEngine 注入 EvaluateRule）。
///
/// <para><b>生命周期</b>：一个 JsBridge 实例服务一条解析链（一次搜索 / 一次目录 / 一次正文），
/// 另由 <see cref="RuleEngine"/> 在每个解析链入口换一份 <see cref="Scope"/>（书源/书籍/章节 + 状态仓库）。
/// <c>java.put/get</c> 与 <c>book.*</c>/<c>source.*</c> 的变量全部落到 <see cref="SourceStateStore"/>：
/// 阶段 4-2 起它们<b>跨链、跨引擎实例、跨进程</b>可见（legado 语义：变量属于书源/书籍，不属于一次解析）。
/// 无 <see cref="Scope"/> 时（纯脚本单测）退化为此前的实例内字典。</para>
///
/// <para><b>重入保护</b>：java.getString 会再次触发规则求值（→ 可能再 RunJs）。
/// 同一 Jint 引擎嵌套求值会互相污染全局状态，故 <see cref="_inUse"/> 为真时
/// 新建临时引擎跑内层脚本（temp=true，用完即弃），外层仍用主引擎。</para>
///
/// <para><b>阶段 4-1 的两处结构性变化</b>：</para>
/// <list type="number">
/// <item><b>动态 <c>java</c> 对象</b>：不再用"CLR 类 + 反射方法"（未知名 → TypeError → 整段脚本被
///   <see cref="Run"/> 的 catch 回退原文，静默失真）。改为按 <see cref="JsApiCatalog"/> 注册真实实现 /
///   桩，并用 Proxy 兜底：未知成员返回"记录诊断后返回 null 的空函数"，脚本继续跑完；</item>
/// <item><b><see cref="Diagnostics"/></b>：把"缺哪个原语 / 调了哪个空桩 / 脚本为什么抛错"记录成可查询数据，
///   不再只留一个 <c>catch</c>。</item>
/// </list>
/// </summary>
internal sealed class JsBridge
{
    /// <summary>
    /// 由 RuleEngine 注入：对当前 result 内容求值某条规则，返回字符串。
    /// <para>第二参数 = <see cref="_contentOverride"/>（<c>java.setContent</c> 设置的替换内容）；
    /// null 表示用脚本运行时的当前 result。阶段 4-5 起与 list/elements 求值同构。</para>
    /// </summary>
    public Func<string, string?, string>? EvaluateRule { get; set; }

    /// <summary>
    /// 由 RuleEngine 注入（阶段 4-5）：<c>java.getStringList</c> 的求值管线，
    /// 返回 JSON 数组文本（每项一个字符串）。
    /// </summary>
    public Func<string, string?, IReadOnlyList<object?>>? EvaluateList { get; set; }

    /// <summary>
    /// 由 RuleEngine 注入（阶段 4-5）：<c>java.getElements/getElement</c> 的元素求值管线，
    /// 返回命中项列表（HtmlNode / JsonNode / 匹配文本；包装见 <see cref="RuleJavaApi"/>）。
    /// </summary>
    public Func<string, string?, IReadOnlyList<object?>>? EvaluateElements { get; set; }

    /// <summary>
    /// 当前书源的 jsLib 原始文本（内联脚本或 <c>{"name":"脚本|URL"}</c> JSON 形式；null/空 = 无库）。
    /// <para>由 RuleEngine 在每条解析链的公开入口设置（见 <c>RuleEngine._jsSource</c>）。
    /// 编译与加载语义见 <see cref="EnsureJsLib"/>。</para>
    /// </summary>
    public string? JsLib { get; set; }

    /// <summary>诊断接收器（默认进程级单例；RuleEngine 可按链替换）。</summary>
    public JsDiagnostics Diagnostics { get; set; } = JsDiagnostics.Shared;

    /// <summary>当前书源主键（诊断归组用，由 RuleEngine 设置）。</summary>
    public string? SourceKey { get; set; }

    /// <summary>当前解析链名（search / bookInfo / toc / content，诊断归组用）。</summary>
    public string? Field { get; set; }

    /// <summary>
    /// 当前解析作用域（书源/书籍/章节 + 状态仓库，阶段 4-2 新增）。
    /// <para>null = 未绑定：全局对象不注入、<c>java.put/get</c> 退回实例内字典
    /// （供只测脚本片段的单测使用）。<see cref="RuleEngine"/> 在每个链入口赋一个新实例。</para>
    /// </summary>
    public JsScope? Scope { get; set; }

    // 主引擎上的库初始化状态：库文本变化或引擎更换才重新执行（对标 reader 的
    // "上下文按 (身份, jsLib md5) 缓存，库代码只在上下文初始化时执行一次"）。
    private string? _libInitText;
    private Engine? _libInitEngine;

    // jsLib 编译结果缓存：同一书源的 JsLib 文本引用不变时直接复用编译产物
    //（JSON 形态的 jsLib 每条规则都要 Compile 一次会重复 JsonNode.Parse；
    //  缓存键用原始文本的**引用相等**，换书源（不同实例）自然失效，零字符串比较成本）。
    private string? _libCompiledFrom;
    private string? _libCompiled;

    // 复用单个 Jint 引擎（构造昂贵 ~0.05ms/个且分配大）。RuleEngine 每次搜索新建一个 JsBridge
    // （见 SearchService），单次搜索内解析是串行的，故复用同一引擎既安全又省去重复初始化开销。
    // 重入（java.getString 再次触发 Run）时新建临时引擎，避免同一引擎嵌套求值互相污染。
    private Engine? _engine;
    private bool _inUse;

    // java.put/get 的变量仓库（仅在未绑定 Scope 时兜底用）：legado 书源常见写法是
    // ruleToc.init / ruleBookInfo.init 里 java.put(key, value)，后续字段规则里 java.get(key)。
    // 绑定 Scope 后走 SourceStateStore（跨链/跨进程），这里只剩"纯脚本单测"这条退化路径。
    private readonly Dictionary<string, object?> _store = new();

    // 每个引擎一份 java 对象（含 Proxy 兜底）；引擎更换即重建。
    private JavaBinding? _binding;

    // 全局对象的绑定缓存：同一个 Scope 只需要绑一次（换链/换书才重建）。
    // 每次 Run 都重建六个代理 = 六次 engine.Evaluate，正文链每章都付这笔钱。
    private Engine? _globalsEngine;
    private JsScope? _globalsScope;

    /// <summary>阶段 4-2 注入的全局对象名（解绑时统一置 undefined）。</summary>
    private static readonly string[] GlobalNames = { "source", "book", "chapter", "cache", "cookie", "html" };

    /// <summary>
    /// 同步网络原语的会话（阶段 4-3）：记录"上一次响应"（<c>java.getStrResponse()</c> 语义），
    /// 换链（scope 变化）时自动清空。
    /// </summary>
    private readonly JsNetworkSession _network = new();

    // 本次脚本的 result/baseUrl：网络原语在"没有真实响应"时用它兜底构造 StrResponse。
    // 重入（java.getString）会临时覆盖，退出时还原（见 Run 的 savedValue/savedBaseUrl）。
    private string _currentValue = string.Empty;
    private string _currentBaseUrl = string.Empty;

    /// <summary>本次（最外层）Run 注入的调用方 globals 键名；下次 Run 先清后注，防残留。</summary>
    private List<string>? _globalKeys;

    /// <summary>
    /// <c>java.setContent(x)</c> 设置的替换内容（阶段 4-5）：后续
    /// <c>getString/getStringList/getElements/getElement</c> 都对它求值
    /// （legado <c>AnalyzeRule.setContent</c> 语义）。每次 <see cref="Run"/> 开始时清空
    /// ——覆盖只在当前脚本内有效，跨脚本残留会把下一段解析打到错误内容上。
    /// </summary>
    private string? _contentOverride;

    /// <summary>
    /// 执行脚本。<paramref name="globals"/> 用于注入额外全局变量（如 formatJs 的 index/title/chapter）。
    /// <para>步骤：① 取引擎（重入 → 临时引擎）；② 绑定 result/baseUrl/java 及 globals；
    /// ③ Evaluate；④ 结果转字符串（null → 空串）；⑤ 异常时**返回原始 value**
    /// （legado：脚本异常回退为原始内容，不中断解析），同时写一条 <see cref="JsDiagnosticKind.ScriptError"/>。</para>
    /// </summary>
    /// <param name="src">
    /// <c>src</c> 绑定值（= 规则所作用的**原文/当前内容**）。null 表示与 <paramref name="value"/> 相同。
    /// <para><b>为什么 result 与 src 必须分开</b>：legado <c>evalJS(rule, result)</c> 绑
    /// <c>result</c> = 上一段规则的结果、<c>src</c> = <c>AnalyzeRule.content</c>（原文）。
    /// 多段规则里两者**不同**：光社漫畫目录 <c>$.data.chapters\n@js: JSON.parse(result)</c> 用
    /// <c>result</c>（JSONPath 结果），而同一脚本里的 <c>src.match(/data":{.../) </c> 要匹配**整份
    /// 响应体**。旧实现把两者都绑成 value，于是 <c>src</c> 拿到的是章节数组 → 正则零命中 →
    /// 目录恒空（快看/光社/吉站三家漫画源"无法获取目录"的直接原因之一）。</para>
    /// </param>
    public string Run(
        string script,
        string value,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? globals = null,
        string? src = null) =>
        RunCore(script, value, baseUrl, globals, captureList: false, src).Text;

    /// <summary>
    /// 执行脚本并保留<b>列表结构</b>（对标 legado <c>AnalyzeRule.getElements</c> 的
    /// <c>Mode.Js</c> 分支：<c>evalJS</c> 返回的对象被直接当条目列表用）。
    /// <para><b>为什么需要它</b>：legado 的列表规则允许脚本直接返回对象数组，例如
    /// <c>&lt;js&gt;java.getElements("id.chapter-items@class.comics-chapters@a")&lt;/js&gt;</c>
    /// （包子漫画类源）或 <c>list.map(x =&gt; ({text: …, href: …}))</c>。
    /// 走 <see cref="Run"/> 只会拿到 <c>Array.prototype.toString()</c> 的
    /// <c>"[object Object],[object Object]"</c>——条目信息已彻底丢失，后续只能退化成
    /// 「整份 body 兜底」，目录恒为空。</para>
    /// <para>返回：脚本结果不是数组 → null（调用方回退到字符串路径，语义不变）；
    /// 是数组 → 每项一个条目片段字符串（元素快照取 <c>outerHtml()</c>，
    /// 纯对象取 JSON 序列化，字符串/数字取原文）。</para>
    /// </summary>
    public IReadOnlyList<string>? RunElements(string script, string value, string baseUrl, string? src = null) =>
        RunCore(script, value, baseUrl, globals: null, captureList: true, src).Items;

    /// <summary>
    /// 把脚本包成块语句，隔离每次求值的顶层 <c>let/const</c>（见 <see cref="RunCore"/> 内的说明）。
    /// <para>脚本以 <c>/</c> 开头时（正则字面量形态整条脚本）包块也是安全的；
    /// 块内不允许顶层 <c>return</c>，而 legado 的字段脚本本来也不能用它。</para>
    /// </summary>
    private static string WrapScript(string script) => "{ " + script + "\n}";

    private (string Text, IReadOnlyList<string>? Items) RunCore(
        string script,
        string value,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? globals,
        bool captureList,
        string? src = null)
    {
        try
        {
            // ⓪ 先解析库代码（可能使旧引擎失效 → 必须在取引擎之前，见 ResolveJsLib 的书源隔离说明）
            var lib = ResolveJsLib();

            // 库文本变化（换书源复用了同一 JsBridge 实例）同样使主引擎失效：
            // Jint 无法从引擎里"卸载"单个函数/顶层 var，同一引擎上执行新库会让
            // 旧库的函数与全局变量残留——下一个书源白拿上一个书源的库（跨源串味）。
            // 旧实现只在"新书源无库"时丢弃引擎，"有不同库"只重新 Execute，
            // 违反红线 3。这里与无库路径同样丢弃，本次调用改用全新引擎。
            if (_libInitEngine is not null
                && !string.Equals(_libInitText, lib, StringComparison.Ordinal))
            {
                _engine = null;
                _binding = null;
                _libInitEngine = null;
                _libInitText = null;
            }

            Engine engine;
            bool temp;
            if (_inUse)
            {
                // ① 重入：临时引擎（隔离嵌套求值的全局状态）
                engine = new Engine();
                temp = true;
            }
            else
            {
                _engine ??= new Engine();
                engine = _engine;
                _inUse = true;
                temp = false;
            }

            // setContent 的覆盖只在"最外层脚本"内有效：重入（getString 的规则本身含 <js> → 嵌套 Run）
            // 会进入这里，必须保存外层的覆盖、退出时还原——否则外层循环的第二次 getString 拿不到覆盖内容。
            var savedOverride = _contentOverride;
            _contentOverride = null;

            // 同理保存重入会改写的其余实例状态：result 兜底值 / globals 注入键。
            // 旧实现只保存了 _contentOverride——嵌套脚本退出后，外层再走
            // getStrResponse 兜底会拿到内层的 result，globals 注入键也被冲掉。
            var savedValue = _currentValue;
            var savedBaseUrl = _currentBaseUrl;
            var savedGlobalKeys = _globalKeys;

            try
            {
                // ② 绑定全局：result（当前内容）/ baseUrl / java API / 调用方 globals
                engine.SetValue("result", value);
                engine.SetValue("baseUrl", baseUrl);
                // src = 规则作用的**原文**（legado evalJS: bindings["src"] = content）。
                // 不是"当前 URL"，也**不是 result**：样本里 src.match(/data-mid="(\d+)"/) 这类写法
                // 是在正则匹配**详情页/目录页响应体**（包子漫画 / 光社漫畫的 tocUrl、目录规则）。
                // 多段规则（`$.data.chapters\n@js:…`）里 result 已是章节数组，拿它去 match 必然零命中。
                engine.SetValue("src", src ?? value);
                engine.SetValue("java", EnsureJava(engine, temp));
                _currentValue = value;
                _currentBaseUrl = baseUrl;

                // ②a 全局对象（source/book/chapter/cache/cookie/html）：有作用域才注入。
                //     同一 Scope 只绑一次（见 EnsureGlobals）。
                EnsureGlobals(engine, temp);

                // ②a2 上一次注入的 globals（formatJs 的 index/title 等）先清掉：
                // 不清理会让下一次无 globals 的 Run 读到上一轮的残留值（静默出错）。
                if (_globalKeys is not null)
                {
                    foreach (var k in _globalKeys)
                    {
                        engine.SetValue(k, JsValue.Undefined);
                    }

                    _globalKeys = null;
                }

                if (globals is not null)
                {
                    foreach (var (k, v) in globals)
                    {
                        engine.SetValue(k, v);
                    }

                    _globalKeys = new List<string>(globals.Keys);
                }

                // ②b 加载 jsLib（书源库代码）：函数/变量定义先于脚本求值生效
                EnsureJsLib(engine, lib, temp);

                // ③④ 求值并转字符串
                // 走编译产物缓存：同一脚本在"每条字段 × 每个片段"上反复执行，
                // engine.Evaluate(string) 每次都会重新解析脚本（4KB 约 5ms，JS 书源每章都付这笔钱）。
                //
                // 脚本外包一层块语句 `{ … }`：Jint 复用同一个 Engine，顶层 `let/const`
                // 会**留在全局词法环境**里，下一个脚本再声明同名变量就抛
                // "Identifier 'u' has already been declared"（整段回退原文）。
                // 光社漫畫就是典型：tocUrl 规则 `let u=src.match(...)` 与 chapterList 规则
                // `let u=result` 在同一个引擎里先后执行 → 目录 0 章。
                // legado 每次 evalJS 都是新作用域，块语句是等价物：
                // 词法声明随块结束即销毁，而**完成值语义不变**（`{ …; a }` 仍返回 a，
                // 脚本靠"最后一条表达式"当返回值的写法全部照旧）。
                var r = engine.Evaluate(JsScriptCache.Get(WrapScript(script)));
                // JS 的 null/undefined 的 ToString() 是 "null"/"undefined" 字面量——
                // 直接转串会把字面量写进目录/正文（脚本写 return null 时整个字段变 "null"）。
                // 判 Jint 类型后落空串（legado/reader 对 JS 空结果均落空串）。
                if (r is null || r.Type == Jint.Runtime.Types.Null || r.Type == Jint.Runtime.Types.Undefined)
                {
                    return (string.Empty, null);
                }

                // 列表模式：脚本返回数组时按条目拆片段（legado getElements 语义）。
                // 必须在 ToString() 之前做——数组一旦被转成 "a,b,c" 就不可逆。
                if (captureList)
                {
                    var items = ConvertListResult(engine, r);
                    if (items is not null)
                    {
                        return (r.ToString() ?? string.Empty, items);
                    }
                }

                return (r.ToString() ?? string.Empty, null);
            }
            finally
            {
                // 重入退出时还原外层的 setContent 覆盖（外层 Run 退出时 savedOverride 本就是 null，等价清空）
                _contentOverride = temp ? savedOverride : null;
                if (temp)
                {
                    _currentValue = savedValue;
                    _currentBaseUrl = savedBaseUrl;
                    _globalKeys = savedGlobalKeys;
                }
                else
                {
                    _inUse = false;
                }
            }
        }
        catch (Exception ex)
        {
            // ⑤ legado：脚本异常时回退为原始内容；同时记录"谁抛了什么"，避免静默失真
            Diagnostics.RecordScriptError(SourceKey, Field, ex);
            return (value, null);
        }
    }

    /// <summary>
    /// 把脚本返回值按"条目列表"拆成片段字符串（legado <c>getElements</c> 的 <c>Mode.Js</c> 结果）。
    /// <para>三种条目形态：</para>
    /// <list type="number">
    /// <item><b>HTML 元素快照</b>（<c>java.getElements</c> 的命中项）→ <c>outerHtml()</c>，
    /// 后续章节字段规则在该片段内重新求值（与 legado <c>setContent(Element)</c> 等价）；</item>
    /// <item><b>纯 JS 对象/数组</b>（样本常见的 <c>list.push({text, href})</c>）→ JSON 序列化，
    /// 让 <c>$.text</c> 这类字段规则可用；</item>
    /// <item><b>字符串/数字/布尔</b> → 原文（有些源直接返回标题数组配 <c>@text</c> 系规则）。</item>
    /// </list>
    /// <para>不是数组 → null：调用方必须回退到原来的字符串路径（例如
    /// <c>&lt;js&gt;t2s(result)&lt;/js&gt;</c> 这种脚本只是"改写原文"，返回的是整份 HTML 字符串）。</para>
    /// </summary>
    private static IReadOnlyList<string>? ConvertListResult(Engine engine, JsValue value)
    {
        var isArray = value.IsArray();
        if (!isArray)
        {
            // arguments / 类数组：有 length 且能被索引访问（legado 也接受 java.util.List 之类的类数组）
            if (!value.IsObject())
            {
                return null;
            }

            var lengthValue = value.AsObject().Get("length");
            if (lengthValue.Type != Jint.Runtime.Types.Number)
            {
                return null;
            }

            var len = (int)lengthValue.AsNumber();
            if (len <= 0)
            {
                return null;
            }

            var arrayLike = new List<string>(len);
            var obj = value.AsObject();
            for (var i = 0; i < len; i++)
            {
                var item = obj.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (item.Type == Jint.Runtime.Types.Undefined)
                {
                    continue;
                }

                arrayLike.Add(ToFragment(engine, item));
            }

            return arrayLike;
        }

        var items = value.AsArray();
        var list = new List<string>((int)items.Length);
        for (uint i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item.Type == Jint.Runtime.Types.Undefined || item.Type == Jint.Runtime.Types.Null)
            {
                continue;
            }

            list.Add(ToFragment(engine, item));
        }

        return list;
    }

    /// <summary>单条目 → 片段字符串（HTML 元素 → outerHtml；对象 → JSON；标量 → 原文）。</summary>
    private static string ToFragment(Engine engine, JsValue item)
    {
        if (item.IsObject())
        {
            var obj = item.AsObject();
            foreach (var fn in FragmentAccessors)
            {
                var member = obj.Get(fn);
                if (member is not Jint.Native.Function.Function)
                {
                    continue;
                }

                try
                {
                    var html = engine.Invoke(member, obj);
                    var text = html.Type == Jint.Runtime.Types.String ? html.ToString() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
                catch (Exception)
                {
                    // 快照方法不应抛；抛了就退到下一个取法（绝不因为取片段失败丢掉整条目录）
                }
            }

            try
            {
                // 纯对象 → JSON（让 $.text / $..id 这类字段规则可用）
                return System.Text.Json.JsonSerializer.Serialize(obj.ToObject());
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        return item.ToString() ?? string.Empty;
    }

    /// <summary>元素快照上取"整段 HTML"的方法名，按优先级尝试（见 RuleJavaApi.WrapHtmlElement）。</summary>
    private static readonly string[] FragmentAccessors = { "outerHtml", "html" };

    /// <summary>
    /// 解析当前书源的 jsLib 为可执行库代码（带编译缓存，命中缓存零解析）。
    /// <para><b>书源隔离</b>：本方法在 <see cref="Run"/> 取引擎**之前**调用。若新书源无库
    /// （或库编译后为空）而主引擎此前加载过别的书源的库，则主引擎被丢弃（置 null，
    /// 本次调用即用全新引擎）——Jint 引擎的全局作用域无法"卸载"单个函数，
    /// 不重建就会让下一个书源白拿到上一个书源的函数（跨源串味）。
    /// 这与 reader 的"上下文按 (书源身份, jsLib) 缓存、身份不同即换上下文"等价。</para>
    /// </summary>
    private string ResolveJsLib()
    {
        var raw = JsLib;
        if (string.IsNullOrEmpty(raw))
        {
            // 无库：若主引擎此前加载过别的书源的库，丢弃该引擎（隔离）
            if (_libInitEngine is not null)
            {
                _engine = null;
                _binding = null;
                _libInitEngine = null;
                _libInitText = null;
            }

            return string.Empty;
        }

        // 编译缓存：同一原始文本引用直接复用（换书源 → 不同引用 → 自然失效）
        if (ReferenceEquals(_libCompiledFrom, raw) && _libCompiled is not null)
        {
            return _libCompiled;
        }

        // 本地部分编译（裸 URL / JSON 映射里的 URL 条目不在此列，见 Compile 注释）
        var compiled = JsLibCompiler.Compile(raw);

        // 远程条目：进程级缓存 + 阻塞下载（Run 在线程池上执行，与 java.ajax 同步桥同性质；
        // legado 也是同步下载 jsLib 后注入 Rhino）。下载失败只记诊断并跳过该条目
        // （reader 语义：下载失败仅记日志，其余条目照常执行）。
        var remoteUrls = JsLibCompiler.ExtractRemoteUrls(raw);
        if (remoteUrls.Count > 0)
        {
            var sb = new System.Text.StringBuilder(compiled);
            foreach (var url in remoteUrls)
            {
                var code = DownloadJsLibCached(url);
                if (code is not null)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(JsLibCompiler.EntrySeparator);
                    }

                    sb.Append(code);
                }
            }

            compiled = sb.ToString();
        }

        _libCompiled = compiled;
        _libCompiledFrom = raw;
        return compiled;
    }

    /// <summary>远程 jsLib 下载缓存（进程级，URL → 库代码）：legado 同样按 URL 缓存库内容，
    /// 避免每次规则求值都重新下载。只缓存成功结果，失败下次重试。
    /// <para>有界（条数 + 单条体积）：恶意/异常书源用大量远程 jsLib URL 会把进程内存灌爆，
    /// 超限的条目不缓存（下次重试，功能不受影响）。</para></summary>
    private const int RemoteLibCacheLimit = 64;
    private const int RemoteLibEntryMaxChars = 512 * 1024;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> RemoteLibCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 下载单个远程 jsLib 条目（带进程级缓存）。
    /// </summary>
    /// <returns>库代码；失败返回 null（已记诊断），调用方跳过该条目。</returns>
    private string? DownloadJsLibCached(string url)
    {
        if (RemoteLibCache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var fetcher = Scope?.Fetcher;
        if (fetcher is null)
        {
            // 无取数管线（单测/离线）：与旧版"离线跳过"一致，但显式记诊断便于发现
            Diagnostics.RecordScriptError(SourceKey, Field, new JsLibDownloadException(url, "无可用取数管线"));
            return null;
        }

        try
        {
            var (content, _) = fetcher
                .FetchAsync(url, new RequestSpec { Method = "GET" }, SourceKey, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(content))
            {
                Diagnostics.RecordScriptError(SourceKey, Field, new JsLibDownloadException(url, "响应为空"));
                return null;
            }

            // 超限不缓存（下次重试）：条数上限 + 单条体积上限，见 RemoteLibCache 注释
            if (content.Length > RemoteLibEntryMaxChars)
            {
                return content;
            }

            if (RemoteLibCache.Count >= RemoteLibCacheLimit)
            {
                return content;
            }

            return RemoteLibCache.GetOrAdd(url, content);
        }
        catch (Exception ex)
        {
            Diagnostics.RecordScriptError(SourceKey, Field, new JsLibDownloadException(url, ex.Message));
            return null;
        }
    }

    /// <summary>远程 jsLib 下载失败专用异常：Detail 里直接带 URL，诊断面板一眼可见。</summary>
    private sealed class JsLibDownloadException : Exception
    {
        public JsLibDownloadException(string url, string reason)
            : base($"远程 jsLib 下载失败: {url}（{reason}）")
        {
        }
    }

    /// <summary>
    /// 把库代码装进指定引擎（对标 reader <c>with_reusable_context</c> 的库加载语义）：
    /// ① 库代码在上下文（引擎）初始化时执行一次，后续脚本求值直接复用已定义的函数/变量；
    /// ② 重入（<c>java.getString</c> 嵌套求值）用的一次性引擎同样要加载库，
    /// 否则嵌套脚本里引用的 jsLib 函数不可用；
    /// ③ 库文本变化（换书源复用了同一 JsBridge 实例）或引擎更换 → 重新执行。
    /// <para>库执行异常不外抛：由 <see cref="Run"/> 的总 catch 兜底回退原始内容
    /// （legado：脚本异常回退，不中断解析）。</para>
    /// </summary>
    private void EnsureJsLib(Engine engine, string lib, bool temp)
    {
        if (lib.Length == 0)
        {
            return;
        }

        if (!temp
            && ReferenceEquals(engine, _libInitEngine)
            && string.Equals(_libInitText, lib, StringComparison.Ordinal))
        {
            return;
        }

        engine.Execute(lib);
        if (!temp)
        {
            _libInitEngine = engine;
            _libInitText = lib;
        }
    }

    // ============================================================
    // 动态 java 对象
    // ============================================================

    /// <summary>
    /// 取（必要时构建）该引擎的 <c>java</c> 对象。
    /// <para>构建规则：① <see cref="JsApiStatus.Implemented"/> → 真实实现；
    /// ② <see cref="JsApiStatus.Stub"/> → 返回空值 + 记 <see cref="JsDiagnosticKind.StubCalled"/>；
    /// ③ <see cref="JsApiStatus.Missing"/> / 未登记 → 不注册，由 Proxy 兜底成"记诊断后返回 null"的空函数。
    /// 这样任何脚本都能跑完，缺什么在诊断里一目了然。</para>
    /// </summary>
    private JsValue EnsureJava(Engine engine, bool temp)
    {
        // temp 引擎（重入隔离用）不写 _binding：旧实现让临时引擎的绑定覆盖实例字段，
        // 外层主引擎每次重入后都要重建 java 对象（每次 Bind = 整个 API 面 + Proxy）。
        if (!temp && _binding is not null && ReferenceEquals(_binding.Engine, engine))
        {
            return _binding.Java;
        }

        var java = new JsObject(engine);
        foreach (var info in JsApiCatalog.All)
        {
            switch (info.Status)
            {
                case JsApiStatus.Implemented:
                    // lang 是命名空间对象：循环里先占位，下面用真实对象覆盖。
                    // 传 engine：网络原语要构造 StrResponse JS 对象，而函数闭包只在建 java 对象时拿到引擎。
                    java.FastSetDataProperty(info.Name, CreateFunction(
                        engine, info.Name, args => InvokeImplemented(engine, info.Name, args)));
                    break;
                case JsApiStatus.Stub:
                    // ruleUrl 在 legado 里是"不存在的属性"（Rhino 下访问即 undefined），
                    // 样本里全是**属性读法**（url = java.ruleUrl）而非调用——注册成空串数据属性，
                    // 注册成函数会让 String(java.ruleUrl) 拿到函数源码、布尔判断恒真，语义全歪。
                    if (string.Equals(info.Name, "ruleUrl", StringComparison.Ordinal))
                    {
                        java.FastSetDataProperty(info.Name, string.Empty);
                        break;
                    }

                    java.FastSetDataProperty(info.Name, CreateFunction(engine, info.Name, args => InvokeStub(info.Name, args)));
                    break;
                default:
                    // Missing：不注册（Proxy 兜底），保证诊断能区分"从未实现"与"实现了但返回空"
                    break;
            }
        }

        // java.lang 命名空间（Rhino 下书源常写 java.lang.System.currentTimeMillis()）
        java.FastSetDataProperty("lang", BuildLangNamespace(engine));

        var wrapped = WrapWithMissingFallback(engine, java, string.Empty);
        if (!temp)
        {
            _binding = new JavaBinding(engine, wrapped);
        }

        return wrapped;
    }

    /// <summary>
    /// 绑定/刷新五个全局对象（阶段 4-2）。
    /// <para>只在"引擎换了"或"作用域换了"时重建：正文链每章都会 Run 多次，但同一章的
    /// Scope 是同一个实例，故重建次数 = 链数而不是 Run 次数。</para>
    /// <para>无 <see cref="Scope"/>（纯脚本单测）时什么都不做：保持 4-1 的行为（只有 result/baseUrl/java）。</para>
    /// </summary>
    private void EnsureGlobals(Engine engine, bool temp)
    {
        var scope = Scope;
        if (scope is null)
        {
            // 解绑：上一个作用域注入的对象绝不能留给下一次无作用域调用（跨链/跨书串味）。
            // 显式置 undefined 而不是只清缓存——引擎里残留的 source/book 会指向旧书。
            if (_globalsScope is not null)
            {
                foreach (var name in GlobalNames)
                {
                    engine.SetValue(name, JsValue.Undefined);
                }

                // temp 引擎不写实例缓存字段（外层引擎的绑定还有效，清了只会白白重建）
                if (!temp)
                {
                    _globalsEngine = null;
                    _globalsScope = null;
                }
            }

            return;
        }

        if (!temp && ReferenceEquals(_globalsEngine, engine) && ReferenceEquals(_globalsScope, scope))
        {
            return;
        }

        JsContextObjects.Bind(engine, scope, Diagnostics, Field);
        if (!temp)
        {
            _globalsEngine = engine;
            _globalsScope = scope;
        }
    }

    /// <summary>已实现原语的运行时派发（需要访问 Bridge 状态的走这里，其余交给 <see cref="JsPureApi"/>）。</summary>
    private object? InvokeImplemented(Engine engine, string name, object?[] args)
    {
        switch (name)
        {
            case "getString":
                // java.getString(rule[, mContent])：mContent 缺省时用 setContent 的替换内容
                return EvaluateRule?.Invoke(
                    JsPureApi.AsString(args, 0), ContentArg(args)) ?? string.Empty;

            // 阶段 4-5：T2 规则原语（对当前 result 或替换内容跑规则）
            case "getStringList":
                if (EvaluateList is { } listEval)
                {
                    return RuleJavaApi.GetStringList(engine, SerializeList(listEval(JsPureApi.AsString(args, 0), ContentArg(args))));
                }

                return new JsArray(engine, Array.Empty<JsValue>());

            case "getElements":
                if (EvaluateElements is { } elementsEval)
                {
                    return RuleJavaApi.GetElements(engine, this, elementsEval(JsPureApi.AsString(args, 0), ContentArg(args)));
                }

                return new JsArray(engine, Array.Empty<JsValue>());

            case "getElement":
                if (EvaluateElements is { } elementEval)
                {
                    return RuleJavaApi.GetElement(engine, this, elementEval(JsPureApi.AsString(args, 0), ContentArg(args)));
                }

                return JsValue.Null;

            case "setContent":
                // legado AnalyzeRule.setContent(content[, baseUrl])：替换后续规则求值对象。
                // baseUrl 参数收下不用（本引擎的字段求值不做 URL 绝对化，BookUrl 字段自己处理）。
                _contentOverride = args.Length > 0 && args[0] is not null ? JsPureApi.AsString(args, 0) : null;
                return JsBoolean.True;

            case "get":
                // legado AnalyzeRule.get：章节变量 → 书籍变量 → 书源 sp: 键（阶段 4-2 起跨链持久）
                if (Scope is { } getScope)
                {
                    return JsContextObjects.RuleGet(getScope, JsPureApi.AsString(args, 0));
                }

                lock (_store)
                {
                    return _store.TryGetValue(JsPureApi.AsString(args, 0), out var legacy) ? legacy : null;
                }

            case "put":
                // legado AnalyzeRule.put：有章节写章节、否则写书籍、再否则写书源（返回值 = 写入值）
                var putKey = JsPureApi.AsString(args, 0);
                var putValue = JsPureApi.AsString(args, 1);
                if (Scope is { } putScope)
                {
                    return JsContextObjects.RulePut(putScope, putKey, putValue);
                }

                lock (_store)
                {
                    _store[putKey] = putValue;
                }

                return putValue;

            case "getCookie":
                // java.getCookie(url[, key])：无 key 返回整串，有 key 返回该键的值（reader 语义）
                if (Scope is { } cookieScope)
                {
                    var url = JsPureApi.AsString(args, 0);
                    return args.Length > 1
                        ? JsCookieJar.GetKey(cookieScope, url, JsPureApi.AsString(args, 1))
                        : JsCookieJar.GetCookie(cookieScope, url);
                }

                return string.Empty;

            case "log":
                Diagnostics.RecordLog(SourceKey, Field, JsContextObjects.FormatArgs(args));
                return null;

            case "toast":
            case "longToast":
                Diagnostics.RecordToast(SourceKey, Field, JsContextObjects.FormatArgs(args));
                return null;

            // 阶段 4-3：同步网络原语（ajax/post/connect/head/ajaxAll/ajaxTestAll/getStrResponse）。
            // 统一交给 JsNetworkSession：它复用注入的取数管线（限速 + 共享 Cookie），
            // 并记录"上一次响应"供无参 getStrResponse 使用。
            case "ajax":
            case "post":
            case "connect":
            case "head":
            case "ajaxAll":
            case "ajaxTestAll":
            case "getStrResponse":
                if (Scope is { } networkScope)
                {
                    return _network.Invoke(engine, networkScope, name, args, _currentValue, _currentBaseUrl);
                }

                Diagnostics.RecordMissingApi(SourceKey, Field, name + "(无解析作用域)");
                // 失败路径与 TryFetch 失败口径统一：网络族返回空 StrResponse、
                // getStrResponse 返回空串——旧实现返回 JS null，脚本 resp.body()
                // 遇 null 抛 TypeError 整段回退，而"请求失败"本不该炸掉整段脚本。
                return name == "getStrResponse"
                    ? string.Empty
                    : JsNetworkSession.EmptyResponse(engine, name);

            // 阶段 4-5b：T0 加解密族（legado JsEncodeUtils 语义 + reader 对称实现）。
            // 失败抛异常 → CreateFunction 统一记 ScriptError 并返回 null（legado String? null 同义）。
            case "createSymmetricCrypto":
                return CryptoJavaApi.CreateSymmetricCrypto(engine, args, new JsContextObjects.JsErrorSink(Diagnostics, SourceKey, Field));

            case "base64DecodeToByteArray":
                return CryptoJavaApi.Base64DecodeToByteArray(engine, args);

            case "hexDecodeToByteArray":
                return CryptoJavaApi.HexDecodeToByteArray(engine, args);

            case "aesBase64DecodeToString":
            case "aesDecodeToString":
            case "aesEncodeToBase64String":
            case "aesEncodeToString":
            case "desEncodeToBase64String":
            case "desBase64DecodeToString":
            case "desDecodeToString":
            case "desEncodeToString":
                return CryptoDispatch(name, args);

            case "HMacHex":
            case "HMacBase64":
            case "digestHex":
            case "digestBase64Str":
                return CryptoDispatch(name, args);

            // 阶段 4-7：T4 浏览器原语（startBrowser/startBrowserAwait/webView，图形验证码场景）。
            // 宿主未接线 / WebView2 不可用时在 BrowserJavaApi 内部记诊断降级。
            case "startBrowser":
            case "startBrowserAwait":
            case "webView":
                if (Scope is { } webScope)
                {
                    return BrowserJavaApi.Invoke(engine, webScope, Diagnostics, name, args);
                }

                Diagnostics.RecordMissingApi(SourceKey, Field, name + "(无解析作用域)");
                return name == "startBrowser" ? true : null;

            default:
                return JsPureApi.TryInvoke(name, args, out var result) ? result : null;
        }
    }

    /// <summary>加解密族的一元/四元原语派发（<see cref="CryptoJavaApi"/>，签名见 legado JsEncodeUtils）。</summary>
    private object? CryptoDispatch(string name, object?[] args) => name switch
    {
        "aesBase64DecodeToString" => CryptoJavaApi.AesBase64DecodeToString(args),
        "aesDecodeToString" => CryptoJavaApi.AesDecodeToString(args),
        "aesEncodeToBase64String" => CryptoJavaApi.AesEncodeToBase64String(args),
        "aesEncodeToString" => CryptoJavaApi.AesEncodeToString(args),
        "desEncodeToBase64String" => CryptoJavaApi.DesEncodeToBase64String(args),
        "desBase64DecodeToString" => CryptoJavaApi.DesBase64DecodeToString(args),
        "desDecodeToString" => CryptoJavaApi.DesDecodeToString(args),
        "desEncodeToString" => CryptoJavaApi.DesEncodeToString(args),
        "HMacHex" => CryptoJavaApi.HMacHex(args),
        "HMacBase64" => CryptoJavaApi.HMacBase64(args),
        "digestHex" => CryptoJavaApi.DigestHex(args),
        "digestBase64Str" => CryptoJavaApi.DigestBase64Str(args),
        _ => null,
    };

    /// <summary>
    /// 桩原语的返回值：保持"不抛错 + 返回空值"的既有行为，并记录一次
    /// <see cref="JsDiagnosticKind.StubCalled"/>（"结果为什么是空的"直接可查）。
    /// <para>返回值按原语语义给最小可用默认值：<c>ajaxJson</c> → <c>{}</c>、
    /// <c>t2s/s2t</c> → 原文（恒等比空串更安全）、其余 → 空串。</para>
    /// </summary>
    private object? InvokeStub(string name, object?[] args)
    {
        Diagnostics.RecordStubCalled(SourceKey, Field, name);
        return name switch
        {
            "ajaxJson" => "{}",
            "t2s" or "s2t" => JsPureApi.AsString(args, 0),
            _ => string.Empty,
        };
    }

    /// <summary>构建 <c>java.lang</c> 命名空间子集（System / Thread / Integer / Long / Double / String / Math / Boolean）。</summary>
    private JsValue BuildLangNamespace(Engine engine)
    {
        var lang = new JsObject(engine);

        var system = new JsObject(engine);
        system.FastSetDataProperty("currentTimeMillis", CreateFunction(engine, "currentTimeMillis",
            _ => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var println = CreateFunction(engine, "println", args =>
        {
            Diagnostics.RecordLog(SourceKey, Field, "lang: " + FormatArgs(args));
            return null;
        });
        system.FastSetDataProperty("out", NamespaceWith(engine, ("println", println), ("print", println)));
        system.FastSetDataProperty("err", NamespaceWith(engine, ("println", println), ("print", println)));
        lang.FastSetDataProperty("System", WrapWithMissingFallback(engine, system, "lang.System."));

        var thread = new JsObject(engine);
        thread.FastSetDataProperty("sleep", CreateFunction(engine, "sleep", args =>
        {
            // 上限 5s：书源用它做"限速"，但无上限会把解析线程挂死（尤其并发批量下载时）
            var ms = Math.Clamp(JsPureApi.AsLong(args, 0), 0, 5000);
            if (ms > 0)
            {
                Thread.Sleep((int)ms);
            }

            return null;
        }));
        lang.FastSetDataProperty("Thread", WrapWithMissingFallback(engine, thread, "lang.Thread."));

        var integer = new JsObject(engine);
        integer.FastSetDataProperty("parseInt", CreateFunction(engine, "parseInt", args =>
        {
            var text = JsPureApi.AsString(args, 0).Trim();
            var radix = args.Length > 1 ? (int)JsPureApi.AsLong(args, 1) : 10;
            try
            {
                return Convert.ToInt64(text, radix is >= 2 and <= 36 ? radix : 10);
            }
            catch
            {
                return 0L;
            }
        }));
        integer.FastSetDataProperty("valueOf", CreateFunction(engine, "valueOf", args => JsPureApi.AsLong(args, 0)));
        integer.FastSetDataProperty("MAX_VALUE", JsValue.FromObject(engine, int.MaxValue));
        lang.FastSetDataProperty("Integer", WrapWithMissingFallback(engine, integer, "lang.Integer."));

        var longType = new JsObject(engine);
        longType.FastSetDataProperty("parseLong", CreateFunction(engine, "parseLong", args => JsPureApi.AsLong(args, 0)));
        longType.FastSetDataProperty("valueOf", CreateFunction(engine, "valueOf", args => JsPureApi.AsLong(args, 0)));
        lang.FastSetDataProperty("Long", WrapWithMissingFallback(engine, longType, "lang.Long."));

        var doubleType = new JsObject(engine);
        doubleType.FastSetDataProperty("parseDouble", CreateFunction(engine, "parseDouble", args => ToDouble(args, 0)));
        doubleType.FastSetDataProperty("valueOf", CreateFunction(engine, "valueOf", args => ToDouble(args, 0)));
        lang.FastSetDataProperty("Double", WrapWithMissingFallback(engine, doubleType, "lang.Double."));

        var stringType = new JsObject(engine);
        stringType.FastSetDataProperty("valueOf", CreateFunction(engine, "valueOf", args => JsPureApi.AsString(args, 0)));
        lang.FastSetDataProperty("String", WrapWithMissingFallback(engine, stringType, "lang.String."));

        var math = new JsObject(engine);
        math.FastSetDataProperty("random", CreateFunction(engine, "random", _ => Random.Shared.NextDouble()));
        // abs 走 ToDouble：旧实现用 AsLong，小数被截断（Math.abs(1.5) → 1）
        math.FastSetDataProperty("abs", CreateFunction(engine, "abs", args => Math.Abs(ToDouble(args, 0))));
        math.FastSetDataProperty("floor", CreateFunction(engine, "floor", args => Math.Floor(ToDouble(args, 0))));
        math.FastSetDataProperty("ceil", CreateFunction(engine, "ceil", args => Math.Ceiling(ToDouble(args, 0))));
        // round = floor(x+0.5)：Java Math.round 与 JS Math.round 都是向 +∞ 舍入
        // （-2.5 → -2）；.NET 的 MidpointRounding.AwayFromZero 是远离零（-2.5 → -3），
        // 负数结果与 Java/JS 相反。
        math.FastSetDataProperty("round", CreateFunction(engine, "round", args => Math.Floor(ToDouble(args, 0) + 0.5)));
        lang.FastSetDataProperty("Math", WrapWithMissingFallback(engine, math, "lang.Math."));

        var booleanType = new JsObject(engine);
        booleanType.FastSetDataProperty("valueOf", CreateFunction(engine, "valueOf", args =>
        {
            var text = JsPureApi.AsString(args, 0).Trim();
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
        }));
        lang.FastSetDataProperty("Boolean", WrapWithMissingFallback(engine, booleanType, "lang.Boolean."));

        return WrapWithMissingFallback(engine, lang, "lang.");
    }

    private static double ToDouble(object?[] args, int index) =>
        double.TryParse(JsPureApi.AsString(args, index), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : 0d;

    /// <summary>把若干 (名字, 函数) 装成一个小对象（给 <c>System.out</c> 这类二级命名空间用）。</summary>
    private static JsValue NamespaceWith(Engine engine, params (string Name, JsValue Fn)[] members)
    {
        var obj = new JsObject(engine);
        foreach (var (name, fn) in members)
        {
            obj.FastSetDataProperty(name, fn);
        }

        return obj;
    }

    /// <summary>
    /// 把一个对象包成"未知成员自动兜底"的 Proxy（实现见 <see cref="JsContextObjects"/>，
    /// <c>java</c> 与 <c>source/book/cache/cookie/html</c> 共用同一份）。
    /// <para>兜底行为：记一条 <see cref="JsDiagnosticKind.MissingApi"/>，返回 <c>null</c>。
    /// 这样 <c>java.不存在的方法()</c> 不再抛 TypeError 把整段脚本废掉。</para>
    /// <para>回调在<b>调用时</b>读 <see cref="Diagnostics"/>/<see cref="SourceKey"/>/<see cref="Field"/>：
    /// java 对象按引擎缓存，若在构建时捕获这些值，换链/换源后诊断会张冠李戴（4-1 的潜在缺陷）。</para>
    /// </summary>
    private JsValue WrapWithMissingFallback(Engine engine, JsValue target, string prefix) =>
        JsContextObjects.WrapWithMissingFallback(
            engine, target, prefix, member => Diagnostics.RecordMissingApi(SourceKey, Field, member));

    /// <summary>
    /// 把一个 CLR 委托包成 JS 函数（实现见 <see cref="JsContextObjects.CreateFunction"/>）。
    /// <para>异常吞在函数内部（记诊断 + 返回 null），不让单个原语把整段脚本作废。</para>
    /// </summary>
    private JsValue CreateFunction(Engine engine, string name, Func<object?[], object?> body) =>
        JsContextObjects.CreateFunction(
            engine, name, body, new JsContextObjects.JsErrorSink(Diagnostics, SourceKey, Field));

    /// <summary>把参数拼成一行文本（诊断样本用；过长的由 <see cref="JsDiagnostics"/> 截断）。</summary>
    private static string FormatArgs(object?[] args) => JsContextObjects.FormatArgs(args);

    /// <summary>
    /// 规则求值的可选内容参数（阶段 4-5）：<c>getString(rule, mContent)</c> 的第二参数优先；
    /// 缺省时回落到 <c>setContent</c> 的替换内容；再缺省 = 当前 result（传 null）。
    /// </summary>
    private string? ContentArg(object?[] args) =>
        args.Length > 1 && args[1] is not null ? JsPureApi.AsString(args, 1) : _contentOverride;

    /// <summary>把求值项列表序列化成 JSON 数组文本（<see cref="RuleJavaApi.GetStringList"/> 入参）。</summary>
    private static string SerializeList(IReadOnlyList<object?> items)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(System.Text.Json.JsonSerializer.Serialize(items[i]?.ToString() ?? string.Empty));
        }

        return sb.Append(']').ToString();
    }

    /// <summary>引擎与其对应 <c>java</c> 对象的绑定（引擎换了就重建）。</summary>
    private sealed record JavaBinding(Engine Engine, JsValue Java);
}
