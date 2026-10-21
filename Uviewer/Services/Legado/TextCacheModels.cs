using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using TitleRule = Uviewer.Services.Legado.Models.TitleRule;
using ReplaceRule = Uviewer.Services.Legado.Models.ReplaceRule;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 本地文本的<b>章节缓存</b>（目录 + 章节分化 + 阅读位置）纯逻辑部分
    /// （不依赖 WinUI，故可被集成测试直接链接，与 <see cref="TextTocBuilder"/> 同拆法）。
    ///
    /// <para><b>解决什么问题</b>：重净化规则集（PCRE 风格正则 + <c>@js:</c> 求值）下，
    /// 整本净化一本 1.4MB 的 TXT 要 10s 量级，每次打开都重复付这笔钱。
    /// 缓存把「完整目录 + 章节分化」落盘，打开时只净化阅读位置前后的一个窗口
    ///（前 2 + 后 5，与书源阅读的预渲染窗口同规格），切换章节时窗口跟着滑动。</para>
    ///
    /// <para><b>与「净化在前、分章在后」铁律的关系</b>：整本（无缓存）路径严格保持
    /// 先净化后分章；缓存路径的章节范围取自<b>原文</b>行坐标（窗口净化必须按原文切片），
    /// 目录<b>名称</b>则在保存缓存时用净化后的全文校验（数量一致即采用净化后名）。
    /// 这是一处有意记录的偏差：代价是极端情况下（净化删掉标题行）目录名可能与正文显示差一行，
    /// 收益是打开从 10s 降到百毫秒级。保存时的计数校验会把这种情况通知给用户。</para>
    /// </summary>
    internal static class TextCacheFormat
    {
        /// <summary>缓存格式版本（结构不兼容变更时 +1，旧缓存自动失效）。</summary>
        public const int Version = 1;

        /// <summary>窗口：当前章之前重算的章数（与书源预取窗口一致）。</summary>
        public const int WindowBefore = 2;

        /// <summary>窗口：当前章之后重算的章数（前2+后5+当前 = 8 章）。</summary>
        public const int WindowAfter = 5;

        /// <summary>目录条目 Tag：承载章下标（boxed int），点击时按下标重开窗口或直接跳行。</summary>
        public const string ChapterTag = "TextCacheChapter";
    }

    /// <summary>缓存里的一个章节（坐标均为<b>原文</b> 0-based 行号；卷标题坐标为 -1）。</summary>
    /// <param name="Name">章名（保存时经净化后全文校验）。</param>
    /// <param name="Level">目录层级。</param>
    /// <param name="IsVolume">卷标题（不进窗口、不跳章）。</param>
    /// <param name="HeadingLine">标题所在行（0-based）。</param>
    /// <param name="StartLine">切片起始行（含；第一章为 0，把卷首内容并进来）。</param>
    /// <param name="EndLine">切片结束行（不含）。</param>
    internal sealed record TextCacheChapter(
        string Name, int Level, bool IsVolume, int HeadingLine, int StartLine, int EndLine);

    /// <summary>
    /// 单个源的缓存内容。JSON 落盘于 <c>%LocalAppData%/Uviewer/textCache/&lt;key-sha1&gt;.json</c>。
    /// </summary>
    internal sealed class TextCacheEntry
    {
        public int Version { get; set; } = TextCacheFormat.Version;

        /// <summary>源键（本地文件 = 规范化全路径；压缩包条目 = 包路径\u0001条目键）。</summary>
        public string SourceKey { get; set; } = string.Empty;

        /// <summary>保存时的文件字节数（变化即失效）。</summary>
        public long FileSize { get; set; }

        /// <summary>保存时的最后写入时间（UTC ticks；变化即失效）。</summary>
        public long FileLastWriteUtcTicks { get; set; }

        /// <summary>保存时生效的替换规则指纹（规则改动即失效）。</summary>
        public string ReplaceRulesFingerprint { get; set; } = string.Empty;

        /// <summary>保存时生效的标题规则指纹（标题规则改动即失效）。</summary>
        public string TitleRulesFingerprint { get; set; } = string.Empty;

        public long SavedAtUtcTicks { get; set; }

        /// <summary>完整目录 + 章节分化（原文坐标）。</summary>
        public List<TextCacheChapter> Chapters { get; set; } = new();

        /// <summary>上次阅读到的章名（空 = 从第一章开始）。</summary>
        public string LastChapterName { get; set; } = string.Empty;

        /// <summary>上次阅读位置在章内的行偏移（0-based，相对该章标题行）。</summary>
        public int LastLineInChapter { get; set; }
    }

    /// <summary>窗口装配结果。</summary>
    /// <param name="Text">装配后的显示文本（各章已净化）。</param>
    /// <param name="HeadingLines">每章标题在 <paramref name="Text"/> 里的 1-based 行号（卷 = -1）。</param>
    /// <param name="SliceLineCounts">每章切片贡献的行数（用于计算章内偏移）。</param>
    internal sealed record WindowAssembly(string Text, int[] HeadingLines, int[] SliceLineCounts);

    /// <summary>
    /// 缓存的纯逻辑：键 / 指纹 / 有效期 / 窗口 / 分章 / 切片 / 装配。
    /// 全部为静态函数，无 IO、无 WinUI，便于测试锁定语义。
    /// </summary>
    internal static class TextCacheLogic
    {
        /// <summary>规范化换行（CRLF/CR → LF），与 <see cref="TextTocBuilder"/> 的行号语义对齐。
        /// <para>实现转调 <see cref="TextNormalization.ToLf"/>（全应用唯一实现）；
        /// 读盘入口已归一，这里是幂等的第二道保险（缓存里也可能存着旧的 CRLF 原文）。</para></summary>
        public static string NormalizeNewlines(string text) => TextNormalization.ToLf(text);

        /// <summary>源键：本地文件走全路径；压缩包条目走「包路径\u0001条目键」。</summary>
        public static string ComputeSourceKey(string? filePath, string? archivePath, string? entryKey)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                return NormalizePath(filePath);
            }

            if (!string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(entryKey))
            {
                return NormalizePath(archivePath) + "\u0001" + entryKey;
            }

            return string.Empty;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>替换规则指纹：启用 + 非空 pattern 的规则，按（Order，存储序）稳定排序后哈希。</summary>
        public static string ComputeReplaceRulesFingerprint(IReadOnlyList<ReplaceRule> rules)
        {
            var sb = new StringBuilder();
            AppendOrdered(sb, rules
                .Where(r => r.IsEnabled && !string.IsNullOrEmpty(r.Pattern))
                .Select((r, i) => (Rule: r, Index: i))
                .OrderBy(x => x.Rule.Order)
                .ThenBy(x => x.Index)
                .Select(x => x.Rule)
                .Select(r => new[]
                {
                    r.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Pattern,
                    r.Replacement,
                    r.IsRegex ? "1" : "0",
                    r.Scope ?? string.Empty,
                }));

            return Hash(sb.ToString());
        }

        /// <summary>标题规则指纹：启用的规则，按（Order，存储序）稳定排序后哈希。</summary>
        public static string ComputeTitleRulesFingerprint(IReadOnlyList<TitleRule> rules)
        {
            var sb = new StringBuilder();
            AppendOrdered(sb, rules
                .Where(r => r.IsEnabled)
                .Select((r, i) => (Rule: r, Index: i))
                .OrderBy(x => x.Rule.Order)
                .ThenBy(x => x.Index)
                .Select(x => x.Rule)
                .Select(r => new[]
                {
                    r.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Pattern,
                    r.TitleGroup.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.IsVolume ? "1" : "0",
                    r.IsBookTitle ? "1" : "0",
                }));

            return Hash(sb.ToString());
        }

        private static void AppendOrdered(StringBuilder sb, IEnumerable<IEnumerable<string>> rows)
        {
            foreach (var row in rows)
            {
                foreach (var field in row)
                {
                    sb.Append(field).Append('\u0001');
                }

                sb.Append('\u0002');
            }
        }

        private static string Hash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(32);
            foreach (var b in bytes.AsSpan(0, 16))
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 缓存是否可用：版本 / 源键 / 文件大小 / 最后写入时间 / 两条规则指纹全部一致。
        /// </summary>
        public static bool IsValid(
            TextCacheEntry? entry,
            string sourceKey,
            long fileSize,
            long fileLastWriteUtcTicks,
            string replaceRulesFingerprint,
            string titleRulesFingerprint)
        {
            if (entry is null || entry.Version != TextCacheFormat.Version)
            {
                return false;
            }

            if (!string.Equals(entry.SourceKey, sourceKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (entry.FileSize != fileSize || entry.FileLastWriteUtcTicks != fileLastWriteUtcTicks)
            {
                return false;
            }

            return string.Equals(entry.ReplaceRulesFingerprint, replaceRulesFingerprint, StringComparison.Ordinal)
                && string.Equals(entry.TitleRulesFingerprint, titleRulesFingerprint, StringComparison.Ordinal);
        }

        /// <summary>窗口区间：[center-before, center+after]（闭区间，转开区间返回），两端收敛。</summary>
        public static (int From, int ToExclusive) ComputeWindow(int chapterCount, int center, int before, int after)
        {
            if (chapterCount <= 0)
            {
                return (0, 0);
            }

            center = Math.Clamp(center, 0, chapterCount - 1);
            var from = Math.Max(0, center - before);
            var to = Math.Min(chapterCount, center + after + 1);
            return (from, to);
        }

        /// <summary>按章名找章（重名取第一个；找不到返回 -1 → 调用方回退到第一章）。</summary>
        public static int FindChapterIndex(IReadOnlyList<TextCacheChapter> chapters, string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            for (var i = 0; i < chapters.Count; i++)
            {
                if (!chapters[i].IsVolume && string.Equals(chapters[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>点击目标解析：卷标题 → 其后第一个非卷章（卷不进窗口、不跳章）。</summary>
        public static int ResolveClickTarget(IReadOnlyList<TextCacheChapter> chapters, int index)
        {
            if (index < 0 || index >= chapters.Count)
            {
                return -1;
            }

            if (!chapters[index].IsVolume)
            {
                return index;
            }

            for (var i = index + 1; i < chapters.Count; i++)
            {
                if (!chapters[i].IsVolume)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 从整篇原文构建章节图（标题规则直接作用于<b>原文</b>——窗口净化需要原文切片）。
        /// 与整本路径的目录<b>同源</b>：走 <see cref="TextTocBuilder.Build"/> 的两级策略
        ///（内置标记 → 用户标题规则），保证开不开缓存看到的是同一套章。
        /// 无章节时返回空列表（调用方应放弃缓存路径）。
        /// </summary>
        /// <param name="purifyTitleLine">
        /// 标题行预净化（作用于标题的替换规则，legado <c>scopeTitle</c>）。与渲染路径同一委托，
        /// 保证"缓存切出的章"与"目录里看到的章"一致。null = 不预净化。
        /// </param>
        public static List<TextCacheChapter> BuildChapterMap(
            string rawText,
            IReadOnlyList<TitleRule> titleRules,
            Func<string, string>? purifyTitleLine = null)
        {
            var normalized = NormalizeNewlines(rawText ?? string.Empty);
            var totalLines = normalized.Split('\n').Length;
            var entries = TextTocBuilder.Build(normalized, titleRules, purifyTitleLine);
            if (entries.Count == 0)
            {
                return new List<TextCacheChapter>();
            }

            // 非卷章的行号表：第 k 章的结束行 = 第 k+1 章的标题行（卷标题不计入章序）
            var chapterLines = entries
                .Where(e => e.SourceLineNumber > 0)
                .Select(e => e.SourceLineNumber - 1)
                .ToList();

            var result = new List<TextCacheChapter>(entries.Count);
            var order = 0;
            foreach (var e in entries)
            {
                if (e.SourceLineNumber <= 0 || e.Tag == TextTocBuilder.LegadoVolumeTocTag)
                {
                    result.Add(new TextCacheChapter(e.Title, Math.Max(1, e.Level), true, -1, -1, -1));
                    continue;
                }

                var heading = e.SourceLineNumber - 1;
                var end = order + 1 < chapterLines.Count ? chapterLines[order + 1] : totalLines;
                var start = order == 0 ? 0 : heading; // 第一章把卷首内容（简介等）并进切片
                result.Add(new TextCacheChapter(e.Title, Math.Max(1, e.Level), false, heading, start, end));
                order++;
            }

            return result;
        }

        /// <summary>切出一章的原文切片（卷标题返回空串）。</summary>
        public static string SliceChapter(string normalizedText, TextCacheChapter chapter)
        {
            if (chapter.IsVolume || chapter.StartLine < 0 || string.IsNullOrEmpty(normalizedText))
            {
                return string.Empty;
            }

            var lines = normalizedText.Split('\n');
            var from = Math.Clamp(chapter.StartLine, 0, lines.Length);
            var to = Math.Clamp(chapter.EndLine, from, lines.Length);
            if (to <= from)
            {
                return string.Empty;
            }

            return string.Join("\n", lines, from, to - from);
        }

        // EN: Assembles a chapter window; the indexed overload allows per-chapter purified-text caching.
        /// <summary>
        /// 装配窗口：逐章切片 → <paramref name="purify"/>（总开关关闭时由调用方传恒等函数）→ 拼接。
        /// 返回每章标题在装配文本里的行号，供目录跳转 / 当前章高亮 / 章内行偏移恢复使用。
        /// <para>行号按「已发出的换行符数」精确计算（不是按 Split 的行数）：
        /// 切片可能以换行结尾（章尾空行），按行数算会把幻影尾行重复计一次，标题行号整体偏移。</para>
        /// </summary>
        public static WindowAssembly AssembleWindow(
            string normalizedText,
            IReadOnlyList<TextCacheChapter> chapters,
            int from,
            int toExclusive,
            Func<string, string> purify) =>
            AssembleWindow(normalizedText, chapters, from, toExclusive, (_, slice) => purify(slice));

        /// <summary>
        /// 装配窗口（带章下标的净化委托版）。
        /// <para>带下标是为了让调用方能按「第几章」缓存净化结果（虚拟窗口的内存缓存）：
        /// 5 参重载内部就是转调本方法、忽略下标。</para>
        /// </summary>
        /// <param name="purifyChapter">(章下标, 原文切片) → 净化后文本。</param>
        public static WindowAssembly AssembleWindow(
            string normalizedText,
            IReadOnlyList<TextCacheChapter> chapters,
            int from,
            int toExclusive,
            Func<int, string, string> purifyChapter)
        {
            var count = Math.Max(0, toExclusive - from);
            var sb = new StringBuilder();
            var headingLines = new int[count];
            var sliceLineCounts = new int[count];
            var newlines = 0; // 已发出的换行符数：下一字符所在行 = newlines + 1

            for (var i = from; i < toExclusive; i++)
            {
                var k = i - from;
                var chapter = chapters[i];
                if (chapter.IsVolume)
                {
                    headingLines[k] = -1;
                    sliceLineCounts[k] = 0;
                    continue;
                }

                var slice = SliceChapter(normalizedText, chapter);
                var purified = purifyChapter(i, slice) ?? string.Empty;

                if (k > 0)
                {
                    // 章与章之间补一个换行：切片不含下一章的标题行，直接拼会并成一行
                    sb.Append('\n');
                    newlines++;
                }

                // 标题在切片内的行下标。不能直接用原始偏移（HeadingLine - StartLine）：
                // 第一章的切片含卷首内容，净化可能删掉其中的行（广告/链接），标题会前移；
                // 非首章的切片从标题行开始，偏移为 0，原文偏移天然正确。
                // 故：期望偏移命中就用；否则按锚点在净化后切片里找，再不行夹紧到合法范围。
                var sliceLines = purified.Split('\n');
                var headingInSlice = FindHeadingInSlice(sliceLines, chapter.Name, chapter.HeadingLine - chapter.StartLine);
                headingLines[k] = newlines + headingInSlice + 1;
                sliceLineCounts[k] = sliceLines.Length;

                sb.Append(purified);
                newlines += sliceLines.Length - 1;
            }

            return new WindowAssembly(sb.ToString(), headingLines, sliceLineCounts);
        }

        /// <summary>由可见行反推当前章（窗口内下标；卷标题与窗口前的内容返回 -1 或窗口首章）。</summary>
        public static int CurrentChapterInWindow(int[] headingLines, int from, int topLine)
        {
            var current = -1;
            for (var k = 0; k < headingLines.Length; k++)
            {
                if (headingLines[k] < 0)
                {
                    continue;
                }

                if (headingLines[k] <= topLine)
                {
                    current = from + k;
                }
                else
                {
                    break;
                }
            }

            return current;
        }

        /// <summary>
        /// 在净化后的切片里定位标题行：期望偏移命中直接用（绝大多数情况）；
        /// 否则按章名锚点（首个空格前的片段，至多 8 字）找第一处命中；
        /// 都找不到夹紧到合法范围（净化把标题行本身删了——目录仍指向切片开头）。
        /// </summary>
        private static int FindHeadingInSlice(string[] lines, string name, int rawOffset)
        {
            var last = Math.Max(0, lines.Length - 1);
            var clamped = Math.Clamp(rawOffset, 0, last);

            var anchor = HeadingAnchor(name);
            if (anchor.Length == 0)
            {
                return clamped;
            }

            if (rawOffset >= 0 && rawOffset < lines.Length &&
                lines[rawOffset].Trim().StartsWith(anchor, StringComparison.Ordinal))
            {
                return rawOffset;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith(anchor, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return clamped;
        }

        /// <summary>章名锚点：第一个空格前的片段（至多 8 字）——"第1节 001 狗的报恩" → "第1节"。</summary>
        private static string HeadingAnchor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var space = name.IndexOf(' ');
            var cut = space > 0 ? space : name.Length;
            return name.Substring(0, Math.Min(cut, 8));
        }

        /// <summary>章内行偏移：可见行相对该章标题行的偏移（≥0）。</summary>
        public static int LineOffsetInChapter(int headingLine, int topLine) =>
            headingLine < 0 ? 0 : Math.Max(0, topLine - headingLine);
    }
}
