using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 标题 / 章节划分规则存储（与 <see cref="ReplaceRuleManager"/> 完全同构：
/// 惰性不可变快照 + tmp/File.Move 原子落盘 + SemaphoreSlim 串行化保存）。
///
/// <para>额外提供 <see cref="ImportFromFileAsync"/>：从用户选的 JSON 文件导入规则。
/// 净化规则此前只能"加载自己的文件"，无法从外部导入——这里把两条规则类型的
/// 导入路径统一为同一套流式实现（零 DOM、峰值内存≈单条规则）。</para>
///
/// <para>文件 = %LocalAppData%/Uviewer/titleRules.json。</para>
/// </summary>
public sealed class TitleRuleManager
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>有序存储（Dictionary 保插入序 = 文件顺序）。</summary>
    private Dictionary<long, TitleRule> _map = new();

    /// <summary>不可变读快照（数组），变更时置 null，下次读取重建。</summary>
    private volatile IReadOnlyList<TitleRule>? _snapshot;

    /// <summary>启用 + 排序后的快照（与 <see cref="_snapshot"/> 同生命周期，一并失效）。</summary>
    private volatile List<TitleRule>? _enabledOrdered;

    public TitleRuleManager(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>默认落盘路径：%LocalAppData%/Uviewer/titleRules.json。</summary>
    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uviewer", "titleRules.json");

    /// <summary>当前规则数（O(1)，不构建快照）。</summary>
    public int Count => _map.Count;

    /// <summary>全部规则（存储顺序，惰性快照）。请勿修改返回内容。</summary>
    public IReadOnlyList<TitleRule> GetAll()
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
    /// 已启用的规则，按 <see cref="TitleRule.Order"/> 升序（同值保持导入顺序）。
    /// <para>供章节切分使用：停用的规则在此就被滤掉，切分时不必再判开关。</para>
    /// <para>惰性快照：旧实现每次调用都重新 Where+OrderBy+ToList（开书/重建目录/
    /// 建文本缓存都会走）——这是存储层范式点名要避免的"每次读重新排序"，
    /// 现在与 <see cref="_snapshot"/> 同生命周期缓存，变更时一并失效。</para>
    /// </summary>
    public List<TitleRule> GetEnabledOrdered()
    {
        var cached = _enabledOrdered;
        if (cached is not null)
        {
            return cached;
        }

        lock (_gate)
        {
            return _enabledOrdered ??= GetAll()
                .Where(r => r.IsEnabled)
                .Select((r, i) => (Rule: r, Index: i))
                .OrderBy(x => x.Rule.Order)
                .ThenBy(x => x.Index)
                .Select(x => x.Rule)
                .ToList();
        }
    }

    /// <summary>
    /// 从磁盘流式加载（文件不存在 = 清空；损坏保留原有数据）。
    /// <para><b>解析失败不抛</b>：坏文件抛 JsonException 会顺着初始化链把
    /// 缓存 faulted 任务的地方整个会话打挂，降级为保留现有数据 + 记错误。</para>
    /// <para><b>Id &lt;= 0 重新分配</b>：手写 JSON 常缺 id 字段（默认 0），
    /// 直接以 Id 为键会让同文件多条无 Id 规则互相覆盖只剩一条（导入路径早有重分配）。</para>
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_gate)
            {
                _map = new Dictionary<long, TitleRule>();
                Invalidate();
            }

            LastLoadError = null;
            return;
        }

        var fresh = new Dictionary<long, TitleRule>();
        try
        {
            using (var stream = BookSourceJsonReader.OpenRead(_filePath))
            {
                await foreach (var rule in BookSourceSerializer.DeserializeTitleRuleEnumerableAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    if (rule is null)
                    {
                        continue;
                    }

                    if (rule.Id <= 0)
                    {
                        rule.Id = NextIdIn(fresh);
                    }

                    fresh[rule.Id] = rule;
                }
            }

            LastLoadError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return;
        }

        lock (_gate)
        {
            _map = fresh;
            Invalidate();
        }
    }

    /// <summary>最近一次加载失败的原因（损坏文件等；null = 上次加载正常）。诊断用。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>从 JSON 文件导入规则（流式，零 DOM）。</summary>
    /// <para>按 Id 覆盖同键规则；Id &lt;= 0 视为新规则（重新分配 Id，避免与既有冲突）。</para>
    public async Task<RuleImportResult> ImportFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;

        using (var stream = BookSourceJsonReader.OpenRead(path))
        {
            await foreach (var rule in BookSourceSerializer.DeserializeTitleRuleEnumerableAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (rule is null)
                {
                    skipped++;
                    continue;
                }

                // 无效规则（既没有正则也不是书名行）不入库——否则切分时永远不命中，纯粹是垃圾数据
                if (string.IsNullOrWhiteSpace(rule.Pattern) && !rule.IsBookTitle)
                {
                    skipped++;
                    continue;
                }

                lock (_gate)
                {
                    if (rule.Id <= 0 || !_map.ContainsKey(rule.Id))
                    {
                        rule.Id = NextIdLocked();
                        _map[rule.Id] = rule;
                        added++;
                    }
                    else
                    {
                        _map[rule.Id] = rule;
                        updated++;
                    }

                    Invalidate();
                }
            }
        }

        return new RuleImportResult(added, updated, skipped);
    }

    /// <summary>全量写回磁盘（流式 + 临时文件原子替换）。</summary>
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
            // 快照在闸内取（闸外取快照会丢更新，同 ReplaceRuleManager）
            var all = GetAll();
            var temp = _filePath + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
                {
                    await BookSourceSerializer.SerializeTitleRuleListAsync(stream, all, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

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

    /// <summary>插入或覆盖（Id &lt;= 0 时分配新 Id，写回传入对象）。</summary>
    public bool Upsert(TitleRule rule)
    {
        if (rule is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (rule.Id <= 0)
            {
                rule.Id = NextIdLocked();
            }

            _map[rule.Id] = rule;
            Invalidate();
            return true;
        }
    }

    /// <summary>按 Id 删除。</summary>
    public bool Remove(long id)
    {
        lock (_gate)
        {
            var removed = _map.Remove(id);
            if (removed)
            {
                Invalidate();
            }

            return removed;
        }
    }

    /// <summary>按 Id 查。</summary>
    public TitleRule? GetById(long id) => GetAll().FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// 只切换启用状态并落盘（规则清单弹窗的复选框走这里）。
    /// <para>存储与落盘分离：先改内存（立即影响切分），再落盘；
    /// 落盘失败不回滚内存——用户看到的是"已生效"，重启后若丢了一次改动，
    /// 也远好过界面显示未生效而实际已生效。</para>
    /// </summary>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(id, out var rule))
            {
                rule.IsEnabled = enabled;
                Invalidate();
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清空（不落盘）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            Invalidate();
        }
    }

    // ---- 内部 ----

    /// <summary>
    /// 使全部读快照失效（任何写操作后调用，持 <c>_gate</c> 调用）。
    /// 快照是不可变数组，失效只是置 null：读者手上的旧数组仍可安全遍历。
    /// </summary>
    private void Invalidate()
    {
        _snapshot = null;
        _enabledOrdered = null;
    }

    /// <summary>在局部字典上分配下一个 Id（max+1；加载路径无锁，用局部 map）。</summary>
    private static long NextIdIn(Dictionary<long, TitleRule> map)
    {
        long max = 0;
        foreach (var id in map.Keys)
        {
            if (id > max)
            {
                max = id;
            }
        }

        return max + 1;
    }

    /// <summary>分配下一个 Id（max+1，确定性；调用方须已持锁）。</summary>
    private long NextIdLocked()
    {
        long max = 0;
        foreach (var id in _map.Keys)
        {
            if (id > max)
            {
                max = id;
            }
        }

        return max + 1;
    }

    /// <summary>构建不可变快照（保序数组）。</summary>
    private static IReadOnlyList<TitleRule> Build(Dictionary<long, TitleRule> map)
    {
        if (map.Count == 0)
        {
            return Array.Empty<TitleRule>();
        }

        var list = new TitleRule[map.Count];
        var i = 0;
        foreach (var rule in map.Values)
        {
            list[i++] = rule;
        }

        return list;
    }

    /// <summary>删临时文件（失败不影响主流程）。</summary>
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
