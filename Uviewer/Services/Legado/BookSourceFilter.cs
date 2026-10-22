using System;
using System.Collections.Generic;
using System.Linq;
using Uviewer.Services.Legado.Models;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 书源筛选条件：<b>关键词 + 类别 + 分组</b> 三者的组合（彼此为「与」关系）。
    ///
    /// <para><b>为什么抽成独立类型</b>：书源管理（列表过滤）与书源搜索（候选集过滤）
    /// 必须<b>同一套判定</b>——否则用户在管理界面按"漫画"筛出 129 条，转到搜索界面勾
    /// "漫画"却搜到别的集合，这种不一致比没有筛选更让人困惑。判定逻辑放在这里，
    /// 两处都只负责把控件状态翻译成这个 record。</para>
    ///
    /// <para><b>纯逻辑</b>：不引用任何 WinUI 类型，可直接链接进测试工程断言。</para>
    /// </summary>
    public sealed record BookSourceFilter
    {
        /// <summary>关键词（匹配名称 / URL / 分组 / 备注 / 类别名），空 = 不按关键词过滤。</summary>
        public string? Keyword { get; init; }

        /// <summary>类别；null = 全部类别（对齐 <c>bookSourceType</c> 语义）。</summary>
        public BookSourceKind? Kind { get; init; }

        /// <summary>分组名（<c>bookSourceGroup</c> 中任意一段完全相等）；null/空 = 全部分组。</summary>
        public string? Group { get; init; }

        /// <summary>是否没有任何实际约束（用于跳过筛选、直接走快路径）。</summary>
        public bool IsEmpty
            => string.IsNullOrWhiteSpace(Keyword) && Kind is null && string.IsNullOrWhiteSpace(Group);

        /// <summary>该条书源是否满足全部条件。</summary>
        public bool Matches(BookSourceModel source)
        {
            if (source is null)
            {
                return false;
            }

            if (Kind is { } kind && source.SourceKind != kind)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Group) && !BookSourceGroups.Contains(source.BookSourceGroup, Group))
            {
                return false;
            }

            return MatchesKeyword(source);
        }

        /// <summary>按条件过滤一组书源（保持原顺序，枚举顺序即导入顺序 = 确定性）。</summary>
        public List<BookSourceModel> Apply(IEnumerable<BookSourceModel> sources)
            => IsEmpty ? sources.ToList() : sources.Where(Matches).ToList();

        /// <summary>
        /// 关键词匹配：名称 / URL / 分组 / 备注 / <b>类别名</b>。
        /// <para>把类别名也纳入匹配是有意的——输入"漫画"就该筛出漫画源，这与在类别下拉里
        /// 选"漫画"结果一致，是用户最自然的两种表达方式。</para>
        /// </summary>
        private bool MatchesKeyword(BookSourceModel source)
        {
            var keyword = Keyword?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                return true;
            }

            return Contains(source.BookSourceName, keyword)
                || Contains(source.BookSourceUrl, keyword)
                || Contains(source.BookSourceGroup, keyword)
                || Contains(source.BookSourceComment, keyword)
                || Contains(BookSourceGroups.DescribeKind(source.SourceKind), keyword);
        }

        private static bool Contains(string? haystack, string needle)
            => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 书源分组的文本处理：legado 的 <c>bookSourceGroup</c> 是<b>一个逗号分隔的字符串</b>
    /// （一条源可以属于多个分组，如 <c>"小说源,校验可用"</c>），本类负责拆分 / 规范化 / 增删。
    ///
    /// <para>分隔符同时接受半角与全角：中文输入法下用户很容易打出「，」「；」，
    /// 而 legado 生态里这两种写法都存在（导入的源里两者都实测出现过）。</para>
    /// </summary>
    public static class BookSourceGroups
    {
        /// <summary>分组名分隔符（半角/全角逗号、分号、顿号、竖线）。</summary>
        private static readonly char[] Separators = { ',', '，', ';', '；', '、', '|' };

        /// <summary>类别下拉的选项（顺序即 UI 顺序：文本、漫画、音频、视频、文件）。</summary>
        public static readonly IReadOnlyList<(string Label, BookSourceKind? Kind)> KindOptions = new[]
        {
            ("全部类别", (BookSourceKind?)null),
            ("文本", (BookSourceKind?)BookSourceKind.Text),
            ("漫画（图片）", (BookSourceKind?)BookSourceKind.Image),
            ("音频", (BookSourceKind?)BookSourceKind.Audio),
            ("视频", (BookSourceKind?)BookSourceKind.Video),
            ("文件", (BookSourceKind?)BookSourceKind.File),
        };

        /// <summary>类别的中文名（用于列表副标题、筛选提示与关键词匹配）。</summary>
        public static string DescribeKind(BookSourceKind kind) => kind switch
        {
            BookSourceKind.Text => "文本",
            BookSourceKind.Audio => "音频",
            BookSourceKind.Image => "漫画",
            BookSourceKind.File => "文件",
            BookSourceKind.Video => "视频",
            _ => $"类型{(int)kind}",
        };

        /// <summary>拆开分组串：去空、去重（忽略大小写）、保留原顺序。</summary>
        public static IReadOnlyList<string> Split(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var part in value.Split(
                Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0 && seen.Add(part))
                {
                    names.Add(part);
                }
            }

            return names;
        }

        /// <summary>规范化（统一成半角逗号分隔、去重去空）。空输入返回空串。</summary>
        public static string Normalize(string? value) => string.Join(",", Split(value));

        /// <summary>分组串里是否含某一组（完全相等，忽略大小写）。</summary>
        public static bool Contains(string? value, string? group)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                return false;
            }

            return Split(value).Any(g => string.Equals(g, group.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>加入一个分组（已存在则不重复）。</summary>
        public static string Add(string? value, string? group)
        {
            var names = Split(value).ToList();
            var target = group?.Trim();
            if (!string.IsNullOrEmpty(target)
                && !names.Any(g => string.Equals(g, target, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(target);
            }

            return string.Join(",", names);
        }

        /// <summary>移除一个分组（返回规范化后的剩余分组串；可能为空串）。</summary>
        public static string Remove(string? value, string? group)
        {
            var target = group?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                return Normalize(value);
            }

            return string.Join(",", Split(value)
                .Where(g => !string.Equals(g, target, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>把一条分组串里的某组<b>改名</b>（组名统一操作，用于自定义分组重命名）。</summary>
        public static string Rename(string? value, string? oldName, string? newName)
        {
            var from = oldName?.Trim();
            var to = newName?.Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)
                || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(value);
            }

            return string.Join(",", Split(value).Select(
                g => string.Equals(g, from, StringComparison.OrdinalIgnoreCase) ? to : g));
        }

        /// <summary>
        /// 汇总当前书源里出现过的全部分组（按出现条数降序，同数按名称排序）。
        /// <para><b>不缓存</b>：3637 条书源全量扫描实测 &lt; 5 ms，缓存反而要在
        /// 导入/保存/删除后逐处失效（漏一处就给出过期的分组列表）。</para>
        /// </summary>
        public static List<(string Name, int Count)> Collect(IEnumerable<BookSourceModel> sources)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                foreach (var name in Split(source.BookSourceGroup))
                {
                    counts.TryGetValue(name, out var n);
                    counts[name] = n + 1;
                }
            }

            return counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
    }
}
