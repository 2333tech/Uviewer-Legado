using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 阶段 0（模型 + 存储）优化后的验收测试。
/// 数据取自 Sample/ 下的真实书源导出（4.69MB / 408 条），默认只取部分内容（前 N 条）以控制耗时。
/// 覆盖：流式读取、并行导入、按 URL 去重、顺序保持、原子落盘、旧字段迁移、并发安全。
/// </summary>
public class Phase0StorageTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"bsm_{Guid.NewGuid():N}.json");

    // ---- 1. 流式部分读取 ----

    [Fact]
    public async Task Streaming_Enumeration_Takes_Only_Part_Of_Sample()
    {
        var slice = await SampleData.SliceAsync(40);

        Assert.Equal(40, slice.Count);
        Assert.All(slice, s => Assert.False(string.IsNullOrWhiteSpace(s.BookSourceUrl)));
        Assert.Equal(SampleData.FirstUrl, slice[0].BookSourceUrl);
        Assert.Equal(SampleData.FirstName, slice[0].BookSourceName);
        Assert.NotNull(slice[0].RuleSearch);
        Assert.NotNull(slice[0].RuleBookInfo);
    }

    // ---- 2. 内存 JSON 并行导入 + 落盘 + 重载 ----

    [Fact]
    public async Task Import_Slice_From_Json_Persists_And_Reloads()
    {
        var path = TempFile();
        try
        {
            var slice = await SampleData.SliceAsync(60);
            var expectedBookList = slice[0].RuleSearch!.BookList;
            var json = BookSourceSerializer.SerializeList(slice);
            var mgr = new BookSourceManager(path);
            var result = await mgr.ImportFromJsonAsync(json);

            Assert.Equal(60, result.Added);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(60, mgr.Count);
            // 顺序 = 原文顺序
            Assert.Equal(SampleData.FirstUrl, mgr.GetAll()[0].BookSourceUrl);

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();

            Assert.Equal(60, reloaded.Count);
            Assert.Equal(SampleData.FirstUrl, reloaded.GetAll()[0].BookSourceUrl);
            Assert.Equal(SampleData.FirstName, reloaded.GetAll()[0].BookSourceName);
            // 规则子对象完整保留
            var first = reloaded.GetAll()[0];
            Assert.NotNull(first.RuleSearch);
            Assert.NotNull(first.RuleBookInfo);
            Assert.NotNull(first.RuleToc);
            Assert.NotNull(first.RuleContent);
            Assert.Equal(expectedBookList, first.RuleSearch!.BookList);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 3. 大文件流式导入（全量，但走流式路径）----

    [Fact]
    public async Task Import_Whole_Sample_File_Streaming_Imports_All()
    {
        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            var result = await mgr.ImportFromFileAsync(SampleData.FilePath);

            Assert.Equal(SampleData.TotalCount, result.Added);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.Updated);
            Assert.Equal(SampleData.TotalCount, mgr.Count);

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(SampleData.TotalCount, reloaded.Count);
            Assert.Equal(SampleData.FirstUrl, reloaded.GetAll()[0].BookSourceUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 4. 去重与顺序稳定性 ----

    [Fact]
    public async Task Import_Same_Slice_Twice_Updates_In_Place()
    {
        var path = TempFile();
        try
        {
            var json = await SampleData.SliceJsonAsync(50);
            var mgr = new BookSourceManager(path);

            var first = await mgr.ImportFromJsonAsync(json);
            Assert.Equal(50, first.Added);

            var second = await mgr.ImportFromJsonAsync(json);
            Assert.Equal(0, second.Added);
            Assert.Equal(50, second.Updated);
            Assert.Equal(50, mgr.Count);

            // 覆盖后位置不变，顺序仍是原文顺序
            Assert.Equal(SampleData.FirstUrl, mgr.GetAll()[0].BookSourceUrl);

            // 单条 Upsert 覆盖同样保留位置
            var updated = (await SampleData.SliceAsync(1))[0];
            updated.BookSourceName = "改名验证";
            Assert.True(mgr.Upsert(updated));
            Assert.Equal(50, mgr.Count);
            Assert.Equal("改名验证", mgr.GetAll()[0].BookSourceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 5. 数据安全：无主键条目被跳过 ----

    [Fact]
    public async Task Import_Skips_Entries_Without_Url()
    {
        const string json = """
        [
          {"bookSourceUrl":"https://a.example.com/","bookSourceName":"A"},
          {"bookSourceName":"无主键"},
          {"bookSourceUrl":"   ","bookSourceName":"空白主键"},
          {"bookSourceUrl":"https://b.example.com/","bookSourceName":"B"}
        ]
        """;

        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            var result = await mgr.ImportFromJsonAsync(json);

            Assert.Equal(2, result.Added);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(2, mgr.Count);
            // 空主键若入库会互相覆盖成一条，跳过才是安全的
            Assert.All(mgr.GetAll(), s => Assert.False(string.IsNullOrWhiteSpace(s.BookSourceUrl)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 6. 旧字段别名迁移（且旧键不回写） ----

    [Fact]
    public async Task Legacy_Alias_Keys_Migrate_And_Never_Write_Back()
    {
        const string json = """
        {"bookSourceUrl":"https://legacy.example.com/","bookSourceName":"旧字段源",
         "enable":false,"serialNumber":7,
         "ruleSearchUrl":"https://legacy.example.com/s?k={{key}}",
         "ruleFindUrl":"https://legacy.example.com/find",
         "ruleBookUrlPattern":"https://legacy.example.com/book/.*"}
        """;

        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            var result = await mgr.ImportFromJsonAsync(json);
            Assert.Equal(1, result.Added);

            var stored = mgr.GetAll()[0];
            Assert.False(stored.IsEnabled);
            Assert.False(stored.Enabled!.Value);
            Assert.Equal(7, stored.CustomOrder);
            Assert.Equal("https://legacy.example.com/s?k={{key}}", stored.SearchUrl);
            Assert.Equal("https://legacy.example.com/find", stored.ExploreUrl);
            Assert.Equal("https://legacy.example.com/book/.*", stored.BookUrlPattern);

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("\"enabled\": false", text);
            Assert.DoesNotContain("\"enable\":", text);
            Assert.DoesNotContain("serialNumber", text);
            Assert.DoesNotContain("ruleSearchUrl", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 7. 落盘原子性：无临时文件残留、内容可解析 ----

    [Fact]
    public async Task Save_Writes_Atomically_Without_Temp_Leftover()
    {
        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            await mgr.ImportFromJsonAsync(await SampleData.SliceJsonAsync(30));
            await mgr.SaveAsync();
            await mgr.SaveAsync();

            Assert.False(File.Exists(path + ".tmp"));
            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(30, reloaded.Count);

            // 文件缺失时加载清空（保留原语义）
            File.Delete(path);
            await reloaded.LoadAsync();
            Assert.Empty(reloaded.GetAll());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 8. 并发读写 + 并发保存 ----

    [Fact]
    public async Task Concurrent_Reads_Writes_Saves_Are_ThreadSafe()
    {
        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            var slice = await SampleData.SliceAsync(60);
            foreach (var s in slice)
            {
                mgr.Upsert(s);
            }

            var errors = new ConcurrentQueue<Exception>();
            var tasks = new List<Task>();

            // 8 个读者：并发遍历快照 / 按 URL 查 / 计数
            for (var t = 0; t < 8; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (var i = 0; i < 200; i++)
                        {
                            var all = mgr.GetAll();
                            var enabled = mgr.GetEnabled();
                            var one = all[i % all.Count];
                            Assert.NotNull(mgr.GetByUrl(one.BookSourceUrl));
                            Assert.True(mgr.Contains(one.BookSourceUrl));
                            // 写者并发新增中，数量只下界确定
                            Assert.True(mgr.Count >= 60);
                            // all 与 enabled 是两次独立快照读取，写者并发插入时后者可能来自更新的快照，
                            // 跨快照比较大小会 flaky（项目教训：并发测试只用下界/单向断言）。
                            // 本测试写者只增不删，故"后读的 Count >= 先读的快照大小"恒成立。
                            Assert.True(enabled.Count <= mgr.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }));
            }

            // 4 个写者：并发 Upsert 新源 + 并发保存
            for (var w = 0; w < 4; w++)
            {
                var index = w;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 25; i++)
                        {
                            mgr.Upsert(new BookSourceModel
                            {
                                BookSourceUrl = $"https://w{index}.example.com/{i}",
                                BookSourceName = $"并发源{index}-{i}",
                            });
                        }

                        await mgr.SaveAsync();
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Empty(errors);
            // 60 条样例 + 4×25 条并发新增
            Assert.Equal(160, mgr.Count);

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(160, reloaded.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 9. 并发导入同一份数据：去重结果确定 ----

    [Fact]
    public async Task Concurrent_Imports_Of_Same_Slice_Stay_Consistent()
    {
        var path = TempFile();
        try
        {
            var json = await SampleData.SliceJsonAsync(30);
            var mgr = new BookSourceManager(path);

            var results = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => mgr.ImportFromJsonAsync(json))));

            // 每次导入对每条书源非增即改，总和恒定
            Assert.Equal(120, results.Sum(r => r.Total));
            Assert.Equal(30, mgr.Count);

            var reloaded = new BookSourceManager(path);
            await reloaded.LoadAsync();
            Assert.Equal(30, reloaded.Count);
            Assert.Equal(SampleData.FirstUrl, reloaded.GetAll()[0].BookSourceUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 10. 遍历快照期间修改集合不抛异常 ----

    [Fact]
    public async Task Snapshot_Enumeration_Is_Isolated_From_Mutation()
    {
        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            await mgr.ImportFromJsonAsync(await SampleData.SliceJsonAsync(20));

            var snapshot = mgr.GetAll();
            var before = snapshot.Count;

            for (var i = 0; i < 50; i++)
            {
                mgr.Upsert(new BookSourceModel
                {
                    BookSourceUrl = $"https://mut.example.com/{i}",
                    BookSourceName = "遍历中新增",
                });
            }

            // 旧快照不受影响
            Assert.Equal(before, snapshot.Count);
            Assert.Equal(70, mgr.GetAll().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 11. 删除 / 启用开关 ----

    [Fact]
    public async Task Remove_And_SetEnabled_Work_Through_Snapshot()
    {
        var path = TempFile();
        try
        {
            var mgr = new BookSourceManager(path);
            await mgr.ImportFromJsonAsync(await SampleData.SliceJsonAsync(10));

            var url = mgr.GetAll()[0].BookSourceUrl;
            Assert.True(mgr.Remove(url));
            Assert.False(mgr.Remove(url));
            Assert.Equal(9, mgr.Count);
            Assert.Null(mgr.GetByUrl(url));

            var kept = mgr.GetAll()[0].BookSourceUrl;
            await mgr.SetEnabledAsync(kept, false);
            Assert.False(mgr.GetByUrl(kept)!.IsEnabled);
            Assert.DoesNotContain(kept, mgr.GetEnabled().Select(s => s.BookSourceUrl));

            await mgr.SetEnabledAsync(kept, true);
            Assert.True(mgr.GetByUrl(kept)!.IsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- 12. 性能护栏：全量导入在时间预算内 ----

    [Fact]
    public async Task Import_Whole_Sample_Completes_Within_Budget()
    {
        var path = TempFile();
        try
        {
            var json = await File.ReadAllTextAsync(SampleData.FilePath);
            var mgr = new BookSourceManager(path);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await mgr.ImportFromJsonAsync(json);
            sw.Stop();

            Assert.Equal(SampleData.TotalCount, mgr.Count);
            // 宽松上限：只防止灾难性退化（CI 机器差异大）
            Assert.True(sw.ElapsedMilliseconds < 15_000, $"全量导入耗时 {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
