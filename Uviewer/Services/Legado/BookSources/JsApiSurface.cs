using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 一段脚本（或整条解析链的所有脚本片段）对 <c>java.*</c> 原语与全局对象的<b>静态调用面</b>。
/// <para>用途：能力评估（"这个源缺哪些原语"）与诊断（"这段脚本本来会用到什么"）。
/// 运行时不依赖它——<see cref="JsBridge"/> 用 Proxy 兜底捕获真实调用，静态面只做预估。</para>
/// </summary>
public sealed class JsApiSurface
{
    internal static readonly JsApiSurface Empty = new(Array.Empty<string>(), Array.Empty<string>());

    internal JsApiSurface(IReadOnlyCollection<string> javaCalls, IReadOnlyCollection<string> globals)
    {
        JavaCalls = javaCalls;
        Globals = globals;
    }

    /// <summary>脚本里出现过的 <c>java.xxx</c> 名字（去重，不含 java 前缀）。</summary>
    public IReadOnlyCollection<string> JavaCalls { get; }

    /// <summary>脚本里确定用到的全局对象根名（source/book/cache/cookie/html，去重）。</summary>
    public IReadOnlyCollection<string> Globals { get; }

    /// <summary>没有任何调用面（纯选择器/正则文本）。</summary>
    public bool IsEmpty => JavaCalls.Count == 0 && Globals.Count == 0;
}

/// <summary>
/// 调用面扫描器：把脚本文本里"看起来是 java.X / source.Y"的用法抽出来。
/// <para><b>误报控制</b>：</para>
/// <list type="bullet">
/// <item>只用"顶层成员名"，不做语法分析（书源脚本碎片化严重，做完整解析不划算）；</item>
/// <item>全局对象要求成员名命中 <see cref="JsGlobalCatalog"/> 的白名单，
///   否则 `book` 这类常见局部变量名会把整源误判成"需要阶段 4"；</item>
/// <item>调用方只对**脚本上下文**的值调用本扫描（带 <c>&lt;js&gt;</c>/<c>@js:</c>/<c>js:</c> 标记，
///   或字段名以 <c>Js</c> 结尾），URL 模板里的 "java." 字样不会进来。</item>
/// </list>
/// <para><b>缓存</b>：<see cref="ScanCached"/> 以脚本文本为键（存的是模型里已有的字符串引用，
/// 不额外复制文本），上限 <see cref="CacheCapacity"/> 条，防止反复导入不同书源把内存顶上去。</para>
/// </summary>
public static class JsApiSurfaceScanner
{
    /// <summary>缓存条数上限（书源脚本数量级为千，2048 足够覆盖常用源）。</summary>
    private const int CacheCapacity = 2048;

    // 一次扫描同时识别 java 与全局根：group1 = 根名，group2 = 顶层成员名。
    // 前置 (?<![\w$.]) 排除 a.book / $.book / 变量名后缀；\s*\.\s* 兼容 "java . ajax" 写法（真实书源有）。
    private static readonly Regex MemberPattern = new(
        @"(?<![\w$.])(java|source|book|cache|cookie|html)\s*\.\s*([A-Za-z_$][\w$]*)",
        RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, JsApiSurface> Cache = new(StringComparer.Ordinal);

    /// <summary>扫描单段文本（无缓存）。</summary>
    public static JsApiSurface Scan(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0)
        {
            return JsApiSurface.Empty;
        }

        var builder = new Builder();
        builder.Add(text);
        return builder.ToSurface();
    }

    /// <summary>扫描单段文本（带缓存，供同一脚本被反复评估的场景）。</summary>
    public static JsApiSurface ScanCached(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0)
        {
            return JsApiSurface.Empty;
        }

        if (Cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var surface = Scan(text);
        if (Cache.Count < CacheCapacity)
        {
            Cache[text] = surface;
        }

        return surface;
    }

    /// <summary>把多段脚本合并成一份调用面（书源：搜索链/阅读链各一份）。</summary>
    public static JsApiSurface ScanMany(IEnumerable<string?> texts)
    {
        var builder = new Builder();
        foreach (var text in texts)
        {
            builder.Add(text);
        }

        return builder.ToSurface();
    }

    /// <summary>清空缓存（测试/导入大量书源后用）。</summary>
    internal static void ClearCache() => Cache.Clear();

    /// <summary>可变累加器：避免"每段文本都分配一次 HashSet + 数组"。</summary>
    internal sealed class Builder
    {
        private readonly HashSet<string> _java = new(StringComparer.Ordinal);
        private readonly HashSet<string> _globals = new(StringComparer.Ordinal);

        public void Add(string? text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0)
            {
                return;
            }

            foreach (Match match in MemberPattern.Matches(StripComments(text)))
            {
                var root = match.Groups[1].Value;
                var member = match.Groups[2].Value;
                if (root == "java")
                {
                    _java.Add(member);
                }
                else if (JsGlobalCatalog.HasMember(root, member))
                {
                    _globals.Add(root);
                }
            }
        }

        /// <summary>
        /// 去掉 JS 注释（<c>//</c> 行注释、<c>/* */</c> 块注释），保留字符串字面量原样。
        /// <para><b>为什么必须做</b>：真实书源里有大量"用法说明"注释，例如
        /// <c>// 可用 java.startBrowser() 不可用 java.webview()</c>——注释里的原语名会把
        /// 一个本来完全可跑的书源误判成"需要阶段 4"（实测 21 个假名字里有一半来自注释）。</para>
        /// <para>实现是最小词法扫描（4 种引号 + 转义），不做完整解析：只要不误伤 <c>https://</c>
        /// 这类字符串内容即可。</para>
        /// </summary>
        internal static string StripComments(string text)
        {
            if (text.IndexOf("//", StringComparison.Ordinal) < 0
                && text.IndexOf("/*", StringComparison.Ordinal) < 0)
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[++i]);
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '\'' or '"' or '`')
                {
                    quote = c;
                    sb.Append(c);
                    continue;
                }

                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        while (i < text.Length && text[i] != '\n')
                        {
                            i++;
                        }

                        sb.Append('\n');
                        continue;
                    }

                    if (text[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                        {
                            i++;
                        }

                        i++;
                        sb.Append(' ');
                        continue;
                    }
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        public JsApiSurface ToSurface()
        {
            if (_java.Count == 0 && _globals.Count == 0)
            {
                return JsApiSurface.Empty;
            }

            var java = new string[_java.Count];
            _java.CopyTo(java);
            var globals = new string[_globals.Count];
            _globals.CopyTo(globals);
            Array.Sort(java, StringComparer.Ordinal);
            Array.Sort(globals, StringComparer.Ordinal);
            return new JsApiSurface(java, globals);
        }
    }
}
