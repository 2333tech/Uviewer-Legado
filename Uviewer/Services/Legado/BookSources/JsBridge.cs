using System.Security.Cryptography;
using System.Text;
using Jint;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// legado 内联 JS / 脚本段执行宿主（基于 Jint）。
/// 暴露 <c>result</c> / <c>baseUrl</c> / <c>java.*</c> API。
/// <c>java.getString(rule)</c> 回灌规则引擎，作用于当前 result 内容（由 RuleEngine 注入 EvaluateRule）。
/// <para>生命周期：一个 JsBridge 实例服务一条解析链（一次搜索 / 一次目录 / 一次正文），
/// <see cref="_store"/>（java.put/get 仓库）按实例共享——对标 reader js.rs 的共享作用域：
/// ruleToc.init 里 put 的变量，后续字段规则里 get 得到。</para>
/// <para>重入保护：java.getString 会再次触发规则求值（→ 可能再 RunJs）。
/// 同一 Jint 引擎嵌套求值会互相污染全局状态，故 <see cref="_inUse"/> 为真时
/// 新建临时引擎跑内层脚本（temp=true，用完即弃），外层仍用主引擎。</para>
/// </summary>
internal sealed class JsBridge
{
    /// <summary>由 RuleEngine 注入：对当前 result 内容求值某条规则，返回字符串。</summary>
    public Func<string, string>? EvaluateRule { get; set; }

    /// <summary>
    /// 当前书源的 jsLib 原始文本（内联脚本或 <c>{"name":"脚本|URL"}</c> JSON 形式；null/空 = 无库）。
    /// <para>由 RuleEngine 在每条解析链的公开入口设置（见 <c>RuleEngine._jsSource</c>）。
    /// 编译与加载语义见 <see cref="EnsureJsLib"/>。</para>
    /// </summary>
    public string? JsLib { get; set; }

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

    // java.put/get 的变量仓库必须跨多次 Run 共享：legado 书源常见写法是
    // ruleToc.init / ruleBookInfo.init 里 java.put(key, value)，后续字段规则里 java.get(key)。
    // reader 侧对应 js.rs 的共享作用域；这里按 JsBridge 实例共享（一个书源一条解析链）。
    private readonly Dictionary<string, object?> _store = new();
    private JavaApi? _api;

    /// <summary>
    /// 执行脚本。<paramref name="globals"/> 用于注入额外全局变量（如 formatJs 的 index/title/chapter）。
    /// <para>步骤：① 取引擎（重入 → 临时引擎）；② 绑定 result/baseUrl/java 及 globals；
    /// ③ Evaluate；④ 结果转字符串（null → 空串）；⑤ 异常时**返回原始 value**
    /// （legado：脚本异常回退为原始内容，不中断解析）。</para>
    /// </summary>
    public string Run(
        string script,
        string value,
        string baseUrl,
        IReadOnlyDictionary<string, object?>? globals = null)
    {
        try
        {
            // ⓪ 先解析库代码（可能使旧引擎失效 → 必须在取引擎之前，见 ResolveJsLib 的书源隔离说明）
            var lib = ResolveJsLib();

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

            try
            {
                // ② 绑定全局：result（当前内容）/ baseUrl / java API / 调用方 globals
                engine.SetValue("result", value);
                engine.SetValue("baseUrl", baseUrl);
                engine.SetValue("java", _api ??= new JavaApi(this));
                if (globals is not null)
                {
                    foreach (var (k, v) in globals)
                    {
                        engine.SetValue(k, v);
                    }
                }

                // ②b 加载 jsLib（书源库代码）：函数/变量定义先于脚本求值生效
                EnsureJsLib(engine, lib, temp);

                // ③④ 求值并转字符串
                // 走编译产物缓存：同一脚本在"每条字段 × 每个片段"上反复执行，
                // engine.Evaluate(string) 每次都会重新解析脚本（4KB 约 5ms，JS 书源每章都付这笔钱）。
                var r = engine.Evaluate(JsScriptCache.Get(script));
                return r?.ToString() ?? string.Empty;
            }
            finally
            {
                if (!temp)
                {
                    _inUse = false;
                }
            }
        }
        catch
        {
            // ⑤ legado：脚本异常时回退为原始内容
            return value;
        }
    }

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

        _libCompiled = JsLibCompiler.Compile(raw);
        _libCompiledFrom = raw;
        return _libCompiled;
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

    /// <summary>
    /// java.* API 桥（Jint 通过反射暴露给 JS 的 CLR 对象）。
    /// <para>离线语义说明：ajax / startBrowserAwait / getElements 等需要网络或浏览器的原语
    /// 在本环境返回空值/空对象，由脚本自行兜底——真实环境由宿主（阶段 4 的 WebView2 管道）替换。</para>
    /// </summary>
    private sealed class JavaApi
    {
        private readonly JsBridge _bridge;

        public JavaApi(JsBridge bridge) => _bridge = bridge;

        /// <summary>java.getString(rule)：对当前 result 回灌规则引擎求值（Reader 侧最常用原语）。</summary>
        public string getString(string rule) => _bridge.EvaluateRule?.Invoke(rule) ?? string.Empty;

        /// <summary>java.put(key, value)：存变量仓库（加锁：JS 单线程但仓库跨 Run 共享）。</summary>
        public void put(string key, object? value)
        {
            lock (_bridge._store)
            {
                _bridge._store[key] = value;
            }
        }

        /// <summary>java.get(key)：取变量仓库（无则 null → JS 侧表现为 null）。</summary>
        public object? get(string key)
        {
            lock (_bridge._store)
            {
                return _bridge._store.TryGetValue(key, out var v) ? v : null;
            }
        }

        /// <summary>java.base64Encode(s)：UTF-8 → Base64。</summary>
        public string base64Encode(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        /// <summary>java.base64Decode(s)：Base64 → UTF-8（非法输入返回空串，不抛）。</summary>
        public string base64Decode(string s)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>java.md5(s)：UTF-8 字节的 MD5，输出小写 Hex。</summary>
        public string md5(string s)
        {
            using var m = MD5.Create();
            var b = m.ComputeHash(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(b).ToLowerInvariant();
        }

        // 离线环境无网络：ajax / 浏览器交互返回空，由脚本自行兜底。

        /// <summary>java.ajax(url)：同步取文本（离线返回空串；阶段 4 接真实管道）。</summary>
        public string ajax(string url) => string.Empty;

        /// <summary>java.ajaxJson(url)：同步取 JSON（离线返回空对象字面量）。</summary>
        public string ajaxJson(string url) => "{}";

        /// <summary>java.startBrowserAwait(url, name)：打开浏览器等待（离线返回空串）。</summary>
        public string startBrowserAwait(string url, string name) => string.Empty;

        /// <summary>java.getElements(rule)：取元素快照列表（离线返回空数组）。</summary>
        public string getElements(string rule) => "[]";

        /// <summary>java.getElement(rule)：取单个元素快照（离线返回空对象）。</summary>
        public string getElement(string rule) => "{}";
    }
}
