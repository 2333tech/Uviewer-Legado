using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.BookSources;
using Uviewer.Services.Legado.Models;
using Xunit;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

namespace Uviewer.Services.Legado.Tests;

/// <summary>
/// 下载文件标记 / sidecar / 差分在线更新（用户 2026-10-21 需求）：
/// <list type="number">
/// <item>文件头标记（UUID/书源URL/书籍URL）解析与幂等插入；sidecar 存大规模内容（目录快照）。</item>
/// <item>差分更新：只联网下载新增/变化章；远程目录大规模修改 → 另存新文件防覆盖。</item>
/// <item>「分卷阅读±数字」底层内置净化：标题/正文双通道恒开。</item>
/// </list>
/// </summary>
public class DownloadMarkerUpdateTests : IDisposable
{
    private readonly string _dir;

    public DownloadMarkerUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "uviewer-marker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ============================================================
    // 标记块：解析 / 幂等插入
    // ============================================================

    [Fact]
    public void Marker_Parse_Roundtrip()
    {
        var block = DownloadedBookMarker.BuildHeaderBlock(
            "abc123", "https://s.test/src", "https://s.test/book", "2026-10-21 18:00:00");
        var raw = "我的书\n" + block + new string('=', 32) + "\n\n第一章";

        var info = DownloadedBookMarker.TryParseHeader(raw);
        Assert.NotNull(info);
        Assert.Equal("abc123", info!.Uuid);
        Assert.Equal("https://s.test/src", info.SourceUrl);
        Assert.Equal("https://s.test/book", info.BookUrl);
        Assert.Equal("2026-10-21 18:00:00", info.ExportedAt);
    }

    [Fact]
    public void Marker_Insert_Is_Idempotent()
    {
        var block = DownloadedBookMarker.BuildHeaderBlock("same-uuid", null, null, null);
        var (once, inserted) = DownloadedBookMarker.InsertHeaderBlock("正文", block);
        Assert.True(inserted);
        Assert.StartsWith(DownloadedBookMarker.HeaderTag, once, StringComparison.Ordinal);

        // 同 UUID 再插：不重复
        var (twice, inserted2) = DownloadedBookMarker.InsertHeaderBlock(once, block);
        Assert.False(inserted2);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Marker_Parse_Ignores_Non_Marker_Files()
    {
        Assert.Null(DownloadedBookMarker.TryParseHeader("第一章 开始\n正文"));
        Assert.Null(DownloadedBookMarker.TryParseHeader(null));
    }

    // ============================================================
    // Sidecar：读写 + 改名后按 UUID 找回
    // ============================================================

    [Fact]
    public void Sidecar_Save_Load_And_Find_By_Uuid_After_Rename()
    {
        var txt = Path.Combine(_dir, "book.txt");
        File.WriteAllText(txt, "[Uviewer-Legado]\nUUID: uuid-1\n第一章");
        var data = new DownloadedBookMarker.SidecarData(
            "uuid-1", "https://s.test", "https://s.test/b", "书名", "t",
            new List<DownloadedBookMarker.SidecarTocItem> { new("第一章", "https://s.test/c/1") });
        DownloadedBookMarker.SaveSidecar(txt, data);

        Assert.NotNull(DownloadedBookMarker.LoadSidecar(txt));

        // 改名：主 sidecar 路径失效，但同目录扫描按 UUID 找回
        var renamed = Path.Combine(_dir, "book-改名的.txt");
        File.Move(txt, renamed);
        var found = DownloadedBookMarker.LoadSidecar(renamed, "uuid-1");
        Assert.NotNull(found);
        Assert.Equal("书名", found!.BookName);
        Assert.Single(found.Toc);
    }

    // ============================================================
    // 差分逻辑：对比 / 大规模判定 / 旧文件切片
    // ============================================================

    private static DownloadedBookMarker.SidecarTocItem Item(string name, string url) => new(name, url);

    [Fact]
    public void Diff_Append_Insert_Rename_Remove()
    {
        var old = new[] { Item("一", "u1"), Item("二", "u2"), Item("三", "u3") };

        // 追加：u4 新章
        var appended = new[] { Item("一", "u1"), Item("二", "u2"), Item("三", "u3"), Item("四", "u4") };
        var d1 = TocDiffLogic.Compare(old, appended);
        Assert.Equal(new[] { 3 }, d1.AddedIndexes);
        Assert.Empty(d1.ChangedIndexes);
        Assert.Equal(0, d1.RemovedCount);
        Assert.False(TocDiffLogic.IsMassiveChange(d1, old.Length));

        // 中间插入 + 改名：u2.5 新章、u3 改名
        var edited = new[] { Item("一", "u1"), Item("二", "u2"), Item("二点五", "u2_5"), Item("三（修）", "u3") };
        var d2 = TocDiffLogic.Compare(old, edited);
        Assert.Equal(new[] { 2 }, d2.AddedIndexes);
        Assert.Equal(new[] { 3 }, d2.ChangedIndexes);
        Assert.Equal(0, d2.RemovedCount);

        // 大规模：半数以上被改 → 另存保护
        var massive = Enumerable.Range(0, 50)
            .Select(i => Item($"第{i}章", i < 12 ? $"new{i}" : $"u{i}")).ToArray();
        var old50 = Enumerable.Range(0, 50).Select(i => Item($"第{i}章", $"u{i}")).ToArray();
        var d3 = TocDiffLogic.Compare(old50, massive);
        Assert.True(TocDiffLogic.IsMassiveChange(d3, old50.Length));
    }

    [Fact]
    public void ExtractOldChapters_Matches_BuildChapter_Format()
    {
        // 与 BuildChapter 完全一致的格式：\n 章名 \n ----32 \n\n 正文 \n
        static string Chapter(string name, string body) =>
            "\n" + name + "\n" + new string('-', 32) + "\n\n" + body + "\n";

        var raw = "书名\n[Uviewer-Legado]\nUUID: u1\n====\n"
                  + Chapter("第一章", "正文一") + Chapter("第二章", "正文二") + Chapter("第一章", "重复章名正文");
        var toc = new[] { Item("第一章", "u1"), Item("第二章", "u2"), Item("第一章", "u1b") };

        var extracted = TocDiffLogic.ExtractOldChapters(raw, toc);

        // 顺序游标：两个同名"第一章"按出现顺序分别对齐
        Assert.Equal(3, extracted.Count);
        Assert.Equal("正文一", extracted[0]);
        Assert.Equal("正文二", extracted[1]);
        Assert.Equal("重复章名正文", extracted[2]);
        // 分隔线与空行不混进正文
        Assert.DoesNotContain("-", extracted[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractOldChapters_Missing_Chapter_Is_Not_In_Result()
    {
        var raw = "\n第一章\n" + new string('-', 32) + "\n\n正文一\n";
        var toc = new[] { Item("第一章", "u1"), Item("第二章", "u2") };

        var extracted = TocDiffLogic.ExtractOldChapters(raw, toc);

        Assert.Single(extracted);
        Assert.Equal("正文一", extracted[0]);   // 第二章缺失 → 调用方标为需重新下载
    }

    // ============================================================
    // 内置净化：分卷阅读±数字（双通道恒开）
    // ============================================================

    [Fact]
    public void Builtin_Purifier_Removes_VolumeReading_Lines()
    {
        var ctx = PurifyContext.Default;
        var purifier = new ContentPurifier();

        // 正文整行剔除（全角数字也认），且不留空行（连换行一起吃掉）
        var content = "上一段\n分卷阅读001\n下一段\n分卷阅读２３\n结尾";
        var purified = purifier.Apply(content, in ctx);
        Assert.Equal("上一段\n下一段\n结尾", purified);

        // 标题通道：该行被剔成空 → 永远不可能成为目录标题
        Assert.Equal(string.Empty, purifier.ApplyToTitle("分卷阅读12", in ctx));
        Assert.Equal("第一章 标题", purifier.ApplyToTitle("第一章 标题", in ctx));

        // 无用户规则也生效（ContentPurifier 直接用，Rules 未设置）
        // 正文中恰好包含"分卷阅读"字样的正常句子不受影响（必须整行匹配）
        var sentence = "他说这是分卷阅读的规则";
        Assert.Equal(sentence, purifier.Apply(sentence, in ctx));
    }

    // ============================================================
    // UUID 绑定缓存：TextCacheEntry.Uuid + FindByUuid
    // ============================================================

    [Fact]
    public void TextCacheStore_Finds_Entry_By_Uuid()
    {
        var store = new TextCacheStore(_dir);
        var entry = new TextCacheEntry
        {
            SourceKey = @"C:\old\path\book.txt",
            Uuid = "cache-uuid-9",
            FileSize = 1,
            FileLastWriteUtcTicks = 1,
        };
        store.SaveAsync(entry).Wait();

        // 路径 key 已失效（文件改名），按 UUID 仍能找回
        Assert.Null(store.Load(@"C:\new\path\book.txt"));
        var found = store.FindByUuid("cache-uuid-9");
        Assert.NotNull(found);
        Assert.Equal(@"C:\old\path\book.txt", found!.SourceKey);
        Assert.Null(store.FindByUuid("no-such-uuid"));
    }

    // ============================================================
    // 在线更新端到端（回环 HTTP + 真实引擎链）
    // ============================================================

    [Fact]
    public async Task UpdateAsync_Differential_Append_Only_Fetches_New_Chapters()
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var tocRef = new[] { (Name: "第一章", Url: "/c/1"), (Name: "第二章", Url: "/c/2") };
        var fetchedPaths = new List<string>();

        using var server = TestLoopbackServer.Start(request =>
        {
            lock (fetchedPaths)
            {
                fetchedPaths.Add(request.Path);
            }

            if (request.Path == "/toc")
            {
                var items = string.Join(",", tocRef.Select(c =>
                    $"{{\"title\":\"{c.Name}\",\"url\":\"{c.Url}\"}}"));
                return new TestLoopbackResponse(200, $"{{\"data\":[{items}]}}");
            }

            return new TestLoopbackResponse(
                200, bodies.GetValueOrDefault(request.Path.TrimStart('/'), "默认正文"));
        });

        bodies["c/1"] = "正文一";
        bodies["c/2"] = "正文二";
        var base_ = server.BaseUrl;

        // 服务：真实 HTTP 取数管线 + 已注册书源
        File.WriteAllText(
            Path.Combine(_dir, "booksources.json"),
            BookSourceSerializer.SerializeList(new[]
            {
                new BookSourceModel
                {
                    BookSourceUrl = base_,
                    RuleToc = new Uviewer.Services.Legado.Models.TocRule
                    {
                        ChapterList = "data[*]",
                        ChapterName = "title",
                        ChapterUrl = "url",
                    },
                    RuleContent = new Uviewer.Services.Legado.Models.ContentRule { Content = string.Empty },
                },
            }));
        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();

        // 模拟"已下载"：TXT 头标记（uuid + 原始链接）+ 两章正文 + sidecar 快照
        var uuid = "update-e2e-uuid";
        var txtPath = Path.Combine(_dir, "测试书.txt");
        var header = DownloadedBookMarker.BuildHeaderBlock(uuid, base_, base_ + "/toc", "2026-10-21 18:00:00");
        File.WriteAllText(txtPath,
            "测试书\n" + header + new string('=', 32) + "\n"
            + $"\n第一章\n{new string('-', 32)}\n\n正文一\n"
            + $"\n第二章\n{new string('-', 32)}\n\n正文二\n",
            new UTF8Encoding(false));
        DownloadedBookMarker.SaveSidecar(txtPath, new DownloadedBookMarker.SidecarData(
            uuid, base_, base_ + "/toc", "测试书", "t",
            new List<DownloadedBookMarker.SidecarTocItem>
            {
                new("第一章", base_ + "/c/1"),
                new("第二章", base_ + "/c/2"),
            }));
        fetchedPaths.Clear();

        // 远程追更：新增第三章（前两章不动）
        bodies["c/3"] = "正文三";
        tocRef = new[] { (Name: "第一章", Url: "/c/1"), (Name: "第二章", Url: "/c/2"), (Name: "第三章", Url: "/c/3") };

        var downloads = new BookSourceDownloadService(service, _dir);
        var result = await downloads.UpdateAsync(txtPath);

        // 差分成功：只联网拉了第三章（旧章零请求；/toc 可能被详情+目录两步各请求一次）
        Assert.True(result.Success, result.Message);
        Assert.False(result.MassiveResaved);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, fetchedPaths.Count(p => p == "/c/3"));
        Assert.DoesNotContain("/c/1", fetchedPaths);
        Assert.DoesNotContain("/c/2", fetchedPaths);

        // 文件内容：三章齐全（旧章正文来自本地切片），UUID 不变
        var updated = File.ReadAllText(txtPath);
        Assert.Contains("正文一", updated, StringComparison.Ordinal);
        Assert.Contains("正文二", updated, StringComparison.Ordinal);
        Assert.Contains("正文三", updated, StringComparison.Ordinal);
        Assert.Contains("UUID: " + uuid, updated, StringComparison.Ordinal);
        Assert.Contains("第三章", updated, StringComparison.Ordinal);

        // sidecar 已刷新为新目录
        var sidecar = DownloadedBookMarker.LoadSidecar(txtPath, uuid);
        Assert.NotNull(sidecar);
        Assert.Equal(3, sidecar!.Toc.Count);
    }

    [Fact]
    public async Task UpdateAsync_Massive_Change_Resaves_To_New_File()
    {
        (string Name, string Url)[] tocRef = null!;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        using var server = TestLoopbackServer.Start(request =>
        {
            if (request.Path == "/toc")
            {
                var items = string.Join(",", tocRef.Select(c =>
                    $"{{\"title\":\"{c.Name}\",\"url\":\"{c.Url}\"}}"));
                return new TestLoopbackResponse(200, $"{{\"data\":[{items}]}}");
            }

            return new TestLoopbackResponse(
                200, bodies.GetValueOrDefault(request.Path.TrimStart('/'), "正文"));
        });

        var base_ = server.BaseUrl;
        File.WriteAllText(
            Path.Combine(_dir, "booksources.json"),
            BookSourceSerializer.SerializeList(new[]
            {
                new BookSourceModel
                {
                    BookSourceUrl = base_,
                    RuleToc = new Uviewer.Services.Legado.Models.TocRule
                    {
                        ChapterList = "data[*]",
                        ChapterName = "title",
                        ChapterUrl = "url",
                    },
                    RuleContent = new Uviewer.Services.Legado.Models.ContentRule { Content = string.Empty },
                },
            }));
        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();

        // 旧目录 50 章；远程把其中 12 章改名（≥ max(10, 20%) → 大规模）
        var oldToc = Enumerable.Range(0, 50)
            .Select(i => new DownloadedBookMarker.SidecarTocItem($"第{i}章", $"{base_}/c/{i}"))
            .ToList();
        var uuid = "massive-e2e-uuid";
        var txtPath = Path.Combine(_dir, "大部头.txt");
        var header = DownloadedBookMarker.BuildHeaderBlock(uuid, base_, base_ + "/toc", "t");
        File.WriteAllText(txtPath, "大部头\n" + header + new string('=', 32) + "\n", new UTF8Encoding(false));
        DownloadedBookMarker.SaveSidecar(txtPath, new DownloadedBookMarker.SidecarData(
            uuid, base_, base_ + "/toc", "大部头", "t", oldToc));

        tocRef = Enumerable.Range(0, 50)
            .Select(i => (Name: i < 12 ? $"改版第{i}章" : $"第{i}章", Url: $"{base_}/c/{i}"))
            .ToArray();

        var downloads = new BookSourceDownloadService(service, _dir);
        var result = await downloads.UpdateAsync(txtPath);

        // 大改 → 另存新文件；原文件一字不动
        Assert.True(result.Success, result.Message);
        Assert.True(result.MassiveResaved);
        Assert.True(File.Exists(result.FilePath));
        Assert.NotEqual(txtPath, result.FilePath);
        Assert.Contains("远程大改", Path.GetFileName(result.FilePath), StringComparison.Ordinal);
        Assert.True(File.Exists(result.FilePath + ".legado.json"));
        Assert.NotEqual(uuid, DownloadedBookMarker.TryReadFile(result.FilePath)!.Uuid);
    }

    [Fact]
    public async Task UpdateAsync_Rejects_File_Without_Marker()
    {
        await using var service = new BookSourceService(_dir);
        await service.EnsureInitializedAsync();
        var downloads = new BookSourceDownloadService(service, _dir);

        var plain = Path.Combine(_dir, "普通.txt");
        File.WriteAllText(plain, "第一章\n正文");

        var result = await downloads.UpdateAsync(plain);
        Assert.False(result.Success);
        Assert.Contains("缺原始链接标记", result.Message, StringComparison.Ordinal);
    }
}

/// <summary>极简回环 HTTP 服务（与 Stage4NetworkTests 同款实现，独立副本避免跨类依赖）。</summary>
internal sealed class TestLoopbackServer : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TestLoopbackServer(Socket listener, Func<TestLoopbackRequest, TestLoopbackResponse> handler)
    {
        _listener = listener;
        var local = (System.Net.IPEndPoint)listener.LocalEndPoint!;
        BaseUrl = $"http://127.0.0.1:{local.Port}";
        _loop = Task.Run(() => AcceptLoopAsync(handler, _cts.Token));
    }

    public string BaseUrl { get; }

    public static TestLoopbackServer Start(Func<TestLoopbackRequest, TestLoopbackResponse> handler)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Listen(16);
        return new TestLoopbackServer(listener, handler);
    }

    private async Task AcceptLoopAsync(Func<TestLoopbackRequest, TestLoopbackResponse> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, handler), CancellationToken.None);
        }
    }

    private static async Task HandleAsync(Socket client, Func<TestLoopbackRequest, TestLoopbackResponse> handler)
    {
        try
        {
            using (client)
            using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var path = parts.Length > 1 ? parts[1] : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                var response = handler(new TestLoopbackRequest(method, path));
                var payload = Encoding.UTF8.GetBytes(response.Body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {response.Status} OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 测试服务：连接级错误直接吞掉
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Dispose();
        }
        catch
        {
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }
}

/// <summary>回环请求（测试只关心方法与路径）。</summary>
internal sealed record TestLoopbackRequest(string Method, string Path);

/// <summary>回环响应。</summary>
internal sealed record TestLoopbackResponse(int Status, string Body);
