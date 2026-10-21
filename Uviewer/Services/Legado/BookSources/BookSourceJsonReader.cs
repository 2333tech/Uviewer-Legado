using System.Text.Json;
using Uviewer.Services.Legado.Models;
// 根命名空间为 Uviewer.Services.Legado，全局隐式存在命名空间 "BookSource"，
// 故类型 BookSource 不能用简单名（CS0118），改用别名（遵循项目约定）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 JSON 的零 DOM 读取器（阶段 0 性能核心）。
/// <para>两趟扫描：</para>
/// <list type="number">
/// <item>结构性扫描——用 Utf8JsonReader 只走 token、不建任何对象，切出每个顶层元素的字节切片；</item>
/// <item>各切片独立反序列化——切片互不重叠且只读，可安全并行。</item>
/// </list>
/// 相比原先 <c>JsonNode.Parse(整份 JSON)</c>：
/// 内存从「整文件 DOM（可达数倍于文件）」降至「输入字节 + 结果对象」，
/// 且反序列化阶段可吃满多核。小输入（&lt; 64KB 或 &lt; 8 条）自动退回串行，避免并行调度开销。
/// </summary>
internal static class BookSourceJsonReader
{
    /// <summary>并行下限：少于 8 个元素不走并行（调度开销大于收益）。</summary>
    private const int ParallelMinElements = 8;

    /// <summary>并行下限：小于 64KB 不走并行（小文件串行更快）。</summary>
    private const int ParallelMinBytes = 64 * 1024;

    /// <summary>顶层元素的字节切片（相对扫描起点的 [Start, Start+Length)）。</summary>
    private readonly record struct Slice(int Start, int Length);

    /// <summary>
    /// 解析 UTF-8 书源 JSON（数组或单个对象），返回去重前的原始顺序列表。
    /// <para>步骤：① 剥 BOM；② 结构性扫描切元素切片；③ 按规模选并行/串行反序列化
    ///（结果按切片下标写定长数组 ⇒ 输出顺序与并行度无关，确定性）；
    /// ④ 过滤 null（理论上不会出现，防御性）按序合并为列表。</para>
    /// </summary>
    public static List<BookSourceModel> ParseUtf8(ReadOnlyMemory<byte> utf8)
    {
        // ① 剥 UTF-8 BOM（Windows 编辑器常带上，STJ 不认）
        var buffer = SkipBom(utf8);
        // ② 结构性扫描：只 tokenize，切出每个顶层元素的 [start, length)
        var slices = ScanTopLevel(buffer.Span);
        if (slices.Count == 0)
        {
            return new List<BookSourceModel>();
        }

        // 结果按切片下标写入定长数组：并行写入互不冲突，也无需加锁的收集容器。
        var results = new BookSourceModel?[slices.Count];
        if (slices.Count >= ParallelMinElements && buffer.Length >= ParallelMinBytes)
        {
            // ③a 大输入：Parallel.For 并行反序列化（每元素独立，吃满多核）
            Parallel.For(0, slices.Count, i => results[i] = DeserializeOne(buffer, slices[i]));
        }
        else
        {
            // ③b 小输入：串行（避免并行调度开销）
            for (var i = 0; i < slices.Count; i++)
            {
                results[i] = DeserializeOne(buffer, slices[i]);
            }
        }

        // ④ 按序合并（跳过 null —— 理论不可达，防御性）
        var list = new List<BookSourceModel>(slices.Count);
        foreach (var item in results)
        {
            if (item is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// 反序列化单个元素切片（并行安全：切片只读，源生成上下文为无状态单例）。
    /// 反序列化后立即做旧键名迁移（LegacyAlias.Migrate，零 DOM）。
    /// </summary>
    private static BookSourceModel? DeserializeOne(ReadOnlyMemory<byte> utf8, Slice slice)
    {
        var source = JsonSerializer.Deserialize(
            utf8.Span.Slice(slice.Start, slice.Length),
            BookSourceJsonContext.Default.BookSource);

        if (source is null)
        {
            return null;
        }

        LegacyAlias.Migrate(source);
        return source;
    }

    /// <summary>
    /// 切出顶层数组/单个对象里的元素切片。只 tokenize，不建对象。
    /// <para>形态处理：① 顶层是数组 → 逐元素：StartObject 切元素；
    /// 嵌套 StartArray 整体 Skip（书源数组里不该有嵌套数组，跳过保持 reader 配对）；
    /// 其他 token（标量/null）忽略——它们不是书源对象。② 顶层是单个对象 → 整体作为一个元素
    ///（单书源导入场景）。③ 其他（空输入/标量）→ 空列表。</para>
    /// <para>reader 选项：AllowTrailingCommas + Skip 注释（legado 导出文件偶有尾逗号/注释）。</para>
    /// </summary>
    private static List<Slice> ScanTopLevel(ReadOnlySpan<byte> utf8)
    {
        var result = new List<Slice>();
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (!reader.Read())
        {
            return result;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    ScanObject(ref reader, result);
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    // 嵌套数组不是书源对象：整段跳过，保持 reader 停在配对 EndArray
                    reader.Skip();
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            ScanObject(ref reader, result);
        }

        return result;
    }

    /// <summary>
    /// 记录一个对象的字节切片：起点 = StartObject 的 TokenStartIndex；
    /// 用 Skip() 一路走到配对 EndObject（只走 token 不建对象），
    /// 终点 = EndObject 的 TokenStartIndex + 1（含收尾 }）。
    /// </summary>
    private static void ScanObject(ref Utf8JsonReader reader, List<Slice> result)
    {
        var start = (int)reader.TokenStartIndex;
        reader.Skip(); // 跳过子元素：只走 token，不建对象；调用后 reader 停在配对 EndObject
        var end = (int)reader.TokenStartIndex + 1;
        result.Add(new Slice(start, end - start));
    }

    /// <summary>剥 UTF-8 BOM（EF BB BF）：有则切掉前 3 字节，无则原样。</summary>
    private static ReadOnlyMemory<byte> SkipBom(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length >= 3)
        {
            var head = utf8.Span;
            if (head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return utf8.Slice(3);
            }
        }

        return utf8;
    }

    /// <summary>
    /// 打开用于流式读取的文件（异步 I/O + 顺序扫描提示，跳过 UTF-8 BOM）。
    /// <para>BOM 处理：先同步读 3 字节嗅探，不是 BOM 则 Position 归零
    ///（FileStream 支持 Seek；BOM 情形保持位置 3 = 跳过）。</para>
    /// <para>BufferSize=0：STJ 自带缓冲区，避免双重缓冲导致的额外内存占用。</para>
    /// </summary>
    public static FileStream OpenRead(string path)
    {
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        var head = new byte[3];
        var read = stream.Read(head, 0, 3);
        if (!(read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF))
        {
            stream.Position = 0;
        }

        return stream;
    }
}
