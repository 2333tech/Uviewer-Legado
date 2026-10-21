using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// jsLib 编译（对标 reader <c>parser/js.rs::compile_js_lib</c>）。
/// <para>两种形态：① 裸脚本（最常见，直接作为库代码）；② JSON 对象
/// <c>{"name": "脚本或URL"}</c>——legado 允许把多个库（含远程 URL）打包在一起。</para>
/// <para>语义锚点（与 reader 逐条一致）：URL 条目（<c>http://</c> / <c>https://</c>）需要联网下载，
/// 离线环境跳过该条目但**不影响其余条目与规则执行**（reader 下载失败同样只记日志后继续）；
/// 非 URL 条目按内联脚本处理；JSON 解析失败时整体退化为裸脚本。</para>
/// </summary>
internal static class JsLibCompiler
{
    /// <summary>多条目拼接分隔符（与 reader 一致：换行 + 空语句，防止末行无分号粘连）。</summary>
    internal const string EntrySeparator = "\n;\n";

    /// <summary>
    /// 把书源的 <c>jsLib</c> 原始文本编译为可执行的库代码；无有效内容时返回空串。
    /// <para>纯字符串处理，无 IO：URL 条目不在此处下载（阶段 4 的联网能力），
    /// 由 <see cref="BookSourceCapabilities.Assess"/> 标记 <see cref="BookSourceCapability.RequiresRemoteJsLib"/>。</para>
    /// </summary>
    public static string Compile(string? jsLib)
    {
        if (string.IsNullOrWhiteSpace(jsLib))
        {
            return string.Empty;
        }

        var trimmed = jsLib.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            // ① 裸脚本形态；但整个 jsLib 就是一个 URL 时（legado 允许 jsLib 直接填库地址），
            //    本地无代码可拼——远程下载由 JsBridge.ResolveJsLib 负责，这里返回空串，
            //    避免把 URL 文本当脚本执行（语法错误）。
            return IsRemoteUrl(trimmed) ? string.Empty : trimmed;
        }

        // ② JSON 对象形态：逐条目取字符串值
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(trimmed);
        }
        catch
        {
            // 形似 JSON 但非法（如以 { 开头的裸脚本）：整体按裸脚本处理（reader 同）
            return trimmed;
        }

        if (root is not JsonObject obj)
        {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed.Length);
        foreach (var entry in obj)
        {
            if (entry.Value is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var item = text.Trim();
            if (IsRemoteUrl(item))
            {
                // 远程库：离线跳过（reader：下载失败仅记日志，其余条目照常执行）
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(EntrySeparator);
            }

            sb.Append(item);
        }

        return sb.ToString();
    }

    /// <summary>是否为远程 URL 条目（reader：<c>resolve_js_lib_entry</c> 只认 http/https 前缀）。</summary>
    private static bool IsRemoteUrl(string item) =>
        item.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || item.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// jsLib 是否含远程 URL 条目（能力评估用：联网下载由运行时完成，这里只做静态标记）。
    /// 两种形态都算：① 整个 jsLib 就是一个 URL；② JSON 对象的值为 URL。
    /// </summary>
    public static bool HasRemoteEntry(string? jsLib)
    {
        if (string.IsNullOrWhiteSpace(jsLib))
        {
            return false;
        }

        var trimmed = jsLib.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return IsRemoteUrl(trimmed);
        }

        try
        {
            if (JsonNode.Parse(trimmed) is not JsonObject obj)
            {
                return false;
            }

            foreach (var entry in obj)
            {
                if (entry.Value is JsonValue value
                    && value.TryGetValue(out string? text)
                    && !string.IsNullOrWhiteSpace(text)
                    && IsRemoteUrl(text.Trim()))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 非法 JSON 按裸脚本处理，无远程条目
        }

        return false;
    }

    /// <summary>
    /// 提取 jsLib 里的全部远程 URL（保持声明顺序、去重）。
    /// <para>与 <see cref="Compile"/> 的口径互补：<see cref="Compile"/> 产出「本地代码部分」，
    /// 本方法产出「待下载的远程部分」，由 <c>JsBridge.ResolveJsLib</c> 下载后按序拼回。
    /// 裸 URL 形态与 JSON 映射形态（<c>{"键": "https://…/x.js"}</c>）都支持。</para>
    /// </summary>
    public static List<string> ExtractRemoteUrls(string? jsLib)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(jsLib))
        {
            return urls;
        }

        var trimmed = jsLib.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            // 整个 jsLib 就是一个库地址
            if (IsRemoteUrl(trimmed))
            {
                urls.Add(trimmed);
            }

            return urls;
        }

        try
        {
            if (JsonNode.Parse(trimmed) is not JsonObject obj)
            {
                return urls;
            }

            foreach (var entry in obj)
            {
                if (entry.Value is JsonValue value
                    && value.TryGetValue(out string? text)
                    && !string.IsNullOrWhiteSpace(text)
                    && IsRemoteUrl(text.Trim()))
                {
                    var url = text.Trim();
                    if (!urls.Contains(url))
                    {
                        urls.Add(url);
                    }
                }
            }
        }
        catch
        {
            // 非法 JSON 按裸脚本处理，无远程条目
        }

        return urls;
    }
}
