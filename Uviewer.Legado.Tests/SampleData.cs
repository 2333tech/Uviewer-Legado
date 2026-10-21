using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
// 根命名空间 Uviewer.Services.Legado 全局隐式建立 "BookSource" 命名空间，
// 类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// Sample/ 真实书源导出文件（4.69MB / 408 条）的测试取数助手。
/// 统一在这里做「向上回溯定位 + 只取部分内容」，各测试文件不再重复实现。
/// </summary>
internal static class SampleData
{
    private const string FileName = "bookSource_202610192010.json";

    /// <summary>样例全量条目数（当前导出为 408）。</summary>
    public const int TotalCount = 408;

    public static string FilePath { get; } = Find();

    /// <summary>样例中第一本书源的主键（用于顺序/字段断言）。</summary>
    public const string FirstUrl = "https://api-x.shrtxs.cn/qd/";

    public const string FirstName = "起点读书（优+++）";

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Sample", FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"未在仓库中找到 Sample/{FileName}");
    }

    /// <summary>流式枚举样例（边读边反序列化，不展开整份 DOM）。</summary>
    public static async IAsyncEnumerable<BookSourceModel> EnumerateAsync(
        int take = int.MaxValue, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(FilePath);
        await foreach (var source in BookSourceSerializer.EnumerateBookSourcesAsync(stream, cancellationToken))
        {
            yield return source;
            if (--take <= 0)
            {
                break;
            }
        }
    }

    /// <summary>取前 <paramref name="take"/> 条完整书源对象（部分内容）。</summary>
    public static async Task<List<BookSourceModel>> SliceAsync(int take)
    {
        var list = new List<BookSourceModel>(take);
        await foreach (var source in EnumerateAsync(take))
        {
            list.Add(source);
        }

        return list;
    }

    /// <summary>
    /// 取前 <paramref name="take"/> 条并重新序列化为一个 JSON 数组文本，
    /// 用于测试「内存中的 JSON 字符串」导入路径（大文件只取部分）。
    /// </summary>
    public static async Task<string> SliceJsonAsync(int take) =>
        BookSourceSerializer.SerializeList(await SliceAsync(take));
}
