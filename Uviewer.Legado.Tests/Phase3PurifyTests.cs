using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 3（正文净化管线 + ReplaceRule）验收。
/// 语义逐条对标 reader frontend stores/reader.ts 的 applyReplaceRules / matchRuleScope：
/// scope 匹配矩阵、order 稳定排序、gm 正则、字面替换、逐规则异常跳过、useReplaceRule 旁路。
/// 存储对标 reader api/commands/replace_rule.rs（replaceRule.json 列表 CRUD）。
/// </summary>
public class Phase3PurifyTests
{
    // ---------- 工具 ----------

    private static ContentPurifier PurifierOf(params ReplaceRule[] rules)
    {
        var p = new ContentPurifier();
        p.Rules = rules;
        return p;
    }

    private static ReplaceRule Rule(
        long id,
        string pattern,
        string replacement,
        string? scope = null,
        bool isRegex = false,
        int order = 0,
        bool isEnabled = true)
        => new()
        {
            Id = id,
            Name = $"r{id}",
            Pattern = pattern,
            Replacement = replacement,
            Scope = scope,
            IsRegex = isRegex,
            Order = order,
            IsEnabled = isEnabled,
        };

    // ---------- 1. scope 匹配矩阵（对标 matchRuleScope 逐分支） ----------

    [Fact]
    public void Scope_Empty_Or_Star_Matches_Any_Book()
    {
        var text = "广告内容";
        foreach (var scope in new[] { null, "", "  ", "*" })
        {
            var p = PurifierOf(Rule(1, "广告", "[已净化]", scope));
            Assert.Equal("[已净化]内容", p.Apply(text, new PurifyContext("书名", "https://b.example/1", "https://s.example")));
        }
    }

    [Fact]
    public void Scope_Source_Prefix_Matches_Origin_Only()
    {
        var p = PurifierOf(Rule(1, "广告", "X", "source:https://s.example"));

        // 命中：origin 精确相等
        Assert.Equal("X内容", p.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", "https://s.example")));
        // 不命中：origin 不同 / 为空
        Assert.Equal("广告内容", p.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", "https://other.example")));
        Assert.Equal("广告内容", p.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", null)));
    }

    [Fact]
    public void Scope_Book_Prefix_Matches_BookUrl_Only()
    {
        var p = PurifierOf(Rule(1, "广告", "X", "book:https://b.example/1"));

        Assert.Equal("X内容", p.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", "https://s.example")));
        Assert.Equal("广告内容", p.Apply("广告内容", new PurifyContext("书名", "https://b.example/2", "https://s.example")));
    }

    [Fact]
    public void Scope_Name_Form_Matches_BookName_And_Optional_BookUrl()
    {
        // 只有书名段
        var byName = PurifierOf(Rule(1, "广告", "X", "书名"));
        Assert.Equal("X内容", byName.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", "https://s.example")));
        Assert.Equal("广告内容", byName.Apply("广告内容", new PurifyContext("别的书", "https://b.example/1", "https://s.example")));

        // 书名 + 书籍 URL 两段：两段都满足才命中
        var both = PurifierOf(Rule(1, "广告", "X", "书名;https://b.example/1"));
        Assert.Equal("X内容", both.Apply("广告内容", new PurifyContext("书名", "https://b.example/1", "https://s.example")));
        Assert.Equal("广告内容", both.Apply("广告内容", new PurifyContext("书名", "https://b.example/2", "https://s.example")));
        Assert.Equal("广告内容", both.Apply("广告内容", new PurifyContext("别的书", "https://b.example/1", "https://s.example")));

        // 第一段 *：任意书名，但第二段仍要匹配
        var starName = PurifierOf(Rule(1, "广告", "X", "*;https://b.example/1"));
        Assert.Equal("X内容", starName.Apply("广告内容", new PurifyContext("任何书", "https://b.example/1", "https://s.example")));
        Assert.Equal("广告内容", starName.Apply("广告内容", new PurifyContext("任何书", "https://b.example/9", "https://s.example")));
    }

    [Fact]
    public void Scope_NonGlobal_Without_Book_Context_Never_Matches()
    {
        // reader：!currentBook → false（空上下文中只有 '*' 命中）
        var p = PurifierOf(
            Rule(1, "A", "X", "source:https://s.example"),
            Rule(2, "B", "Y", "book:https://b.example/1"),
            Rule(3, "C", "Z", "书名"));

        Assert.Equal("ABC", p.Apply("ABC", new PurifyContext()));
    }

    // ---------- 2. order 与链式效果 ----------

    [Fact]
    public void Rules_Apply_In_Order_Ascending_And_Chain()
    {
        // order 小的先执行，后者能看到前者的输出（链式）：
        // order 1 先把"甲"替成"乙"，order 2 再把"乙"替成"丙"（存储顺序故意倒置）
        var p = PurifierOf(
            Rule(1, "乙", "丙", order: 2),
            Rule(2, "甲", "乙", order: 1));

        Assert.Equal("丙", p.Apply("甲", new PurifyContext()));
    }

    [Fact]
    public void Rules_Same_Order_Keep_Storage_Order_Stable()
    {
        // 同 order 保持存储顺序（reader V8 sort 稳定性；LINQ OrderBy 同样稳定）
        var p = PurifierOf(
            Rule(1, "x", "1", order: 5),
            Rule(2, "x", "2", order: 5));

        // 第一条把 x→1 后第二条无匹配（x 已不存在），结果是 1 而非 2
        Assert.Equal("1", p.Apply("x", new PurifyContext()));
    }

    // ---------- 3. 正则 / 字面 ----------

    [Fact]
    public void Regex_Rule_Supports_Groups_And_Global_Multiline()
    {
        var p = PurifierOf(Rule(1, "<span class=\"ad\">(.*?)</span>", "【$1】", isRegex: true));

        Assert.Equal(
            "前【广告甲】中【广告乙】后",
            p.Apply("前<span class=\"ad\">广告甲</span>中<span class=\"ad\">广告乙</span>后", new PurifyContext()));
    }

    [Fact]
    public void Regex_Multiline_Anchors_Match_Line_Boundaries()
    {
        // reader 用 'gm'：^ 匹配每一行首（\s* 顺带吃掉行内前导空白）
        var p = PurifierOf(Rule(1, "^//\\s*", "", isRegex: true));

        Assert.Equal("注释1\n第一行\n注释2\n第二行", p.Apply("// 注释1\n第一行\n// 注释2\n第二行", new PurifyContext()));
    }

    [Fact]
    public void Literal_Rule_Replaces_All_Occurrences()
    {
        var p = PurifierOf(Rule(1, "广告", ""));

        // 从左到右非重叠替换（与 JS replaceAll 一致）："广广告告前后"只有 1 处"广告"
        Assert.Equal("广告前后", p.Apply("广广告告前后", new PurifyContext()));
    }

    // ---------- 4. 异常与边界 ----------

    [Fact]
    public void Invalid_Regex_Rule_Is_Skipped_Others_Still_Apply()
    {
        var p = PurifierOf(
            Rule(1, "([", "X", isRegex: true),   // 非法正则：整条跳过
            Rule(2, "保留", "OK"));

        // 非法正则规则不生效，其余规则照常；前导 "[" 是原文自带的
        Assert.Equal("[OK内容", p.Apply("[保留内容", new PurifyContext()));
    }

    [Fact]
    public void Empty_Pattern_Rule_Is_Skipped()
    {
        // legado 语义：空 pattern 跳过（reader/JS 会在每处插入 replacement，属数据错误行为）
        var p = PurifierOf(
            Rule(1, "", "X"),
            Rule(2, "a", "b"));

        Assert.Equal("b", p.Apply("a", new PurifyContext()));
    }

    [Fact]
    public void Disabled_Rule_Is_Skipped()
    {
        var p = PurifierOf(Rule(1, "a", "X", isEnabled: false));

        Assert.Equal("a", p.Apply("a", new PurifyContext()));
    }

    [Fact]
    public void UseReplaceRule_False_Bypasses_Pipeline()
    {
        var p = PurifierOf(Rule(1, "a", "X"));

        Assert.Equal("a", p.Apply("a", new PurifyContext(UseReplaceRule: false)));
    }

    [Fact]
    public void Empty_Text_Returns_Empty()
    {
        var p = PurifierOf(Rule(1, "a", "X"));

        Assert.Equal(string.Empty, p.Apply(string.Empty, new PurifyContext()));
        Assert.Equal(string.Empty, p.Apply(null!, new PurifyContext()));
    }

    [Fact]
    public void No_Rules_Returns_Original_Reference()
    {
        var p = new ContentPurifier();
        var text = "原文";

        // 无规则：快路径应原样返回（同一实例，零分配）
        Assert.Same(text, p.Apply(text, new PurifyContext()));
    }

    // ---------- 5. 快照失效与并发 ----------

    [Fact]
    public void Rules_Swap_Rebuilds_Snapshot()
    {
        var p = PurifierOf(Rule(1, "a", "X"));
        Assert.Equal("X", p.Apply("a", new PurifyContext()));

        // 换新列表（宿主语义：ReplaceRuleManager 每次变更给新快照）
        p.Rules = new[] { Rule(2, "a", "Y") };
        Assert.Equal("Y", p.Apply("a", new PurifyContext()));

        // 置空 = 停用
        p.Rules = null;
        Assert.False(p.HasRules);
        Assert.Equal("a", p.Apply("a", new PurifyContext()));
    }

    [Fact]
    public void Apply_Is_Deterministic_Under_Concurrency()
    {
        // 零填充 token（w01..w20）：避免"词1"是"词10"前缀导致的顺序敏感误判
        var rules = Enumerable.Range(1, 20)
            .Select(i => Rule(i, $"w{i:D2}", $"[{i}]", isRegex: false, order: i))
            .ToArray();
        var p = PurifierOf(rules);
        var input = string.Join("", Enumerable.Range(1, 20).Select(i => $"w{i:D2}"));
        var expected = string.Join("", Enumerable.Range(1, 20).Select(i => $"[{i}]"));

        // 串行基准
        Assert.Equal(expected, p.Apply(input, new PurifyContext("书名", "https://b.example/1", "https://s.example")));

        // 64 路并行：结果必须逐字节一致（编译快照不可变 + 正则只读）
        var bag = new ConcurrentBag<string>();
        Parallel.For(0, 64, _ =>
            bag.Add(p.Apply(input, new PurifyContext("书名", "https://b.example/1", "https://s.example"))));

        Assert.Equal(64, bag.Count);
        Assert.All(bag, r => Assert.Equal(expected, r));
    }

    // ---------- 6. 存储（ReplaceRuleManager） ----------

    private static string TempFile() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rr_{System.Guid.NewGuid():N}.json");

    [Fact]
    public async Task Manager_RoundTrip_Preserves_Order_And_Fields()
    {
        var path = TempFile();
        try
        {
            var mgr = new ReplaceRuleManager(path);
            Assert.True(mgr.Upsert(Rule(0, "a", "X", order: 2)));   // Id<=0 → 自动分配 1
            Assert.True(mgr.Upsert(Rule(0, "b", "Y", order: 1)));
            Assert.Equal(2, mgr.Count);
            Assert.Equal(1, mgr.GetAll()[0].Id);                   // 先插入的在前
            Assert.Equal(2, mgr.GetAll()[1].Id);

            await mgr.SaveAsync();
            Assert.False(System.IO.File.Exists(path + ".tmp"));    // 无临时文件残留

            var reloaded = new ReplaceRuleManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(2, reloaded.Count);
            Assert.Equal("a", reloaded.GetAll()[0].Pattern);       // 顺序 = 落盘顺序
            Assert.Equal("b", reloaded.GetAll()[1].Pattern);

            // 覆盖保位
            var updated = Rule(1, "a2", "X2", order: 9);
            reloaded.Upsert(updated);
            Assert.Equal("a2", reloaded.GetAll()[0].Pattern);
            Assert.Equal(2, reloaded.Count);

            // 删除（先落盘，让后续"重载"验证删除已持久化）
            Assert.True(reloaded.Remove(2));
            await reloaded.SaveAsync();
            Assert.Equal(1, reloaded.Count);
            Assert.False(reloaded.Remove(2));

            // 重载：文件里应是删除后的 1 条
            var reloaded2 = new ReplaceRuleManager(path);
            await reloaded2.LoadAsync();
            Assert.Equal(1, reloaded2.Count);
            Assert.Equal(1, reloaded2.GetAll()[0].Id);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Manager_AutoId_Never_Collides_After_Removal()
    {
        var path = TempFile();
        try
        {
            var mgr = new ReplaceRuleManager(path);
            mgr.Upsert(Rule(0, "a", "1"));   // Id=1
            mgr.Upsert(Rule(0, "b", "2"));   // Id=2
            mgr.Remove(2);
            mgr.Upsert(Rule(0, "c", "3"));   // max(1)+1 = 2，不撞已存在的 1

            Assert.Equal(2, mgr.Count);
            Assert.Equal(new long[] { 1, 2 }, mgr.GetAll().Select(r => r.Id).OrderBy(i => i));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ---------- 7. 端到端（引擎 + 净化） ----------

    private sealed class MapFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;
        public MapFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            foreach (var kv in _map)
            {
                if (url.Contains(kv.Key, StringComparison.Ordinal))
                {
                    return Task.FromResult((kv.Value, url));
                }
            }
            return Task.FromResult((string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static BookSourceModel JsonContentSource() => new()
    {
        BookSourceUrl = "https://api.example.com",
        RuleContent = new ContentRule { Content = "data.content", Title = "data.title" },
    };

    [Fact]
    public async Task Engine_Returns_Raw_And_Purify_Happens_Before_Render()
    {
        var source = JsonContentSource();
        var fetcher = new MapFetcher(new Dictionary<string, string>
        {
            ["/ch/1"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>正文</p><p>广告招租</p>\"}}",
        });

        var purifier = PurifierOf(
            Rule(1, "广告招租", "[净]", isRegex: false),
            Rule(2, "<p>([^<]*)</p>", "【$1】", isRegex: true));

        var engine = new BookSourceEngine(fetcher, purifier);

        // ① 抓取阶段：不做净化，返回原文（这正是总开关可逆的前提）
        var content = await engine.GetContentAsync(source, "https://api.example.com/ch/1", "https://api.example.com/book/1");
        Assert.NotNull(content);
        Assert.Equal("<p>正文</p><p>广告招租</p>", content!.Content);
        Assert.Equal("第一章", content.Title);

        // ② 读取 → 渲染之间：净化在此发生（宿主行为）
        var display = purifier.Apply(content.Content, PurifyContext.Default);
        Assert.Equal("【正文】【[净]】", display);
    }

    [Fact]
    public async Task Engine_Without_Purifier_Leaves_Content_Untouched()
    {
        var source = JsonContentSource();
        var fetcher = new MapFetcher(new Dictionary<string, string>
        {
            ["/ch/1"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>广告招租</p>\"}}",
        });

        var engine = new BookSourceEngine(fetcher);   // 无 purifier
        var content = await engine.GetContentAsync(source, "https://api.example.com/ch/1");

        Assert.Equal("<p>广告招租</p>", content!.Content);
    }

    [Fact]
    public async Task Engine_Batch_Purify_Matches_Serial()
    {
        var source = JsonContentSource();
        var map = new Dictionary<string, string>();
        var chapters = new List<TocItem>();
        for (var i = 0; i < 16; i++)
        {
            var url = $"https://api.example.com/ch/{i}";
            map[$"/ch/{i}"] = $"{{\"data\":{{\"title\":\"第{i}章\",\"content\":\"<p>内容{i}</p><p>广告{i}</p>\"}}}}";
            chapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
        }

        var purifier = PurifierOf(Rule(1, "广告\\d", "AD", isRegex: true));
        var engine = new BookSourceEngine(new MapFetcher(map), purifier);

        var serial = new List<ChapterContent>();
        foreach (var c in chapters)
        {
            serial.Add((await engine.GetContentAsync(source, c.ChapterUrl!, "https://api.example.com/book/1"))!);
        }

        var batch = await engine.GetContentsAsync(source, chapters, "https://api.example.com/book/1", 8, null);

        // 抓取阶段两条路径都返回原文，且彼此一致（并行的结果与串行确定性相同）
        Assert.Equal(serial.Count, batch.Count);
        for (var i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i].Content, batch[i].Content);
            Assert.Contains("广告", batch[i].Content);

            // 净化统一在渲染前做，对批量/串行同样生效
            Assert.Contains("AD", purifier.Apply(batch[i].Content, PurifyContext.Default));
        }
    }

    [Fact]
    public async Task Engine_Respects_UseReplaceRule_Context()
    {
        // 管线旁路：直接对 purifier 以 UseReplaceRule=false 调用应原文返回
        var source = JsonContentSource();
        var fetcher = new MapFetcher(new Dictionary<string, string>
        {
            ["/ch/1"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>广告</p>\"}}",
        });
        var purifier = PurifierOf(Rule(1, "广告", "X"));
        var engine = new BookSourceEngine(fetcher, purifier);

        // 抓取阶段不净化：无论净化器里有没有规则，返回的都是原文
        var content = await engine.GetContentAsync(source, "https://api.example.com/ch/1");
        Assert.Equal("<p>广告</p>", content!.Content);

        // 渲染前净化：正常上下文生效
        Assert.Equal("<p>X</p>", purifier.Apply(content.Content, PurifyContext.Default));

        // 旁路上下文（UseReplaceRule=false）不动文本
        Assert.Equal("<p>广告</p>", purifier.Apply("<p>广告</p>", new PurifyContext(UseReplaceRule: false)));
    }

    // ---------- 8. @js: 替换（对标 legado RegexExtensions.replace 的 isJs 分支） ----------

    [Fact]
    public void JsReplacement_ExpressionForm_Evaluated_Per_Match_With_Result_Bound()
    {
        // 真实规则形状（ReplaceSample.json #02 全角字符的迷你版）：三元链 + result 绑定
        var p = PurifierOf(Rule(
            1,
            "[Ａ-Ｚａ-ｚ]",
            "@js:R=result;\nR==\"ａ\"?\"a\":\nR==\"ｂ\"?\"b\":\nR",
            isRegex: true));

        Assert.Equal("ab", p.Apply("ａｂ", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_StatementForm_Uses_Completion_Value()
    {
        // 语句型（if/else）：取分支 completion 值（legado Rhino eval 语义，Jint 同）
        var p = PurifierOf(Rule(
            1,
            "第[0-9]+章",
            "@js:if (result.slice(0,1)==\"第\") { \"《\"+result+\"》\" } else { \"\" }",
            isRegex: true));

        Assert.Equal("《第12章》", p.Apply("第12章", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Sees_Each_Match_Individually()
    {
        // 逐匹配求值：同一个规则对不同匹配各自绑定 result（不是整段文本）
        var p = PurifierOf(Rule(1, "[ab]", "@js:result==\"a\"?\"A\":\"B\"", isRegex: true));

        Assert.Equal("ABBA", p.Apply("abba", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Dollar_Is_Literal_No_Group_Expansion()
    {
        // MatchEvaluator 语义：JS 返回值里的 $ 不触发分组展开
        // （legado 用 quoteReplacementJs 转义 $ 达到同样效果）
        var p = PurifierOf(Rule(1, "x", "@js:\"$1 $& end\"", isRegex: true));

        Assert.Equal("$1 $& end", p.Apply("x", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Error_Skips_Whole_Rule_No_Partial_Rewrite()
    {
        // 运行期错误（引用未定义函数）：整条规则跳过，文本保持原值
        // （legado：异常使整个 replace 调用失败；reader：try/catch 包住整条规则）
        var p = PurifierOf(
            Rule(1, "[０-９]", "@js:notDefinedFn(result)", isRegex: true),
            Rule(2, "保留", "OK"));

        // 规则 1 整体不生效（１２ 原样保留），规则 2 照常
        Assert.Equal("１２OK内容", p.Apply("１２保留内容", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Syntax_Error_Skips_Whole_Rule()
    {
        // 语法错误：同样整条跳过（编译期即失败，缓存不写坏数据）
        var p = PurifierOf(Rule(1, "a", "@js:if ( {", isRegex: true));

        Assert.Equal("a", p.Apply("a", new PurifyContext()));
    }

    [Fact]
    public void JsPrefix_On_Literal_Rule_Is_Not_Evaluated()
    {
        // 字面（isRegex=false）规则不评估 @js: —— legado 字面分支走 stdlib replace、
        // reader 走 replaceAll，两端都按字面量插入。本管线保持一致。
        var p = PurifierOf(Rule(1, "X", "@js:result+\"!\""));

        Assert.Equal("@js:result+\"!\"", p.Apply("X", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Coexists_With_Plain_Regex_Rules()
    {
        // 混合管线：@js: 规则与普通正则规则按 order 串行生效
        var p = PurifierOf(
            Rule(1, "[，,]", "@js:result==\"，\"?\"、\":\",\"", isRegex: true),
            Rule(2, "、", "·", isRegex: true));

        Assert.Equal("a·b,c", p.Apply("a，b,c", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Empty_Result_Deletes_Match()
    {
        // JS 返回空串 = 删除匹配（净化规则最常见的用法）
        var p = PurifierOf(Rule(1, "\\s*<script>[\\s\\S]*?</script>", "@js:\"\"", isRegex: true));

        Assert.Equal("前后", p.Apply("前<script>var x=1;</script>后", new PurifyContext()));
    }

    [Fact]
    public void JsReplacement_Works_Under_Concurrency()
    {
        // 引擎池 + 编译缓存必须在并行 Apply 下正确（窗口预取并发 4 / 下载并发 8）
        var p = PurifierOf(Rule(1, "[ａ-ｃ]", "@js:R=result;R==\"ａ\"?\"a\":R==\"ｂ\"?\"b\":\"c\"", isRegex: true));
        var expected = "abc";

        Parallel.For(0, 16, i =>
        {
            for (var round = 0; round < 20; round++)
            {
                Assert.Equal(expected, p.Apply("ａｂｃ", new PurifyContext()));
            }
        });
    }
}
