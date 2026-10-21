using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.Models;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 劣质代码清理批次的回归测试：每条用例对应一个已修复的缺陷，
/// 修复前必败（挂死 / 错值 / 抛异常），修复后通过。
/// </summary>
public class QualityFixRegressionTests
{
    // ============================================================
    // A. JsonPathEval：负步长死循环 / 并集语法 / 递归降序顺序
    // ============================================================

    [Fact]
    public void JsonPath_Negative_Step_Slice_Terminates_And_Degrades_To_Forward()
    {
        // 修复前：step=-1 时 for (k=0; k<end; k+=-1) 无限循环，整个进程挂死
        var node = JsonNode.Parse("""{"items":["a","b","c"]}""");
        var result = JsonPathEval.Query(node, "$.items[::-1]");
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void JsonPath_Negative_Step_Mid_Slice_Also_Terminates()
    {
        var node = JsonNode.Parse("""{"items":["a","b","c","d"]}""");
        var result = JsonPathEval.Query(node, "$.items[1:3:-1]");
        Assert.Equal(new[] { "b", "c" }, result);
    }

    [Fact]
    public void JsonPath_Union_Bracket_Returns_Indexed_Elements()
    {
        // 修复前：[1,2] 落入切片分支 → Slice(0, MaxValue) 返回整个数组
        var node = JsonNode.Parse("""{"items":["a","b","c"]}""");
        Assert.Equal(new[] { "a", "c" }, JsonPathEval.Query(node, "$.items[0,2]"));
        Assert.Equal(new[] { "b", "c" }, JsonPathEval.Query(node, "$.items[1,2]"));
        // 重复下标保留多重性（集合语义，与标准 JSONPath 一致）
        Assert.Equal(new[] { "c", "c" }, JsonPathEval.Query(node, "$.items[2,2]"));
    }

    [Fact]
    public void JsonPath_Recursive_Descent_Follows_Document_Order()
    {
        // 修复前：子节点正序入栈 LIFO 出栈 → 逆文档序
        var node = JsonNode.Parse("""{"a":{"n":"1"},"b":{"n":"2"},"c":{"n":"3"}}""");
        Assert.Equal(new[] { "1", "2", "3" }, JsonPathEval.Query(node, "$..n"));
    }

    // ============================================================
    // B. RuleAnalyzer：模式前缀大小写 / 空正则防护
    // ============================================================

    [Fact]
    public void StripModePrefix_Accepts_Case_Variants()
    {
        // 修复前：@Css:/@Xpath:/@REGEX: 被识别为对应模式但剥不掉前缀 →
        // 前缀残留在选择器里，QuerySelectorAll 抛异常被吞成空结果
        Assert.Equal(".a", RuleAnalyzer.StripModePrefix("@Css:.a"));
        Assert.Equal(".a", RuleAnalyzer.StripModePrefix("@CSS:.a"));
        Assert.Equal("//div", RuleAnalyzer.StripModePrefix("@XPath://div"));
        Assert.Equal("^a$", RuleAnalyzer.StripModePrefix("@Regex:^a$"));
        Assert.Equal("^a$", RuleAnalyzer.StripModePrefix("@REGEX:^a$"));
        Assert.Equal("$.a", RuleAnalyzer.StripModePrefix("@Json:$.a"));
        // js:/@js: 保持大小写敏感（与 TryDetectModeFromRule 同口径）
        Assert.Equal("1+1", RuleAnalyzer.StripModePrefix("@js:1+1"));
        Assert.Equal("@JS:1+1", RuleAnalyzer.StripModePrefix("@JS:1+1"));
    }

    [Fact]
    public void GetRegex_Empty_Pattern_Returns_Null()
    {
        // 修复前：空 pattern 合法且每个位置命中空串 → 一次求值 N+1 个空匹配
        Assert.Null(RuleAnalyzer.GetRegex(""));
        Assert.Null(RuleAnalyzer.GetRegex(null!));
        Assert.NotNull(RuleAnalyzer.GetRegex("^a$"));
    }

    // ============================================================
    // C. RuleEngine：@@ 前缀链 / @js: 尾随 ## 替换 / 嵌套重入
    // ============================================================

    private const string Html = """
        <html><body>
        <div class="item" data-id="7"><p>内层甲</p><span class="tag">A</span></div>
        <div class="item" data-id="8">第二个</div>
        <span class="tag">C</span>
        </body></html>
        """;

    private static string RunContent(BookSourceModel source, string body) =>
        new RuleEngine { Store = SourceStateStore.InMemory }.ParseContent(source, body, "https://x.example.com/book/1.html");

    private static BookSourceModel ContentSource(string jsRule) => new()
    {
        BookSourceUrl = "https://x.example.com",
        BookSourceName = "质量回归测试源",
        RuleContent = new ContentRule { Content = jsRule },
    };

    [Fact]
    public void Double_At_Prefix_Chain_Selector_Works()
    {
        // 样例里几十处 {{@@class.w-100 h-100.0@src}} 写法：@@ 前缀切出空首段，
        // SplitChain 过滤空段后按普通链求值（回归护栏）
        var rule = "@js:java.getString('@@class.tag@text')";
        Assert.Equal("A\nC", RunContent(ContentSource(rule), Html));
    }

    [Fact]
    public void Js_Rule_Applies_Trailing_Replace_Segment()
    {
        // 修复前：@js:/<js> 分支直接 return，尾随 ##pat##rep 被静默吞掉
        // （@js: 形态下 script 还会把 ## 吃进 JS 文本 → 语法错误整段回退原文）
        var atJsForm = "@js:'A' + 'C'##A##Z";
        Assert.Equal("ZC", RunContent(ContentSource(atJsForm), Html));

        var jsTagForm = "class.tag<js>result.trim()</js>##A##Z";
        Assert.Equal("Z\nC", RunContent(ContentSource(jsTagForm), Html));
    }

    [Fact]
    public void Nested_GetString_Does_Not_Corrupt_Outer_Evaluation()
    {
        // 修复前：嵌套 RunJs 覆写 EvaluateRule 委托且不恢复——外层脚本后续的
        // java.getString 全部作用到内层片段上，静默产出错误字段值
        var rule = """
            @js:
                 var inner = java.getString('class.tag<js>result.trim()</js>');
                 var after = java.getString('class.item.0@p');
                 inner + '|' + after
            """;
        Assert.Equal("A\nC|内层甲", RunContent(ContentSource(rule), Html));
    }

    [Fact]
    public void Js_Result_Null_Undefined_Become_Empty_String()
    {
        // 修复前：JsValue.Null.ToString() == "null" → 字面量写进字段值
        Assert.Equal(string.Empty, RunContent(ContentSource("@js:null"), Html));
        Assert.Equal(string.Empty, RunContent(ContentSource("@js:undefined"), Html));
        Assert.Equal(string.Empty, RunContent(ContentSource("@js:var x; x"), Html));
        Assert.Equal("v", RunContent(ContentSource("@js:'v'"), Html));
    }

    // ============================================================
    // D. JsBridge：换库重建引擎（红线 3）/ java.put 布尔口径 / JSON toString
    // ============================================================

    [Fact]
    public void Changing_JsLib_Rebuilds_Engine_No_Cross_Source_Leak()
    {
        // 修复前：库文本变化只重新 Execute，不重建引擎——旧库函数残留在引擎里，
        // 下一个书源白拿上一个书源的 jsLib 函数（跨源串味，违反红线 3）
        var engine = new RuleEngine { Store = SourceStateStore.InMemory };
        var srcA = new BookSourceModel
        {
            BookSourceUrl = "https://a.example.com",
            RuleContent = new ContentRule { Content = "@js:onlyA()" },
            JsLib = "function onlyA(){return 'A';}",
        };
        var srcB = new BookSourceModel
        {
            BookSourceUrl = "https://b.example.com",
            RuleContent = new ContentRule
            {
                Content = "@js:(typeof onlyA === 'function') ? 'LEAK:' + onlyA() : 'clean:' + other()",
            },
            JsLib = "function other(){return 'B';}",
        };

        Assert.Equal("A", engine.ParseContent(srcA, "body", "https://a.example.com/1"));
        Assert.Equal("clean:B", engine.ParseContent(srcB, "body", "https://b.example.com/1"));
    }

    [Fact]
    public void Java_Put_Get_Boolean_Uses_Js_Literal()
    {
        // 修复前：bool.ToString() = "True"（.NET 口径），与 JS/Java 的 "true" 不一致
        var rule = "@js:java.put('flag', true); java.get('flag')";
        Assert.Equal("true", RunContent(ContentSource(rule), Html));
    }

    [Fact]
    public void Json_Element_ToString_Serializes_Not_Object_Object()
    {
        // 修复前：HasOwnTextMethod 用 Get（走原型链），Object.prototype.toString
        // 恒命中 → 自定义 toString 永远挂不上，拼对象拿到 "[object Object]"
        var body = """{"a":{"x":1,"y":"z"}}""";
        var rule = "@js:'' + java.getElement('$.a')";
        Assert.Equal("""{"x":1,"y":"z"}""", RunContent(ContentSource(rule), body));
    }

    [Fact]
    public void Lang_Math_Round_And_Abs_Follow_Java_Semantics()
    {
        // round = floor(x+0.5)（Java/JS 口径，-2.5 → -2）；abs 不截断小数
        Assert.Equal("-2", RunContent(ContentSource("@js:'' + java.lang.Math.round(-2.5)"), Html));
        Assert.Equal("1.5", RunContent(ContentSource("@js:'' + java.lang.Math.abs(-1.5)"), Html));
    }

    [Fact]
    public void GetElements_Text_Node_Item_Has_Text()
    {
        // 修复前：文本节点被当元素包，text() 恒为空
        var rule = "@js:java.getStringList('class.tag@textNodes')[0] + '|' + java.getElements('class.tag@textNodes')[0].text()";
        Assert.Equal("A|A", RunContent(ContentSource(rule), Html));
    }

    // ============================================================
    // E. 存储层：损坏文件容错 / Id<=0 重分配
    // ============================================================

    [Fact]
    public async Task BookSourceManager_Load_Corrupt_File_Keeps_Data_And_Records_Error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "booksources.json");
            var good = """[{"bookSourceUrl":"https://a/","bookSourceName":"A"}]""";
            await File.WriteAllTextAsync(path, good);

            var manager = new BookSourceManager(path);
            await manager.LoadAsync();
            Assert.Equal(1, manager.Count);
            Assert.Null(manager.LastLoadError);

            // 损坏后重载：不抛、不清空、记错误（旧实现把 JsonException 冒泡到
            // 初始化链 → 缓存 faulted 任务 → 书源功能整个会话永久退化）
            await File.WriteAllTextAsync(path, "{ 这不是合法 JSON");
            await manager.LoadAsync();
            Assert.Equal(1, manager.Count);
            Assert.NotNull(manager.LastLoadError);

            // 修复文件后再加载：恢复正常（证明没有"一次损坏永久打挂"）
            await File.WriteAllTextAsync(path, good);
            await manager.LoadAsync();
            Assert.Equal(1, manager.Count);
            Assert.Null(manager.LastLoadError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TitleRuleManager_Load_Reassigns_Missing_Ids()
    {
        // 修复前：缺 id 字段的规则（默认 0）以 Id 为键互相覆盖，多条只剩一条
        var dir = Path.Combine(Path.GetTempPath(), "qfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "titleRules.json");
            await File.WriteAllTextAsync(path, """
                [
                  {"rule":"^第[0-9]+章.*$","order":10,"level":1},
                  {"rule":"^Chapter [0-9]+.*$","order":10,"level":1},
                  {"rule":"^第[0-9]+卷.*$","order":5,"level":1,"isVolume":true}
                ]
                """);

            var manager = new TitleRuleManager(path);
            await manager.LoadAsync();
            Assert.Equal(3, manager.Count);
            Assert.Equal(3, manager.GetAll().Select(r => r.Id).Distinct().Count());
            Assert.All(manager.GetAll(), r => Assert.True(r.Id > 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceRuleManager_Load_Reassigns_Missing_Ids()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "replaceRules.json");
            await File.WriteAllTextAsync(path, """
                [
                  {"pattern":"\\\\s+","replacement":" ","isRegex":true,"order":1},
                  {"pattern":"广告","replacement":"","order":2}
                ]
                """);

            var manager = new ReplaceRuleManager(path);
            await manager.LoadAsync();
            Assert.Equal(2, manager.Count);
            Assert.Equal(2, manager.GetAll().Select(r => r.Id).Distinct().Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ============================================================
    // F. LegadoHtmlText：br/结束标签变体 / 注释 / 实体解出的 CR
    // ============================================================

    [Fact]
    public void ToPlainText_Handles_Br_And_Close_Tag_Variants()
    {
        // 修复前：<br class="x"> 与 </p > 匹配不上，落入 RxAnyTag 被静默删除，段落分隔丢失
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("甲<br class=\"x\">乙"));
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("甲<BR/>乙"));
        Assert.Equal("甲\n乙", LegadoHtmlText.ToPlainText("<p>甲</p ><p>乙</p>"));
    }

    [Fact]
    public void ToPlainText_Removes_Comments_With_Inner_Gt()
    {
        // 修复前：注释里的 > 让 RxAnyTag 提前收尾，残留 "开始 -->" 碎片
        var text = LegadoHtmlText.ToPlainText("<p>前</p><!-- 正文 <b>开始</b> --><p>后</p>");
        Assert.Equal("前\n后", text);
        Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPlainText_Normalizes_Cr_From_Entities()
    {
        // 修复前：&#13; 在 HtmlDecode 之后才解出 \r，后续环节假定 LF-only
        var text = LegadoHtmlText.ToPlainText("<p>甲&#13;乙</p>");
        Assert.Equal("甲\n乙", text);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    // ============================================================
    // G. ContentPurifier：探针与管线的内置噪音口径一致
    // ============================================================

    [Fact]
    public void FindEffectiveRuleIds_Matches_Apply_Pipeline_For_Builtin_Noise()
    {
        // 修复前：探针不剥内置噪音行——针对"分卷阅读"行的用户规则会出现
        // 「生效标记亮着但实际不生效」的错位（Apply 先剥噪音，规则永不着陆）
        var purifier = new ContentPurifier
        {
            Rules = new[]
            {
                new ReplaceRule
                {
                    Id = 1,
                    Pattern = @"^[ \t　]*分卷阅读[0-9]{0,6}[ \t　]*\r?\n?",
                    Replacement = string.Empty,
                    IsRegex = true,
                    IsEnabled = true,
                    Order = 1,
                },
            },
        };
        var ctx = new PurifyContext();

        var text = "分卷阅读123\n正文第一行";
        Assert.Equal("正文第一行", purifier.Apply(text, ctx));
        Assert.DoesNotContain(1L, purifier.FindEffectiveRuleIds(text, ctx));
    }

    // ============================================================
    // H. TxtChapterSplitter：英文 Section 前缀不再被前置筛选漏掉
    // ============================================================

    [Fact]
    public void Splitter_Prefilter_Admits_Section_Headings()
    {
        // 修复前：MaybeHeading 不含 S/s——内置默认规则认 section 却永远走不到正则
        var result = TxtChapterSplitter.Split(
            "Section 1 开端\n内容甲\nSection 2 发展\n内容乙",
            TxtChapterSplitter.CreateDefaultRules());
        Assert.Equal(2, result.Chapters.Count);
        Assert.Equal("Section 1 开端", result.Chapters[0].Title);
        Assert.Equal("Section 2 发展", result.Chapters[1].Title);
    }
}
