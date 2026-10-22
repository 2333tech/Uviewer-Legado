using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.Models;
using Xunit;

namespace Uviewer.Legado.Tests
{
    /// <summary>
    /// 漫画落盘（CBZ 章包 + 清单）与窗口/命名/元数据的回归测试。
    /// <para>覆盖点都是"改错了会静默丢数据"的地方：章包名复用、页序、元数据转义、
    /// 清单损坏容错、图片后缀嗅探、窗口优先级。</para>
    /// </summary>
    public sealed class MangaTests : IDisposable
    {
        private readonly string _root;

        public MangaTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uvmanga-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        private static byte[] FakeJpeg(int size = 8)
        {
            var bytes = new byte[Math.Max(size, 4)];
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
            bytes[3] = 0xE0;
            return bytes;
        }

        // ---------------- 命名 ----------------

        [Fact]
        public void ChapterArchiveName_UsesOrdinalPrefixAndExtension()
        {
            Assert.Equal("ch0001_第1话.cbz", MangaLibrary.BuildChapterArchiveName(0, "第1话"));
            Assert.Equal("ch0010.cbz", MangaLibrary.BuildChapterArchiveName(9, "  "));
        }

        [Theory]
        [InlineData("第1话：起", "第1话：起")]
        [InlineData("  a/b\\c  ", "a_b_c")]
        [InlineData("...dots...", "dots")]
        public void SanitizeName_ReplacesInvalidCharacters(string input, string expected)
        {
            Assert.Equal(expected, MangaLibrary.SanitizeName(input, 60));
        }

        [Fact]
        public void PageFileName_PadsToThreeDigits()
        {
            Assert.Equal("001.jpg", MangaLibrary.PageFileName(0, "jpg"));
            Assert.Equal("012.png", MangaLibrary.PageFileName(11, ".PNG"));
        }

        [Fact]
        public void BuildBookFolder_DiffersForDifferentBookUrls()
        {
            var a = MangaLibrary.BuildBookFolder(_root, "同名书", "https://a.example/book/1");
            var b = MangaLibrary.BuildBookFolder(_root, "同名书", "https://b.example/book/1");
            Assert.NotEqual(a, b);
        }

        // ---------------- 图片格式嗅探 ----------------

        [Fact]
        public void SniffImageExtension_DetectsByMagicBytes_EvenWithoutUrlExtension()
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };
            Assert.Equal(".jpg", MangaLibrary.SniffImageExtension(jpeg, "https://x/img?id=1"));
            Assert.Equal(".png", MangaLibrary.SniffImageExtension(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "https://x/img?id=1"));
            Assert.Equal(".webp", MangaLibrary.SniffImageExtension(
                new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' },
                "https://x/img"));
        }

        [Fact]
        public void SniffImageExtension_FallsBackToUrlExtension()
        {
            var unknown = new byte[] { 1, 2, 3, 4 };
            Assert.Equal(".avif", MangaLibrary.SniffImageExtension(unknown, "https://x/cover.AVIF"));
            Assert.Equal(".jpg", MangaLibrary.SniffImageExtension(unknown, "https://x/no-ext"));
        }

        // ---------------- URL 清理 ----------------

        [Fact]
        public void NormalizeImageUrls_AddsSchemeDeduplicatesAndFilters()
        {
            var urls = MangaLibrary.NormalizeImageUrls(new[]
            {
                "//cdn.example/1.jpg",
                "https://cdn.example/1.jpg",
                "javascript:void(0)",
                "  ",
                "https://cdn.example/2.jpg",
            });

            // "//cdn…" 补齐协议后与 https 版本同值 → 去重
            Assert.Equal(new[] { "https://cdn.example/1.jpg", "https://cdn.example/2.jpg" }, urls);
        }

        // ---------------- 目录合并 ----------------

        [Fact]
        public void MergeToc_ReusesArchiveNameAndKeepsNewChapters()
        {
            var manifest = new MangaManifest
            {
                Chapters = new List<MangaChapterEntry>
                {
                    new() { Index = 0, Name = "第1话", Url = "u1", Archive = "ch0001_第1话.cbz", Complete = true, Pages = 10 },
                    new() { Index = 1, Name = "第2话", Url = "u2", Archive = "ch0002_第2话.cbz", Complete = false },
                },
                Position = new MangaPosition { ChapterIndex = 1, PageIndex = 3 },
            };

            var toc = new List<TocItem>
            {
                new() { Index = 0, ChapterName = "第1话（改）", ChapterUrl = "u1" },
                new() { Index = 1, ChapterName = "第2话", ChapterUrl = "u2" },
                new() { Index = 2, ChapterName = "第3话", ChapterUrl = "u3" },
            };

            var added = MangaReadingSession.MergeToc(manifest, toc);

            Assert.Equal(1, added);
            Assert.Equal(3, manifest.Chapters.Count);
            Assert.Equal("ch0001_第1话.cbz", manifest.Chapters[0].Archive);
            Assert.Equal("第1话（改）", manifest.Chapters[0].Name);
            Assert.True(manifest.Chapters[0].Complete);
            Assert.Equal("ch0003_第3话.cbz", manifest.Chapters[2].Archive);
            Assert.False(manifest.Chapters[2].Complete);
            Assert.Equal(1, manifest.Position.ChapterIndex);
        }

        [Fact]
        public void MergeToc_ClampsPositionWhenTocShrinks()
        {
            var manifest = new MangaManifest
            {
                Chapters = new List<MangaChapterEntry> { new() { Index = 0, Url = "a" } },
                Position = new MangaPosition { ChapterIndex = 5, PageIndex = 2 },
            };

            var toc = new List<TocItem> { new() { Index = 0, ChapterUrl = "a", ChapterName = "A" } };
            MangaReadingSession.MergeToc(manifest, toc);

            Assert.Equal(0, manifest.Position.ChapterIndex);
            Assert.Equal(0, manifest.Position.PageIndex);
        }

        // ---------------- 下载窗口 ----------------

        [Fact]
        public void BuildWindow_TargetChapterFirst_ThenAfterThenBefore()
        {
            var window = MangaLibrary.BuildWindow(center: 10, count: 20, before: 2, after: 3);
            Assert.Equal(new[] { 10, 11, 12, 13, 9, 8 }, window);
        }

        [Fact]
        public void BuildWindow_ClampsAtBothEnds()
        {
            Assert.Equal(new[] { 0, 1, 2 }, MangaLibrary.BuildWindow(0, 10, before: 2, after: 2));
            Assert.Equal(new[] { 9, 8, 7 }, MangaLibrary.BuildWindow(9, 10, before: 2, after: 2));
            Assert.Empty(MangaLibrary.BuildWindow(0, 0));
        }

        // ---------------- 章包（CBZ）读写 ----------------

        [Fact]
        public void ChapterArchiveWriter_WritesPagesMetaAndComicInfo()
        {
            var archive = Path.Combine(_root, "ch0001.cbz");
            var partial = archive + ".part";

            var meta = new MangaChapterImages
            {
                Index = 0,
                Name = "第1话",
                Url = "https://src/ch1",
                Images = new List<MangaImageRecord>
                {
                    new() { File = "001.jpg", Url = "https://img/1", Bytes = FakeJpeg().Length },
                    new() { File = "002.jpg", Url = "https://img/2", Bytes = FakeJpeg().Length },
                },
                Complete = true,
            };

            using (var writer = new MangaLibrary.ChapterArchiveWriter(partial))
            {
                Assert.True(writer.TryAddPage("001.jpg", FakeJpeg()));
                Assert.True(writer.TryAddPage("002.jpg", FakeJpeg()));
                // 同名条目不会再写一遍（续下不会产生重复页）
                Assert.False(writer.TryAddPage("001.jpg", FakeJpeg()));
                writer.PutText(MangaFormat.PagesEntryName, MangaLibrary.SerializeChapterMeta(meta));
                writer.PutText(MangaFormat.ComicInfoEntryName, MangaLibrary.BuildComicInfoXml(
                    "书", "第1话", 1, 2, "源名", "https://src/source", "https://src/book", "uuid1", "https://src/ch1"));
            }

            File.Move(partial, archive);

            Assert.Equal(new[] { "001.jpg", "002.jpg" }, MangaLibrary.ListArchivePages(archive));
            Assert.True(MangaLibrary.IsArchiveComplete(archive, 2));

            var read = MangaLibrary.ReadChapterMeta(archive);
            Assert.NotNull(read);
            Assert.Equal("第1话", read!.Name);
            Assert.Equal(2, read.Images.Count);
            Assert.Equal("https://img/2", read.Images[1].Url);

            using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
            {
                var comicInfo = zip.GetEntry(MangaFormat.ComicInfoEntryName);
                Assert.NotNull(comicInfo);
            }
        }

        [Fact]
        public void ComicInfoXml_ContainsSourceIdentityAndEscapesMarkup()
        {
            var xml = MangaLibrary.BuildComicInfoXml(
                "书 & <b>", "第1话", 3, 12,
                "源\"名", "https://s.example/a?x=1&y=2", "https://s.example/book", "abc123", "https://s.example/ch/1");

            Assert.Contains("<Series>书 &amp; &lt;b&gt;</Series>", xml);
            Assert.Contains("source=https://s.example/a?x=1&amp;y=2", xml);
            Assert.Contains("uuid=abc123", xml);
            Assert.Contains("<Number>3</Number>", xml);
            Assert.Contains("<PageCount>12</PageCount>", xml);
        }

        [Fact]
        public void ListArchivePages_IgnoresMetaEntries()
        {
            var archive = Path.Combine(_root, "ch.cbz");
            using (var writer = new MangaLibrary.ChapterArchiveWriter(archive + ".part"))
            {
                writer.TryAddPage("001.png", FakeJpeg());
                writer.PutText(MangaFormat.PagesEntryName, "{}");
                writer.PutText("ComicInfo.xml", "<ComicInfo/>");
            }

            File.Move(archive + ".part", archive);

            Assert.Equal(new[] { "001.png" }, MangaLibrary.ListArchivePages(archive));
        }

        [Fact]
        public void ReadChapterMeta_ReturnsNullForMissingOrCorruptArchive()
        {
            Assert.Null(MangaLibrary.ReadChapterMeta(Path.Combine(_root, "nope.cbz")));

            var bad = Path.Combine(_root, "bad.cbz");
            File.WriteAllText(bad, "not a zip");
            Assert.Null(MangaLibrary.ReadChapterMeta(bad));
        }

        // ---------------- 书级清单 ----------------

        [Fact]
        public void Manifest_RoundTripsWithSourceIdentity()
        {
            var path = MangaLibrary.ManifestPathOf(Path.Combine(_root, "book"));
            var manifest = new MangaManifest
            {
                Uuid = "u1",
                Title = "漫画 & 名",
                SourceUrl = "https://src/s",
                SourceName = "源",
                BookUrl = "https://src/b?x=1&y=2",
                CreatedAt = MangaLibrary.NowStamp(),
                UpdatedAt = MangaLibrary.NowStamp(),
                Position = new MangaPosition { ChapterIndex = 2, PageIndex = 7 },
                Chapters = new List<MangaChapterEntry>
                {
                    new() { Index = 0, Name = "第1话", Url = "c1", Archive = "ch0001_第1话.cbz", Complete = true, Pages = 9, Downloaded = 9 },
                    new() { Index = 1, Name = "卷标题", IsVolume = true },
                },
            };

            MangaLibrary.WriteManifest(path, manifest);
            var read = MangaLibrary.ReadManifest(path);

            Assert.NotNull(read);
            Assert.Equal("u1", read!.Uuid);
            Assert.Equal("https://src/b?x=1&y=2", read.BookUrl);
            Assert.Equal(2, read.Position.ChapterIndex);
            Assert.Equal(7, read.Position.PageIndex);
            Assert.Equal("ch0001_第1话.cbz", read.Chapters[0].Archive);
            Assert.True(read.Chapters[1].IsVolume);

            // 落盘一律 LF、无 BOM
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB);
            Assert.DoesNotContain("\r\n", File.ReadAllText(path));
        }

        [Fact]
        public void ReadManifest_RejectsNonMangaJsonAndCorruptFiles()
        {
            var path = Path.Combine(_root, "book.uvmanga");

            File.WriteAllText(path, "{\"kind\":\"something-else\",\"title\":\"x\"}");
            Assert.Null(MangaLibrary.ReadManifest(path));

            File.WriteAllText(path, "{ not json");
            Assert.Null(MangaLibrary.ReadManifest(path));

            Assert.Null(MangaLibrary.ReadManifest(Path.Combine(_root, "missing.uvmanga")));
        }

        [Fact]
        public void LooksLikeMangaManifest_RequiresExtensionAndKind()
        {
            var good = Path.Combine(_root, "good.uvmanga");
            File.WriteAllText(good, "{\n  \"kind\": \"uvmanga\",\n  \"title\": \"x\"\n}");
            Assert.True(MangaLibrary.LooksLikeMangaManifest(good));

            var wrongExt = Path.Combine(_root, "good.json");
            File.Copy(good, wrongExt);
            Assert.False(MangaLibrary.LooksLikeMangaManifest(wrongExt));

            var wrongKind = Path.Combine(_root, "other.uvmanga");
            File.WriteAllText(wrongKind, "{\"kind\":\"pdf-marker\"}");
            Assert.False(MangaLibrary.LooksLikeMangaManifest(wrongKind));
        }

        [Fact]
        public void ResolveRoot_AppendsMangaFolder()
        {
            var root = MangaLibrary.ResolveRoot(@"D:\Books");
            Assert.Equal(Path.Combine(@"D:\Books", "Manga"), root);
        }
    }
}
