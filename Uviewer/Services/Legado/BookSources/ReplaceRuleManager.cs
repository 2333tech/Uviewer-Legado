using System.Collections.Concurrent;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 全局替换规则存储（对标 reader api/commands/replace_rule.rs 的 CRUD：
/// 读写用户目录下的 replaceRule.json 列表）。
/// <para>与 <see cref="BookSourceManager"/> 保持同构的存储策略（阶段 0 性能架构复用）：</para>
/// <list type="bullet">
/// <item>读 = 惰性不可变快照（List 数组，一次构建、任意多读者并发共享）；</item>
/// <item>写 = 短锁内替换 map/列表 + 失效快照；</item>
/// <item>落盘 = 流式序列化 + .tmp 临时文件 + File.Move 原子替换（半截文件不会出现）；</item>
/// <item>文件 = %LocalAppData%/Uviewer/replaceRules.json（reader: replaceRule.json）。</item>
/// </list>
/// <para>键语义：ReplaceRule 以 <see cref="ReplaceRule.Id"/> 为键；Id &lt;= 0 视为"未分配"，
/// Upsert 时分配 max(Id)+1（确定性，避免并发导入碰撞）。覆盖保留原位置 ⇒ 落盘顺序稳定。</para>
/// </summary>
public sealed class ReplaceRuleManager
{
    private readonly string _filePath;

    /// <summary>写侧短锁：仅包住列表变更 + 快照失效，读侧不参与。</summary>
    private readonly object _gate = new();

    /// <summary>落盘串行化：同一实例的并发保存不会争用同一个临时文件。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>有序存储（Dictionary 保插入序 = 文件顺序；枚举顺序即落盘顺序）。</summary>
    private Dictionary<long, ReplaceRule> _map = new();

    /// <summary>不可变读快照（数组），变更时置 null，下次读取重建。</summary>
    private volatile IReadOnlyList<ReplaceRule>? _snapshot;

    public ReplaceRuleManager(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>默认落盘路径：%LocalAppData%/Uviewer/replaceRules.json。</summary>
    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uviewer", "replaceRules.json");

    /// <summary>当前规则数（O(1)，不构建快照）。</summary>
    public int Count => _map.Count;

    /// <summary>
    /// 全部规则（存储顺序）。返回缓存快照：O(1) 无锁，遍历期间其他线程改动集合不抛异常。
    /// 请勿修改返回内容（按不可变契约共享）——<see cref="ContentPurifier"/> 依赖引用比较判失效。
    /// </summary>
    public IReadOnlyList<ReplaceRule> GetAll()
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
    /// 从磁盘流式加载（整份读完才原子换入：中途失败/文件损坏保留原有数据）。
    /// 文件不存在 = 清空（与 BookSourceManager 语义一致）。
    /// <para><b>解析失败不抛</b>：坏文件会让枚举器抛 JsonException，旧实现任其冒泡到
    /// 初始化链（那里缓存 faulted 任务不重试 → 净化功能整个会话永久退化），
    /// 这里降级为"保留现有数据 + 记 <see cref="LastLoadError"/>"。</para>
    /// <para><b>Id &lt;= 0 重新分配</b>：第三方/手写 JSON 常缺 id 字段（默认 0），
    /// 直接以 Id 为键会让同文件里的多条无 Id 规则互相覆盖只剩最后一条
    /// （导入路径早有重分配，加载路径对齐）。</para>
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            lock (_gate)
            {
                _map = new Dictionary<long, ReplaceRule>();
                _snapshot = null;
            }

            LastLoadError = null;
            return;
        }

        // 局部累积再原子换入（失败不影响现有数据）
        var fresh = new Dictionary<long, ReplaceRule>();
        try
        {
            using (var stream = BookSourceJsonReader.OpenRead(_filePath))
            {
                await foreach (var rule in BookSourceSerializer.DeserializeReplaceRuleEnumerableAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    if (rule is null)
                    {
                        continue;
                    }

                    // 同 Id 后者覆盖前者（防御坏文件里的重复 Id）；
                    // 无 Id 条目重新分配（max+1，保序且不撞已有 Id）——
                    // 直接以 0 为键会让同文件多条无 Id 规则互相覆盖只剩一条
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
            // 解析失败：保留现有数据不换入，只记错误
            LastLoadError = ex.Message;
            return;
        }

        lock (_gate)
        {
            _map = fresh;
            _snapshot = null;
        }
    }

    /// <summary>最近一次加载失败的原因（损坏文件等；null = 上次加载正常）。诊断用。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 全量写回磁盘：流式序列化 + 临时文件原子替换（策略同 BookSourceManager.SaveAsync）。
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
            // 快照在闸内取（同 BookSourceManager：闸外取快照会丢更新）
            var all = GetAll();
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
                    await BookSourceSerializer.SerializeReplaceRuleListAsync(stream, all, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 按 Id 插入或覆盖。Id &lt;= 0 时分配 max(Id)+1（写回传入对象，调用方可见）。
    /// 覆盖保留原位置（顺序稳定）；返回是否入库（pattern 为空的规则仍可入库——
    /// 是否跳过由 ContentPurifier 决定，存储层不判业务语义）。
    /// </summary>
    public bool Upsert(ReplaceRule rule)
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

            _map[rule.Id] = rule; // Dictionary 索引器：新键追加到尾，覆盖保位
            _snapshot = null;
            return true;
        }
    }

    /// <summary>
    /// 从 JSON 文件导入替换规则（流式，零 DOM）。
    /// <para><b>为什么需要</b>：此前本管理器只能"加载自己的文件"（<see cref="LoadAsync"/>），
    /// 用户无法把 legado/reader 导出的规则文件导进来——净化功能等于无法使用。
    /// 这里补上与 <see cref="TitleRuleManager.ImportFromFileAsync"/> 同构的导入路径。</para>
    /// <para>按 Id 覆盖同键规则；Id &lt;= 0 视为新规则（重新分配 Id，避免与既有冲突）。
    /// pattern 为空的规则跳过（legado 语义：空 pattern 的规则永不命中）。</para>
    /// </summary>
    public async Task<RuleImportResult> ImportFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;

        using (var stream = BookSourceJsonReader.OpenRead(path))
        {
            await foreach (var rule in BookSourceSerializer.DeserializeReplaceRuleEnumerableAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern))
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

    /// <summary>
    /// 只切换启用状态并落盘（规则清单弹窗的复选框走这里）。
    /// <para>存储与落盘分离：先改内存（净化器立即感知），再落盘。落盘失败不回滚内存
    /// ——界面显示"已生效"而实际未生效比反过来更难排查。</para>
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

    /// <summary>按 Id 删除，返回是否命中。</summary>
    public bool Remove(long id)    {
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

    /// <summary>按 Id 查（快照内查找，O(n)；规则量级为百，无需建索引）。</summary>
    public ReplaceRule? GetById(long id)
    {
        foreach (var rule in GetAll())
        {
            if (rule.Id == id)
            {
                return rule;
            }
        }

        return null;
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

    // ---- 内部 ----

    /// <summary>分配下一个 Id：max(Id)+1；空表从 1 开始（确定性，不用时间戳避免碰撞）。</summary>
    private static long NextIdIn(Dictionary<long, ReplaceRule> map)
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

    /// <summary>分配下一个 Id（调用方须已持 <c>_gate</c>）。</summary>
    private long NextIdLocked() => NextIdIn(_map);

    /// <summary>构建不可变快照（保序数组）。</summary>
    private static IReadOnlyList<ReplaceRule> Build(Dictionary<long, ReplaceRule> map)
    {
        if (map.Count == 0)
        {
            return Array.Empty<ReplaceRule>();
        }

        var list = new ReplaceRule[map.Count];
        var i = 0;
        foreach (var rule in map.Values)
        {
            list[i++] = rule;
        }

        return list;
    }

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
