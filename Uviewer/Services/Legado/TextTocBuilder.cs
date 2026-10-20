using System.Collections.Generic;
using System.Text.RegularExpressions;
using BookSource.Core.Services;
using TitleRule = BookSource.Core.Models.TitleRule;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 纯文本 → 目录条目的<b>纯逻辑</b>部分（不依赖 WinUI，故可被集成测试直接链接）。
    ///
    /// <para><b>两级策略</b>（顺序不可颠倒）：
    /// <list type="number">
    /// <item>先试内置标记（<c>［＃大見出し］</c> / <c>#</c> / <c>##</c> / <c>###</c>）；</item>
    /// <item>内置标记一个都没命中 → 用<b>用户导入的标题/章节划分规则</b>切分。</item>
    /// </list></para>
    ///
    /// <para><b>为什么要第 2 步</b>：绝大多数中文 TXT 小说用"第一章 xxx"这类写法，
    /// 内置标记一个都匹配不上 → 目录为空 → 顶栏目录按钮点开只有"无目录"。
    /// 接入 <see cref="TxtChapterSplitter"/> 后这类文件也能正常生成目录并跳章。</para>
    ///
    /// <para><b>为什么内置标记优先</b>：它们是确定的、已被验证的约定；
    /// 用户规则是兜底，不能反过来抢在前面把标准格式解析坏。</para>
    /// </summary>
    internal static class TextTocBuilder
    {
        /// <summary>卷标题的标记（与 <c>DocumentReaderController.LegadoChapterTag</c> 同范式）。</summary>
        internal const string LegadoVolumeTocTag = "LegadoVolume";

        /// <summary>
        /// 从整本文本里切出目录条目。
        /// </summary>
        /// <param name="content">整本文本（通常是<b>已净化</b>的内容）。</param>
        /// <param name="titleRules">用户导入的标题/章节划分规则（可为空）。</param>
        /// <returns>目录条目；无标题时返回空列表（不硬造目录）。</returns>
        internal static List<TextTocEntry> Build(string content, IReadOnlyList<TitleRule>? titleRules)
        {
            var list = new List<TextTocEntry>();
            if (string.IsNullOrEmpty(content))
            {
                return list;
            }

            var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            for (var i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i].Trim();
                var level = 0;
                string text = "";

                if (line.Contains("［＃大見出し］") || line.StartsWith("# ")) { level = 1; text = line.Replace("［＃大見出し］", "").TrimStart('#', ' '); }
                else if (line.Contains("［＃中見出し］") || line.StartsWith("## ")) { level = 2; text = line.Replace("［＃中見出し］", "").TrimStart('#', ' '); }
                else if (line.Contains("［＃小見出し］") || line.StartsWith("### ")) { level = 3; text = line.Replace("［＃小見出し］", "").TrimStart('#', ' '); }

                if (level > 0)
                {
                    text = Regex.Replace(text, @"［＃[^］]+］|\[.*?\]|[#]", "").Trim();
                    list.Add(new TextTocEntry(text, i + 1, level, null));
                }
            }

            // 内置标记一个都没命中 → 交给用户导入的标题/章节划分规则切分
            if (list.Count == 0 && titleRules is { Count: > 0 })
            {
                var split = TxtChapterSplitter.Split(content, titleRules);
                foreach (var chapter in split.Chapters)
                {
                    // 卷标题进目录但不跳章（SourceLineNumber = -1，与 LegadoTocAdapter 约定一致）
                    list.Add(new TextTocEntry(
                        chapter.Title,
                        chapter.IsVolume ? -1 : chapter.LineNumber + 1,
                        chapter.Level,
                        chapter.IsVolume ? LegadoVolumeTocTag : null));
                }
            }

            return list;
        }
    }

    /// <summary>目录条目（与宿主 <c>Uviewer.Models.TocItem</c> 对应，但不依赖 WinUI 类型）。</summary>
    /// <param name="Title">标题文本。</param>
    /// <param name="SourceLineNumber">源行号（1-based）；卷标题为 -1（不可跳章）。</param>
    /// <param name="Level">层级（1=章，2=节…）。</param>
    /// <param name="Tag">附加标记（卷标题 = <see cref="TextTocBuilder.LegadoVolumeTocTag"/>）。</param>
    internal readonly record struct TextTocEntry(string Title, int SourceLineNumber, int Level, string? Tag);
}
