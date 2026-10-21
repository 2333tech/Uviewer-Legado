using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 0 验收：书源 JSON 三端互通（能被解析且字段无丢失）、序列化往返无损失、管理器导入/落盘/重载。
/// 后续阶段把"黄金比对集"接入此测试基类即可扩展。
/// </summary>
public class ModelRoundTripTests
{
    private static string SampleJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SampleBookSource.json"));

    [Fact]
    public void Deserialize_Preserves_All_Key_Fields()
    {
        var src = BookSourceSerializer.DeserializeBookSource(SampleJson)!;

        Assert.Equal("https://example.com", src.BookSourceUrl);
        Assert.Equal("示例书源", src.BookSourceName);
        Assert.Equal(0, src.BookSourceType);
        Assert.True(src.IsEnabled);
        Assert.Equal("0", src.ConcurrentRate);

        Assert.NotNull(src.RuleSearch);
        Assert.Equal("class.book-item", src.RuleSearch!.BookList);
        Assert.Equal("class.cover@src", src.RuleSearch.CoverUrl);

        Assert.NotNull(src.RuleBookInfo);
        Assert.Equal("class.toc@href", src.RuleBookInfo!.TocUrl);

        Assert.NotNull(src.RuleToc);
        Assert.Equal("class.next@href", src.RuleToc!.NextTocUrl);

        Assert.NotNull(src.RuleContent);
        Assert.Equal("id.content@html", src.RuleContent!.Content);
        Assert.Equal("full", src.RuleContent.ImageStyle);
    }

    [Fact]
    public void RoundTrip_Does_Not_Lose_Fields()
    {
        var src = BookSourceSerializer.DeserializeBookSource(SampleJson)!;
        var json = BookSourceSerializer.Serialize(src);
        var again = BookSourceSerializer.DeserializeBookSource(json)!;

        Assert.Equal(src.BookSourceUrl, again.BookSourceUrl);
        Assert.Equal(src.RuleSearch!.Name, again.RuleSearch!.Name);
        Assert.Equal(src.RuleContent!.Content, again.RuleContent!.Content);
        Assert.Equal(src.RuleContent.ImageStyle, again.RuleContent.ImageStyle);
    }

    [Fact]
    public async Task Manager_Import_Then_Load_Persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsm_test_{Guid.NewGuid():N}.json");
        try
        {
            var mgr = new BookSourceManager(path);
            await mgr.ImportFromJsonAsync(SampleJson);

            var all = mgr.GetAll();
            Assert.Single(all);
            Assert.Equal("https://example.com", all[0].BookSourceUrl);

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Single(reloaded.GetAll());
            Assert.Equal("示例书源", reloaded.GetAll()[0].BookSourceName);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Manager_Upsert_Replaces_By_Url()
    {
        var mgr = new BookSourceManager(Path.Combine(Path.GetTempPath(), $"bsm_up_{Guid.NewGuid():N}.json"));
        var a = BookSourceSerializer.DeserializeBookSource(SampleJson)!;
        mgr.Upsert(a);

        var b = BookSourceSerializer.DeserializeBookSource(SampleJson)!;
        b.BookSourceName = "改名后";
        mgr.Upsert(b);

        Assert.Single(mgr.GetAll());
        Assert.Equal("改名后", mgr.GetAll()[0].BookSourceName);
    }

    // ---- Sample/ 目录下真实书源导出文件测试（4.5MB 真实数据）----

    /// <summary>
    /// 从测试程序集输出目录向上回溯，定位仓库根目录下的 Sample/bookSource_202610192010.json。
    /// 不把 4.5MB 文件复制进 bin，避免膨胀构建产物。
    /// </summary>
    private static string FindSampleFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Sample", "bookSource_202610192010.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("未在仓库中找到 Sample/bookSource_202610192010.json");
    }

    [Fact]
    public void SampleFile_Deserializes_To_NonEmpty_List()
    {
        var json = File.ReadAllText(FindSampleFile());
        var list = BookSourceSerializer.DeserializeBookSourceList(json);

        Assert.NotEmpty(list);
        var first = list[0];
        Assert.Equal("https://api-x.shrtxs.cn/qd/", first.BookSourceUrl);
        Assert.Equal("起点读书（优+++）", first.BookSourceName);
        Assert.NotNull(first.RuleSearch);
        Assert.NotNull(first.RuleBookInfo);
        Assert.NotNull(first.RuleToc);
        Assert.NotNull(first.RuleContent);
    }

    [Fact]
    public async Task SampleFile_Import_Persists_And_Reloads()
    {
        var json = await File.ReadAllTextAsync(FindSampleFile());

        var path = Path.Combine(Path.GetTempPath(), $"bsm_sample_{Guid.NewGuid():N}.json");
        try
        {
            var mgr = new BookSourceManager(path);
            await mgr.ImportFromJsonAsync(json);

            var all = mgr.GetAll();
            Assert.True(all.Count > 0, "导入后应有书源");
            Assert.All(all, s => Assert.False(string.IsNullOrEmpty(s.BookSourceUrl)));

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(all.Count, reloaded.GetAll().Count);

            var entry = reloaded.GetAll()
                .FirstOrDefault(s => s.BookSourceUrl == "https://api-x.shrtxs.cn/qd/");
            Assert.NotNull(entry);
            Assert.Equal("起点读书（优+++）", entry!.BookSourceName);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
