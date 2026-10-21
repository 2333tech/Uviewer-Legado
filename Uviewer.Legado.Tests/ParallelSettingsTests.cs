using Uviewer.Services.Legado;
using Uviewer.Services.Legado.BookSources;
using Xunit;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 并行设置（搜索 / 下载并发数）的服务层测试：
/// 越界钳制、持久化回读、默认值口径（范围与 UI 提示文本一致：搜索 1–64，下载 1–16）。
/// </summary>
public class ParallelSettingsTests : IDisposable
{
    private readonly string _dir;

    public ParallelSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"legado-parallel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task Defaults_Match_Limits_And_Hint_Ranges()
    {
        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();

        // UI 提示文本「搜索 1–64 / 下载 1–16」的锚点：默认值落在范围内
        Assert.InRange(service.SearchConcurrency, 1, BookSourceLimits.SearchMaxConcurrency);
        Assert.InRange(service.DownloadConcurrency, 1, BookSourceLimits.DownloadMaxConcurrency);
        Assert.Equal(BookSourceLimits.SearchConcurrency, service.SearchConcurrency);
        Assert.Equal(BookSourceLimits.DownloadConcurrencyDefault, service.DownloadConcurrency);
    }

    [Fact]
    public async Task SetParallelism_Clamps_Out_Of_Range_Input()
    {
        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();

        // 越界钳制到边界（不抛错）：UI 侧 NumberBox 也可能被手输绕过
        var (s, d) = await service.SetParallelismAsync(0, 999);
        Assert.Equal(1, s);
        Assert.Equal(BookSourceLimits.DownloadMaxConcurrency, d);

        var (s2, d2) = await service.SetParallelismAsync(999, -5);
        Assert.Equal(BookSourceLimits.SearchMaxConcurrency, s2);
        Assert.Equal(1, d2);
    }

    [Fact]
    public async Task SetParallelism_Persists_And_Reloads()
    {
        // 写入 → 重建服务实例（重新 LoadSettings）→ 值还在
        await using (var service = new BookSourceService(_dir))
        {
            await service.EnsureInitializedAsync();
            await service.SetParallelismAsync(48, 12);
        }

        await using var reloaded = new BookSourceService(_dir);
        await reloaded.EnsureInitializedAsync();
        Assert.Equal(48, reloaded.SearchConcurrency);
        Assert.Equal(12, reloaded.DownloadConcurrency);
    }

    [Fact]
    public async Task Corrupted_Settings_Fall_Back_To_Defaults()
    {
        // 配置被手改坏（非法 JSON / 越界数字）不该让搜索下载不可用
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "legadoSettings.json"), "{ broken json");

        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();
        Assert.Equal(BookSourceLimits.SearchConcurrency, service.SearchConcurrency);
        Assert.Equal(BookSourceLimits.DownloadConcurrencyDefault, service.DownloadConcurrency);
    }

    [Fact]
    public async Task Out_Of_Range_Saved_Values_Are_Clamped_On_Load()
    {
        // 手改配置文件写越界值：加载时钳制，而不是带着非法值跑
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "legadoSettings.json"),
            """{"purifyEnabled": true, "searchConcurrency": 999, "downloadConcurrency": 0}""");

        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();
        Assert.Equal(BookSourceLimits.SearchMaxConcurrency, service.SearchConcurrency);
        Assert.Equal(1, service.DownloadConcurrency);
    }
}
