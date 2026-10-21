using System.Net;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.BookSources;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源正文 HTML → 纯文本转换（阶段 5 集成的纯逻辑部分，无任何 UI/引擎依赖）。
    ///
    /// <para><b>转换步骤</b>：① 统一换行符；② 去 script/style/注释（否则脚本源码会混进正文）；
    /// ③ 折叠「标签之间只含换行的空白」（HTML 源码为了可读性会在块级标签之间换行，
    /// 那不是内容——不折叠会让同一份标记因源码排版不同而产出不同结果：
    /// <c>&lt;/p&gt;\r\n&lt;p&gt;</c> 与 <c>&lt;/p&gt;&lt;p&gt;</c> 必须得到相同的段落分隔）；
    /// ④ 块级结束标签与 <c>&lt;br&gt;</c>（含带属性/空格的变体）换行（保留段落结构——
    /// 这是纯文本模式可读性的关键，也是与既有 <c>AozoraParserService.ParseHtml</c> 的主要差别：
    /// 那里把标签一律替成 \n，内联标签（<c>&lt;span&gt;</c>/<c>&lt;b&gt;</c>）也会撑出空行；
    /// 注意块级<b>开始</b>标签不换行：<c>&lt;p&gt;甲&lt;/p&gt;&lt;p&gt;乙&lt;/p&gt;</c> 靠结束标签
    /// 已能正确分段，开始标签也换行会每段间多出一个空行）；
    /// ⑤ 剥其余标签；⑥ HTML 实体解码（解码后再次归一换行——<c>&amp;#13;</c> 会解出 \r）；
    /// ⑦ 裁行尾空白；⑧ 压缩 2 行以上空行为 1 个空行（阅读器按行排版，过多空行浪费屏幕）。</para>
    ///
    /// <para><b>性能</b>：静态正则一律<b>解释执行</b>（不加 <see cref="RegexOptions.Compiled"/>）——
    /// 项目红线：净化/行内规则实测编译态慢 5.8×（Reflection.Emit 开销摊不回匹配收益），
    /// 本类是每章全文的热路径，同样适用。单遍线性扫描；刻意不做 DOM 解析
    /// （书源正文常有残缺标签，DOM 解析反而更贵更脆）。
    /// 调用方负责把它放在后台线程（见 <c>BookSourceReadingSession</c>）。</para>
    /// </summary>
    public static class LegadoHtmlText
    {
        /// <summary>标签之间"只含换行/空白"的排版空白（不含单个空格——行内空格有语义）。</summary>
        private static readonly Regex RxTagGapNewline = new(
            @">\s*\n\s*<");

        private static readonly Regex RxScript = new(
            @"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase);

        private static readonly Regex RxStyle = new(
            @"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase);

        private static readonly Regex RxComment = new(
            @"<!--[\s\S]*?-->");

        // 块级结束标签（容忍 </p > 的空格变体）+ <br> 全形态（<br> / <br/> / <br /> / <br class="x">）。
        // 旧模式写死 </p>|<br\s*/?>：带属性的 <br> 与带空格的 </p > 匹配不上，
        // 落入 RxAnyTag 被静默删除，段落分隔凭空丢失。
        private static readonly Regex RxBreakTags = new(
            @"</(?:p|div|br|li|tr|h[1-6]|section|article|blockquote)\s*>|<br\b[^>]*>",
            RegexOptions.IgnoreCase);

        private static readonly Regex RxAnyTag = new(
            @"<[^>]+>");

        private static readonly Regex RxBlankRuns = new(
            @"\n{3,}");

        /// <summary>把章节正文 HTML 转成阅读器可直接排版的纯文本。空白输入返回空串。</summary>
        public static string ToPlainText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            // ① 先统一换行符（后面所有正则都假定只有 \n）
            // 与全应用共用同一实现（Uviewer.Services.Legado.BookSources.TextNormalization），
            // 避免"这里认 CRLF、那里不认"的分歧。
            var s = TextNormalization.ToLf(html);

            // ② 去 script/style
            s = RxScript.Replace(s, string.Empty);
            s = RxStyle.Replace(s, string.Empty);

            // ②b 去注释：RxAnyTag 的 <[^>]+> 遇注释里的 > 会提前收尾，
            // 残留 "开始 -->" 之类的注释碎片在正文里
            s = RxComment.Replace(s, string.Empty);

            // ③ 折叠标签间的排版换行空白：</p>\n<p> → </p><p>（单个空格保留，行内语义不受影响）
            s = RxTagGapNewline.Replace(s, "><");

            // ④ 块级标签换行 + ⑤ 剥标签
            s = RxBreakTags.Replace(s, "\n");
            s = RxAnyTag.Replace(s, string.Empty);

            // ⑥ 实体解码；解码后再次归一换行（&#13; 等会解出 \r，后续环节假定 LF-only）
            s = TextNormalization.ToLf(WebUtility.HtmlDecode(s));

            // ⑦ 每行只裁行尾空白（保留行首缩进：小说正文的段首缩进是有意义的）
            var lines = s.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd();
            }

            // ⑧ 压缩 3 行以上空行为 1 个空行
            s = string.Join("\n", lines);
            s = RxBlankRuns.Replace(s, "\n\n");

            return s.Trim('\n');
        }
    }
}