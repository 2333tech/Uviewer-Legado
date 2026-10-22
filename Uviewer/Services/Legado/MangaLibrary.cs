using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Uviewer.Services.Legado.Models;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 漫画落盘库：目录结构、文件命名、章包（CBZ）读写、清单读写、图片格式嗅探、下载窗口计算。
    ///
    /// <para><b>目录结构</b>（<c>&lt;下载目录&gt;\Manga\&lt;书名&gt;_&lt;hash8&gt;\</c>）：</para>
    /// <code>
    /// 书名_hash8\
    ///   book.uvmanga          ← 书级清单（入口 / 目录 / 书源识别 / 阅读位置）
    ///   ch0001_第1话.cbz      ← 章包（ZIP）：ComicInfo.xml + pages.json + 001.jpg…
    ///   ch0002_第2话.cbz
    /// </code>
    ///
    /// <para><b>为什么打包成 CBZ</b>：散图一章几十个文件、一本上万个文件，磁盘簇浪费 +
    /// 备份/同步/杀毒全变慢。CBZ 就是 ZIP，宿主阅读器已有完整归档链路
    /// （<c>ArchiveSession</c> 只挑图片条目、按名自然排序），零新增渲染代码；
    /// 同时 CBZ 是漫画界事实标准，第三方阅读器可直接打开。</para>
    ///
    /// <para><b>为什么入口是 <c>book.uvmanga</c> 而不是"扫描目录里的图片"</b>：
    /// 打开一张图片时如果靠"父目录里有没有图片"来猜漫画，会把普通图片文件夹
    /// 全部误判（还每次都要遍历目录）。改成"清单文件声明"后，判定只需 stat 一个文件，
    /// 非漫画图片与普通目录零干扰。</para>
    ///
    /// <para><b>线程</b>：本类只做纯函数与短时文件 IO；同一本书的写操作由调用方串行化。</para>
    /// </summary>
    public static class MangaLibrary
    {
        /// <summary>首次打开/下载时只抓前 5 章图片（大图 + 风控，避免"下载全书"被拦）。</summary>
        public const int InitialChapterImageCount = 5;

        /// <summary>阅读窗口：当前章之前预取章数（项目统一"前 2"）。</summary>
        public const int WindowBefore = 2;

        /// <summary>阅读窗口：当前章之后预取章数（项目统一"后 5"）。</summary>
        public const int WindowAfter = 5;

        /// <summary>章序号宽度（ch0001）。</summary>
        private const int ChapterIndexWidth = 4;

        /// <summary>页文件名宽度（001.jpg）。</summary>
        private const int PageNameWidth = 3;

        // ============================================================
        // 路径与命名
        // ============================================================

        /// <summary>漫画库根目录：跟随"下载设置"里的目录，子目录固定叫 Manga。</summary>
        public static string ResolveRoot(string downloadDirectory) =>
            Path.Combine(string.IsNullOrWhiteSpace(downloadDirectory)
                ? DownloadPathRules.DefaultDirectory
                : downloadDirectory, "Manga");

        /// <summary>书根目录：书名（安全化，限长 60）+ 书籍 URL 短哈希（同名不同源不互相覆盖）。</summary>
        public static string BuildBookFolder(string root, string title, string bookUrl) =>
            Path.Combine(root, $"{SanitizeName(title, 60)}_{ShortHash(bookUrl)}");

        /// <summary>书级清单路径。</summary>
        public static string ManifestPathOf(string bookFolder) =>
            Path.Combine(bookFolder, MangaFormat.ManifestFileName);

        /// <summary>章包文件名：<c>ch0001_章名.cbz</c>（章名安全化 + 限长 40）。</summary>
        public static string BuildChapterArchiveName(int index, string? name)
        {
            var ordinal = (index + 1).ToString(CultureInfo.InvariantCulture)
                .PadLeft(ChapterIndexWidth, '0');
            var safe = SanitizeName(name, 40);
            var stem = safe.Length == 0 ? $"ch{ordinal}" : $"ch{ordinal}_{safe}";
            return stem + MangaFormat.ChapterArchiveExtension;
        }

        /// <summary>页文件名：<c>001.jpg</c>（序号从 1 开始，保证归档内自然排序即页序）。</summary>
        public static string PageFileName(int pageIndex, string extension)
        {
            var number = (pageIndex + 1).ToString(CultureInfo.InvariantCulture)
                .PadLeft(PageNameWidth, '0');
            return number + NormalizeExtension(extension);
        }

        /// <summary>规范化扩展名（补点、小写；空则回退 .jpg）。</summary>
        public static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".jpg";
            }

            var value = extension!.Trim().ToLowerInvariant();
            return value.StartsWith('.') ? value : "." + value;
        }

        /// <summary>
        /// 文件名安全化：非法字符与控制字符替换为 '_'，去首尾空白与结尾点，限长。
        /// <para>与 <c>BookSourceDownloadService.Sanitize</c> 同策略（那边是文本下载的文件名），
        /// 保持两处口径一致：同名书在同一台机器上不会一处能建一处不能建。</para>
        /// </summary>
        public static string SanitizeName(string? name, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(Math.Min(name!.Length, maxLength));
            foreach (var ch in name)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
            }

            var text = builder.ToString().Trim().Trim('.');
            return text.Length <= maxLength ? text : text[..maxLength].Trim();
        }

        /// <summary>FNV-1a 32 位短哈希（纯文件名用途，不需要密码学强度）。</summary>
        public static string ShortHash(string? value)
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

        /// <summary>当前时间戳（清单里的 CreatedAt / UpdatedAt / FetchedAt 统一格式）。</summary>
        public static string NowStamp() =>
            DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        // ============================================================
        // 图片格式
        // ============================================================

        /// <summary>
        /// 按图片字节头嗅探扩展名（HTTP 不给扩展名时也能落成正确的文件后缀）。
        /// <para>漫画源常见 <c>/img?id=123</c> 这类无扩展名地址；写错后缀不影响解码
        /// （按内容解码），但会让归档条目被扩展名过滤掉（<c>ArchiveSession</c> 只挑图片扩展名），
        /// 所以必须按内容判定。</para>
        /// </summary>
        public static string SniffImageExtension(ReadOnlySpan<byte> data, string? url)
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return ".jpg";
            }

            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return ".png";
            }

            if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            {
                return ".gif";
            }

            if (data.Length >= 12 &&
                data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
                data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            {
                return ".webp";
            }

            if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
            {
                return ".bmp";
            }

            // 未知内容：退回 URL 上的扩展名（有些 CDN 直接给 .avif / .jxl）
            var fromUrl = ExtensionFromUrl(url);
            return fromUrl.Length > 0 ? fromUrl : ".jpg";
        }

        /// <summary>从 URL 路径取图片扩展名（识别不出返回空串）。</summary>
        public static string ExtensionFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            string path;
            try
            {
                path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url!;
            }
            catch
            {
                path = url!;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".avif" or ".jxl" or ".tiff" or ".tif" => extension,
                _ => string.Empty,
            };
        }

        /// <summary>是否图片扩展名（含 avif / jxl 等宿主已支持的格式）。</summary>
        public static bool IsImageFile(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"
                or ".bmp" or ".avif" or ".jxl" or ".tiff" or ".tif" or ".ico";
        }

        // ============================================================
        // 书级清单
        // ============================================================

        /// <summary>
        /// 读书级清单；文件不存在 / 损坏 / kind 不符 → null（调用方按"新建"处理）。
        /// <para>刻意不抛：清单损坏只应让这本漫画回到"重新建库"，不能让整个阅读链路崩掉。</para>
        /// </summary>
        public static MangaManifest? ReadManifest(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.MangaManifest);
                if (manifest is null || !string.Equals(manifest.Kind, MangaFormat.ManifestKind, StringComparison.Ordinal))
                {
                    return null;
                }

                manifest.Chapters ??= new List<MangaChapterEntry>();
                manifest.Position ??= new MangaPosition();
                return manifest;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写书级清单（原子写：先 .tmp 再替换，断电/崩溃不留下半份 JSON）。</summary>
        public static void WriteManifest(string path, MangaManifest manifest) =>
            WriteTextAtomic(path, JsonSerializer.Serialize(manifest, BookSourceJsonContext.Default.MangaManifest));

        /// <summary>这个 <c>.uvmanga</c> 是不是漫画清单（只读前 1KB 做 kind 判定）。</summary>
        public static bool LooksLikeMangaManifest(string path)
        {
            try
            {
                if (!File.Exists(path) ||
                    !string.Equals(Path.GetExtension(path), MangaFormat.ManifestExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                using var stream = File.OpenRead(path);
                var buffer = new byte[1024];
                var read = stream.Read(buffer, 0, buffer.Length);
                var head = Encoding.UTF8.GetString(buffer, 0, read);
                return head.Contains(MangaFormat.ManifestKind, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // 章包（CBZ）
        // ============================================================

        /// <summary>
        /// 读章包内的页清单（<c>pages.json</c>）；缺失/损坏 → null（调用方按"整章重抓"处理）。
        /// </summary>
        public static MangaChapterImages? ReadChapterMeta(string archivePath)
        {
            try
            {
                if (!File.Exists(archivePath))
                {
                    return null;
                }

                using var zip = ZipFile.OpenRead(archivePath);
                var entry = zip.GetEntry(MangaFormat.PagesEntryName);
                if (entry is null)
                {
                    return null;
                }

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var json = reader.ReadToEnd();
                var meta = JsonSerializer.Deserialize(json, BookSourceJsonContext.Default.MangaChapterImages);
                if (meta is null || !string.Equals(meta.Kind, MangaFormat.ChapterKind, StringComparison.Ordinal))
                {
                    return null;
                }

                meta.Images ??= new List<MangaImageRecord>();
                return meta;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 列出章包内已落盘的图片条目名（按名排序 = 页序）。
        /// <para>以磁盘为准而不是以 pages.json 为准：清单丢了也还能读，
        /// 半包（下载中断）也能照实反映"现在有几页可用"。</para>
        /// </summary>
        public static List<string> ListArchivePages(string archivePath)
        {
            try
            {
                if (!File.Exists(archivePath))
                {
                    return new List<string>();
                }

                using var zip = ZipFile.OpenRead(archivePath);
                return zip.Entries
                    .Select(e => e.FullName)
                    .Where(name => !string.IsNullOrEmpty(name) && IsImageFile(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>章包是否齐全：存在 <c>.cbz</c> 且图片条目数达到期望页数。</summary>
        public static bool IsArchiveComplete(string archivePath, int expectedPages)
        {
            if (!File.Exists(archivePath))
            {
                return false;
            }

            var pages = ListArchivePages(archivePath).Count;
            return pages > 0 && (expectedPages <= 0 || pages >= expectedPages);
        }

        /// <summary>把页清单序列化成 JSON 文本（写进章包）。</summary>
        public static string SerializeChapterMeta(MangaChapterImages meta) =>
            JsonSerializer.Serialize(meta, BookSourceJsonContext.Default.MangaChapterImages)
                .Replace("\r\n", "\n", StringComparison.Ordinal);

        /// <summary>
        /// 生成章包内 <c>ComicInfo.xml</c>（漫画界标准元数据）。
        /// <para>承载"这本书从哪来"：Series=书名、Number=章序、Web=书籍 URL、
        /// Notes 里带书源 URL / 书籍 URL / UUID / 章节 URL——即使清单 JSON 丢了、
        /// 章包被单独拷到别处，仍能自证出处并支撑重新关联。</para>
        /// </summary>
        public static string BuildComicInfoXml(
            string series,
            string chapterName,
            int chapterOrdinal,
            int pageCount,
            string sourceName,
            string sourceUrl,
            string bookUrl,
            string uuid,
            string chapterUrl)
        {
            var builder = new StringBuilder(512);
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            builder.Append("<ComicInfo>\n");
            builder.Append("  <Title>").Append(Xml(chapterName)).Append("</Title>\n");
            builder.Append("  <Series>").Append(Xml(series)).Append("</Series>\n");
            builder.Append("  <Number>").Append(chapterOrdinal.ToString(CultureInfo.InvariantCulture)).Append("</Number>\n");
            builder.Append("  <PageCount>").Append(pageCount.ToString(CultureInfo.InvariantCulture)).Append("</PageCount>\n");
            builder.Append("  <Manga>Yes</Manga>\n");
            builder.Append("  <Summary>来源：").Append(Xml(sourceName)).Append("</Summary>\n");
            builder.Append("  <Web>").Append(Xml(bookUrl)).Append("</Web>\n");
            builder.Append("  <Notes>")
                .Append(Xml($"Uviewer manga 1.0; source={sourceUrl}; book={bookUrl}; uuid={uuid}; chapter={chapterUrl}"))
                .Append("</Notes>\n");
            builder.Append("</ComicInfo>\n");
            return builder.ToString();
        }

        /// <summary>XML 文本转义（属性/文本节点通用的保守做法）。</summary>
        private static string Xml(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value!
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);
        }

        /// <summary>
        /// 章包写入器：一次打开、逐页追加、最后写元数据并关闭。
        /// <para><b>为什么不用"每张图打开一次 zip"</b>：<see cref="ZipArchiveMode.Update"/>
        /// 每次打开都会把整个包复制一遍，60 页 × 1MB 就是反复几十 MB 的 IO。
        /// 这里把生命周期交给下载循环，正常路径全程只写一遍。</para>
        /// <para><b>续下</b>：<c>.cbz.part</c> 已存在时用 Update 模式打开，
        /// 已存在的页条目按文件名跳过（只补缺页），因此中断/取消不会白下已完成的页。</para>
        /// </summary>
        public sealed class ChapterArchiveWriter : IDisposable
        {
            private readonly ZipArchive _zip;
            private readonly HashSet<string> _names;
            private readonly bool _appendable;

            /// <summary>打开（或新建）章包临时文件。</summary>
            public ChapterArchiveWriter(string partialPath)
            {
                var directory = Path.GetDirectoryName(partialPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(partialPath) && new FileInfo(partialPath).Length > 0)
                {
                    // 续下：Update 模式能枚举已有条目（已下的页按名跳过）
                    _zip = ZipFile.Open(partialPath, ZipArchiveMode.Update);
                    _appendable = true;
                    _names = new HashSet<string>(_zip.Entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // 新建（或上次留下 0 字节残包 → 删掉重来，否则 Update 模式会抛 InvalidDataException）
                    if (File.Exists(partialPath))
                    {
                        try
                        {
                            File.Delete(partialPath);
                        }
                        catch
                        {
                        }
                    }

                    // Create 模式顺序写，不把整包读进内存（漫画单章几十 MB）。
                    // ⚠ Create 模式下 Entries / GetEntry 都会抛 NotSupportedException，
                    // 所以此时不枚举、也不做重名检查（新包本来就没有条目）。
                    _zip = ZipFile.Open(partialPath, ZipArchiveMode.Create);
                    _appendable = false;
                    _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }

            /// <summary>包里已有的图片条目名（续下时用于跳过）。</summary>
            public IReadOnlyCollection<string> ExistingPageNames => _names;

            /// <summary>追加一页（同名条目已存在则跳过，返回 false）。</summary>
            public bool TryAddPage(string fileName, byte[] bytes)
            {
                if (!_names.Add(fileName))
                {
                    return false;
                }

                var entry = _zip.CreateEntry(fileName, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
                return true;
            }

            /// <summary>写入/覆盖一个文本条目（pages.json / ComicInfo.xml）。</summary>
            public void PutText(string entryName, string content)
            {
                if (_appendable)
                {
                    var existing = _zip.GetEntry(entryName);
                    if (existing is not null)
                    {
                        _names.Remove(entryName);
                        existing.Delete();
                    }
                }

                _names.Add(entryName);
                var entry = _zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }

            public void Dispose() => _zip.Dispose();
        }

        // ============================================================
        // 窗口 / URL 清理
        // ============================================================

        /// <summary>
        /// 下载/预取窗口：<paramref name="center"/> 排第一位（用户跳转的目标章优先），
        /// 其后是 center+1 … center+after，最后是 center-1 … center-before。
        /// <para>顺序即优先级：同一批任务里目标章先落地，用户不必等"顺手预取"的那几章。</para>
        /// </summary>
        public static int[] BuildWindow(int center, int count, int before = WindowBefore, int after = WindowAfter)
        {
            if (count <= 0)
            {
                return Array.Empty<int>();
            }

            var ordered = new List<int>(before + after + 1);
            var start = Math.Max(0, center);
            if (start < count)
            {
                ordered.Add(start);
            }

            for (var offset = 1; offset <= after; offset++)
            {
                var index = start + offset;
                if (index < count)
                {
                    ordered.Add(index);
                }
            }

            for (var offset = 1; offset <= before; offset++)
            {
                var index = start - offset;
                if (index >= 0)
                {
                    ordered.Add(index);
                }
            }

            return ordered.ToArray();
        }

        /// <summary>
        /// 章内图片 URL 列表清理：补协议、去空白、去重（保序）、只留 http(s)/data 形态。
        /// </summary>
        public static List<string> NormalizeImageUrls(IEnumerable<string>? urls)
        {
            var result = new List<string>();
            if (urls is null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in urls)
            {
                var url = raw?.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                if (url!.StartsWith("//", StringComparison.Ordinal))
                {
                    url = "https:" + url;
                }

                if (!url!.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(url))
                {
                    result.Add(url);
                }
            }

            return result;
        }

        /// <summary>原子写文本（UTF-8 无 BOM、LF、先 .tmp 再替换）。</summary>
        private static void WriteTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, normalized, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
