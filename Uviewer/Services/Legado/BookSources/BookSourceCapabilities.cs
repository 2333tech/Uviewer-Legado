using System;
using System.Collections.Concurrent;
using System.Reflection;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源能力标记（阶段 5 集成用：让阅读器在打开书源前就能提示"该书源需要阶段 4 能力"，
/// 而不是拉到一个空目录/空正文才静默失败）。
/// <para>标注依据来自 Sample 408 源静态分析（见 <c>.workbuddy/memory/2026-10-20.md</c>）：
/// 约 31% 的样例书源阅读链路依赖 <c>java.*</c> 或 WebView，2 个源用 <c>webJs</c>，
/// 74 个源带 <c>loginUrl</c>。</para>
/// </summary>
[Flags]
public enum BookSourceCapability
{
    /// <summary>无特殊依赖：纯选择器/正则/纯 JS，当前引擎完整支持。</summary>
    None = 0,

    /// <summary>依赖 <c>java.*</c> 原语（ajax/md5Encode/getElements…），需阶段 4 补齐。</summary>
    RequiresJsEngine = 1,

    /// <summary>依赖 WebView 后处理（<c>webJs</c> 字段或 <c>@webjs:</c> 标记），需阶段 4 WebView2。</summary>
    RequiresWebView = 2,

    /// <summary>带 <c>loginUrl</c>：完整登录流需阶段 4；已注入 Cookie 的会话可先用。</summary>
    RequiresLogin = 4,

    /// <summary><c>jsLib</c> 含远程 URL 条目：需阶段 4 联网下载后才能生效。</summary>
    RequiresRemoteJsLib = 8,

    /// <summary>带 <c>jsLib</c>（内联库已由当前引擎注入，仅作信息标记）。</summary>
    HasJsLib = 16,
}

/// <summary>
/// 书源能力静态评估（纯函数，无 IO；对标 reader 侧"源能力面板"的判定维度）。
/// <para>扫描范围（与可行性分析一致）：<c>searchUrl</c> / <c>exploreUrl</c> / <c>loginUrl</c> /
/// <c>loginCheckJs</c> / <c>jsLib</c> / <c>ruleSearch</c> / <c>ruleBookInfo</c> /
/// <c>ruleToc</c> / <c>ruleContent</c> / <c>ruleExplore</c>。
/// 不含 <c>header</c>（请求头 JS 属阶段 1 取数层，已有插值/选项处理）。</para>
/// <para>判定规则：① <c>java.*</c> 只算在**脚本上下文**里——带 JS 标记
/// （<c>&lt;js&gt;</c> / <c>@js:</c> / <c>js:</c> 前缀）的规则字段、或本身就是脚本的字段
/// （名以 <c>Js</c> 结尾、<c>jsLib</c>、<c>loginCheckJs</c>）——避免 URL 路径里的
/// "java." 字样误报；② <c>webJs</c> 字段非空或任何值含 <c>@webjs:</c> → WebView 依赖。</para>
/// </summary>
public static class BookSourceCapabilities
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StringProperties = new();

    /// <summary>评估单个书源的能力标记。</summary>
    public static BookSourceCapability Assess(BookSourceModel source)
    {
        if (source is null)
        {
            return BookSourceCapability.None;
        }

        var caps = BookSourceCapability.None;

        // ① 顶层 URL 字段：仅当带 JS 标记时按脚本扫描（URL 模板里的 {{}} 插值不算脚本）
        ScanMarkedRuleValue(ref caps, source.SearchUrl);
        ScanMarkedRuleValue(ref caps, source.ExploreUrl);
        ScanMarkedRuleValue(ref caps, source.LoginUrl);

        // ② 本身就是脚本的顶层字段：整段扫描（无标记也扫）
        if (ContainsJavaCall(source.LoginCheckJs))
        {
            caps |= BookSourceCapability.RequiresJsEngine;
        }

        if (!string.IsNullOrWhiteSpace(source.JsLib))
        {
            caps |= BookSourceCapability.HasJsLib;
            if (ContainsJavaCall(source.JsLib))
            {
                caps |= BookSourceCapability.RequiresJsEngine;
            }

            if (JsLibCompiler.HasRemoteEntry(source.JsLib))
            {
                caps |= BookSourceCapability.RequiresRemoteJsLib;
            }
        }

        if (!string.IsNullOrWhiteSpace(source.LoginUrl))
        {
            caps |= BookSourceCapability.RequiresLogin;
        }

        // ③ 规则对象：反射遍历全部字符串属性（SearchRule/BookInfoRule/TocRule/ContentRule/ExploreRule）
        foreach (var rule in new object?[]
                 {
                     source.RuleSearch, source.RuleBookInfo, source.RuleToc,
                     source.RuleContent, source.RuleExplore
                 })
        {
            if (rule is null)
            {
                continue;
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
                    caps |= BookSourceCapability.RequiresWebView;
                }

                // 名以 Js 结尾的字段（PreUpdateJs/FormatJs/ImageDecodeJs…）本身就是脚本，整段扫；
                // 其余字段（Init/ChapterUrl/Content…）只在带 JS 标记时按脚本扫。
                if (name.EndsWith("Js", StringComparison.Ordinal) || IsMarkedJs(value))
                {
                    if (ContainsJavaCall(value))
                    {
                        caps |= BookSourceCapability.RequiresJsEngine;
                    }
                }
            }
        }

        return caps;
    }

    /// <summary>便捷判定：当前引擎（阶段 0~3）能否不依赖阶段 4 能力跑通该书源。</summary>
    public static bool IsFullySupported(BookSourceModel source)
    {
        var caps = Assess(source);
        return caps == BookSourceCapability.None || caps == BookSourceCapability.HasJsLib;
    }

    /// <summary>便捷判定：阅读链路（目录+正文）是否需要阶段 4 的 JS/WebView 能力。</summary>
    public static bool ReadingNeedsStage4(BookSourceModel source)
    {
        var caps = Assess(source);
        return (caps & (BookSourceCapability.RequiresJsEngine | BookSourceCapability.RequiresWebView)) != 0;
    }

    /// <summary>带 JS 标记的规则值按脚本扫描（无标记的规则值只是选择器/URL 模板，跳过）。</summary>
    private static void ScanMarkedRuleValue(ref BookSourceCapability caps, string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsMarkedJs(value))
        {
            return;
        }

        if (ContainsJavaCall(value))
        {
            caps |= BookSourceCapability.RequiresJsEngine;
        }
    }

    /// <summary>JS 标记：<c>&lt;js&gt;</c> 内联段 / <c>@js:</c> 前缀 / <c>js:</c> 前缀（legado 三种脚本形态）。</summary>
    private static bool IsMarkedJs(string value) =>
        value.Contains("<js>", StringComparison.Ordinal)
        || value.Contains("@js:", StringComparison.Ordinal)
        || value.TrimStart().StartsWith("js:", StringComparison.Ordinal);

    /// <summary>是否出现 <c>java.*</c> 调用（<c>\bjava\s*\.</c>：兼容 <c>java .ajax</c> 写法）。</summary>
    private static bool ContainsJavaCall(string? value) =>
        !string.IsNullOrEmpty(value) && JavaCallPattern.IsMatch(value);

    private static readonly System.Text.RegularExpressions.Regex JavaCallPattern =
        new(@"\bjava\s*\.", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 枚举对象的字符串属性（按类型缓存 <see cref="PropertyInfo"/> 数组，避免每次评估都反射）。
    /// 只取公开实例属性；嵌套规则对象不再递归（书源规则模型只有一层）。
    /// </summary>
    private static (string Name, string? Value)[] EnumerateStrings(object rule)
    {
        var type = rule.GetType();
        var props = StringProperties.GetOrAdd(type, static t =>
        {
            var list = new System.Collections.Generic.List<PropertyInfo>();
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
