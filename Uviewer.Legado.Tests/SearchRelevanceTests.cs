using System.Linq;
using Uviewer.Services.Legado;
using Xunit;

namespace Uviewer.Services.Legado.Tests
{
    /// <summary>
    /// 搜索结果相关性评分（用户要求"按文本相关性从上到下排列"）。
    /// 分档口径见 <see cref="SearchRelevance"/>：完全匹配 &gt; 前缀 &gt; 包含 &gt;
    /// 去副标题后再比 &gt; 子序列覆盖 &gt; 作者命中 &gt; 其余。
    /// </summary>
    public class SearchRelevanceTests
    {
        [Fact]
        public void Exact_Match_Beats_Everything()
        {
            var exact = SearchRelevance.Score("凡人修仙传", "忘语", "凡人修仙传");
            var prefix = SearchRelevance.Score("凡人修仙传外传", "某人", "凡人修仙传");
            var contains = SearchRelevance.Score("新凡人修仙传书", "某人", "凡人修仙传");

            Assert.Equal(100, exact);
            Assert.True(exact > prefix);
            Assert.True(prefix > contains);
        }

        [Fact]
        public void Prefix_Beats_Contains()
        {
            Assert.True(
                SearchRelevance.Score("凡人修仙传外传", null, "凡人修仙传")
                > SearchRelevance.Score("重制凡人修仙传", null, "凡人修仙传"));
        }

        [Fact]
        public void Book_Number_Marks_And_Punctuation_Are_Ignored()
        {
            // 《书名》与全角标点不应影响判定
            Assert.Equal(100, SearchRelevance.Score("《凡人修仙传》", null, "凡人修仙传"));
            Assert.Equal(100, SearchRelevance.Score("凡人修仙传！", null, "凡人修仙传"));
            Assert.Equal(100, SearchRelevance.Score("凡人 修仙传", null, "凡人修仙传"));
        }

        [Fact]
        public void Subtitle_In_Brackets_Is_Stripped_Before_Comparing()
        {
            // "凡人修仙传（精校版）" → 去副标题后完全一致
            var withSubtitle = SearchRelevance.Score("凡人修仙传（精校版）", null, "凡人修仙传");
            Assert.True(withSubtitle >= 88, $"实际 {withSubtitle}");
        }

        [Fact]
        public void Subsequence_Still_Scores_Above_Unrelated()
        {
            // 漏字但顺序一致：凡人仙传 ⊂ 凡人修仙传
            var near = SearchRelevance.Score("凡人仙传", null, "凡人修仙传");
            var unrelated = SearchRelevance.Score("斗罗大陆", null, "凡人修仙传");

            Assert.True(near > unrelated, $"near={near} unrelated={unrelated}");
            Assert.True(near >= 40);
        }

        [Fact]
        public void Author_Hit_Ranks_Above_Unrelated_Title()
        {
            // 关键词是作者名：书名完全不相关时，作者命中也应优于乱命中
            var byAuthor = SearchRelevance.Score("斗罗大陆", "忘语", "忘语");
            var unrelated = SearchRelevance.Score("斗罗大陆", "唐家三少", "忘语");

            Assert.Equal(60, byAuthor);
            Assert.Equal(10, unrelated);
            Assert.True(byAuthor > unrelated);
        }

        [Fact]
        public void Empty_Keyword_Scores_Zero_And_Null_Safe()
        {
            Assert.Equal(0, SearchRelevance.Score("书", "作者", null));
            Assert.Equal(0, SearchRelevance.Score("书", "作者", "   "));
            Assert.True(SearchRelevance.Score(null, null, "关键词") >= 10);
        }

        [Fact]
        public void Ranking_Order_Is_Stable_For_Realistic_Batch()
        {
            var keyword = "凡人修仙传";
            var names = new[]
            {
                "斗罗大陆", "凡人修仙传外传", "凡人修仙传", "凡人修仙传（精校版）",
                "重生之凡人修仙传", "凡人修仙", "凡人修仙传之仙界篇",
            };

            var ordered = names
                .Select(n => (Name: n, Score: SearchRelevance.Score(n, null, keyword)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Name)
                .ToList();

            Assert.Equal("凡人修仙传", ordered[0]);
            Assert.Equal("斗罗大陆", ordered[^1]);
        }
    }
}
