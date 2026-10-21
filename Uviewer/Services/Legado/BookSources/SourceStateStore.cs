using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 JS 状态存储（阶段 4-2 新增）：<c>java.put/get</c>、<c>source.getVariable</c>、
/// <c>book.getVariable</c>、<c>cache.put/get</c>、<c>cookie.setCookie/getCookie</c> 的唯一落点。
///
/// <para><b>为什么需要一个独立仓库</b>：阶段 4-1 之前 <c>java.put/get</c> 只是 <see cref="JsBridge"/>
/// 实例上的一个 <c>Dictionary</c> —— 解析链一结束就丢，换一个 <c>RuleEngine</c>（并行正文、下一次打开书）
/// 就再也读不到。而 legado 书源的常规写法正是"目录链里 <c>java.put('bid', …)</c>、正文链里
/// <c>java.get('bid')</c>"，状态必须跨链、跨引擎实例、跨进程存活。</para>
///
/// <para><b>作用域模型（与 legado/reader 对齐）</b>：所有状态都挂在"书源"这一个维度下，
/// 具体层级由键前缀区分（见 <see cref="JsScope"/>）：</para>
/// <list type="bullet">
/// <item><c>sv:*</c>：书源级元数据（variable / loginInfo / loginHeader）；</item>
/// <item><c>sp:*</c>：<c>source.put/get</c> 的键值；</item>
/// <item><c>bv:&lt;bookUrl&gt;:*</c>：书籍变量（<c>book.putVariable</c> / <c>java.put</c> 无章节时）；</item>
/// <item><c>cv:&lt;chapterUrl&gt;:*</c>：章节变量（<c>chapter.putVariable</c> / 正文链的 <c>java.put</c>）；</item>
/// <item><c>c:*</c> / <c>file:*</c>：<c>cache.*</c>；</item>
/// <item><c>ck:&lt;domain&gt;</c>：<c>cookie.*</c> 的 Cookie 串；</item>
/// <item><c>brc:&lt;bookUrl&gt;</c>：<c>book.setUseReplaceRule</c> 的阅读配置。</item>
/// </list>
///
/// <para><b>持久化口径（有意决策，见方案 §7.1）</b>：<c>java.put</c> 默认<b>落盘</b>（与 legado 一致——
/// legado 把变量存进 Book/BookChapter 的 variableMap；reader 存进 <c>js_store.json</c>）。
/// 方案里曾建议"仅 variableComment 非空才落盘"，但那会让"登录 token / 设备指纹"这类
/// 书源真实依赖的变量在重启后丢失；为避免"一次性变量污染"，这里改为：容量/长度有界 + 提供
/// <see cref="Clear"/> + 每次覆盖写（同一键不会无限增殖）。</para>
///
/// <para><b>文件布局</b>：<c>%LocalAppData%\Uviewer\legado\sources\&lt;sha1(书源URL)&gt;.json</c>，
/// 内容 <c>{"source":"书源URL","values":{…}}</c>。用哈希做文件名是为了避开 URL 里的
/// <c>/ : ?</c> 等非法字符（也避免不同 URL 清洗后撞名）。</para>
///
/// <para><b>写盘策略</b>：每次 <see cref="Set"/> 立即写（tmp 文件 + <c>File.Move(overwrite)</c> 原子替换）。
/// 单源变量量级是个位数（原始样本 408 条里 <c>java.put</c> 共 232 次），整份文件只有几百字节，
/// 写透比"攒批 + 退出时落盘"更安全（进程被强杀也不丢状态），且不需要额外的生命周期钩子。</para>
///
/// <para><b>失败绝不反噬解析</b>：目录不可写、文件损坏、超长值等情况一律只记录到
/// <see cref="LastLoadError"/> / <see cref="LastSaveError"/>，读取退化为空、写入静默丢弃 ——
/// 与"诊断不能影响解析"的项目约定一致。</para>
/// </summary>
public sealed class SourceStateStore
{
    /// <summary>单源键数上限（防书源脚本把 <c>cache.put</c> 当数据库刷爆磁盘）。</summary>
    public const int DefaultMaxKeysPerSource = 512;

    /// <summary>单值长度上限（字符）。超长值直接拒写（书源变量都是 token/JSON 小串）。</summary>
    public const int DefaultMaxValueLength = 256 * 1024;

    /// <summary>进程级内存缓存（<c>cache.*Memory</c>）条目上限。</summary>
    private const int MaxMemoryEntries = 512;

    /// <summary>不落盘的静态序列化选项（文件很小，缩进只会让体积翻倍）。</summary>
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _directory;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>进程级内存缓存（<c>cache.putMemory/getFromMemory</c>），不落盘、随进程退出释放。</summary>
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);

    /// <summary>内存态实例（<paramref name="directory"/> 为 null = 不落盘）。</summary>
    /// <param name="directory">落盘目录；null / 空白 = 纯内存。</param>
    public SourceStateStore(string? directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
    }

    /// <summary>纯内存态实例（不落盘）：单元测试、以及"宿主还没注入持久目录"时的默认值。</summary>
    public SourceStateStore()
        : this(null)
    {
    }

    /// <summary>
    /// 进程级默认内存实例。<b>刻意不落盘</b>：<see cref="RuleEngine"/> / <see cref="BookSourceEngine"/>
    /// 的默认存储用它，这样引擎单测不会往用户的 <c>%LocalAppData%</c> 里写东西；
    /// 生产路径由 <c>BookSourceService</c> 按数据目录构造持久实例并注入。
    /// <para>进程内共享，故"目录链 put → 正文链 get"在无宿主接线时也成立。</para>
    /// </summary>
    public static SourceStateStore InMemory { get; } = new();

    /// <summary>默认落盘目录：<c>%LocalAppData%\Uviewer\legado\sources</c>。</summary>
    public static string DefaultDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                // 极端环境（无用户配置目录）：退临时目录，宁可重启丢状态也不要抛异常
                local = Path.GetTempPath();
            }

            return Path.Combine(local, "Uviewer", "legado", "sources");
        }
    }

    /// <summary>单源键数上限。</summary>
    public int MaxKeysPerSource { get; init; } = DefaultMaxKeysPerSource;

    /// <summary>单值长度上限（字符）。</summary>
    public int MaxValueLength { get; init; } = DefaultMaxValueLength;

    /// <summary>是否落盘（false = 纯内存，重启即丢）。</summary>
    public bool IsPersistent => _directory is not null;

    /// <summary>落盘目录（纯内存实例为 null）。</summary>
    public string? Directory => _directory;

    /// <summary>
    /// 读一个键；不存在 / 加载失败返回 null。
    /// <para>null 与空串语义不同：legado 脚本常写 <c>var v = cache.get('k'); if (v === null) …</c>，
    /// 故"没写过"必须返回 null 而不是 ""。</para>
    /// </summary>
    public string? Get(string sourceKey, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            return bucket.Values.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>
    /// 写一个键。返回是否被接受（超长 / 超出键数上限 / 空键 → false，调用方按"静默失败"处理）。
    /// <para><paramref name="value"/> 为 null 等价于删除（legado <c>put(key, null)</c> 语义）。</para>
    /// </summary>
    public bool Set(string sourceKey, string key, string? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (value is null)
        {
            Remove(sourceKey, key);
            return true;
        }

        if (value.Length > MaxValueLength)
        {
            return false;
        }

        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);

            if (!bucket.Values.ContainsKey(key) && bucket.Values.Count >= MaxKeysPerSource)
            {
                return false;
            }

            if (bucket.Values.TryGetValue(key, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return true;
            }

            bucket.Values[key] = value;
            Persist(normalized, bucket);
            return true;
        }
    }

    /// <summary>删除一个键（不存在也算成功）。</summary>
    public bool Remove(string sourceKey, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            if (!bucket.Values.Remove(key))
            {
                return false;
            }

            Persist(normalized, bucket);
            return true;
        }
    }

    /// <summary>该源当前全部键值（副本；测试/诊断用，调用频率低）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot(string sourceKey)
    {
        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            return new Dictionary<string, string>(bucket.Values, StringComparer.Ordinal);
        }
    }

    /// <summary>该源当前键数。</summary>
    public int Count(string sourceKey)
    {
        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            return bucket.Values.Count;
        }
    }

    /// <summary>清空某源的全部状态（退出登录、重新导入书源、测试隔离）。落盘实例同时删文件。</summary>
    public void Clear(string sourceKey)
    {
        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            bucket.Values.Clear();
            bucket.LoadError = null;
            bucket.SaveError = null;
            if (_directory is not null)
            {
                try
                {
                    var path = FilePathOf(normalized);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    bucket.SaveError = ex.Message;
                }
            }
        }
    }

    /// <summary>把某源的内存态强制写盘（当前实现是写透，故只有设置过 <see cref="Set"/> 失败后需要）。</summary>
    public void Flush(string sourceKey)
    {
        if (_directory is null)
        {
            return;
        }

        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            Persist(normalized, bucket);
        }
    }

    /// <summary>最近一次加载失败的原因（文件损坏 / IO 失败）；null = 一切正常。</summary>
    public string? LastLoadError(string sourceKey)
    {
        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            EnsureLoaded(normalized, bucket);
            return bucket.LoadError;
        }
    }

    /// <summary>最近一次写盘失败的原因；null = 一切正常。</summary>
    public string? LastSaveError(string sourceKey)
    {
        var normalized = Normalize(sourceKey);
        var bucket = BucketFor(normalized);
        lock (bucket.Gate)
        {
            return bucket.SaveError;
        }
    }

    // ==================== 进程级内存缓存（cache.*Memory） ====================

    /// <summary>读内存缓存（<c>cache.getFromMemory</c>）；不存在返回 null。</summary>
    public string? GetMemory(string sourceKey, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return _memory.TryGetValue(MemoryKey(sourceKey, key), out var value) ? value : null;
    }

    /// <summary>写内存缓存（不进磁盘，重启即丢；超上限时静默丢弃）。</summary>
    public void SetMemory(string sourceKey, string key, string? value)
    {
        if (string.IsNullOrEmpty(key) || value is null || value.Length > MaxValueLength)
        {
            return;
        }

        // 简单容量熔断：满了不再接受新键（缓存是尽力而为，淘汰策略的复杂度不值得）
        if (_memory.Count >= MaxMemoryEntries && !_memory.ContainsKey(MemoryKey(sourceKey, key)))
        {
            return;
        }

        _memory[MemoryKey(sourceKey, key)] = value;
    }

    /// <summary>删内存缓存。</summary>
    public void RemoveMemory(string sourceKey, string key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            _memory.TryRemove(MemoryKey(sourceKey, key), out _);
        }
    }

    /// <summary>清空全部内存缓存（测试/切换书源集合）。</summary>
    public void ClearMemory() => _memory.Clear();

    /// <summary>书源 URL 归一化（去空白 + 去尾斜杠）：<c>https://a.com/</c> 与 <c>https://a.com</c> 共用一个状态。</summary>
    public static string Normalize(string? sourceKey) =>
        (sourceKey ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>
    /// 落盘文件名：<c>sha1(归一化书源URL).json</c>。
    /// <para>公开是为了让测试/诊断能定位"这个源的状态存在哪个文件"，不要用来拼业务逻辑。</para>
    /// </summary>
    public string FilePathOf(string sourceKey)
    {
        if (_directory is null)
        {
            return string.Empty;
        }

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Normalize(sourceKey)));
        return Path.Combine(_directory, Convert.ToHexString(hash).ToLowerInvariant() + ".json");
    }

    private static string MemoryKey(string? sourceKey, string key) => Normalize(sourceKey) + "\u0001" + key;

    private Bucket BucketFor(string normalized) =>
        _buckets.GetOrAdd(normalized, static _ => new Bucket());

    /// <summary>按需加载该源的文件（每个源每个进程只读一次；失败退化为空并记原因）。</summary>
    private void EnsureLoaded(string normalized, Bucket bucket)
    {
        if (bucket.Loaded)
        {
            return;
        }

        bucket.Loaded = true;
        if (_directory is null)
        {
            return;
        }

        var path = FilePathOf(normalized);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<PersistedState>(stream, FileJson);
            if (state?.Values is { Count: > 0 } values)
            {
                foreach (var pair in values)
                {
                    // 加载时也套用长度上限：手改文件塞进巨串不应拖垮后续解析
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value is not null
                        && pair.Value.Length <= MaxValueLength)
                    {
                        bucket.Values[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 损坏文件：退化为空态 + 备份成 .corrupt（保留现场，下一次写入会重建正常文件）
            bucket.LoadError = ex.Message;
            TryQuarantine(path);
        }
    }

    /// <summary>原子写：先写 <c>.tmp</c> 再 <c>File.Move(overwrite)</c>，断电/被强杀都不会留下半份 JSON。</summary>
    private void Persist(string normalized, Bucket bucket)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = FilePathOf(normalized);
            var tmp = path + ".tmp";

            // 排序输出：同一份状态两次写入字节一致，便于 diff / 排障
            var values = new SortedDictionary<string, string>(bucket.Values, StringComparer.Ordinal);
            var payload = JsonSerializer.Serialize(
                new PersistedState { Source = normalized, Values = new Dictionary<string, string>(values, StringComparer.Ordinal) },
                FileJson);

            File.WriteAllBytes(tmp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(payload));
            File.Move(tmp, path, overwrite: true);
            bucket.SaveError = null;
        }
        catch (Exception ex)
        {
            bucket.SaveError = ex.Message;
        }
    }

    /// <summary>把损坏文件改名成 <c>.corrupt</c>（改名失败也不影响主流程）。</summary>
    private static void TryQuarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch
        {
            // 只读目录 / 文件被占用：保留原文件，下次写入会覆盖它
        }
    }

    /// <summary>一个书源的内存态 + 加载/保存状态（<see cref="Gate"/> 串行化同源读写）。</summary>
    private sealed class Bucket
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public bool Loaded;
        public string? LoadError;
        public string? SaveError;
    }

    /// <summary>文件格式（<c>{"source":"…","values":{…}}</c>）。</summary>
    private sealed class PersistedState
    {
        public string? Source { get; set; }

        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    }
}
