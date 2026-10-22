using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 漫画的"整本下载"与"在线更新"。
    ///
    /// <para><b>与文本整本下载的关键差别</b>：文本整本下载会抓齐全部章节正文（几百 KB 级），
    /// 漫画一章就是几十 MB —— 一次抓整本必然触发源站风控，也几乎肯定中途被限速/断连。
    /// 因此本服务的口径是：</para>
    /// <list type="bullet">
    /// <item><b>目录全量</b>：目录（章名/章节 URL）一次抓完并写进 <c>book.uvmanga</c>；</item>
    /// <item><b>图片只抓前 5 章</b>（<see cref="MangaLibrary.InitialChapterImageCount"/>）；
    /// 其余章在阅读时按"前二后五"窗口补齐（<see cref="MangaReadingSession.PrefetchWindowAsync"/>）；</item>
    /// <item><b>在线更新</b>只处理"清单里还没有章包"的章，同样受前 5 章上限约束。</item>
    /// </list>
    ///
    /// <para><b>落盘形态</b>：每章一个 <c>.cbz</c>（内嵌 ComicInfo.xml + pages.json），
    /// 磁盘上只有"清单 JSON + N 个章包"，不会出现上万个散图文件。</para>
    ///
    /// <para><b>取消</b>：取消时保留已完成的章包与半包 <c>.cbz.part</c>；
    /// 半包下次会被续补（已下好的页不重下），不会产生"看起来完整其实缺页"的章。</para>
    /// </summary>
    public sealed class MangaDownloadService
    {
        private readonly BookSourceService _service;

        public MangaDownloadService(BookSourceService service) =>
            _service = service ?? throw new ArgumentNullException(nameof(service));

        /// <summary>漫画库根目录（跟随"下载设置"里的目录）。</summary>
        public string MangaRoot => MangaLibrary.ResolveRoot(_service.DownloadDirectory);

        /// <summary>
        /// 下载整本：目录全量 + 前 <paramref name="initialChapterImages"/> 章的图片（默认 5）。
        /// <para>会话必须已经 <c>OpenOnlineAsync</c> 过（目录已在清单里）；这里只补章包。</para>
        /// </summary>
        public async Task<MangaDownloadResult> DownloadAsync(
            MangaReadingSession session,
            int initialChapterImages = MangaLibrary.InitialChapterImageCount,
            IProgress<MangaDownloadProgress>? progress = null,
            CancellationToken token = default)
        {
            try
            {
                var chapters = session.Chapters
                    .Where(c => !c.IsVolume && !string.IsNullOrWhiteSpace(c.Url))
                    .ToList();
                if (chapters.Count == 0)
                {
                    return new MangaDownloadResult(false, false, string.Empty,
                        "没有可下载的章节（目录为空或全部为卷标题）");
                }

                var targets = chapters
                    .Where(c => !IsChapterArchiveReady(session, c))
                    .Take(Math.Clamp(initialChapterImages, 1, MangaLibrary.InitialChapterImageCount))
                    .ToList();

                if (targets.Count == 0)
                {
                    return new MangaDownloadResult(true, false, session.ManifestPath,
                        $"目录共 {chapters.Count} 章，前 {initialChapterImages} 章的章包已就绪");
                }

                var done = await DownloadChaptersAsync(session, targets, progress, token).ConfigureAwait(false);

                return new MangaDownloadResult(true, false, session.ManifestPath,
                    $"已保存到 {Path.GetFileName(session.BookFolder)}\\：目录 {chapters.Count} 章，" +
                    $"已下载前 {done} 章图片（其余章在阅读时按需补齐）");
            }
            catch (OperationCanceledException)
            {
                return new MangaDownloadResult(false, true, string.Empty, "漫画下载已取消");
            }
            catch (Exception ex)
            {
                return new MangaDownloadResult(false, false, string.Empty, $"漫画下载失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 在线更新：按清单里的书源识别信息（书源 URL + 书籍 URL）重新取目录，
        /// 补齐"清单里没有章包"的章（最多前 5 章）。
        /// <para>已下载的章包一律不动（不做重下/覆盖）——漫画重下的代价是几十 MB，
        /// 而源站图片几乎不变；确需重下时用户删除对应 <c>.cbz</c> 即可。</para>
        /// </summary>
        public async Task<MangaDownloadResult> UpdateAsync(
            string manifestPath,
            int initialChapterImages = MangaLibrary.InitialChapterImageCount,
            IProgress<MangaDownloadProgress>? progress = null,
            CancellationToken token = default)
        {
            try
            {
                var manifest = MangaLibrary.ReadManifest(manifestPath);
                if (manifest is null)
                {
                    return new MangaDownloadResult(false, false, string.Empty,
                        "当前文件不是有效的漫画清单（.uvmanga），无法在线更新");
                }

                if (string.IsNullOrWhiteSpace(manifest.SourceUrl) || string.IsNullOrWhiteSpace(manifest.BookUrl))
                {
                    return new MangaDownloadResult(false, false, string.Empty,
                        "清单缺少原始链接（书源/书籍 URL），无法在线更新");
                }

                var source = _service.Sources.GetByUrl(manifest.SourceUrl);
                if (source is null)
                {
                    return new MangaDownloadResult(false, false, string.Empty,
                        "来源书源已不在书源列表中（可能已删除），无法在线更新");
                }

                using var session = MangaReadingSession.OpenExisting(_service, source, manifestPath);
                var opened = await session.OpenOnlineAsync(token).ConfigureAwait(false);

                var chapters = session.Chapters
                    .Where(c => !c.IsVolume && !string.IsNullOrWhiteSpace(c.Url))
                    .ToList();
                var targets = chapters
                    .Where(c => !IsChapterArchiveReady(session, c))
                    .Take(Math.Clamp(initialChapterImages, 1, MangaLibrary.InitialChapterImageCount))
                    .ToList();

                if (targets.Count == 0)
                {
                    return new MangaDownloadResult(true, false, manifestPath,
                        opened.AddedChapters > 0
                            ? $"目录新增 {opened.AddedChapters} 章，但前 {initialChapterImages} 章的图片都已就绪"
                            : "没有需要更新的章（章包均已就绪）");
                }

                var done = await DownloadChaptersAsync(session, targets, progress, token).ConfigureAwait(false);
                return new MangaDownloadResult(true, false, manifestPath,
                    opened.AddedChapters > 0
                        ? $"在线更新：目录新增 {opened.AddedChapters} 章，已补齐 {done} 章图片"
                        : $"在线更新：已补齐 {done} 章图片");
            }
            catch (OperationCanceledException)
            {
                return new MangaDownloadResult(false, true, string.Empty, "在线更新已取消");
            }
            catch (Exception ex)
            {
                return new MangaDownloadResult(false, false, string.Empty, $"在线更新失败：{ex.Message}");
            }
        }

        /// <summary>逐章下载（session 内部已串行化，这里只负责进度与统计）。</summary>
        private static async Task<int> DownloadChaptersAsync(
            MangaReadingSession session,
            List<MangaChapterEntry> targets,
            IProgress<MangaDownloadProgress>? progress,
            CancellationToken token)
        {
            var done = 0;
            foreach (var chapter in targets)
            {
                token.ThrowIfCancellationRequested();

                var chapterProgress = progress is null
                    ? null
                    : new Progress<MangaReadingSession.MangaImageProgress>(p =>
                        progress.Report(new MangaDownloadProgress(
                            done, targets.Count, p.ChapterName, p.Done, p.Total)));

                var result = await session
                    .EnsureChapterAsync(chapter.Index, chapterProgress, token)
                    .ConfigureAwait(false);

                if (result.HasPages)
                {
                    done++;
                }

                progress?.Report(new MangaDownloadProgress(
                    done, targets.Count, chapter.Name, result.PageNames.Count, result.PageNames.Count));
            }

            return done;
        }

        /// <summary>该章的章包是否已就绪（<c>.cbz</c> 存在且清单标记齐全）。</summary>
        private static bool IsChapterArchiveReady(MangaReadingSession session, MangaChapterEntry chapter)
        {
            if (chapter.Complete && !string.IsNullOrWhiteSpace(chapter.Archive))
            {
                return true;
            }

            var path = session.ArchivePath(chapter);
            return File.Exists(path) && chapter.Complete;
        }

        /// <summary>下载结果。<see cref="ManifestPath"/> 为 <c>book.uvmanga</c> 路径。</summary>
        public sealed record MangaDownloadResult(
            bool Success,
            bool Cancelled,
            string ManifestPath,
            string Message);

        /// <summary>下载进度（工作线程回调）。</summary>
        public sealed record MangaDownloadProgress(
            int ChaptersDone,
            int ChaptersTotal,
            string ChapterName,
            int PagesDone,
            int PagesTotal)
        {
            /// <summary>0~1 总进度（章级粒度；页级进度只用于状态文本）。</summary>
            public double Ratio => ChaptersTotal <= 0
                ? 0d
                : Math.Clamp((double)ChaptersDone / ChaptersTotal, 0d, 1d);
        }
    }
}
