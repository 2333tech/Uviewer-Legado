using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;
using CoreTocItem = Uviewer.Services.Legado.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 整本下载：把一本 book's 全部章节抓下来落成单个 UTF-8 文本文件，之后可离线阅读。
    ///
    /// <para><b>为什么落成 txt 而不是直接进内存</b>：整本小说常有 500~2000 章、十几 MB。
    /// 落盘后（①）阅读走现成的本地 txt 通路（目录/进度/排版全部复用，零新增渲染代码）；
    /// （②）关掉对话框、断网、退出程序都不影响已下内容；（③）内存有界，不与渲染抢资源。</para>
    ///
    /// <para><b>并发与顺序</b>：按批（默认 25 章一批）调用
    /// <see cref="BookSourceEngine.GetContentsIndexedAsync"/> 并行抓取，
    /// <b>按下标对齐</b>后逐章写文件——所以文件里的章节顺序永远与目录一致，
    /// 且单章失败只留空缺不会让后面的章名错位。</para>
    ///
/// <para><b>取消</b>：每个下载任务持一个 <see cref="CancellationTokenSource"/>；
    /// 取消时删除半成品文件（不留下"看起来完整其实缺章"的 txt）。</para>
  ///
    /// <para><b>落盘的是原文（未净化）</b>：抓取阶段不做净化（净化统一在"读取 → 渲染"
    /// 之间执行），所以下载下来的 txt 保留原始内容——之后用"替换净化总开关"打开它，
    /// 总开关依然能立即生效，不会因为"下载时已经净化过"而无法还原。</para>
    ///
    /// <para><b>线程模型</b>：单本书同一时刻只允许一个下载任务（按键即切换状态）。
    /// 进度回调在工作线程触发，UI 侧需自行切回 UI 线程。</para>
    /// </summary>
    public sealed class BookSourceDownloadService
    {
        /// <summary>每批章数：一批 = 一次并行窗口。25 章 @16 并发 ≈ 1~2 个连接往返。</summary>
        private const int BatchSize = 25;

        /// <summary>单批内的抓取并发（对齐库侧 BookSourceLimits 默认值）。</summary>
        private const int FetchConcurrency = 8;

        private readonly BookSourceService _service;

        /// <summary>构造时显式指定的目录（null = 跟随服务侧配置，每次下载时现取）。</summary>
        private readonly string? _downloadDirOverride;

        /// <summary>下载进度（一次回调一个快照；UI 直接取字段渲染）。</summary>
        public sealed record DownloadProgress(
            int Done,
            int Total,
            string ChapterName,
            string FilePath)
        {
            /// <summary>0~1 进度（总数未知时为 0）。</summary>
            public double Ratio => Total <= 0 ? 0d : Math.Clamp((double)Done / Total, 0d, 1d);
        }

        /// <summary>下载结果。</summary>
        public sealed record DownloadResult(bool Success, bool Cancelled, string FilePath, string Message);

        public BookSourceDownloadService(BookSourceService service, string? downloadDirectory = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _downloadDirOverride = string.IsNullOrWhiteSpace(downloadDirectory) ? null : downloadDirectory;
        }

        /// <summary>
        /// 下载目录：显式传入优先，否则<b>每次现取</b>服务侧配置
        /// （设置菜单 →「书源管理」→「下载设置」可改，默认 Windows 的"下载"文件夹）。
        /// <para>不缓存到字段：长驻对象（如搜索对话框）里的旧快照会让"改了目录却还下到老地方"。</para>
        /// </summary>
        public string DownloadDirectory
        {
            get
            {
                if (_downloadDirOverride is not null)
                {
                    return _downloadDirOverride;
                }

                var configured = _service.DownloadDirectory;
                return string.IsNullOrWhiteSpace(configured)
                    ? BookSourceService.DefaultDownloadDirectory
                    : configured;
            }
        }

        /// <summary>
        /// 下载整本书。进度经 <paramref name="progress"/> 回报（工作线程触发）。
        /// <para>失败策略：目录为空 / 无可下载章节 → 返回失败并给出可读原因（不抛异常，
        /// 让 UI 直接显示）；网络异常同样转为失败结果。</para>
        /// </summary>
        public async Task<DownloadResult> DownloadAsync(
            BookSourceReadingSession session,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken token = default)
        {
            try
            {
                return await DownloadCoreAsync(session, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DownloadResult(false, true, string.Empty, "下载已取消");
            }
            catch (Exception ex)
            {
                return new DownloadResult(false, false, string.Empty, $"下载失败：{ex.Message}");
            }
        }

        private async Task<DownloadResult> DownloadCoreAsync(
            BookSourceReadingSession session,
            IProgress<DownloadProgress>? progress,
            CancellationToken token)
        {
            var opened = await session.OpenAsync(token).ConfigureAwait(false);
            var chapters = opened.Chapters;

            // 卷标题不是正文章节（下标由 LegadoTocAdapter 标为不可跳转），下载时跳过
            var targets = chapters
                .Select((c, i) => (Item: c, Index: i))
                .Where(x => !x.Item.IsVolume && !string.IsNullOrWhiteSpace(x.Item.ChapterUrl))
                .ToList();

            if (targets.Count == 0)
            {
                return new DownloadResult(false, false, string.Empty,
                    "没有可下载的章节（目录为空或全部为卷标题）");
            }

            // 目录在本次下载内固定：中途改设置（另一处 UI）不该让同一次下载分落两处
            var dir = DownloadDirectory;
            Directory.CreateDirectory(dir);

            var bookName = string.IsNullOrWhiteSpace(session.BookName) ? "未命名书籍" : session.BookName;
            var path = BuildFilePath(dir, bookName, session.BookUrl);

            // 唯一身份：写入文件头标记块 + sidecar，改名/移动后仍可关联缓存与在线更新
            var uuid = Guid.NewGuid().ToString("N");
            var exportedAt = MarkerTimestamp.Now();
            var header = BuildHeader(
                bookName, session.Source.BookSourceName, targets.Count,
                uuid, session.Source.BookSourceUrl, session.BookUrl, exportedAt);

            var (withContent, total) = await FetchAndWriteWholeBookAsync(
                session, targets, path, header, progress, token).ConfigureAwait(false);

            // 全书零正文 = 抓取链路对该源不成立（多为需登录/阶段4 的源）。
            // 此时若把文件留下，用户会得到一本"有目录没内容"的书还以为下完了——
            // 明确判失败并给出原因，让用户换源。
            if (withContent == 0)
            {
                return new DownloadResult(false, false, string.Empty,
                    "该书源未能取到任何正文（可能需要登录或该源已失效），请换一个书源");
            }

            // 目录快照落 sidecar（在线更新做差分的依据；文件头只存简单索引信息）
            SaveSidecarFor(path, uuid, session, exportedAt, targets);
            return new DownloadResult(true, false, path, $"已下载 {withContent}/{total} 章（合并保存为 TXT）");
        }

        /// <summary>
        /// 拉取全部章节正文并写成一个合并 TXT（<c>.part</c> + 原子替换）。
        /// <para><b>下载结果必须合并保存为单个 TXT</b>（本项目口径）：一章一段、章名单独成行，
        /// 不产生分章文件；<see cref="UpdateAsync"/> 的差分重组与"远程大改另存"复用本方法。</para>
        /// </summary>
        /// <returns>(有正文的章数, 总章数)；失败/取消抛异常（调用方清理 .part）。</returns>
        private static async Task<(int WithContent, int Total)> FetchAndWriteWholeBookAsync(
            BookSourceReadingSession session,
            List<(Models.TocItem Item, int Index)> targets,
            string path,
            string header,
            IProgress<DownloadProgress>? progress,
            CancellationToken token)
        {
            var tmpPath = path + ".part";
            var total = targets.Count;
            var done = 0;
            var withContent = 0;

            try
            {
                // 无 BOM 的 UTF-8：阅读器按 UTF-8 直读，避免中文变乱码
                await using (var writer = new StreamWriter(
                    tmpPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    await writer.WriteAsync(header).ConfigureAwait(false);

                    for (var offset = 0; offset < total; offset += BatchSize)
                    {
                        token.ThrowIfCancellationRequested();

                        var batch = targets.Skip(offset).Take(BatchSize).ToList();
                        var slice = batch.Select(x => x.Item).ToArray();

                        var contents = await session.FetchContentsAsync(slice, token).ConfigureAwait(false);

                        for (var i = 0; i < batch.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();

                            var (item, _) = batch[i];
                            var content = i < contents.Length ? contents[i] : null;

                            // 失败章写占位而不是跳过：章名与目录仍能对齐，用户能看出哪章没下到
                            var text = string.Empty;
                            if (content is not null)
                            {
                                text = LegadoHtmlText.ToPlainText(content.Content);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    withContent++;
                                }
                            }

                            await writer
                                .WriteAsync(BuildChapter(item.ChapterName, text, done + 1))
                                .ConfigureAwait(false);

                            done++;
                            progress?.Report(new DownloadProgress(done, total, item.ChapterName ?? string.Empty, path));
                        }
                    }
                }

                // 原子替换：取消/崩溃不会留下半成品被误当成完整版
                File.Move(tmpPath, path, overwrite: true);
                return (withContent, total);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }

        /// <summary>把会话目录快照写成 sidecar（<see cref="DownloadedBookMarker.SaveSidecar"/>）。
        /// <para>章名存 <b>HtmlDecode + Trim 后</b>的形态——那才是实际写进 TXT 的章名行
        /// （<see cref="BuildChapter"/> 做了同样处理），差分提取按它与文件行严格匹配。</para></summary>
        private static void SaveSidecarFor(
            string path, string uuid, BookSourceReadingSession session,
            string exportedAt, List<(Models.TocItem Item, int Index)> targets)
        {
            var toc = targets
                .Select(x => new DownloadedBookMarker.SidecarTocItem(
                    System.Net.WebUtility.HtmlDecode(x.Item.ChapterName ?? string.Empty).Trim(),
                    x.Item.ChapterUrl ?? string.Empty))
                .ToList();
            DownloadedBookMarker.SaveSidecar(path, new DownloadedBookMarker.SidecarData(
                uuid, session.Source.BookSourceUrl, session.BookUrl, session.BookName, exportedAt, toc));
        }

        // ============================================================
        // 在线更新（差分）
        // ============================================================

        /// <summary>在线更新结果。<see cref="MassiveResaved"/> = 远程目录大改，已另存新文件（原文件未动）。</summary>
        public sealed record UpdateResult(
            bool Success,
            bool MassiveResaved,
            string FilePath,
            string Message,
            int Added,
            int Updated);

        /// <summary>
        /// 对书源下载的 TXT 做<b>在线更新</b>（差分）。
        /// <para><b>流程</b>：读文件头标记（书源URL/书籍URL/UUID）→ 拉远程最新目录 →
        /// 与 sidecar 目录快照做差分 → <b>只联网下载新增/改名/提取失败的章</b>，
        /// 未变章正文直接从旧文件切片复用 → 重组合并 TXT 原子覆盖 → 更新 sidecar。</para>
        /// <para><b>大改保护</b>：被删+被改章达到 <see cref="TocDiffLogic.IsMassiveChange"/> 阈值时，
        /// 不覆盖原文件——改为整本另存为「<c>原名.远程大改{时间戳}.txt</c>」，旧版本保留。</para>
        /// <para>UUID 全程沿用文件头标记的值（差分重组不换身份：章节缓存/阅读进度不因更新而丢）。</para>
        /// </summary>
        public async Task<UpdateResult> UpdateAsync(
            string txtPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken token = default)
        {
            try
            {
                return await UpdateCoreAsync(txtPath, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new UpdateResult(false, false, string.Empty, "在线更新已取消", 0, 0);
            }
            catch (Exception ex)
            {
                return new UpdateResult(false, false, string.Empty, $"在线更新失败：{ex.Message}", 0, 0);
            }
        }

        private async Task<UpdateResult> UpdateCoreAsync(
            string txtPath,
            IProgress<DownloadProgress>? progress,
            CancellationToken token)
        {
            var marker = DownloadedBookMarker.TryReadFile(txtPath);
            if (marker is null || string.IsNullOrEmpty(marker.SourceUrl) || string.IsNullOrEmpty(marker.BookUrl))
            {
                return new UpdateResult(false, false, string.Empty,
                    "当前文件不是书源下载的文本（缺原始链接标记），无法在线更新", 0, 0);
            }

            var source = _service.Sources.GetByUrl(marker.SourceUrl!);
            if (source is null)
            {
                return new UpdateResult(false, false, string.Empty,
                    "来源书源已不在书源列表中（可能已删除），无法在线更新", 0, 0);
            }

            var sidecar = DownloadedBookMarker.LoadSidecar(txtPath, marker.Uuid);
            var session = new BookSourceReadingSession(_service, source, marker.BookUrl!);
            var opened = await session.OpenAsync(token).ConfigureAwait(false);

            // 与下载同口径：卷标题与无 URL 的条目不进正文文件，也不进差分
            var newTargets = opened.Chapters
                .Select((c, i) => (Item: c, Index: i))
                .Where(x => !x.Item.IsVolume && !string.IsNullOrWhiteSpace(x.Item.ChapterUrl))
                .ToList();
            if (newTargets.Count == 0)
            {
                return new UpdateResult(false, false, string.Empty, "远程目录为空（源可能已失效）", 0, 0);
            }

            var bookName = string.IsNullOrWhiteSpace(session.BookName)
                ? (string.IsNullOrWhiteSpace(sidecar?.BookName) ? "未命名书籍" : sidecar!.BookName)
                : session.BookName;
            var exportedAt = MarkerTimestamp.Now();

            // 无 sidecar（用户只拷了 TXT）：没有差分依据 → 整本重下覆盖，并补写 sidecar。
            // UUID 沿用文件头标记：缓存关联不因重下而断。
            if (sidecar is null || sidecar.Toc.Count == 0)
            {
                var header = BuildHeader(
                    bookName, source.BookSourceName, newTargets.Count,
                    marker.Uuid, marker.SourceUrl!, marker.BookUrl!, exportedAt);
                var (withContent, total) = await FetchAndWriteWholeBookAsync(
                    session, newTargets, txtPath, header, progress, token).ConfigureAwait(false);
                SaveSidecarFor(txtPath, marker.Uuid, session, exportedAt, newTargets);
                return new UpdateResult(true, false, txtPath,
                    $"未找到目录缓存，已整本重新下载 {withContent}/{total} 章（合并 TXT）", total, 0);
            }

            var diff = TocDiffLogic.Compare(sidecar.Toc, ToSidecarToc(newTargets));
            if (TocDiffLogic.IsMassiveChange(diff, sidecar.Toc.Count))
            {
                // 远程大改 → 另存新文件防大规模覆盖：新文件 = 全量重下（旧目录对不上号，
                // 切片复用不安全），并生成新 UUID（新文件 = 新身份，避免与旧文件的缓存撞车）
                var directory = Path.GetDirectoryName(txtPath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(txtPath);
                var newPath = Path.Combine(directory, $"{name}.远程大改{MarkerTimestamp.ForFileName()}.txt");
                var newUuid = Guid.NewGuid().ToString("N");
                var header = BuildHeader(
                    bookName, source.BookSourceName, newTargets.Count,
                    newUuid, marker.SourceUrl!, marker.BookUrl!, exportedAt);
                var (withContent, total) = await FetchAndWriteWholeBookAsync(
                    session, newTargets, newPath, header, progress, token).ConfigureAwait(false);
                SaveSidecarFor(newPath, newUuid, session, exportedAt, newTargets);
                return new UpdateResult(true, true, newPath,
                    $"远程目录大规模修改（删改 {diff.MutatedCount} 章）：已另存「{Path.GetFileName(newPath)}」" +
                    $"（{withContent}/{total} 章），原文件未改动",
                    diff.AddedIndexes.Count, diff.ChangedIndexes.Count);
            }

            // ── 差分合并：只联网下载必要的章 ──
            var oldRaw = await File.ReadAllTextAsync(txtPath, token).ConfigureAwait(false);
            var extracted = TocDiffLogic.ExtractOldChapters(oldRaw, sidecar.Toc);
            var oldBodyByKey = new Dictionary<string, string>(sidecar.Toc.Count, StringComparer.Ordinal);
            for (var i = 0; i < sidecar.Toc.Count; i++)
            {
                if (extracted.TryGetValue(i, out var body))
                {
                    oldBodyByKey[TocDiffLogic.KeyOf(sidecar.Toc[i])] = body;
                }
            }

            // 需要联网的章 = 新增 ∪ 改名 ∪（未变但旧文件里没切出正文的章——宁可多下一章也不丢内容）
            var needIndexes = new SortedSet<int>(diff.AddedIndexes.Concat(diff.ChangedIndexes));
            var newTocItems = ToSidecarToc(newTargets);
            for (var i = 0; i < newTocItems.Count; i++)
            {
                if (!needIndexes.Contains(i) && !oldBodyByKey.ContainsKey(TocDiffLogic.KeyOf(newTocItems[i])))
                {
                    needIndexes.Add(i);
                }
            }

            var fetched = new Dictionary<int, string>(needIndexes.Count);
            var needList = needIndexes.ToList();
            for (var offset = 0; offset < needList.Count; offset += BatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = needList.Skip(offset).Take(BatchSize).ToList();
                var slice = batch.Select(i => newTargets[i].Item).ToArray();
                var contents = await session.FetchContentsAsync(slice, token).ConfigureAwait(false);
                for (var i = 0; i < batch.Count; i++)
                {
                    if (i < contents.Length && contents[i] is not null)
                    {
                        var text = LegadoHtmlText.ToPlainText(contents[i]!.Content);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            fetched[batch[i]] = text;
                        }
                    }
                }
            }

            // 重组合并 TXT（口径与整本下载一致：章名行 + 空行 + 正文），原子覆盖
            var newTotal = newTargets.Count;
            var header2 = BuildHeader(
                bookName, source.BookSourceName, newTotal,
                marker.Uuid, marker.SourceUrl!, marker.BookUrl!, exportedAt);
            var tmpPath = txtPath + ".part";
            try
            {
                await using (var writer = new StreamWriter(
                    tmpPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    await writer.WriteAsync(header2).ConfigureAwait(false);
                    var done = 0;
                    for (var i = 0; i < newTargets.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = newTargets[i].Item;
                        var body = fetched.TryGetValue(i, out var f)
                            ? f
                            : oldBodyByKey.GetValueOrDefault(TocDiffLogic.KeyOf(newTocItems[i]), string.Empty);
                        await writer.WriteAsync(BuildChapter(item.ChapterName, body, done + 1)).ConfigureAwait(false);
                        done++;
                        progress?.Report(new DownloadProgress(done, newTotal, item.ChapterName ?? string.Empty, txtPath));
                    }
                }

                File.Move(tmpPath, txtPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }

            SaveSidecarFor(txtPath, marker.Uuid, session, exportedAt, newTargets);
            var kept = newTotal - fetched.Count;
            return new UpdateResult(true, false, txtPath,
                $"在线更新完成：新增 {diff.AddedIndexes.Count} 章、更新 {diff.ChangedIndexes.Count} 章、" +
                $"保留 {kept} 章（本次联网 {fetched.Count} 章）",
                diff.AddedIndexes.Count, diff.ChangedIndexes.Count);
        }

        private static List<DownloadedBookMarker.SidecarTocItem> ToSidecarToc(
            List<(Models.TocItem Item, int Index)> targets) =>
            targets
                .Select(x => new DownloadedBookMarker.SidecarTocItem(
                    System.Net.WebUtility.HtmlDecode(x.Item.ChapterName ?? string.Empty).Trim(),
                    x.Item.ChapterUrl ?? string.Empty))
                .ToList();

        /// <summary>文件头：书名 / 书源 / 章节数 + <b>机器可读标记块</b>（纯文本，兼容任何阅读器）。
        /// <para><b>标记块</b>（<see cref="DownloadedBookMarker"/>）：UUID（唯一身份：改名/移动后凭它
        /// 关联本地章节缓存与 sidecar）、书源URL / 书籍URL（在线更新的原始链接）——文件内只存这几行
        /// 简单信息，目录快照等大规模内容在同目录 sidecar JSON 里（方案 A）。</para>
        /// <para><b>一律 LF</b>：不要用 <see cref="Environment.NewLine"/>——那会让文件头是 CRLF、
        /// 正文是 LF，同一份文件混排两种换行。混排会让"以 $ 收尾"的净化/标题规则分支在
        /// 部分行上静默失效，也让阅读进度的行号在两种换行间漂移。</para></summary>
        private static string BuildHeader(
            string bookName, string sourceName, int chapterCount,
            string uuid, string sourceUrl, string bookUrl, string exportedAt) =>
            $"{bookName}\n" +
            $"作者或来源：{sourceName}\n" +
            $"共 {chapterCount.ToString(CultureInfo.InvariantCulture)} 章\n" +
            DownloadedBookMarker.BuildHeaderBlock(uuid, sourceUrl, bookUrl, exportedAt) +
            new string('=', 32) + "\n\n";

        /// <summary>
        /// 单章文本：章名单独一行 + 空行 + 正文。
        /// <para>刻意用纯文本而非 Markdown 井号——<c>TextTocProvider</c> 只把
        /// <c># / ## / ###</c> 或 <c>［＃大見出し］</c> 当标题，正文里的书名不该被误判为章节，
        /// 因此章名不参与标题解析，目录由书源会话直接提供（见 <c>LegadoTocAdapter</c>）。</para>
        /// </summary>
        private static string BuildChapter(string? chapterName, string body, int ordinal)
        {
            // 章名必须"单独成行、写在每章最前面"——这是离线 txt 的唯一分章依据
            // （用户导入的标题规则按行匹配）。顺手去空白 + 解实体：
            // 书源的章名偶带 &nbsp; / 首尾空格，原样写进文件会让标题规则匹配不上。
            var name = string.IsNullOrWhiteSpace(chapterName)
                ? $"第 {ordinal.ToString(CultureInfo.InvariantCulture)} 章"
                : System.Net.WebUtility.HtmlDecode(chapterName!).Trim();

            if (name.Length == 0)
            {
                name = $"第 {ordinal.ToString(CultureInfo.InvariantCulture)} 章";
            }

            return "\n" + name + "\n" +
                   new string('-', 32) + "\n\n" +
                   Uviewer.Services.Legado.BookSources.TextNormalization.ToLf(body) + "\n";
        }

        /// <summary>
        /// 目标文件名：书名 + 书源 URL 短哈希（同名不同源不互相覆盖）。
        /// 书名里的非法文件名字符统一替换为 '_'，并限长避免超 255 字节路径上限。
        /// </summary>
        private static string BuildFilePath(string directory, string bookName, string bookUrl)
        {
            var safe = Sanitize(bookName, 60);
            var hash = ShortHash(bookUrl);
            return Path.Combine(directory, $"{safe}_{hash}.txt");
        }

        private static string Sanitize(string name, int maxLength)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(Math.Min(name.Length, maxLength));
            foreach (var ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
            }

            var text = sb.ToString().Trim().Trim('.');
            if (text.Length == 0)
            {
                text = "book";
            }

            return text.Length <= maxLength ? text : text[..maxLength];
        }

        /// <summary>
        /// FNV-1a 32 位短哈希（纯字符串→文件名用，不需要密码学强度，也就不引入 MD5/SHA 依赖）。
        /// </summary>
        private static string ShortHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }

                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

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
                // 删除失败不阻断（半成品 .part 由用户手动清理，不静默当作成功）
            }
        }
    }
}
