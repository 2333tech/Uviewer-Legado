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
    /// </summary>
    public List<TitleRule> GetEnabledOrdered() =>
        GetAll()
            .Where(r => r.IsEnabled)
            .Select((r, i) => (Rule: r, Index: i))
            .OrderBy(x => x.Rule.Order)
            .ThenBy(x => x.Index)
            .Select(x => x.Rule)
            .ToList();

    /// <summary>从磁盘流式加载（文件不存在 = 清空；损坏保留原有数据）。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_gate)
            {
                _map = new Dictionary<long, TitleRule>();
                _snapshot = null;
            }

            return;
        }

        var fresh = new Dictionary<long, TitleRule>();
        using (var stream = BookSourceJsonReader.OpenRead(_filePath))
        {
            await foreach (var rule in BookSourceSerializer.DeserializeTitleRuleEnumerableAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (rule is null)
                {
                    continue;
                }

                fresh[rule.Id] = rule;
            }
        }

        lock (_gate)
        {
            _map = fresh;
            _snapshot = null;
        }
    }

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

                    _snapshot = null;
                }
            }
        }

        return new RuleImportResult(added, updated, skipped);
    }

    /// <summary>全量写回磁盘（流式 + 临时文件原子替换）。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var all = GetAll();
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            _snapshot = null;
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
                _snapshot = null;
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
                _snapshot = null;
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
            _snapshot = null;
        }
    }

    // ---- 内部 ----

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
