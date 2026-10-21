using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 换行符归一（CRLF / CR → LF）在**净化引擎**上的契约。
///
/// <para><b>为什么单独一组测试</b>：替换规则的作者（legado / reader 生态）一律假设正文是 LF，
/// 而我们读到的 TXT 往往是 CRLF。若不归一，会出现两类**静默**故障：
/// <list type="number">
/// <item><c>$</c> 收尾的分支永不命中——.NET 的 <c>$</c> 只在 <c>\n</c> 之前成立，
/// <c>^ab$</c> 对 <c>"ab\r\n"</c> 匹配不到（<c>\r</c> 挡在结尾）；</item>
/// <item>否定字符类 <c>[^\n…]</c> 把 <c>\r</c> 当普通字符吃掉，替换后行尾结构被改写
/// （真实事故：#07 标点—— 的 <c>^[^\n\w…]{4,}</c> 把行首 4 个 <c>&amp;nbsp;</c> 缩进
/// 整块替换成 <c>——</c>，4053 行中招）。</item>
/// </list></para>
/// </summary>
public class LineEndingTests
{
    // ---------- 工具 ----------

    private static ReplaceRule Rule(long id, string pattern, string replacement, bool isRegex = true, int order = 0)
        => new()
        {
            Id = id,
            Name = $"r{id}",
            Pattern = pattern,
            Replacement = replacement,
            IsRegex = isRegex,
            Order = order,
            IsEnabled = true,
        };

    private static ContentPurifier PurifierOf(params ReplaceRule[] rules)
    {
        var p = new ContentPurifier();
        p.Rules = rules;
        return p;
    }

    private static string Apply(ContentPurifier purifier, string text)
        => purifier.Apply(text, new PurifyContext(null, null, null, null));

    // ---------- 1. 归一函数本身 ----------

    [Fact]
    public void ToLf_Handles_CrLf_Cr_Lf_Mixed()
    {
        Assert.Equal("a\nb\nc", TextNormalization.ToLf("a\r\nb\rc"));
        Assert.Equal("a\nb", TextNormalization.ToLf("a\nb"));
        Assert.Equal(string.Empty, TextNormalization.ToLf(null));
        Assert.Equal(string.Empty, TextNormalization.ToLf(string.Empty));
    }

    [Fact]
    public void ToLf_AlreadyNormalized_ReturnsSameInstance()
    {
        // 热路径契约：已归一（无 \r）时返回原引用，零分配；可反复调用
        var text = "第一行\n第二行\n第三行";
        Assert.Same(text, TextNormalization.ToLf(text));
        Assert.Same(text, TextNormalization.ToLf(TextNormalization.ToLf(text)));
    }

    [Fact]
    public void ToLf_Normalizes_Other_Line_Terminators()
    {
        // LS / PS / NEL 也归一到 LF（书源 HTML 里偶见，留着会让"按行切分"少一行）
        Assert.Equal("a\nb\nc", TextNormalization.ToLf("a\u2028b\u2029c"));
        Assert.Equal("a\nb", TextNormalization.ToLf("a\u0085b"));
    }

    // ---------- 2. 净化引擎：$ 锚点分支在 CRLF 输入上必须命中 ----------

    [Fact]
    public void Purify_CrlfInput_DollarAnchoredRule_FiresOnEveryLine()
    {
        var purifier = PurifierOf(Rule(1, "^ab$", "X"));

        // 不归一的话：.NET 的 $ 落在 \n 之前，行尾还有 \r 挡着 → 一行都匹配不到
        Assert.Equal("X\nX\nX", Apply(purifier, "ab\r\nab\r\nab"));
        Assert.Equal("X\nX", Apply(purifier, "ab\rab"));
    }

    [Fact]
    public void Purify_CrlfInput_OutputHasNoCarriageReturn()
    {
        var purifier = PurifierOf(Rule(1, "^一", "1"));

        var output = Apply(purifier, "一\r\n二\r\n");

        Assert.False(TextNormalization.HasCarriageReturn(output));
        Assert.Equal("1\n二\n", output);
    }

    [Fact]
    public void Purify_NoRules_Still_Returns_Input_Verbatim()
    {
        // 无规则快路径契约不变：原样返回（换行归一是"要跑规则"时才做的事，
        // 否则关掉总开关却改了文本 = 不可逆，违反"源头存原文"铁律）
        var purifier = new ContentPurifier();
        const string text = "一\r\n二";
        Assert.Same(text, Apply(purifier, text));
    }

    // ---------- 3. 真实事故回归：#07 标点—— 不得吞掉行首缩进 ----------

    /// <summary>
    /// 用户真实规则（已修正版）：分支 2 补了 <c>\r?$</c> 行尾锚点，零宽备选已删除。
    /// 原始规则缺 <c>$</c>，把"行首任意 4 个以上非文字字符"当装饰行，
    /// 于是 4×U+00A0 的段首缩进被整块换成 <c>——</c>（真实文件 4053 行中招）。
    /// </summary>
    private const string UserRule07Pattern = """
        (?mi)(?<=^|\w[^\n\w]{0,3})(?!(?<=\d)--\d|\b——\b)([\u002d\u2010-\u2015\u2212\u2500\u2501\u268a\u268b\u2e3a\u2e3b\uff0d\uff70\uffda～~])(?:\1+|[\u2010-\u2013]+|(?<=\u2014|\u2015)(?!\d)|(?<=\w[～~])\B|(?<!\u002d)[。，、]+)|^(?:[—\–_×ꁘ]{2,}\r?$|[^\n\r\wꁘ“”【】（）…]{4,}\r?$|[■◎](?=(?:\[\w{1,4}\])?[\w\u00b7\u318d]{1,18}\r?$))
        """;

    [Fact]
    public void UserRule07_Keeps_Nbsp_Indent_Intact()
    {
        var purifier = PurifierOf(Rule(7, UserRule07Pattern, "——"));

        // 4 个 U+00A0 = 书源 &nbsp; 解码后的段首缩进（不是 U+3000）
        var text = "\u00A0\u00A0\u00A0\u00A0“儿子——”小舞猛然惊醒。\r\n" +
                   "\u00A0\u00A0\u00A0\u00A0这已经是她第多少次惊醒了。\r\n";

        var output = Apply(purifier, text);

        Assert.StartsWith("\u00A0\u00A0\u00A0\u00A0“儿子——", output);
        Assert.DoesNotContain("——\u00A0", output);
        Assert.False(TextNormalization.HasCarriageReturn(output));
        // 修正前：两行都被替换成"——缩进丢失"，行首变成 ——（本次事故的画像）
        Assert.Equal(0, output.Split('\n').Count(l => l.StartsWith("——")));
    }

    [Fact]
    public void UserRule07_Still_Normalizes_Decorator_Lines()
    {
        var purifier = PurifierOf(Rule(7, UserRule07Pattern, "——"));

        var text = "第一章\n" + new string('-', 32) + "\n正文\n" + new string('=', 32) + "\n完";

        var output = Apply(purifier, text);
        var lines = output.Split('\n');

        Assert.Equal("——", lines[1]);            // 32 个 ASCII '-' 的章分隔线
        Assert.Equal("正文", lines[2]);
        Assert.Equal("——", lines[3]);            // 32 个 '=' 的书头装饰线
    }
}
