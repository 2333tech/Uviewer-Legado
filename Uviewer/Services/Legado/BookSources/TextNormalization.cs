namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 换行符归一（<b>全库唯一实现</b>）：CRLF / CR / NEL / LS / PS → LF。
///
/// <para><b>为什么必须集中归一</b>：书源规则（替换净化 / 标题分章 / 目录切分）大量使用
/// <c>^</c>、<c>$</c>、<c>\n</c>，而这些规则的作者（legado / reader 生态）一律假设文本是
/// <b>LF</b>。.NET 的 <c>$</c> 只在 <c>\n</c> 之前成立，对 <c>"ab\r\n"</c> 用 <c>^ab$</c>
/// <b>匹配不到</b>（<c>\r</c> 挡在结尾），于是所有"以 <c>$</c> 收尾"的分支会<b>静默失效</b>；
/// 反过来 <c>[^\n…]</c> 这类否定字符类又把 <c>\r</c> 当普通字符吃掉，替换后行尾结构被改写。
/// 同一份书源规则在 legado/reader 上正常、在本库上"少了一半效果"，根因通常就是它。</para>
///
/// <para><b>归一方向是"上游归一"</b>：读盘 / 抓取 / HTML 转文本 / 规则入口各归一一次，
/// 而不是在每个正则里写 <c>\r?</c>。理由：
/// <list type="bullet">
/// <item>用户规则是<b>外部文本</b>，我们无权要求作者写 CRLF 兼容；</item>
/// <item>归一一次（线性扫描、无正则）比让几十条规则各自兼容便宜得多；</item>
/// <item>行号（目录 / 章节缓存 / 阅读进度 / 搜索命中行）在全应用里必须同源，
/// 否则 <c>\r\n</c> 与 <c>\n</c> 混排的文件会出现"跳章差一行"。</item>
/// </list></para>
///
/// <para><b>性能</b>：走 <see cref="string.ReplaceLineEndings(string)"/>——BCL 内部先扫描
/// 是否含有需要替换的字符，无 <c>\r</c> 时<b>返回同一实例</b>（零分配），因此可以对
/// "已经归一过"的文本反复调用（幂等 + 免费）。</para>
/// </summary>
public static class TextNormalization
{
    /// <summary>归一后的换行符（本库全链路唯一换行符）。</summary>
    public const char Lf = '\n';

    /// <summary>纯 LF 换行串（写文件 / 拼行时用它，<b>不要</b>用 <see cref="Environment.NewLine"/>）。</summary>
    public const string LfString = "\n";

    /// <summary>
    /// 把任意换行风格（CRLF / CR / LF / NEL / LS / PS）统一成 LF。
    /// 已归一（不含 <c>\r</c>）时返回<b>原引用</b>，可安全地在热路径反复调用。
    /// </summary>
    /// <param name="text">待归一的文本；<c>null</c> 返回空串（调用方无需判空）。</param>
    public static string ToLf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // ReplaceLineEndings 内部即"无匹配则返回原实例"，这里不加额外扫描，
        // 保证 228KB 正文的重复调用≈零成本。
        return text.ReplaceLineEndings(LfString);
    }

    /// <summary>文本里是否还残留回车（<c>\r</c>）——仅用于断言 / 诊断，不做替换。</summary>
    public static bool HasCarriageReturn(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains('\r');
}
