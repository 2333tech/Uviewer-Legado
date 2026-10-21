using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 书源 Cookie 仓库契约（阶段 4-3 新增）：<b>静态 HTTP 请求、<c>java.ajax</c> 同步桥、
/// 脚本 <c>cookie.*</c> / <c>java.getCookie</c> 共用同一份 Cookie</b>。
///
/// <para><b>为什么需要它（D3：会话割裂）</b>：阶段 4-2 之前每个 <c>HttpBookSourceFetcher</c>
/// 实例自带一个 <c>CookieContainer</c> 字典，而 <c>BookSourceEngine</c> / <c>SearchService</c> /
/// <c>BookSourceSearchService</c> 各自 <c>new</c> 一个 fetcher —— 于是"搜索用的 Cookie"和
/// "阅读用的 Cookie"是两套，登录态、验证码会话永远无法贯通。现在所有取数实现都从本契约拿容器。</para>
///
/// <para><b>语义对齐</b>：legado 的 <c>CookieStore</c> 按"二级域名"存取（<c>www.a.com</c> 登录后
/// <c>m.a.com</c> 可读），本实现用 <see cref="CookieContainer"/> 但把脚本写入的 Cookie 的
/// Domain 归并到近似二级域名（<see cref="SourceCookieStore.DomainOf"/>），与 legado 一致；
/// 服务端 <c>Set-Cookie</c> 则按真实 HTTP 语义（Domain/Path/过期）落到容器。</para>
/// </summary>
public interface ISourceCookieStore
{
    /// <summary>取某 URL 的 Cookie 请求头（无 Cookie 返回空串）。</summary>
    string GetCookieHeader(string sourceKey, string url);

    /// <summary>取 Cookie 串里某个键的值（legado <c>CookieStore.getKey</c>）；不存在返回空串。</summary>
    string GetCookieValue(string sourceKey, string url, string name);

    /// <summary>
    /// 覆盖写该 URL 所属域的 Cookie（legado <c>setCookie</c>：整串替换，不做合并）。
    /// <para>空串等价于"该域没有 Cookie"（全部清除）。</para>
    /// </summary>
    bool SetCookieHeader(string sourceKey, string url, string cookieHeader);

    /// <summary>按名合并写 Cookie（legado <c>replaceCookie</c>：旧值保留，同名以新值为准）。</summary>
    bool ReplaceCookieHeader(string sourceKey, string url, string cookieHeader);

    /// <summary>删除该 URL 所属域的全部 Cookie（legado <c>removeCookie</c>）。</summary>
    bool RemoveCookie(string sourceKey, string url);

    /// <summary>
    /// 收集 HTTP 响应的 <c>Set-Cookie</c>（按真实 HTTP 语义：Domain/Path/过期/Max-Age=0 删除）。
    /// 返回成功写入的条数；单个非法 Cookie 不影响其它。
    /// </summary>
    int CaptureSetCookie(string sourceKey, string url, IEnumerable<string> setCookieHeaders);

    /// <summary>清空某书源的全部 Cookie（退出登录 / 重新导入书源）。</summary>
    void Clear(string sourceKey);
}

/// <summary>
/// <see cref="ISourceCookieStore"/> 的默认实现：<b>每源一个 <see cref="CookieContainer"/></b>，
/// 进程内共享；可选地把 Cookie 快照落盘到 <see cref="SourceStateStore"/>（默认开启）。
///
/// <para><b>落盘口径</b>：脚本主动写（<c>cookie.setCookie</c>/<c>replaceCookie</c>/<c>removeCookie</c>、
/// <c>source.putLoginHeader</c>）立即落盘（登录 token 必须跨重启）；HTTP <c>Set-Cookie</c> 走
/// <b>节流落盘</b>（每源最多 2 秒一次），避免每个响应都写一次磁盘。落盘内容是 Cookie 快照
/// （名/值/域/路径/过期/安全标志），不是原始请求头，故重定向/路径语义不丢。</para>
///
/// <para><b>线程安全</b>：<see cref="CookieContainer"/> 本身不是线程安全的，本类对同源容器
/// 的读写全部串行化（按源加锁）；不同书源之间互不阻塞。</para>
/// </summary>
public sealed class SourceCookieStore : ISourceCookieStore
{
    /// <summary>落盘节流窗口：同一书源 2 秒内最多写一次（HTTP Set-Cookie 路径）。</summary>
    private const int PersistThrottleMs = 2000;

    /// <summary>持久化键（每源一个：Cookie 快照 JSON）。</summary>
    private const string PersistKey = "cookies";

    /// <summary>持久化 JSON 选项（紧凑 + camelCase，与 <see cref="SourceStateStore"/> 文件风格一致）。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>常见"二级公共后缀"（末两段是它时再往前取一段，与 4-2 的域归并口径一致）。</summary>
    private static readonly string[] TwoLevelSuffixes =
    {
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "com.tw", "com.hk",
        "co.uk", "org.uk", "co.jp", "ne.jp", "com.au", "com.sg",
    };

    private readonly SourceStateStore? _state;
    private readonly ConcurrentDictionary<string, Jar> _jars = new(StringComparer.Ordinal);

    /// <summary>纯内存实例（不落盘）：引擎/脚本单测的默认值。</summary>
    public static SourceCookieStore Memory { get; } = new(null);

    /// <param name="state">落盘仓库；null = 纯内存（重启即丢）。</param>
    public SourceCookieStore(SourceStateStore? state = null) => _state = state;

    /// <summary>是否落盘。</summary>
    public bool IsPersistent => _state is not null;

    /// <inheritdoc />
    public string GetCookieHeader(string sourceKey, string url) =>
        Read(sourceKey, jar =>
        {
            var uri = UriOf(url);
            return uri is null ? string.Empty : jar.Container.GetCookieHeader(uri);
        });

    /// <inheritdoc />
    public string GetCookieValue(string sourceKey, string url, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var header = GetCookieHeader(sourceKey, url);
        if (header.Length == 0)
        {
            return string.Empty;
        }

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(pair.AsSpan(0, separator).Trim().ToString(), name, StringComparison.Ordinal))
            {
                return pair.AsSpan(separator + 1).Trim().ToString();
            }
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public bool SetCookieHeader(string sourceKey, string url, string cookieHeader)
    {
        var uri = UriOf(url);
        if (uri is null)
        {
            return false;
        }

        var domain = DomainOf(url);
        var pairs = ParsePairs(cookieHeader);
        var jar = JarFor(sourceKey);
        lock (jar.Gate)
        {
            EnsureLoaded(sourceKey, jar);

            // 覆盖写 = 先清同域旧 Cookie，再按"归属到近似二级域名"写入
            // （legado 的 setCookie 是整域替换；脚本随后在任意子域都能读到）
            RemoveDomainCookies(jar, domain);
            if (!string.IsNullOrEmpty(domain) && pairs.Count > 0)
            {
                foreach (var (name, value) in pairs)
                {
                    TryAdd(jar.Container, name, value, domain);
                }
            }

            Persist(sourceKey, jar, force: true);
        }

        return true;
    }

    /// <inheritdoc />
    public bool ReplaceCookieHeader(string sourceKey, string url, string cookieHeader)
    {
        var uri = UriOf(url);
        if (uri is null || string.IsNullOrEmpty(cookieHeader))
        {
            return false;
        }

        var domain = DomainOf(url);
        var jar = JarFor(sourceKey);
        lock (jar.Gate)
        {
            EnsureLoaded(sourceKey, jar);

            // 以当前请求头的键序为基，新值覆盖同名键（legado replaceCookie 的 cookieToMap+putAll）
            var merged = ParsePairs(jar.Container.GetCookieHeader(uri));
            foreach (var (name, value) in ParsePairs(cookieHeader))
            {
                var index = merged.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    merged[index] = (name, value);
                }
                else
                {
                    merged.Add((name, value));
                }
            }

            RemoveDomainCookies(jar, domain);
            if (!string.IsNullOrEmpty(domain))
            {
                foreach (var (name, value) in merged)
                {
                    TryAdd(jar.Container, name, value, domain);
                }
            }

            Persist(sourceKey, jar, force: true);
        }

        return true;
    }

    /// <inheritdoc />
    public bool RemoveCookie(string sourceKey, string url)
    {
        var domain = DomainOf(url);
        if (string.IsNullOrEmpty(domain))
        {
            return false;
        }

        var jar = JarFor(sourceKey);
        lock (jar.Gate)
        {
            EnsureLoaded(sourceKey, jar);
            var removed = RemoveDomainCookies(jar, domain);
            Persist(sourceKey, jar, force: true);
            return removed > 0;
        }
    }

    /// <inheritdoc />
    public int CaptureSetCookie(string sourceKey, string url, IEnumerable<string> setCookieHeaders)
    {
        var uri = UriOf(url);
        if (uri is null)
        {
            return 0;
        }

        var jar = JarFor(sourceKey);
        var applied = 0;
        lock (jar.Gate)
        {
            EnsureLoaded(sourceKey, jar);
            foreach (var header in setCookieHeaders)
            {
                if (string.IsNullOrWhiteSpace(header))
                {
                    continue;
                }

                try
                {
                    // 交给 CookieContainer 按真实语义解析（Domain/Path/Expires/Max-Age）
                    jar.Container.SetCookies(uri, header);
                    applied++;
                }
                catch (CookieException)
                {
                    // 单个非法 Set-Cookie 不影响其它
                }
                catch (ArgumentException)
                {
                    // 非法 Domain/Path 等参数错误同理
                }
            }

            if (applied > 0)
            {
                Persist(sourceKey, jar, force: false);
            }
        }

        return applied;
    }

    /// <inheritdoc />
    public void Clear(string sourceKey)
    {
        var key = SourceStateStore.Normalize(sourceKey);
        var jar = JarFor(key);
        lock (jar.Gate)
        {
            jar.Container = new CookieContainer();
            jar.Loaded = true;
            _state?.Remove(key, PersistKey);
        }
    }

    /// <summary>
    /// 近似二级域名（Cookie 归并键，legado <c>NetworkUtils.getSubDomain</c> 的近似）：
    /// <c>a.b.com.cn</c> → <c>b.com.cn</c>，<c>www.a.com</c> → <c>a.com</c>，非法 URL → 原文小写化。
    /// </summary>
    public static string DomainOf(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return text.TrimEnd('/').ToLowerInvariant();
        }

        var host = uri.Host.ToLowerInvariant();
        // IP 地址没有"二级域名"概念：整段作为域（否则 127.0.0.1 会被切成 "0.1"）
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        var labels = host.Split('.');
        if (labels.Length <= 2)
        {
            return host;
        }

        var lastTwo = labels[^2] + "." + labels[^1];
        foreach (var suffix in TwoLevelSuffixes)
        {
            if (string.Equals(lastTwo, suffix, StringComparison.Ordinal) && labels.Length >= 3)
            {
                return labels[^3] + "." + lastTwo;
            }
        }

        return lastTwo;
    }

    // ============================================================
    // 内部
    // ============================================================

    private Jar JarFor(string? sourceKey) =>
        _jars.GetOrAdd(SourceStateStore.Normalize(sourceKey), static _ => new Jar());

    private T Read<T>(string? sourceKey, Func<Jar, T> body)
    {
        var jar = JarFor(sourceKey);
        lock (jar.Gate)
        {
            EnsureLoaded(SourceStateStore.Normalize(sourceKey), jar);
            return body(jar);
        }
    }

    /// <summary>绝对 URL 才可交给 CookieContainer（容器按 scheme/host/path 选择 Cookie）。</summary>
    private static Uri? UriOf(string? url) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>按首次出现顺序解析 <c>a=1; b=2</c>（值里允许再出现 '='，如 base64）。</summary>
    private static List<(string Name, string Value)> ParsePairs(string? cookieHeader)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return pairs;
        }

        foreach (var pair in cookieHeader.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair.AsSpan(0, separator).Trim().ToString();
            if (name.Length == 0)
            {
                continue;
            }

            pairs.Add((name, pair.AsSpan(separator + 1).Trim().ToString()));
        }

        return pairs;
    }

    /// <summary>把一条 Cookie 归属到近似二级域名（带前导点 → 对所有子域可见，对齐 legado）。</summary>
    private static void TryAdd(CookieContainer container, string name, string value, string domain)
    {
        try
        {
            // IP（如 127.0.0.1）没有子域概念，必须 host-only：带前导点的 IP 会被容器判非法
            var hostOnly = domain.IndexOf('.') < 0 || IPAddress.TryParse(domain, out _);
            var cookie = new Cookie(name, value, "/", hostOnly ? domain : "." + domain)
            {
                // 脚本写入的 Cookie 是"书源会话"语义：给它 30 天寿命，避免被容器当会话 Cookie
                // 在某些实现下丢弃；过期由 CookieContainer 自己判定。
                Expires = DateTime.UtcNow.AddDays(30),
            };
            container.Add(cookie);
        }
        catch (CookieException)
        {
            // 非法名/值（超长、控制字符）忽略：不能让脚本写坏整个容器
        }
        catch (ArgumentException)
        {
            // 非法域名同理
        }
    }

    /// <summary>删除属于该近似二级域名的全部 Cookie（CookieContainer 不支持单条删除 → 重建容器）。</summary>
    private static int RemoveDomainCookies(Jar jar, string domain)
    {
        if (string.IsNullOrEmpty(domain))
        {
            return 0;
        }

        var all = jar.Container.GetAllCookies().Cast<Cookie>().ToList();
        var removed = all.RemoveAll(c => BelongsTo(c.Domain, domain));
        if (removed == 0)
        {
            return 0;
        }

        var rebuilt = new CookieContainer();
        foreach (var cookie in all)
        {
            try
            {
                // GetAllCookies 的 Cookie 是"松对象"，不能直接 Add 回容器（会因 Domain 校验失败），
                // 按字段重建一份。
                rebuilt.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                {
                    Expires = cookie.Expires,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                });
            }
            catch (CookieException)
            {
                // 丢掉无法重建的条目（原容器里也已是不可用状态）
            }
            catch (ArgumentException)
            {
                // 同上：字段组合非法（空 Name 等）的条目原容器里也用不了，直接丢
            }
        }

        jar.Container = rebuilt;
        return removed;
    }

    /// <summary>Cookie 的 Domain 是否属于给定近似二级域名（带不带前导点都算）。</summary>
    private static bool BelongsTo(string? cookieDomain, string domain)
    {
        var value = (cookieDomain ?? string.Empty).TrimStart('.').ToLowerInvariant();
        var target = domain.TrimStart('.').ToLowerInvariant();
        return value.Length > 0
            && (string.Equals(value, target, StringComparison.Ordinal)
                || value.EndsWith("." + target, StringComparison.Ordinal));
    }

    /// <summary>按需从磁盘恢复该源 Cookie 快照（每源每进程只读一次）。</summary>
    private void EnsureLoaded(string key, Jar jar)
    {
        if (jar.Loaded)
        {
            return;
        }

        jar.Loaded = true;
        var json = _state?.Get(key, PersistKey);
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<CookieSnapshot>(json, Json);
            if (snapshot?.Cookies is not { Count: > 0 } records)
            {
                return;
            }

            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.Name) || string.IsNullOrEmpty(record.Domain))
                {
                    continue;
                }

                var expires = record.ExpiresMs is { } ms
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.MinValue;
                if (expires != DateTime.MinValue && expires <= DateTime.UtcNow)
                {
                    // 过期 Cookie 不再恢复（否则会在下一次读时突然"复活"）
                    continue;
                }

                jar.Container.Add(new Cookie(record.Name, record.Value ?? string.Empty,
                    string.IsNullOrEmpty(record.Path) ? "/" : record.Path, record.Domain)
                {
                    Expires = expires,
                    Secure = record.Secure,
                    HttpOnly = record.HttpOnly,
                });
            }
        }
        catch (Exception)
        {
            // 快照损坏：当作"没有 Cookie"（登录态丢失优于崩溃）
        }
    }

    /// <summary>把该源 Cookie 快照落盘（<paramref name="force"/> 为 false 时按 2s 节流）。</summary>
    private void Persist(string key, Jar jar, bool force)
    {
        if (_state is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now - jar.LastPersistTicks < PersistThrottleMs)
        {
            return;
        }

        jar.LastPersistTicks = now;

        var records = new List<CookieRecord>();
        foreach (var cookie in jar.Container.GetAllCookies().Cast<Cookie>())
        {
            if (string.IsNullOrEmpty(cookie.Domain) || string.IsNullOrEmpty(cookie.Name))
            {
                continue;
            }

            records.Add(new CookieRecord
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = cookie.Path,
                ExpiresMs = cookie.Expires == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(cookie.Expires).ToUnixTimeMilliseconds(),
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
            });
        }

        if (records.Count == 0)
        {
            _state.Remove(key, PersistKey);
            return;
        }

        _state.Set(key, PersistKey, JsonSerializer.Serialize(new CookieSnapshot { Cookies = records }, Json));
    }

    /// <summary>一个书源的 Cookie 容器 + 串行锁 + 恢复/落盘状态。</summary>
    private sealed class Jar
    {
        public readonly object Gate = new();
        public CookieContainer Container = new();
        public bool Loaded;
        public long LastPersistTicks;
    }

    /// <summary>落盘格式：<c>{"cookies":[{name,value,domain,path,expiresMs,secure,httpOnly}]}</c>。</summary>
    private sealed class CookieSnapshot
    {
        public List<CookieRecord> Cookies { get; set; } = new();
    }

    private sealed class CookieRecord
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Domain { get; set; }
        public string? Path { get; set; }
        public long? ExpiresMs { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
    }
}
