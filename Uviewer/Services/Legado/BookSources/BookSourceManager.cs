using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Uviewer.Services.Legado.Models;
// 别名：根命名空间为 Uviewer.Services.Legado，故全局命名空间已隐式存在命名空间 "BookSource"，
// 不能再把别名命名为 "BookSource"（会与命名空间冲突，报 CS0576）。
// 改用 BookSourceModel 指向类型，避免与命名空间 "BookSource" 撞名。
// 该别名只影响简单名，不影响成员访问（如 BookSourceJsonContext.Default.BookSource）。
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>导入结果统计。</summary>
public readonly record struct ImportResult(int Added, int Updated, int Skipped)
{
    /// <summary>成功入库条数（新增 + 覆盖）。</summary>
    public int Total => Added + Updated;

    public static ImportResult Empty => new(0, 0, 0);
}

/// <summary>
/// 书源存储与生命周期管理（阶段 0，性能优化版）。
/// <para>存储结构：<c>ConcurrentDictionary&lt;url, (source, seq)&gt;</c> 替代 List + lock：
/// 按主键的读写从 O(n) 降为 O(1)，且读路径完全无锁。</para>
/// <para>读路径：惰性不可变快照——变更时只把快照置 null，下次读取才重建；
/// 读侧拿到的是不可变数组，可安全地在遍历时被其他线程修改。</para>
/// <para>I/O：落盘走流（不经中间字符串）+ 临时文件原子替换，
/// 进程被杀也不会留下半截 booksources.json。</para>
/// <para>导入三条路径：<see cref="ImportFromJsonAsync"/> 走零 DOM + 并行反序列化；
/// <see cref="ImportFromUtf8Async"/> 免 UTF-16 转码；<see cref="ImportFromFileAsync"/>
/// 走流式枚举 + 分批合入（峰值内存 ≈ 一个批次）。</para>
/// <para>数据安全：无 bookSourceUrl 的条目直接跳过——否则它们会被当成同一条互相覆盖。</para>
/// </summary>
public sealed class BookSourceManager
{
    /// <summary>存储项：书源 + 插入序号（用于保持导入顺序，使落盘输出确定）。</summary>
    private readonly record struct Entry(BookSourceModel Source, long Seq);

    /// <summary>不可变读快照，一次构建、供任意多读者并发共享。</summary>
    private sealed class ReadSnapshot
    {
        public BookSourceModel[] All = Array.Empty<BookSourceModel>();
        public BookSourceModel[] Enabled = Array.Empty<BookSourceModel>();
        public Dictionary<string, BookSourceModel> ByUrl = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 落盘路径（构造时可注入，便于测试用临时文件；默认 <see cref="DefaultFilePath"/>）。
    /// <para>整个实例只读一次：路径变了要换实例，不支持热改——否则并发保存可能写错文件。</para>
    /// </summary>
    private readonly string _filePath;

    /// <summary>写侧短锁：仅包住字典变更 + 快照失效，读侧不参与。</summary>
    private readonly object _gate = new();

    /// <summary>落盘串行化：同一实例的并发保存不会争用同一个临时文件。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private ConcurrentDictionary<string, Entry> _map = NewMap();
    private volatile ReadSnapshot? _snapshot;
    private long _seq;

    public BookSourceManager(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>默认落盘路径：%LocalAppData%/Uviewer/booksources.json。</summary>
    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uviewer", "booksources.json");

    /// <summary>当前书源数（O(1)，不构建快照）。</summary>
    public int Count => _map.Count;

    /// <summary>
    /// 全部书源（按导入/插入顺序）。返回的是缓存快照数组：
    /// O(1) 无锁，且遍历期间其他线程改动集合不会抛异常。
    /// 请勿修改返回内容（它按不可变契约共享）。
    /// </summary>
    public IReadOnlyList<BookSourceModel> GetAll() => Current().All;

    /// <summary>仅启用的书源（同样来自快照，O(1)。Enabled 缺省视为启用，见 BookSource.IsEnabled）。</summary>
    public IReadOnlyList<BookSourceModel> GetEnabled() => Current().Enabled;

    /// <summary>按主键查书源（快照内的 Ordinal 字典，O(1)）。</summary>
    public BookSourceModel? GetByUrl(string url) =>
        Current().ByUrl.TryGetValue(url, out var source) ? source : null;

    /// <summary>按主键判存在（O(1)）。</summary>
    public bool Contains(string url) => Current().ByUrl.ContainsKey(url);

    /// <summary>
    /// 从磁盘流式加载（对标 reader 启动时读书源文件）。
    /// <para>原子性：整份读完才换入新 map——中途失败/文件损坏时保留原有数据
    /// （旧 map 仍在 _map 里，读者无感）。序号按文件顺序重新编排（落盘顺序 = 文件顺序）。</para>
    /// <para>文件不存在 = 清空（reader：无文件即无书源，保留原语义）。</para>
    /// <para><b>解析失败不抛</b>：0 字节/截断的 JSON 会让枚举器抛 JsonException——
    /// 旧实现任其冒泡到 <c>BookSourceService.EnsureInitializedAsync</c>，而那里缓存了
    /// faulted 的初始化任务不再重试 → 书源功能整个会话永久退化。这里降级为
    /// "保留现有数据 + 记 <see cref="LastLoadError"/>"，与阅读进度存储的容错口径一致。</para>
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_gate)
            {
                _map = NewMap();
                _seq = 0;
                _snapshot = null;
            }

            LastLoadError = null;
            return;
        }

        // 先在局部 fresh map 上累积，读完再原子换入（失败不影响现有数据）
        var fresh = NewMap();
        long order = 0;
        try
        {
            // 强制在线程池执行：本方法常由 UI 线程的首次访问触发（打开书源管理/搜索）。
            // 26 MB / 3600+ 条的解析若在调用线程上跑，窗口会先冻结数秒；
            // 流式枚举在首个真正异步读之前的那一段是同步的，靠 ConfigureAwait 救不了。
            await Task.Run(
                async () =>
                {
                    using (var stream = BookSourceJsonReader.OpenRead(_filePath))
                    {
                        await foreach (var source in BookSourceSerializer.EnumerateBookSourcesAsync(stream, cancellationToken).ConfigureAwait(false))
                        {
                            // 无主键条目不入库（数据安全，同导入路径）
                            if (!IsValid(source))
                            {
                                continue;
                            }

                            // 同 URL 后者覆盖前者，序号取后者（保持文件内顺序）
                            fresh[source.BookSourceUrl] = new Entry(source, order++);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            LastLoadError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 解析失败：保留现有数据不换入，只记错误（调用方可在诊断面板展示）
            LastLoadError = ex.Message;
            return;
        }

        lock (_gate)
        {
            _map = fresh;
            // 取 max 而不是直接赋值：Upsert 的序号分配在锁外（Interlocked），
            // 并发加载与插入交错时直接重置会让后续 Upsert 发出重复序号
            // （同 Seq 两条 → 落盘顺序不确定）。
            _seq = Math.Max(_seq, order);
            _snapshot = null;
        }
    }

    /// <summary>最近一次加载失败的原因（损坏文件等；null = 上次加载正常）。诊断用。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 全量写回磁盘：流式序列化 + 临时文件原子替换（确保父目录存在）。
    /// <para>原子性：写 .tmp → Flush → File.Move(overwrite) 换入；任何失败删 tmp、
    /// 原文件毫发无损。<see cref="_saveGate"/> 把并发保存串行化（共享同一个 tmp 路径）。</para>
    /// <para>BufferSize=0：STJ 自带写缓冲，避免 FileStream 双重缓冲的额外内存。</para>
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 快照在闸内取：旧实现在闸外 Current().All，并发导入/启停后
            // 旧快照可能覆盖新数据（磁盘回退一版，内存还在 → 直到下次保存才恢复）。
            var all = Current().All;
            var temp = _filePath + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0, // STJ 自带缓冲，避免双重缓冲
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
                {
                    await BookSourceSerializer.SerializeListAsync(stream, all, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // 原子替换（同卷内 Move 是元数据操作，读者要么看到旧文件要么看到新文件）
                File.Move(temp, _filePath, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// 导入一份书源 JSON（单个对象或数组）并落盘。
    /// 走零 DOM + 并行反序列化路径，适合已在内存中的 JSON 文本（UI 粘贴/网络下载）。
    /// 结果按原文顺序合并入库，与并行度无关（确定性）。
    /// <para>字节路径：string → ArrayPool 租借缓冲转 UTF-8 → <see cref="BookSourceJsonReader.ParseUtf8"/>
    /// （结构性扫描切片 + Parallel.For 反序列化）→ 顺序合并 → 落盘。全程不建 JsonNode DOM。</para>
    /// </summary>
    public async Task<ImportResult> ImportFromJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ImportResult.Empty;
        }

        var size = Encoding.UTF8.GetByteCount(json);
        var rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var written = Encoding.UTF8.GetBytes(json.AsSpan(), rented.AsSpan());
            var memory = rented.AsMemory(0, written);

            // 解析是 CPU 密集（含 Parallel.For）：放到线程池，避免阻塞调用方（对标 reader 的 spawn_blocking）
            var parsed = await Task.Run(() => BookSourceJsonReader.ParseUtf8(memory), cancellationToken)
                .ConfigureAwait(false);
            return await ImportParsedAsync(parsed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>导入 UTF-8 书源字节（免一次 UTF-16 转码；解析同样走线程池）。</summary>
    public async Task<ImportResult> ImportFromUtf8Async(
        ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken = default)
    {
        // 同上：CPU 密集的并行解析放到线程池
        var parsed = await Task.Run(() => BookSourceJsonReader.ParseUtf8(utf8), cancellationToken)
            .ConfigureAwait(false);
        return await ImportParsedAsync(parsed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从文件导入：流式枚举 + 分批合入，不在内存中展开整份 JSON，
    /// 峰值内存 ≈ 一个批次（<see cref="MergeBatchSize"/> 条）+ 存储本身，适合导入大文件。
    /// <para>流程：边枚举边攒批，满 <see cref="MergeBatchSize"/> 条合入一次（短锁），
    /// 收尾合入剩余，最后统一落盘。合并语义与 <see cref="ImportParsedAsync"/> 完全一致
    ///（覆盖保留原序号、无主键跳过、顺序确定）。</para>
    /// </summary>
    public async Task<ImportResult> ImportFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var batch = new List<BookSourceModel>(MergeBatchSize);

        using (var stream = BookSourceJsonReader.OpenRead(path))
        {
            await foreach (var source in BookSourceSerializer.EnumerateBookSourcesAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                batch.Add(source);
                if (batch.Count >= MergeBatchSize)
                {
                    MergeBatch(batch, ref added, ref updated, ref skipped);
                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            MergeBatch(batch, ref added, ref updated, ref skipped);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new ImportResult(added, updated, skipped);
    }

    /// <summary>文件导入的合入批次大小：兼顾锁竞争频率与峰值内存。</summary>
    private const int MergeBatchSize = 256;

    /// <summary>
    /// 把一批书源按原文顺序合入存储（短锁内完成）。
    /// <para>合并语义（与 <see cref="ImportParsedAsync"/> 的循环逐行同构，那处不再重复注释）：</para>
    /// ① 无主键跳过（skipped++）；② 取新序号（Interlocked，锁外也安全）；
    /// ③ 已存在 → 覆盖但**保留原插入序号**（顺序稳定，落盘确定性）；
    /// ④ 统计 added / updated；⑤ 批次结束失效快照（下次读取重建）。
    /// </summary>
    private void MergeBatch(
        List<BookSourceModel> batch, ref int added, ref int updated, ref int skipped)
    {
        lock (_gate)
        {
            MergeCore(batch, ref added, ref updated, ref skipped);
            _snapshot = null;
        }
    }

    /// <summary>
    /// 已解析列表的合入 + 落盘（ImportFromJsonAsync / ImportFromUtf8Async 的公共尾部）。
    /// <para>合并循环与 <see cref="MergeBatch"/> 逐行同构（同语义：无主键跳过 / 覆盖保留序号 /
    /// 顺序确定），此处为整份一次性合入（调用方已并行解析完，无需再分批）。</para>
    /// </summary>
    private async Task<ImportResult> ImportParsedAsync(
        List<BookSourceModel> parsed, CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;

        // 反序列化已在前一步并行完成；这里按原文顺序顺序合并 => 结果与并行度无关。
        lock (_gate)
        {
            MergeCore(parsed, ref added, ref updated, ref skipped);
            _snapshot = null;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new ImportResult(added, updated, skipped);
    }

    /// <summary>
    /// 合并内核（调用方须已持 <c>_gate</c>）：无主键跳过 / 覆盖保留原序号 / 统计 added/updated。
    /// <para><see cref="MergeBatch"/> 与 <see cref="ImportParsedAsync"/> 共用——
    /// 两份逐行复制的循环曾因改一处漏一处出过漂移风险，合并语义只许有一个实现。</para>
    /// </summary>
    private void MergeCore(
        List<BookSourceModel> items, ref int added, ref int updated, ref int skipped)
    {
        foreach (var source in items)
        {
            if (!IsValid(source))
            {
                // 无主键的书源会被视为同一条互相覆盖，直接跳过（数据安全）
                skipped++;
                continue;
            }

            var seq = Interlocked.Increment(ref _seq);
            var existed = _map.TryGetValue(source.BookSourceUrl, out var old);
            // 覆盖时保留原插入位置，顺序稳定
            _map[source.BookSourceUrl] = new Entry(source, existed ? old.Seq : seq);
            if (existed)
            {
                updated++;
            }
            else
            {
                added++;
            }
        }
    }

    /// <summary>按主键插入或覆盖。返回是否入库（无主键的书源被拒绝）。</summary>
    public bool Upsert(BookSourceModel source)
    {
        if (!IsValid(source))
        {
            return false;
        }

        var seq = Interlocked.Increment(ref _seq);
        lock (_gate)
        {
            _map.AddOrUpdate(
                source.BookSourceUrl,
                _ => new Entry(source, seq),
                (_, old) => new Entry(source, old.Seq)); // 覆盖保留原位

            _snapshot = null;
        }

        return true;
    }

    /// <summary>按 URL 删除，返回是否命中。</summary>
    public bool Remove(string url)
    {
        lock (_gate)
        {
            var removed = _map.TryRemove(url, out _);
            if (removed)
            {
                _snapshot = null;
            }

            return removed;
        }
    }

    /// <summary>清空（不落盘；调用方需要持久化时自行 SaveAsync）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _snapshot = null;
        }
    }

    /// <summary>
    /// 启用/停用某书源（按 URL），并立即落盘。
    /// <para>注意：Enabled 参与 Enabled 快照的过滤，故除了改对象还要失效快照；
    /// 书源对象本身是快照与调用方共享的，就地修改对所有读者可见（不可变契约的例外：
    /// 标量属性修改，阅读器侧可接受）。</para>
    /// </summary>
    public async Task SetEnabledAsync(string url, bool enabled, CancellationToken cancellationToken = default)
    {
        var source = GetByUrl(url);
        if (source is null)
        {
            return;
        }

        source.Enabled = enabled;
        lock (_gate)
        {
            // IsEnabled 参与 Enabled 快照，失效后下次读取重建
            _snapshot = null;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- 内部 ----

    /// <summary>
    /// 取当前快照（惰性重建）。快照有效 → 直接返回（无锁热路径）；
    /// 失效 → 短锁内双重检查重建（_snapshot ??= 保证只建一次）。
    /// </summary>
    private ReadSnapshot Current()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_gate)
        {
            return _snapshot ??= Build(_map);
        }
    }

    /// <summary>
    /// 构建快照。步骤：
    /// ① 枚举 map 取值（ConcurrentDictionary 枚举是无锁且线程安全的快照语义）；
    /// ② 按插入序号排序（输出顺序 = 导入顺序，落盘确定性）；
    /// ③ 一次遍历同时产出 All 数组、ByUrl 字典、Enabled 列表（IsEnabled 缺省为真）。
    /// </summary>
    private static ReadSnapshot Build(ConcurrentDictionary<string, Entry> map)
    {
        // ConcurrentDictionary 的枚举是无锁且线程安全的快照语义
        var entries = new List<Entry>(map.Count);
        foreach (var pair in map)
        {
            entries.Add(pair.Value);
        }

        // 按插入序号排序：输出顺序 = 导入顺序（确定性落盘）
        entries.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));

        var all = new BookSourceModel[entries.Count];
        var byUrl = new Dictionary<string, BookSourceModel>(entries.Count, StringComparer.Ordinal);
        var enabled = new List<BookSourceModel>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var source = entries[i].Source;
            all[i] = source;
            byUrl[source.BookSourceUrl] = source;
            if (source.IsEnabled)
            {
                enabled.Add(source);
            }
        }

        return new ReadSnapshot { All = all, Enabled = enabled.ToArray(), ByUrl = byUrl };
    }

    /// <summary>新建存储字典（Key 用 Ordinal：URL 大小写敏感，避免不同源被误并为一条）。</summary>
    private static ConcurrentDictionary<string, Entry> NewMap() =>
        new(StringComparer.Ordinal);

    /// <summary>有效性判定：非空且有非空白主键（无主键条目互相覆盖，必须拒绝）。</summary>
    private static bool IsValid(BookSourceModel source) =>
        source is not null && !string.IsNullOrWhiteSpace(source.BookSourceUrl);

    /// <summary>删文件（清理 tmp 用）；失败不影响主流程。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
