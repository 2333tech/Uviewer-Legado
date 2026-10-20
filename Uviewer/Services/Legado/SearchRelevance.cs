using System;
using System.Text;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 搜索结果的<b>文本相关性</b>评分（纯逻辑，无 WinUI 依赖，可被集成测试链接）。
    ///
    /// <para><b>为什么要排序</b>：多源搜索是"边搜边出"的流式渲染（每命中一个书源就追加卡片），
    /// 卡片天然按<b>书源返回顺序</b>排列。同一本书在几十个源里命中时，用户最关心的
    /// "名字完全对得上"那一本可能排在第 40 张卡片——等于没有排序。</para>
    ///
    /// <para><b>评分口径</b>（分档而非连续打分：书源返回的书名千奇百怪，细粒度分数没有意义）：
    /// <list type="bullet">
    /// <item>100 书名与关键词完全一致；</item>
    /// <item>92 书名以关键词开头（"凡人修仙传" ← "凡人修仙"）；</item>
    /// <item>84 书名包含关键词；</item>
    /// <item>88/80/74 去掉书名副标题（括号/【】/「」内容）后再比一次；</item>
    /// <item>40~70 关键词的字符按顺序出现在书名里（覆盖率越高分越高，"凡人修仙传" ← "凡人仙传"）；</item>
    /// <item>60/52 作者名命中（书名完全不相关时，作者命中仍比乱命中有意义）；</item>
    /// <item>10 其余（保留展示，排在最后）。</item>
    /// </list></para>
    ///
    /// <para><b>归一化</b>：忽略大小写与空白，并剔除书名常见的标点/书名号——
    /// "《凡人修仙传》"、"凡人修仙传（精校版）"都应命中"凡人修仙传"。</para>
    /// </summary>
    public static class SearchRelevance
    {
        /// <summary>书名比对的标点/装饰字符（归一化时剔除）。</summary>
        private const string Noise = " \t\u3000·．.，,、。;；:：!！?？-—–_~～|｜/\\()（）[]［］【】《》〈〉<>「」『』\"'“”‘’*#@&+=";

        /// <summary>评分（0~100，越大越相关）。<paramref name="keyword"/> 为空时返回 0。</summary>
        public static int Score(string? name, string? author, string? keyword)
        {
            var key = Normalize(keyword);
            if (key.Length == 0)
            {
                return 0;
            }

            var title = Normalize(name);
            if (title.Length > 0)
            {
                var score = ScoreTitle(title, key);
                if (score > 0)
                {
                    return score;
                }
            }

            var writer = Normalize(author);
            if (writer.Length > 0)
            {
                if (writer == key)
                {
                    return 60;
                }

                if (writer.Contains(key, StringComparison.Ordinal))
                {
                    return 52;
                }
            }

            return 10;
        }

        /// <summary>书名分档评分；0 = 书名完全不相关（交给作者档继续判）。</summary>
        private static int ScoreTitle(string title, string key)
        {
            if (title == key)
            {
                return 100;
            }

            if (title.StartsWith(key, StringComparison.Ordinal))
            {
                return 92;
            }

            if (title.Contains(key, StringComparison.Ordinal))
            {
                return 84;
            }

            // 去掉副标题（括号里的"精校版/全本"等）后再比
            var core = StripBrackets(title);
            if (core.Length > 0 && core != title)
            {
                if (core == key)
                {
                    return 88;
                }

                if (core.StartsWith(key, StringComparison.Ordinal))
                {
                    return 80;
                }

                if (core.Contains(key, StringComparison.Ordinal))
                {
                    return 74;
                }
            }

            // 子序列覆盖率：关键词的字符按顺序命中多少（容忍漏字/错位）
            var coverage = SubsequenceCoverage(core.Length > 0 ? core : title, key);
            if (coverage >= 0.6)
            {
                return 40 + (int)Math.Round(coverage * 30);
            }

            return 0;
        }

        /// <summary>归一化：小写 + 剔除空白与标点。</summary>
        internal static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (Noise.IndexOf(c) >= 0)
                {
                    continue;
                }

                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        /// <summary>剔除括号内的副标题（成对括号内内容消失，括号本身也消失）。</summary>
        internal static string StripBrackets(string text)
        {
            var sb = new StringBuilder(text.Length);
            var depth = 0;
            foreach (var c in text)
            {
                if (c is '(' or '（' or '[' or '［' or '【' or '《' or '「' or '『' or '<')
                {
                    depth++;
                    continue;
                }

                if (c is ')' or '）' or ']' or '］' or '】' or '》' or '」' or '』' or '>')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }

                    continue;
                }

                if (depth == 0)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>关键词字符在文本中按顺序出现的比例（0~1）。</summary>
        internal static double SubsequenceCoverage(string text, string key)
        {
            if (key.Length == 0 || text.Length == 0)
            {
                return 0;
            }

            var matched = 0;
            var pos = 0;
            foreach (var c in key)
            {
                var found = text.IndexOf(c, pos);
                if (found < 0)
                {
                    continue;
                }

                matched++;
                pos = found + 1;
            }

            return (double)matched / key.Length;
        }
    }
}
