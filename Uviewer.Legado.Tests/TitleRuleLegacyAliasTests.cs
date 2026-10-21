using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 标题规则的 legado 旧键名迁移（TXTTopicRule.json：rule / enable / serialNumber）。
/// <para>不迁移的后果是整份文件解析成"空正则 + 停用"的垃圾数据、导入被静默跳过——
/// 用户完全看不出所以然，故这里把三个键的映射与回写策略逐条锁死。</para>
/// </summary>
public class TitleRuleLegacyAliasTests
{
    private const string LegadoJson = """
[
  {
    "enable": true,
    "example": "匹配原网络书籍目录",
    "id": 1712187373170,
    "name": "全局完整规则①",
    "rule": "^第[0-9]+节.{0,35}$",
    "serialNumber": -7
  },
  {
    "enable": false,
    "id": -2,
    "name": "目录",
    "rule": "^[ 　]{0,4}第.{0,30}$",
    "serialNumber": 1
  }
]
""";

    [Fact]
    public void Deserialize_Maps_Legado_Keys_To_Model()
    {
        var rules = BookSourceSerializer.DeserializeTitleRuleList(LegadoJson);

        Assert.Equal(2, rules.Count);

        Assert.Equal(1712187373170, rules[0].Id);
        Assert.Equal("全局完整规则①", rules[0].Name);
        Assert.Equal("^第[0-9]+节.{0,35}$", rules[0].Pattern);
        Assert.True(rules[0].IsEnabled);
        Assert.Equal(-7, rules[0].Order);

        Assert.Equal("^[ 　]{0,4}第.{0,30}$", rules[1].Pattern);
        Assert.False(rules[1].IsEnabled);
        Assert.Equal(1, rules[1].Order);
    }

    [Fact]
    public void Aliases_Are_Nulled_After_Migration_So_They_Never_RoundTrip()
    {
        var rules = BookSourceSerializer.DeserializeTitleRuleList(LegadoJson);

        foreach (var rule in rules)
        {
            Assert.Null(rule.LegacyRule);
            Assert.Null(rule.LegacyEnable);
            Assert.Null(rule.LegacySerialNumber);
        }

        // 回写只输出本模型键名（WhenWritingNull 保证别名不出现）
        var json = JsonSerializer.Serialize(rules, BookSourceJsonContext.Default.ListTitleRule);
        Assert.DoesNotContain("\"rule\":", json);
        Assert.DoesNotContain("\"enable\"", json);
        Assert.DoesNotContain("serialNumber", json);
        Assert.Contains("\"pattern\"", json);
        Assert.Contains("\"isEnabled\"", json);
        Assert.Contains("\"order\"", json);
    }

    [Fact]
    public void Model_Keys_Win_When_Both_Present()
    {
        // move_if_absent 语义：pattern 有值时忽略 rule；enable/serialNumber 显式覆盖
        const string both = """
[{"pattern":"^MODEL$","rule":"^LEGACY$","enable":false,"isEnabled":true,"serialNumber":99,"order":5}]
""";
        var rule = BookSourceSerializer.DeserializeTitleRuleList(both)[0];

        Assert.Equal("^MODEL$", rule.Pattern);
        Assert.False(rule.IsEnabled);   // legado enable 显式给了值 → 以它为准
        Assert.Equal(99, rule.Order);
    }

    [Fact]
    public async Task Import_Legado_File_Through_Manager()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legado-title-rules-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, LegadoJson);

        try
        {
            var manager = new TitleRuleManager(Path.Combine(Path.GetTempPath(), $"title-rules-{System.Guid.NewGuid():N}.json"));
            var result = await manager.ImportFromFileAsync(path);

            // 两条都有正则，都应入库（未被当垃圾跳过）
            Assert.Equal(2, result.Added);
            Assert.Equal(0, result.Skipped);

            var all = manager.GetAll();
            Assert.Contains(all, r => r.Pattern == "^第[0-9]+节.{0,35}$" && r.IsEnabled && r.Order == -7);
            Assert.Contains(all, r => r.Pattern == "^[ 　]{0,4}第.{0,30}$" && !r.IsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_File_Then_Split_Produces_Chapters()
    {
        // 端到端：legado 键名的规则直接进切分器就能用（不再需要手工转换）
        var rules = BookSourceSerializer.DeserializeTitleRuleList(LegadoJson);
        var enabled = new List<TitleRule>();
        foreach (var r in rules)
        {
            if (r.IsEnabled)
            {
                enabled.Add(r);
            }
        }

        var split = TxtChapterSplitter.Split("前言几句\n第1节 开局\n正文第一段\n第2节 发展\n正文第二段", enabled);

        Assert.Equal(2, split.Chapters.Count);
        Assert.Equal("第1节 开局", split.Chapters[0].Title);
        Assert.Equal("第2节 发展", split.Chapters[1].Title);
    }
}
