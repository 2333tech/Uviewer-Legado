using System;
using System.Collections.Generic;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 JS 原语的能力分层（阶段 4 排期口径）。
/// <para>层次不是"难度"而是**依赖**：上一层不落地，下一层就无从谈起。</para>
/// </summary>
public enum JsApiTier
{
    /// <summary>T0 纯函数：无网络、无宿主状态，只做字符串/时间/编码/摘要变换。</summary>
    Pure = 0,

    /// <summary>T1 网络：需要同步桥接 <see cref="IBookSourceFetcher"/>（ajax/post/connect）。</summary>
    Network = 1,

    /// <summary>T2 规则：需要回灌规则引擎（getStringList/getElements/ruleUrl/setContent）。</summary>
    Rule = 2,

    /// <summary>T3 状态：需要按书源/书籍维护的变量、缓存、Cookie 仓库。</summary>
    State = 3,

    /// <summary>T4 浏览器：需要 WebView2 宿主（startBrowser/webView/openUrl）。</summary>
    Browser = 4,
}

/// <summary>
/// 原语当前落地状态。
/// <para><b>Stub 与 Missing 的区别很重要</b>：Stub 是"方法存在但返回空"，脚本不会抛错，
/// 只是拿到空值（静默失真）；Missing 是"方法根本不存在"，脚本会抛 TypeError。
/// 阶段 4 的静态 API 面预注册把 Missing 也变成"会记录诊断的空桩"，从而不再整段作废。</para>
/// </summary>
public enum JsApiStatus
{
    /// <summary>已实现，行为对齐 legado。</summary>
    Implemented,

    /// <summary>存在但返回空/占位（结果会静默失真，诊断里可见）。</summary>
    Stub,

    /// <summary>完全没有实现（脚本调用即抛错）。</summary>
    Missing,
}

/// <summary>一条原语说明（名字 / 层级 / 状态 / 备注）。</summary>
public readonly record struct JsApiInfo(string Name, JsApiTier Tier, JsApiStatus Status, string Note);

/// <summary>
/// legado <c>java.*</c> 原语目录（阶段 4 的唯一权威清单）。
/// <para>三个用途：</para>
/// <list type="number">
/// <item><b>运行时</b>：<see cref="JsBridge"/> 按本表注册真实实现；未登记/未实现的名字由 Proxy 兜底为空桩；</item>
/// <item><b>能力评估</b>：<see cref="BookSourceCapabilities"/> 用本表把"用到某原语"翻译成"是否真的缺能力"，
///   取代此前"出现 java.* 就算需要阶段 4"的粗口径；</item>
/// <item><b>排期</b>：<see cref="RequiredTier"/> 给出一个书源需要做到哪一层才算可跑。</item>
/// </list>
/// <para>清单来源：`Sample/bookSource_202610192010.json` 全量扫描（408 条）的调用频次 +
/// legado `JsExtensions`/`JsEncodeUtils` 的公开面。新增原语必须同时登记本表，否则会被当成 Unknown
/// （Unknown 默认按 <see cref="JsApiStatus.Missing"/> 计，宁可报缺也不要假成功）。</para>
/// </summary>
public static class JsApiCatalog
{
    /// <summary>未登记名字的默认状态：按"缺失"处理（能力评估会如实报出）。</summary>
    public const JsApiStatus UnknownStatus = JsApiStatus.Missing;

    private static readonly Dictionary<string, JsApiInfo> Index = Build();

    /// <summary>全部已登记原语（按名字排序，便于测试与界面展示）。</summary>
    public static IReadOnlyCollection<JsApiInfo> All => _all;

    private static readonly JsApiInfo[] _all = BuildArray();

    /// <summary>查一条原语；未登记返回 false（调用方按 <see cref="UnknownStatus"/> 处理）。</summary>
    public static bool TryGet(string name, out JsApiInfo info) => Index.TryGetValue(name, out info);

    /// <summary>该原语是否已可正常使用（只有 <see cref="JsApiStatus.Implemented"/> 算）。</summary>
    public static bool IsImplemented(string name) =>
        Index.TryGetValue(name, out var info) && info.Status == JsApiStatus.Implemented;

    /// <summary>该原语需要哪一层才可跑（未登记 = 纯函数层都不知道，按最高层保守处理）。</summary>
    public static JsApiTier TierOf(string name) =>
        Index.TryGetValue(name, out var info) ? info.Tier : JsApiTier.Browser;

    /// <summary>
    /// 一组原语里"还需要做到哪一层"：取未实现原语的最高层；全部已实现返回 <see cref="JsApiTier.Pure"/>。
    /// <para>语义：返回值 = 该书源要跑通，阶段 4 至少要完成到这一层。</para>
    /// </summary>
    public static JsApiTier RequiredTier(IEnumerable<string> names)
    {
        var max = JsApiTier.Pure;
        foreach (var name in names)
        {
            if (IsImplemented(name))
            {
                continue;
            }

            var tier = TierOf(name);
            if (tier > max)
            {
                max = tier;
            }
        }

        return max;
    }

    /// <summary>一组原语里尚未实现的名字（保持传入顺序去重，供 UI 显示"缺什么"）。</summary>
    public static List<string> MissingNames(IEnumerable<string> names)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!IsImplemented(name) && seen.Add(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static JsApiInfo[] BuildArray()
    {
        var list = new List<JsApiInfo>(Index.Count);
        foreach (var pair in Index)
        {
            list.Add(pair.Value);
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list.ToArray();
    }

    /// <summary>
    /// 目录内容。状态列的口径 = <b>当前代码的真实行为</b>（不是"计划"）：
    /// 谁改成真实实现，必须同时把这里改成 <see cref="JsApiStatus.Implemented"/>，
    /// 否则能力评估会继续把可用书源判成"需要阶段 4"（这正是 4-1 之前的老毛病）。
    /// </summary>
    private static Dictionary<string, JsApiInfo> Build()
    {
        var list = new (string Name, JsApiTier Tier, JsApiStatus Status, string Note)[]
        {
            // ── T0 纯函数：4-1 已实现 ───────────────────────────────────────────
            ("log", JsApiTier.Pure, JsApiStatus.Implemented, "调试日志（进诊断环形缓冲，不改变脚本行为）"),
            ("toast", JsApiTier.Pure, JsApiStatus.Implemented, "短提示（进诊断，宿主可后续接 UI）"),
            ("longToast", JsApiTier.Pure, JsApiStatus.Implemented, "长提示（同 toast）"),
            ("md5", JsApiTier.Pure, JsApiStatus.Implemented, "UTF-8 MD5 小写 hex（32 位）"),
            ("md5Encode", JsApiTier.Pure, JsApiStatus.Implemented, "Legado 主用名，同 md5"),
            ("md5Encode16", JsApiTier.Pure, JsApiStatus.Implemented, "32 位 hex 的第 9~24 位（legado substring(8,24)）"),
            ("base64Encode", JsApiTier.Pure, JsApiStatus.Implemented, "UTF-8 → Base64"),
            ("base64Decode", JsApiTier.Pure, JsApiStatus.Implemented, "Base64 → UTF-8（非法输入返回空串）"),
            ("timeFormat", JsApiTier.Pure, JsApiStatus.Implemented, "毫秒时间戳 → yyyy/MM/dd HH:mm（本地时区，legado AppConst.dateFormat）"),
            ("timeFormatUTC", JsApiTier.Pure, JsApiStatus.Implemented, "(时间戳, 格式?, 时区小时偏移?) → 指定时区格式化"),
            ("encodeURI", JsApiTier.Pure, JsApiStatus.Implemented, "Java URLEncoder 语义（空格→+）"),
            ("hexDecodeToString", JsApiTier.Pure, JsApiStatus.Implemented, "hex → UTF-8 字符串"),
            ("hexEncodeToString", JsApiTier.Pure, JsApiStatus.Implemented, "UTF-8 → 小写 hex"),
            ("toNumChapter", JsApiTier.Pure, JsApiStatus.Implemented, "章节名中文数字归一（第X章 → 第N章）"),
            ("getUserAgent", JsApiTier.Pure, JsApiStatus.Implemented, "返回引擎默认 UA（与静态抓取一致）"),
            ("getWebViewUA", JsApiTier.Pure, JsApiStatus.Implemented, "同上（无 WebView2 时用静态 UA，避免脚本取到 null）"),
            ("randomUUID", JsApiTier.Pure, JsApiStatus.Implemented, "随机 UUID 字符串（legado java.randomUUID）"),
            ("lang", JsApiTier.Pure, JsApiStatus.Implemented, "java.lang 命名空间子集（System/Thread/Integer/Long/Double/String/Math）"),

            // ── T0 纯函数：仍缺（需要词典/大表，延后） ──────────────────────────
            ("t2s", JsApiTier.Pure, JsApiStatus.Stub, "繁转简：需简繁词典，暂恒等返回并记诊断"),
            ("s2t", JsApiTier.Pure, JsApiStatus.Stub, "简转繁：同上"),

            // ── T0 纯函数：加解密族（样本里 10~20 处，独立小项） ────────────────
            ("createSymmetricCrypto", JsApiTier.Pure, JsApiStatus.Missing, "对称加解密对象（AES/DES）"),
            ("aesBase64DecodeToString", JsApiTier.Pure, JsApiStatus.Missing, "AES 解密（旧 API）"),
            ("desEncodeToBase64String", JsApiTier.Pure, JsApiStatus.Missing, "DES 加密（旧 API）"),
            ("HMacHex", JsApiTier.Pure, JsApiStatus.Missing, "HMAC → hex"),
            ("digestHex", JsApiTier.Pure, JsApiStatus.Missing, "摘要 → hex"),
            ("createSign", JsApiTier.Pure, JsApiStatus.Missing, "签名对象"),
            ("createAsymmetricCrypto", JsApiTier.Pure, JsApiStatus.Missing, "非对称加解密对象"),
            ("base64DecodeToByteArray", JsApiTier.Pure, JsApiStatus.Missing, "Base64 → 字节数组（加解密前置）"),
            ("hexDecodeToByteArray", JsApiTier.Pure, JsApiStatus.Missing, "hex → 字节数组（加解密前置）"),
            ("androidId", JsApiTier.Pure, JsApiStatus.Missing, "Android 设备号（桌面端无对应语义）"),
            ("util", JsApiTier.Pure, JsApiStatus.Missing, "java.util 命名空间（UUID/Base64/List…）"),
            ("security", JsApiTier.Pure, JsApiStatus.Missing, "java.security 命名空间（MessageDigest/SecureRandom…）"),

            // ── T1 网络：4-3 已实现（同步桥，复用取数管线：限速 + 共享 Cookie） ─────────
            ("ajax", JsApiTier.Network, JsApiStatus.Implemented,
                "同步取文本（含 ,{json} 选项块；失败返回空串并记诊断）"),
            ("ajaxJson", JsApiTier.Network, JsApiStatus.Stub, "非 legado 正式 API（样本无调用），保持空 {}"),
            ("ajaxAll", JsApiTier.Network, JsApiStatus.Implemented, "串行取多个 URL，返回 Array<StrResponse>"),
            ("ajaxTestAll", JsApiTier.Network, JsApiStatus.Implemented, "同上 + 单请求超时（毫秒）"),
            ("post", JsApiTier.Network, JsApiStatus.Implemented, "同步 POST，返回 StrResponse（body()/code()/…）"),
            ("head", JsApiTier.Network, JsApiStatus.Implemented, "同步 HEAD，返回 StrResponse"),
            ("connect", JsApiTier.Network, JsApiStatus.Implemented, "自定义请求（选项块 method/headers/body），返回 StrResponse"),
            ("getStrResponse", JsApiTier.Network, JsApiStatus.Implemented,
                "返回 StrResponse（无参 = 本链最近一次响应）"),

            // ── T2 规则回灌 ─────────────────────────────────────────────────────
            ("getString", JsApiTier.Rule, JsApiStatus.Implemented, "对当前 result 跑规则求值"),
            ("getStringList", JsApiTier.Rule, JsApiStatus.Stub, "对当前 result 跑规则求字符串列表（当前空数组）"),
            ("getElements", JsApiTier.Rule, JsApiStatus.Stub, "取元素快照数组（当前空数组）"),
            ("getElement", JsApiTier.Rule, JsApiStatus.Stub, "取单个元素快照（当前空对象）"),
            ("ruleUrl", JsApiTier.Rule, JsApiStatus.Missing, "按规则重新解析 URL"),
            ("refreshTocUrl", JsApiTier.Rule, JsApiStatus.Missing, "重算目录地址"),
            ("setContent", JsApiTier.Rule, JsApiStatus.Missing, "改写当前解析内容"),
            ("htmlFormat", JsApiTier.Rule, JsApiStatus.Missing, "HTML → 纯文本（保留图片标记）"),
            ("toURL", JsApiTier.Rule, JsApiStatus.Missing, "URL 相对解析辅助对象"),

            // ── T3 宿主状态 ─────────────────────────────────────────────────────
            ("get", JsApiTier.State, JsApiStatus.Implemented,
                "取变量：章节变量 → 书籍变量 → 书源变量（4-2 起跨链、跨进程，落 legado/sources）"),
            ("put", JsApiTier.State, JsApiStatus.Implemented,
                "存变量：有章节写章节、否则写书籍、再否则写书源（同 get 的作用域链）"),
            ("getCookie", JsApiTier.State, JsApiStatus.Implemented,
                "按域取 Cookie（4-2 的 Cookie 仓库；4-3 起与 HTTP 会话共用同一份）"),
            ("io", JsApiTier.State, JsApiStatus.Missing, "java.io 命名空间（文件读写）"),

            // ── T4 浏览器 ───────────────────────────────────────────────────────
            ("startBrowserAwait", JsApiTier.Browser, JsApiStatus.Stub, "打开浏览器并等待（当前恒空串）"),
            ("startBrowser", JsApiTier.Browser, JsApiStatus.Missing, "打开浏览器（不等待）"),
            ("webView", JsApiTier.Browser, JsApiStatus.Missing, "在页面上下文执行脚本"),
            ("webViewGetOverrideUrl", JsApiTier.Browser, JsApiStatus.Missing, "WebView 加载指定 URL 并取覆盖内容"),
            ("getVerificationCode", JsApiTier.Browser, JsApiStatus.Missing, "验证码识别（宿主能力）"),
            ("openUrl", JsApiTier.Browser, JsApiStatus.Missing, "用系统浏览器打开"),
            ("openVideoPlayer", JsApiTier.Browser, JsApiStatus.Missing, "打开视频播放器（宿主 UI）"),

            // ── 宿主 UI 能力（legado Android 特有，桌面引擎不做，仅供能力评估识别） ──
            ("readBookConfig", JsApiTier.State, JsApiStatus.Missing, "读阅读配置（宿主 UI）"),
            ("getReadBookConfigMap", JsApiTier.State, JsApiStatus.Missing, "读阅读配置表（宿主 UI）"),
            ("getThemeConfigMap", JsApiTier.State, JsApiStatus.Missing, "读主题配置表（宿主 UI）"),
            ("importScript", JsApiTier.State, JsApiStatus.Missing, "导入书源脚本（宿主 UI）"),
            ("initUrl", JsApiTier.State, JsApiStatus.Missing, "刷新登录态后重取（宿主流程）"),
            ("refreshContent", JsApiTier.State, JsApiStatus.Missing, "刷新当前阅读内容（宿主 UI）"),
            ("refreshExplore", JsApiTier.State, JsApiStatus.Missing, "刷新发现页（宿主 UI）"),
            ("searchBook", JsApiTier.State, JsApiStatus.Missing, "搜索书籍并打开（宿主 UI）"),
            ("showBrowser", JsApiTier.State, JsApiStatus.Missing, "显示内置浏览器（宿主 UI）"),
            ("upLoginData", JsApiTier.State, JsApiStatus.Missing, "上传登录数据（宿主流程）"),
            ("key", JsApiTier.State, JsApiStatus.Missing, "legado 遗留字段（脚本里多作守卫，恒 null 即可）"),
            ("math", JsApiTier.Pure, JsApiStatus.Missing, "java.math 命名空间（BigInteger 等）"),
            ("net", JsApiTier.Network, JsApiStatus.Missing, "java.net 命名空间（URL/HttpURLConnection）"),
            ("nio", JsApiTier.Pure, JsApiStatus.Missing, "java.nio.charset 命名空间（Charset）"),
        };

        var map = new Dictionary<string, JsApiInfo>(list.Length, StringComparer.Ordinal);
        foreach (var item in list)
        {
            map[item.Name] = new JsApiInfo(item.Name, item.Tier, item.Status, item.Note);
        }

        return map;
    }
}

/// <summary>
/// 书源 JS 全局对象目录（<c>source</c> / <c>book</c> / <c>chapter</c> / <c>cache</c> / <c>cookie</c> / <c>html</c>）。
/// <para><b>阶段 4-2：除 <c>html</c> 外全部注入真实实现</b>（见 <see cref="JsContextObjects"/>），
/// 能力评估据此不再把它们算作缺口；<c>html</c> 只有"记录型空桩"，且<b>不参与能力判定</b>
/// （见 <see cref="IsGated"/> 的说明）。</para>
/// <para><c>chapter</c> 是方案 §3.2.3 之外顺带补的：约 10 个视频类书源的正文规则依赖
/// <c>chapter.index/title</c>，只有 <c>book</c> 没有 <c>chapter</c> 时脚本仍会
/// <c>ReferenceError</c>（半修复）。</para>
/// <para>成员表来自原始样本 408 条的实调用统计；不在表内的成员名不参与判定
/// （防止把脚本里的同名字符串/局部变量误判成全局对象用法）。</para>
/// </summary>
public static class JsGlobalCatalog
{
    /// <summary>全局对象根名（判定顺序固定，便于诊断输出稳定）。</summary>
    public static readonly string[] Roots = { "source", "book", "chapter", "cache", "cookie", "html" };

    /// <summary>各根对象在样本里真实出现过的成员名。</summary>
    private static readonly Dictionary<string, string[]> Members = new(StringComparer.Ordinal)
    {
        ["source"] = new[]
        {
            "key", "getVariable", "putVariable", "setVariable", "get", "put",
            "getLoginInfo", "getLoginInfoMap", "putLoginInfo", "removeLoginInfo",
            "getLoginHeader", "getLoginHeaderMap", "putLoginHeader", "removeLoginHeader",
            "loginUrl", "bookSourceUrl", "bookSourceName", "bookSourceComment", "bookSourceType",
            "getKey", "refreshExplore", "refreshJSLib", "login", "header", "variableComment",
            "lastUpdateTime",
        },
        ["book"] = new[]
        {
            "name", "author", "bookUrl", "tocUrl", "intro", "kind", "type", "coverUrl", "origin",
            "originName", "lastChapter", "group", "totalChapterNum",
            "durChapterIndex", "durChapterTitle", "getVariable", "putVariable", "setVariable",
            "customIntro", "readConfig", "setReverseToc", "variableMap", "getKey",
            "putCustomVariable", "getUseReplaceRule", "setUseReplaceRule",
        },
        ["cache"] = new[]
        {
            "get", "put", "delete", "getFromMemory", "putMemory", "deleteMemory",
            "getFile", "putFile", "deleteFile",
        },
        ["chapter"] = new[]
        {
            "title", "url", "index", "getVariable", "putVariable", "setVariable", "getKey",
        },
        ["cookie"] = new[]
        {
            "getKey", "getCookie", "removeCookie", "setCookie", "setWebCookie", "replaceCookie",
        },
        ["html"] = new[]
        {
            "match", "replace", "select", "includes", "split",
        },
    };

    /// <summary>
    /// 该根对象是否已注入<b>真实实现</b>（阶段 4-2：source/book/chapter/cache/cookie 为 true）。
    /// <para><c>html</c> 恒为 false：legado 的 JS 绑定里没有这个全局对象（只有
    /// <c>java/cookie/cache/source/book/chapter</c> 等），我们只放一个记录型空桩避免
    /// <c>html.xxx</c> 抛 TypeError，不算"已实现"。</para>
    /// </summary>
    public static bool IsImplemented(string root) =>
        root is "source" or "book" or "chapter" or "cache" or "cookie";

    /// <summary>
    /// 该根对象是否作为"能力缺口"的判定依据。
    /// <para><b>为什么 <c>html</c> 不判定</b>：原始样本里 15 处 <c>html.match(…)</c> 经逐条核对
    /// <b>全部是脚本内的局部变量</b>（<c>let html = java.ajax(…)</c>），legado 本身也没有
    /// <c>html</c> 全局对象。把它算作缺口等于把 4 个本来能跑的源误判成"需要阶段 4"
    /// （与 4-1 修掉的 D1 误杀同类）。</para>
    /// </summary>
    public static bool IsGated(string root) =>
        root is "source" or "book" or "chapter" or "cache" or "cookie";

    /// <summary>该根对象是否有这个成员（用于把"疑似全局用法"收紧成"确定用法"）。</summary>
    public static bool HasMember(string root, string member) =>
        Members.TryGetValue(root, out var list) && Array.IndexOf(list, member) >= 0;

    /// <summary>该根对象的已知成员（诊断/文档用）。</summary>
    public static IReadOnlyList<string> MembersOf(string root) =>
        Members.TryGetValue(root, out var list) ? list : Array.Empty<string>();
}
