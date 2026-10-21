using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 阅读进度存储（断点续读的数据层；对标 legado readRecord / reader 前端阅读进度）。
/// <para>文件：<c>%LocalAppData%/Uviewer/readingProgress.json</c>，与
/// <c>booksources.json</c> / <c>replaceRules.json</c> 同目录同约定。</para>
/// <para>架构与 <see cref="BookSourceManager"/> 一致（阶段 0 定下的存储范式，务必保持）：</para>
/// ① 存储 = <see cref="ConcurrentDictionary{TKey,TValue}"/>（键 = <c>书源URL\u0001书籍URL</c>，
/// <see cref="StringComparer.Ordinal"/>），按主键 O(1)，读路径无锁；
/// ② 读 = 惰性不可变快照（<see cref="Positions"/> 一次构建，<c>volatile</c> 字段；
/// 变更时只在短锁内置 null，下次读重建）——**遍历快照时可被并发修改**；
/// ③ 落盘 = 直写流 + <c>.tmp</c> 临时文件 + <c>File.Move(overwrite)</c> 原子替换，
/// 并发保存由 <see cref="SemaphoreSlim"/> 串行化；
/// ④ 缺失文件 = 空存储（首次运行正常）。
/// </summary>
public sealed class BookSourceReadingStore
{
    /// <summary>进度记录主键分隔符（U+0001：URL 文本里不可能出现的控制字符）。</summary>
    private const char KeySeparator = '\u0001';

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, ReadingPosition> _positions = new(StringComparer.Ordinal);

    /// <summary>
    /// 变更短锁：加载的 Clear+重填与并发 Upsert/Remove/Clear 都在此串行化。
    /// <para>没有它，加载期的 <c>_positions.Clear()</c> 会抹掉并发写入的进度
    /// （或被随后灌入的旧值覆盖——读-改-写无锁的经典丢更新）。读路径不参与。</para>
    /// </summary>
    private readonly object _gate = new();

    // 惰性快照：变更只置 null，下次读重建（与 BookSourceManager 同范式）
    private volatile IReadOnlyList<ReadingPosition>? _snapshot;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>最近一次加载失败的原因（损坏文件等；null = 上次加载正常）。诊断用。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>进度文件路径（<c>%LocalAppData%/Uviewer/readingProgress.json</c>）。</summary>
    public string FilePath => _filePath;

    /// <summary>当前记录数（并发写时只保证不小于最近一次观测值，测试用下界断言）。</summary>
    public int Count => _positions.Count;

    public BookSourceReadingStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uviewer", "readingProgress.json");
    }

    /// <summary>记录主键（书源 URL + 书籍 URL；两者共同决定"同一本书的同一份进度"）。</summary>
    public static string MakeKey(string bookSourceUrl, string bookUrl) => bookSourceUrl + KeySeparator + bookUrl;

    /// <summary>
    /// 从磁盘加载（流式反序列化，不在内存展开 DOM）。
    /// 文件缺失 / 内容为空 / JSON 非法 → 空存储（首启与损坏文件都不抛）。
    /// <para>损坏文件先隔离为 <c>.corrupt</c> 再降级：否则下一次 <see cref="SaveAsync"/>
    /// 的原子替换会把损坏的原文直接覆盖掉，用户丢失全部进度且现场无保留
    /// （与 <see cref="SourceStateStore"/> 的隔离策略一致）。</para>
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        List<ReadingPosition> parsed;
        if (!File.Exists(_filePath))
        {
            parsed = new List<ReadingPosition>();
            LastLoadError = null;
        }
        else
        {
            try
            {
                await using var stream = BookSourceJsonReader.OpenRead(_filePath);
                parsed = await BookSourceSerializer.DeserializeReadingPositionListAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                LastLoadError = null;
            }
            catch (Exception ex)
            {
                // 损坏文件不阻塞阅读器：隔离原文件后按空存储启动（用户重新阅读即覆盖）
                TryQuarantine(_filePath);
                parsed = new List<ReadingPosition>();
                LastLoadError = ex.Message;
            }
        }

        lock (_gate)
        {
            _positions.Clear();
            foreach (var p in parsed)
            {
                if (!string.IsNullOrEmpty(p.BookSourceUrl))
                {
                    _positions[MakeKey(p.BookSourceUrl, p.BookUrl)] = p;
                }
            }
        }

        Invalidate();
    }

    /// <summary>
    /// 不可变快照（含全部记录）。首次访问或上次变更后重建一次，之后零分配读。
    /// 注意：返回的列表是快照——其后的并发增删不会反映在已取到的列表上。
    /// </summary>
    public IReadOnlyList<ReadingPosition> Positions => _snapshot ?? BuildSnapshot();

    /// <summary>取某本书的进度（无则 null）。</summary>
    public ReadingPosition? Get(string bookSourceUrl, string bookUrl) =>
        _positions.TryGetValue(MakeKey(bookSourceUrl, bookUrl), out var p) ? p : null;

    /// <summary>
    /// 写入 / 更新进度（同键覆盖；覆盖保留原记录无意义——进度是"最近一次"语义）。
    /// <para>只改内存与快照，不落盘：批量翻章时逐章 Save 会是 O(章数) 次磁盘写，
    /// 由调用方在"停止阅读"时刻（关书/切书/节流）统一 <see cref="SaveAsync"/>。</para>
    /// </summary>
    public void Upsert(ReadingPosition position)
    {
        if (position is null || string.IsNullOrEmpty(position.BookSourceUrl))
        {
            return;
        }

        lock (_gate)
        {
            _positions[MakeKey(position.BookSourceUrl, position.BookUrl)] = position;
        }

        Invalidate();
    }

    /// <summary>删除某本书的进度（读完删记录 / 用户清除）。返回是否确实删除。</summary>
    public bool Remove(string bookSourceUrl, string bookUrl)
    {
        bool removed;
        lock (_gate)
        {
            removed = _positions.TryRemove(MakeKey(bookSourceUrl, bookUrl), out _);
        }

        return removed && Invalidate();
    }

    /// <summary>清空全部进度（仅内存）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _positions.Clear();
        }

        Invalidate();
    }

    /// <summary>
    /// 原子落盘（并发保存串行化；失败时删 tmp 不留垃圾）。
    /// 记录按"最近阅读"倒序输出——文件里越靠前越是最近在读的书（加载时顺序无关，仅便于人工查看）。
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 快照在闸内取：并发 Upsert 发生在"取快照"与"写盘"之间时，
            // 旧实现会把刚写入的进度漏掉（取快照在闸外的丢更新）。
            var ordered = Positions
                .OrderByDescending(p => p.LastReadTicks)
                .ToList();

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _filePath + ".tmp";
            try
            {
                await using (var stream = File.Create(tmp))
                {
                    await BookSourceSerializer.SerializeReadingPositionListAsync(stream, ordered, cancellationToken)
                        .ConfigureAwait(false);
                }

                // 原子替换：读者要么看到旧的完整文件，要么看到新的完整文件，不存在半截状态
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch
            {
                // 失败不留垃圾（与 BookSourceManager.SerializeAsync 同约定）
                TryDelete(tmp);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>删临时文件，失败静默（清理是尽力而为，不该盖掉原始异常）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略：删除失败（如文件被占用）不影响正确性，下次保存会覆盖同名 tmp
        }
    }

    /// <summary>隔离损坏的进度文件（改名 .corrupt，失败静默）——保留现场便于人工恢复。</summary>
    private static void TryQuarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch
        {
            // 只读目录 / 文件被占用：保留原文件，下次保存会覆盖它
        }
    }

    private IReadOnlyList<ReadingPosition> BuildSnapshot() =>
        _snapshot ??= _positions.Values.ToArray();

    /// <summary>
    /// 使读快照失效（任何写操作后调用），并返回 true 以便在 <c>_positions.AddOrUpdate</c>
    /// 这类"必须返回一个值"的 lambda 里顺手失效快照。
    /// <para>快照本身是不可变数组，失效只是把引用置 null：读者手上的旧数组仍可安全遍历，
    /// 下次 <see cref="BuildSnapshot"/> 才重建。</para>
    /// </summary>
    private bool Invalidate()
    {
        _snapshot = null;
        return true;
    }
}
