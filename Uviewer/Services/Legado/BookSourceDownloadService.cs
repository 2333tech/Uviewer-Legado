using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookSource.Core.Models;
using BookSource.Core.Services.BookSources;
using BookSourceModel = BookSource.Core.Models.BookSource;
using CoreTocItem = BookSource.Core.Models.TocItem;

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
        private readonly string _downloadDir;

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

            // 默认落在书源数据目录旁的 books/ 子目录（与 booksources.json 同级，便于整体备份/清理）
            _downloadDir = string.IsNullOrWhiteSpace(downloadDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Uviewer", "books")
                : downloadDirectory!;
        }

        /// <summary>下载目录（不存在时首次下载才创建）。</summary>
        public string DownloadDirectory => _downloadDir;

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

            Directory.CreateDirectory(_downloadDir);

            var bookName = string.IsNullOrWhiteSpace(session.BookName) ? "未命名书籍" : session.BookName;
            var path = BuildFilePath(bookName, session.BookUrl);
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
                    await writer.WriteAsync(BuildHeader(bookName, session.Source.BookSourceName, total))
                        .ConfigureAwait(false);

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

                // 全书零正文 = 抓取链路对该源不成立（多为需登录/阶段4 的源）。
                // 此时若把文件留下，用户会得到一本"有目录没内容"的书还以为下完了——
                // 明确判失败并给出原因，让用户换源。
                if (withContent == 0)
                {
                    TryDelete(tmpPath);
                    return new DownloadResult(false, false, string.Empty,
                        "该书源未能取到任何正文（可能需要登录或该源已失效），请换一个书源");
                }

                // 原子替换：取消/崩溃不会留下半成品被误当成完整版
                File.Move(tmpPath, path, overwrite: true);
                return new DownloadResult(true, false, path, $"已下载 {withContent}/{total} 章");
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }

        /// <summary>文件头：书名 / 书源 / 章节数（纯文本，兼容任何阅读器）。</summary>
        private static string BuildHeader(string bookName, string sourceName, int chapterCount) =>
            $"{bookName}{Environment.NewLine}" +
            $"作者或来源：{sourceName}{Environment.NewLine}" +
            $"共 {chapterCount.ToString(CultureInfo.InvariantCulture)} 章{Environment.NewLine}" +
            new string('=', 32) + Environment.NewLine + Environment.NewLine;

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

            return Environment.NewLine + name + Environment.NewLine +
                   new string('-', 32) + Environment.NewLine + Environment.NewLine +
                   body + Environment.NewLine;
        }

        /// <summary>
        /// 目标文件名：书名 + 书源 URL 短哈希（同名不同源不互相覆盖）。
        /// 书名里的非法文件名字符统一替换为 '_'，并限长避免超 255 字节路径上限。
        /// </summary>
        private string BuildFilePath(string bookName, string bookUrl)
        {
            var safe = Sanitize(bookName, 60);
            var hash = ShortHash(bookUrl);
            return Path.Combine(_downloadDir, $"{safe}_{hash}.txt");
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
