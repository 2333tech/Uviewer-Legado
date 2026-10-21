using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源能力标记（粗分类，界面与过滤用）。
/// <para>阶段 4-1 起，本枚举只是 <see cref="SourceCapabilityReport"/> 的摘要；
/// 判定依据从"出现过 <c>java.*</c>"改成"用到**尚未实现**的原语"（见 <see cref="JsApiCatalog"/>）。</para>
/// </summary>
[Flags]
public enum BookSourceCapability
{
    /// <summary>无特殊依赖：纯选择器/正则/纯 JS，当前引擎完整支持。</summary>
    None = 0,

    /// <summary>用到当前引擎尚未实现的 <c>java.*</c> 原语或未注入的全局对象（source/book/cache/cookie/html）。</summary>
    RequiresJsEngine = 1,

    /// <summary>依赖 WebView 后处理（<c>webJs</c> 字段 / <c>@webjs:</c> 标记 / 浏览器类原语）。</summary>
    RequiresWebView = 2,

    /// <summary>带 <c>loginUrl</c>：完整登录流需 WebView2 登录窗；已注入 Cookie 的会话可先用。</summary>
    RequiresLogin = 4,

    /// <summary><c>jsLib</c> 含远程 URL 条目：需联网下载后才能生效。</summary>
    RequiresRemoteJsLib = 8,

    /// <summary>带 <c>jsLib</c>（内联库已由当前引擎注入，仅作信息标记）。</summary>
    HasJsLib = 16,
}

/// <summary>
/// 书源能力评估结果（阶段 4-1 新增，取代"一个 Flags 枚举说不清缺什么"的旧口径）。
/// <para>阅读链路（ruleToc + ruleContent）与全源分开统计：一个源的搜索规则可能用了
/// <c>java.ajax</c> 而目录/正文是干净选择器——这种源应当允许搜索并深链阅读（旧实现会整源排除）。</para>
/// </summary>
public sealed record SourceCapabilityReport
{
    /// <summary>粗分类标记（界面徽标 / 兼容既有调用方）。</summary>
    public BookSourceCapability Flags { get; init; }

    /// <summary>该书源要跑通至少要做到的原语层（T0~T4）。全部已实现时为 T0。</summary>
    public JsApiTier RequiredTier { get; init; }

    /// <summary>全源范围内尚未实现的 <c>java.*</c> 原语名（升序去重）。</summary>
    public IReadOnlyList<string> MissingApis { get; init; } = Array.Empty<string>();

    /// <summary>阅读链路（ruleToc/ruleContent）尚未实现的原语名。</summary>
    public IReadOnlyList<string> ReadingMissingApis { get; init; } = Array.Empty<string>();

    /// <summary>全源范围内用到但未注入的全局对象根名。</summary>
    public IReadOnlyList<string> MissingGlobals { get; init; } = Array.Empty<string>();

    /// <summary>阅读链路用到但未注入的全局对象根名。</summary>
    public IReadOnlyList<string> ReadingMissingGlobals { get; init; } = Array.Empty<string>();

    /// <summary>用到 <c>webJs</c> / <c>@webjs:</c>（需要 WebView 页面上下文执行脚本）。</summary>
    public bool NeedsWebView { get; init; }

    /// <summary>阅读链路用到 <c>webJs</c> / <c>@webjs:</c>。</summary>
    public bool ReadingNeedsWebView { get; init; }

    /// <summary>带 <c>loginUrl</c>。</summary>
    public bool NeedsLogin { get; init; }

    /// <summary><c>jsLib</c> 含远程 URL 条目。</summary>
    public bool HasRemoteJsLib { get; init; }

    /// <summary>阅读链路当前是否可跑（不看登录：登录是运行期条件，不影响静态能力判定）。</summary>
    public bool ReadingSupported =>
        ReadingMissingApis.Count == 0 && ReadingMissingGlobals.Count == 0 && !ReadingNeedsWebView;

    /// <summary>整源（含搜索/详情）当前是否可跑。</summary>
    public bool AllSupported =>
        MissingApis.Count == 0 && MissingGlobals.Count == 0 && !NeedsWebView;

    /// <summary>把缺口压成一句人话（界面徽标/日志用）：<c>缺 ajax,md5Encode；缺对象 book</c>。</summary>
    public string DescribeMissing(int maxNames = 4, bool readingOnly = true)
    {
        var apis = readingOnly ? ReadingMissingApis : MissingApis;
        var globals = readingOnly ? ReadingMissingGlobals : MissingGlobals;
        var webView = readingOnly ? ReadingNeedsWebView : NeedsWebView;

        var parts = new List<string>(3);
        if (apis.Count > 0)
        {
            parts.Add("缺 " + Join(apis, maxNames));
        }

        if (globals.Count > 0)
        {
            parts.Add("缺对象 " + Join(globals, maxNames));
        }

        if (webView)
        {
            parts.Add("需 WebView");
        }

        return string.Join("；", parts);
    }

    private static string Join(IReadOnlyList<string> names, int maxNames)
    {
        if (names.Count <= maxNames)
        {
            return string.Join(",", names);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < maxNames; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(names[i]);
        }

        sb.Append("…(共").Append(names.Count).Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// 书源能力静态评估（纯函数，无 IO）。
///
/// <para><b>2026-10-21 口径修正（阶段 4-1，重要）</b>：旧实现把"规则里出现 <c>java.*</c>"直接判为
/// "需要阶段 4"，于是 <c>java.getString</c> / <c>java.put</c> 这类**早已实现**的原语也会整源排除。
/// 按原始样本 408 条实测：旧口径放行 277 条，而按"未实现原语"精算是 289 条——12 条被误杀
/// （用户侧表现为"这本书搜不到"）。现口径：</para>
/// <list type="number">
/// <item>用 <see cref="JsApiSurfaceScanner"/> 抽取脚本上下文里的 <c>java.X</c> 与全局对象用法；</item>
/// <item>逐个查 <see cref="JsApiCatalog"/>，只有**未实现**的才算缺口；</item>
/// <item>全局对象：阶段 4-2 起 <c>source/book/cache/cookie</c> 已注入真实实现，不再算缺口；
///   <c>html</c> 不作为判定依据（样本里的用法全是局部变量，见 <see cref="JsGlobalCatalog.IsGated"/>）；</item>
/// <item>阅读链路与全源分开统计，搜索/详情链的缺口不再连坐阅读。</item>
/// </list>
///
/// <para>扫描范围：<c>searchUrl</c>/<c>exploreUrl</c>/<c>loginUrl</c>（仅带 JS 标记时）、
/// <c>loginCheckJs</c>/<c>jsLib</c>（本身就是脚本）、以及五个规则对象的全部字符串字段
/// （字段名以 <c>Js</c> 结尾或带 <c>&lt;js&gt;</c>/<c>@js:</c>/<c>js:</c> 标记才按脚本处理）。
/// 不含 <c>header</c>（请求头在取数层处理，不进 JS 作用域）。</para>
/// </summary>
public static class BookSourceCapabilities
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StringProperties = new();

    /// <summary>完整评估（缺哪些原语、哪些全局对象、需要哪一层）。</summary>
    public static SourceCapabilityReport AssessDetailed(BookSourceModel source)
    {
        if (source is null)
        {
            return new SourceCapabilityReport { Flags = BookSourceCapability.None };
        }

        var all = new JsApiSurfaceScanner.Builder();
        var reading = new JsApiSurfaceScanner.Builder();
        var flags = BookSourceCapability.None;
        var needsWebView = false;
        var readingNeedsWebView = false;

        // ① 顶层 URL 字段：只有带 JS 标记时才按脚本扫描（URL 模板里的 "java." 不误报）
        ScanTopLevel(all, source.SearchUrl);
        ScanTopLevel(all, source.ExploreUrl);
        ScanTopLevel(all, source.LoginUrl);

        // ② 本身就是脚本的顶层字段：整段扫描
        if (!string.IsNullOrWhiteSpace(source.LoginCheckJs))
        {
            all.Add(source.LoginCheckJs);
        }

        var hasRemoteJsLib = false;
        if (!string.IsNullOrWhiteSpace(source.JsLib))
        {
            flags |= BookSourceCapability.HasJsLib;
            all.Add(source.JsLib);
            hasRemoteJsLib = JsLibCompiler.HasRemoteEntry(source.JsLib);
        }

        // ③ 规则对象：阅读链路（Toc/Content）单独累计
        ScanRule(source.RuleSearch, isReading: false, all, reading, ref needsWebView, ref readingNeedsWebView);
        ScanRule(source.RuleExplore, isReading: false, all, reading, ref needsWebView, ref readingNeedsWebView);
        ScanRule(source.RuleBookInfo, isReading: false, all, reading, ref needsWebView, ref readingNeedsWebView);
        ScanRule(source.RuleToc, isReading: true, all, reading, ref needsWebView, ref readingNeedsWebView);
        ScanRule(source.RuleContent, isReading: true, all, reading, ref needsWebView, ref readingNeedsWebView);

        var allSurface = all.ToSurface();
        var readingSurface = reading.ToSurface();

        var missingApis = JsApiCatalog.MissingNames(allSurface.JavaCalls);
        var readingMissingApis = JsApiCatalog.MissingNames(readingSurface.JavaCalls);
        var missingGlobals = UnimplementedGlobals(allSurface.Globals);
        var readingMissingGlobals = UnimplementedGlobals(readingSurface.Globals);

        // ④ 汇总标记
        if (missingApis.Count > 0 || missingGlobals.Count > 0)
        {
            flags |= BookSourceCapability.RequiresJsEngine;
        }

        if (needsWebView || ContainsBrowserApi(missingApis))
        {
            flags |= BookSourceCapability.RequiresWebView;
        }

        if (!string.IsNullOrWhiteSpace(source.LoginUrl))
        {
            flags |= BookSourceCapability.RequiresLogin;
        }

        if (hasRemoteJsLib)
        {
            flags |= BookSourceCapability.RequiresRemoteJsLib;
        }

        var tier = JsApiCatalog.RequiredTier(allSurface.JavaCalls);
        if (missingGlobals.Count > 0 && tier < JsApiTier.State)
        {
            tier = JsApiTier.State;
        }

        return new SourceCapabilityReport
        {
            Flags = flags,
            RequiredTier = tier,
            MissingApis = missingApis,
            ReadingMissingApis = readingMissingApis,
            MissingGlobals = missingGlobals,
            ReadingMissingGlobals = readingMissingGlobals,
            NeedsWebView = needsWebView,
            ReadingNeedsWebView = readingNeedsWebView,
            NeedsLogin = !string.IsNullOrWhiteSpace(source.LoginUrl),
            HasRemoteJsLib = hasRemoteJsLib,
        };
    }

    /// <summary>粗分类评估（兼容既有调用方；需要细节请用 <see cref="AssessDetailed"/>）。</summary>
    public static BookSourceCapability Assess(BookSourceModel source) => AssessDetailed(source).Flags;

    /// <summary>便捷判定：当前引擎能否不依赖阶段 4 能力跑通整源（登录/远程库算作不能）。</summary>
    public static bool IsFullySupported(BookSourceModel source)
    {
        var caps = Assess(source);
        return caps == BookSourceCapability.None || caps == BookSourceCapability.HasJsLib;
    }

    /// <summary>
    /// 便捷判定：阅读链路（目录+正文）是否需要阶段 4 的 JS/WebView 能力。
    /// <para><b>这是搜索候选过滤的依据</b>（<c>BookSourceService.GetSearchableSources</c>）：
    /// 只看阅读链路，搜索/详情链的缺口不连坐——否则一个源搜索规则用了 ajax、
    /// 目录正文却是干净选择器时，用户连"搜到并打开"的机会都没有。</para>
    /// </summary>
    public static bool ReadingNeedsStage4(BookSourceModel source) => !AssessDetailed(source).ReadingSupported;

    /// <summary>未注入的全局对象根名（按 <see cref="JsGlobalCatalog.Roots"/> 顺序，输出稳定）。</summary>
    private static List<string> UnimplementedGlobals(IReadOnlyCollection<string> used)
    {
        var list = new List<string>(2);
        if (used.Count == 0)
        {
            return list;
        }

        foreach (var root in JsGlobalCatalog.Roots)
        {
            // 已注入真实实现（4-2：source/book/cache/cookie）→ 不是缺口
            if (JsGlobalCatalog.IsImplemented(root))
            {
                continue;
            }

            // 不作为判定依据的根（html）：样本里的 html.* 全是局部变量，legado 也无此全局对象
            if (!JsGlobalCatalog.IsGated(root))
            {
                continue;
            }

            foreach (var name in used)
            {
                if (string.Equals(name, root, StringComparison.Ordinal))
                {
                    list.Add(root);
                    break;
                }
            }
        }

        return list;
    }

    /// <summary>缺口里是否含"浏览器类"原语（用于把 RequiresWebView 也标出来）。</summary>
    private static bool ContainsBrowserApi(IReadOnlyList<string> missing)
    {
        foreach (var name in missing)
        {
            if (JsApiCatalog.TierOf(name) == JsApiTier.Browser)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>顶层 URL 字段：带 JS 标记时才按脚本扫描。</summary>
    private static void ScanTopLevel(JsApiSurfaceScanner.Builder all, string? value)
    {
        if (!string.IsNullOrEmpty(value) && IsMarkedJs(value))
        {
            all.Add(value);
        }
    }

    /// <summary>
    /// 扫描一个规则对象：脚本上下文字段累计调用面，同时识别 <c>webJs</c>/<c>@webjs:</c>。
    /// <para><paramref name="isReading"/> 为真时同时累计到阅读链路那份调用面。</para>
    /// </summary>
    private static void ScanRule(
        object? rule,
        bool isReading,
        JsApiSurfaceScanner.Builder all,
        JsApiSurfaceScanner.Builder reading,
        ref bool needsWebView,
        ref bool readingNeedsWebView)
    {
        if (rule is null)
        {
            return;
        }

        foreach (var (name, value) in EnumerateStrings(rule))
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            // webJs 字段非空（或任何值里出现 @webjs: 标记）→ WebView 依赖
            if ((name == "WebJs" && !string.IsNullOrWhiteSpace(value))
                || value.Contains("@webjs:", StringComparison.Ordinal))
            {
                needsWebView = true;
                if (isReading)
                {
                    readingNeedsWebView = true;
                }
            }

            // 名以 Js 结尾的字段（PreUpdateJs/FormatJs/ImageDecodeJs…）本身就是脚本，整段扫；
            // 其余字段（Init/ChapterUrl/Content…）只在带 JS 标记时按脚本扫。
            if (name.EndsWith("Js", StringComparison.Ordinal) || IsMarkedJs(value))
            {
                all.Add(value);
                if (isReading)
                {
                    reading.Add(value);
                }
            }
        }
    }

    /// <summary>JS 标记：<c>&lt;js&gt;</c> 内联段 / <c>@js:</c> 前缀 / <c>js:</c> 前缀（legado 三种脚本形态）。</summary>
    private static bool IsMarkedJs(string value) =>
        value.Contains("<js>", StringComparison.Ordinal)
        || value.Contains("@js:", StringComparison.Ordinal)
        || value.TrimStart().StartsWith("js:", StringComparison.Ordinal);

    /// <summary>
    /// 枚举对象的字符串属性（按类型缓存 <see cref="PropertyInfo"/> 数组，避免每次评估都反射）。
    /// 只取公开实例属性；嵌套规则对象不再递归（书源规则模型只有一层）。
    /// </summary>
    private static (string Name, string? Value)[] EnumerateStrings(object rule)
    {
        var type = rule.GetType();
        var props = StringProperties.GetOrAdd(type, static t =>
        {
            var list = new List<PropertyInfo>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    list.Add(p);
                }
            }

            return list.ToArray();
        });

        var result = new (string, string?)[props.Length];
        for (var i = 0; i < props.Length; i++)
        {
            result[i] = (props[i].Name, (string?)props[i].GetValue(rule));
        }

        return result;
    }
}
