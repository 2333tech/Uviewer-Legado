using System.Text;
using System.Text.RegularExpressions;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// TXT 章节切分：按 <see cref="TitleRule"/> 把整本文本切成章节，供目录与跳章使用。
///
/// <para><b>为什么需要独立于 <c>TextTocProvider</c></b>：后者只认三种固定标记
/// （<c>［＃大見出し］</c> / <c>#</c> / <c>##</c>），而绝大多数中文 TXT 小说用的是
/// "第一章 xxx""第1节 xxx""Chapter 3" 这类写法——一个都匹配不上，于是目录为空、
/// 顶栏目录按钮点开只有"无目录"。这里用可导入的正则规则覆盖这些写法。</para>
///
/// <para><b>匹配规则</b>：逐行用规则正则做<b>整行匹配</b>（<see cref="RegexOptions"/> 无
/// <c>Multiline</c>，因为已按行切分）；第一条命中的规则生效（规则已按 Order 排好序）。
/// 章名取 <see cref="TitleRule.TitleGroup"/> 指定的捕获组，为空时回退整行——
/// 避免出现"有目录但条目空白"。</para>
///
/// <para><b>性能</b>：整本小说动辄数万行，逐行 <c>Regex.IsMatch</c> 会产生大量临时对象。
/// 这里先做一组<b>廉价前置筛选</b>（行首字符必须命中"可能是标题"的特征），
/// 绝大多数正文行在正则之前就被排除；且规则正则全部预编译并缓存。</para>
/// </summary>
public static class TxtChapterSplitter
{
    /// <summary>
    /// 一次切分的结果。
    /// </summary>
    /// <param name="Chapters">识别到的章节（<see cref="ChapterSpan.LineNumber"/> 为 0-based 行号）。</param>
    /// <param name="BookTitle">书名行（由 <see cref="TitleRule.IsBookTitle"/> 规则识别，取首个非空）。</param>
    /// <param name="TotalLines">总行数（便于 UI 显示"识别到 N 章 / 共 M 行"）。</param>
    public sealed record SplitResult(
        List<ChapterSpan> Chapters,
        string BookTitle,
        int TotalLines)
    {
        /// <summary>是否识别出任何章节。</summary>
        public bool HasChapters => Chapters.Count > 0;
    }

    /// <summary>一个章节在原文中的位置。</summary>
    /// <param name="Title">章名（已按 TitleGroup 提取）。</param>
    /// <param name="LineNumber">标题所在行（0-based）。</param>
    /// <param name="Level">层级（1 = 章，2 = 节）。</param>
    /// <param name="IsVolume">是否为卷标题（不直接跳章）。</param>
    public sealed record ChapterSpan(string Title, int LineNumber, int Level, bool IsVolume);

    /// <summary>预编译后的规则（避免每次切分重复编译正则）。</summary>
    private sealed record CompiledRule(TitleRule Rule, Regex? Pattern);

    /// <summary>规则集合 → 预编译形态（<b>按 Order 升序</b>，同值保持输入顺序）。</summary>
    /// <remarks>
    /// 排序在这里做而不是指望调用方：<c>CreateDefaultRules()</c> 与用户导入的 JSON
    /// 都是任意顺序，若不排，"第一条命中生效"就会变成"文件里第一条生效"——
    /// 卷规则（Order=5）会被章规则（Order=10）抢先匹配，卷全被当成章。
    /// </remarks>
    private static List<CompiledRule> Compile(IEnumerable<TitleRule> rules)
    {
        var ordered = rules
            .Select((r, i) => (Rule: r, Index: i))
            .OrderBy(x => x.Rule.Order)
            .ThenBy(x => x.Index);

        var list = new List<CompiledRule>();
        foreach (var (rule, _) in ordered)
        {
            Regex? regex = null;
            if (!string.IsNullOrWhiteSpace(rule.Pattern))
            {
                try
                {
                    // IgnoreCase：中文无大小写，但英文章节名（"Chapter 3"）需要
                    regex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    // 用户导入的非法正则：跳过该规则而不是让整次切分失败
                    regex = null;
                }
            }

            if (regex is null && !rule.IsBookTitle)
            {
                continue;
            }

            list.Add(new CompiledRule(rule, regex));
        }

        return list;
    }

    /// <summary>
    /// 廉价前置筛选：判断这一行<b>可能</b>是章节标题。
    /// <para>不依赖规则内容，只看中文小说的常见形态：行首是"第/卷/篇/章/节/回"，
    /// 或以 "#" 开头，或含"Chapter"。目的是让绝大多数正文行在进入正则前就被淘汰。</para>
    /// </summary>
    /// <summary>行首是否带装饰性前缀（书名号/方括号/括号/"序"等）——这类行去前缀后可能就是标题。</summary>
    private static bool HasLeadingDecoration(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed.Length > 120)
        {
            return false;
        }

        return trimmed[0] is '【' or '［' or '《' or '「' or '『' or '[' or '（' or '(';
    }

    private static bool MaybeHeading(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed.Length > 120)
        {
            // 章节标题不会很长；过长行直接排除（省掉正则调用）
            return false;
        }

        var c = trimmed[0];
        if (c == '#' || c == '【' || c == '［')
        {
            return true;
        }

        if (c == '第' || c == '卷' || c == '篇' || c == '章' || c == '节' || c == '回' || c == '序')
        {
            return true;
        }

        // 英文章节："Chapter 3" / "CHAPTER III" / "Part 2" / "Section 5"
        // （必须与内置默认规则的正则覆盖面对齐：规则认 chapter|part|section，
        // 前置筛选漏掉某个首字母 = 对应写法永远走不到正则——"Section 5" 就是这样
        // 静默失效的。）
        if ((c == 'C' || c == 'c' || c == 'P' || c == 'p' || c == 'S' || c == 's') &&
            trimmed.Length > 1 &&
            (trimmed[1] == 'h' || trimmed[1] == 'H' || trimmed[1] == 'a' || trimmed[1] == 'A' ||
             trimmed[1] == 'r' || trimmed[1] == 'R' || trimmed[1] == 'e' || trimmed[1] == 'E'))
        {
            return true;
        }

        return false;
    }

    // EN: Splits a book into chapters; an optional hook lets title-scope rules clean a line before matching.
    /// <summary>
    /// 切分整本文本。
    /// <para><paramref name="content"/> 会被规范化换行（CRLF/CR → LF）后再按行处理；
    /// 返回的行号基于规范化后的行序，因此可直接用于 <c>TextReader</c> 定位。</para>
    /// </summary>
    /// <param name="content">全文（可很大，故内部用 Span/逐行处理，不建整份 DOM）。</param>
    /// <param name="rules">已启用的规则（<see cref="TitleRuleManager.GetEnabledOrdered"/> 的输出）。</param>
    /// <param name="purifyTitleLine">
    /// 标题行预净化（作用于标题的替换规则，legado <c>scopeTitle</c>）：识别标题<b>之前</b>逐行调用。
    /// 只在"可能是标题"的行上执行（<see cref="MaybeHeading"/> 或带装饰前缀），正文行不付这份成本。
    /// </param>
    public static SplitResult Split(
        string? content,
        IReadOnlyList<TitleRule> rules,
        Func<string, string>? purifyTitleLine = null)
    {
        var chapters = new List<ChapterSpan>();
        var bookTitle = string.Empty;

        if (string.IsNullOrEmpty(content) || rules is null || rules.Count == 0)
        {
            return new SplitResult(chapters, bookTitle, 0);
        }

        var compiled = Compile(rules);
        if (compiled.Count == 0)
        {
            return new SplitResult(chapters, bookTitle, 0);
        }

        var normalized = TextNormalization.ToLf(content);
        var lineNumber = 0;
        var totalLines = 0;

        // 书名行单独处理：书名不以"第/卷/#"开头（"我的书名"就不会通过 MaybeHeading），
        // 若混在标题循环里判，几乎永远匹配不上。故取正文开头若干行单独扫。
        var bookTitleRules = compiled.Where(c => c.Rule.IsBookTitle).ToList();

        foreach (var (start, length) in EnumerateLines(normalized))
        {
            totalLines++;
            var line = normalized.Substring(start, length).Trim();
            lineNumber++;

            if (line.Length == 0)
            {
                continue;
            }

            // 标题行预净化：先去前缀/装饰符，"【玄幻】第1章 风起"才能被标题规则认出来。
            // 只对"看起来像标题"的行执行，避免为整本正文付一遍替换成本。
            if (purifyTitleLine is not null && (MaybeHeading(line) || HasLeadingDecoration(line)))
            {
                var purified = purifyTitleLine(line).Trim();
                if (purified.Length > 0)
                {
                    line = purified;
                }
            }

            // 前置筛选：绝大多数正文行在这里就被淘汰，不进入正则
            if (!MaybeHeading(line))
            {
                // 书名只可能出现在正文开头（章节开始后就不再是书名了）
                if (bookTitleRules.Count > 0 && chapters.Count == 0 &&
                    bookTitleRules.Any(c => c.Pattern is null || c.Pattern.IsMatch(line)))
                {
                    if (string.IsNullOrEmpty(bookTitle) && line.Length <= 60)
                    {
                        bookTitle = line;
                    }
                }

                continue;
            }

            foreach (var (rule, regex) in compiled)
            {
                if (rule.IsBookTitle)
                {
                    // 书名行不计入章节。
                    // 无正则的书名规则取首个短行；有正则的必须真的匹配上。
                    var titleHit = regex is null
                        ? line.Length <= 60
                        : regex.IsMatch(line);

                    if (titleHit && string.IsNullOrEmpty(bookTitle))
                    {
                        var t = regex is null ? line : ExtractTitle(regex.Match(line), rule.TitleGroup, line);
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            bookTitle = t;
                        }
                    }

                    continue;
                }

                if (regex is null || !regex.IsMatch(line))
                {
                    continue;
                }

                var match = regex.Match(line);
                var title = ExtractTitle(match, rule.TitleGroup, line);

                if (string.IsNullOrWhiteSpace(title))
                {
                    // 章名为空：退回整行，避免目录出现空条目
                    title = line;
                }
                else
                {
                    // 剥掉 Markdown 井号前缀：目录里显示 "# 卷首" 是噪音。
                    // 与既有 TextTocProvider 的处理保持一致（它也会 TrimStart('#',' ')）。
                    title = title.TrimStart('#', ' ').Trim();
                    if (title.Length == 0)
                    {
                        title = line;
                    }
                }

                chapters.Add(new ChapterSpan(
                    title,
                    lineNumber - 1,
                    rule.Level <= 0 ? 1 : rule.Level,
                    rule.IsVolume));

                break; // 第一条命中的规则生效
            }
        }

        return new SplitResult(chapters, bookTitle, totalLines);
    }

    /// <summary>按指定捕获组取章名；组不存在/未捕获时回退整行。</summary>
    private static string ExtractTitle(Match match, int titleGroup, string wholeLine)
    {
        if (titleGroup <= 0 || titleGroup > match.Groups.Count)
        {
            return wholeLine;
        }

        var group = match.Groups[titleGroup];
        return group.Success ? group.Value.Trim() : wholeLine;
    }

    /// <summary>逐行枚举（返回每行的 [start, length) 区间，避免 Substring 产生全部分行字符串）。</summary>
    private static IEnumerable<(int Start, int Length)> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            yield return (start, i - start);
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return (start, text.Length - start);
        }
    }

    /// <summary>
    /// 内置默认规则集（首次使用、用户尚未导入任何规则时的兜底）。
    ///
    /// <para><b>为什么需要默认值</b>：绝大多数中文 TXT 的章节标题是"第N章 xxx"这类顶格行，
    /// 内置标记（<c>#</c>/<c>［＃大見出し］</c>）一个都匹配不上；没有默认规则时目录恒空，
    /// 顶栏目录按钮点开只有"无目录"。</para>
    ///
    /// <para><b>Order 的作用</b>：切分时按 Order 升序"第一条命中生效"，
    /// 所以更具体的规则必须排在更宽泛的规则前面（否则宽规则会抢走本该属于具体规则的标题）。
    /// 这里的 Order 取值：Markdown 标记 1~3 &lt; 卷 5 &lt; 章/节 10 &lt; 数字小标题 20。</para>
    ///
    /// <para><b>各条覆盖的写法</b>（用户导入规则后以导入为准，本表只做兜底）：</para>
    /// <list type="table">
    /// <item><term>中文章节</term><description>"第1章 / 第十二节 / 第三回 / 第五篇 / 第一卷 xxx"，
    /// 允许数字前后有空格、标题最长 60 字。</description></item>
    /// <item><term>中文卷</term><description>"第N卷 xxx"——标为卷：进目录但不跳章（Order 5 先于章规则）。</description></item>
    /// <item><term>英文章节</term><description>"Chapter 3 / Part IV / Section 2"（大小写不敏感）。</description></item>
    /// <item><term>数字标题</term><description>"1. xxx / 2、xxx / 一、xxx"——层级 2（更细的小节）。</description></item>
    /// <item><term>Markdown 1~3 级标题</term><description>"# / ## / ### xxx"（与内置标记识别保持一致）。</description></item>
    /// </list>
    /// </summary>
    public static List<TitleRule> CreateDefaultRules() => new()
    {
        // ① 中文章节：最常用的一条。数字允许中文数字与前后空格（"第 1 章"也常见）
        new TitleRule
        {
            Id = 1,
            Name = "中文章节（第N章/节/回/篇）",
            Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*[章节回篇卷].{0,60}$",
            Order = 10,
            Level = 1,
        },
        // ② 中文卷：Order=5 比章规则（10）小 → 先命中，卷不会被当成章
        new TitleRule
        {
            Id = 2,
            Name = "中文卷（第N卷）",
            Pattern = @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*卷.{0,60}$",
            Order = 5,
            Level = 1,
            IsVolume = true,
        },
        // ③ 英文章节：IgnoreCase 由切分器统一开启（中文无大小写，英文标题需要）
        new TitleRule
        {
            Id = 3,
            Name = "英文章节（Chapter N）",
            Pattern = @"^\s*(chapter|part|section)\s+[0-9ivxlcdm]+\b.{0,80}$",
            Order = 10,
            Level = 1,
        },
        // ④ 数字小标题：层级 2（"1. xxx"通常是章内小节，不是整章）
        new TitleRule
        {
            Id = 4,
            Name = "数字标题（1. / 1、/ 一、）",
            Pattern = @"^\s*[0-9]{1,4}\s*[.、．]\s*\S.{0,60}$",
            Order = 20,
            Level = 2,
        },
        // ⑤~⑦ Markdown 1~3 级：Order 1~3 最小，先于其它规则；
        // TitleGroup=0 表示"整行即标题"，井号由切分器 TrimStart('#',' ') 剥掉
        new TitleRule
        {
            Id = 5,
            Name = "Markdown 一级标题（#）",
            Pattern = @"^#\s+\S.{0,80}$",
            TitleGroup = 0,
            Order = 1,
            Level = 1,
        },
        new TitleRule
        {
            Id = 6,
            Name = "Markdown 二级标题（##）",
            Pattern = @"^##\s+\S.{0,80}$",
            TitleGroup = 0,
            Order = 2,
            Level = 2,
        },
        new TitleRule
        {
            Id = 7,
            Name = "Markdown 三级标题（###）",
            Pattern = @"^###\s+\S.{0,80}$",
            TitleGroup = 0,
            Order = 3,
            Level = 3,
        },
    };
}
