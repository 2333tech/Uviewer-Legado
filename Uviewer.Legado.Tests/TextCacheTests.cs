using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado;
using Xunit;
using TitleRule = Uviewer.Services.Legado.Models.TitleRule;
using ReplaceRule = Uviewer.Services.Legado.Models.ReplaceRule;

namespace Uviewer.Services.Legado.Tests
{
    /// <summary>
    /// 章节缓存纯逻辑验收：源键 / 指纹 / 有效期 / 窗口 / 分章 / 切片 / 装配 / 落盘。
    /// 语义要点：缓存必须在「文件变了 / 规则变了」时失效——端上过期净化结果比没有缓存更糟。
    /// </summary>
    public class TextCacheTests
    {
        // ---------- 工具 ----------

        private static readonly TitleRule ChapterRule = new()
        {
            Id = 1,
            Name = "第N节",
            Pattern = @"^\s*第\s*[0-9]+\s*节.{0,60}$",
            Order = 10,
            Level = 1,
            IsEnabled = true,
        };

        private static readonly ReplaceRule ReplaceRule = new()
        {
            Id = 1,
            Name = "全角a",
            Pattern = "ａ",
            Replacement = "a",
            IsRegex = true,
            IsEnabled = true,
            Order = 1,
        };

        /// <summary>构造一本 5 章的测试书（标题顶格，正文带全角 ａ 供净化断言）。</summary>
        private static string BuildBook(int chapters = 5)
        {
            var lines = new List<string> { "卷首内容几行", "" };
            for (var i = 1; i <= chapters; i++)
            {
                lines.Add($"第{i}节 第{i}章标题");
                lines.Add($"　　正文ａ第{i}段第一行");
                lines.Add($"　　正文第{i}段第二行");
                lines.Add("");
            }

            return string.Join("\n", lines);
        }

        private static List<TextCacheChapter> Map(string text, IReadOnlyList<TitleRule>? rules = null) =>
            TextCacheLogic.BuildChapterMap(text, rules ?? new[] { ChapterRule });

        // ---------- 1. 源键 ----------

        [Fact]
        public void SourceKey_LocalFile_UsesFullPath()
        {
            var key = TextCacheLogic.ComputeSourceKey(@"C:\Books\a.txt", null, null);
            Assert.Equal(Path.GetFullPath(@"C:\Books\a.txt"), key);
        }

        [Fact]
        public void SourceKey_ArchiveEntry_CombinesArchiveAndEntry()
        {
            var key = TextCacheLogic.ComputeSourceKey(null, @"C:\Books\a.zip", "novel/001.txt");
            Assert.Equal(Path.GetFullPath(@"C:\Books\a.zip") + "\u0001" + "novel/001.txt", key);
        }

        [Fact]
        public void SourceKey_Empty_When_NoSource()
        {
            Assert.Equal(string.Empty, TextCacheLogic.ComputeSourceKey(null, null, null));
        }

        // ---------- 2. 指纹与有效期 ----------

        [Fact]
        public void Fingerprint_Changes_When_Rule_Edited()
        {
            var a = TextCacheLogic.ComputeReplaceRulesFingerprint(new[] { ReplaceRule });
            var edited = new ReplaceRule
            {
                Id = 1, Name = "全角a", Pattern = "ａ", Replacement = "b",
                IsRegex = true, IsEnabled = true, Order = 1,
            };
            var b = TextCacheLogic.ComputeReplaceRulesFingerprint(new[] { edited });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Fingerprint_Ignores_Disabled_Rules()
        {
            var on = TextCacheLogic.ComputeReplaceRulesFingerprint(new[] { ReplaceRule });
            var off = new ReplaceRule
            {
                Id = 1, Name = "全角a", Pattern = "ａ", Replacement = "a",
                IsRegex = true, IsEnabled = false, Order = 1,
            };
            Assert.Equal(on, TextCacheLogic.ComputeReplaceRulesFingerprint(new[] { ReplaceRule, off }));
        }

        [Fact]
        public void TitleFingerprint_Changes_When_TitleRule_Edited()
        {
            var a = TextCacheLogic.ComputeTitleRulesFingerprint(new[] { ChapterRule });
            var edited = new TitleRule
            {
                Id = 1, Name = "第N节", Pattern = @"^\s*第\s*[0-9]+\s*[章节].{0,60}$",
                Order = 10, Level = 1, IsEnabled = true,
            };
            Assert.NotEqual(a, TextCacheLogic.ComputeTitleRulesFingerprint(new[] { edited }));
        }

        [Fact]
        public void IsValid_False_When_File_Or_Rules_Changed()
        {
            var entry = new TextCacheEntry
            {
                Version = TextCacheFormat.Version,
                SourceKey = "K",
                FileSize = 100,
                FileLastWriteUtcTicks = 123,
                ReplaceRulesFingerprint = "R1",
                TitleRulesFingerprint = "T1",
            };

            Assert.True(TextCacheLogic.IsValid(entry, "K", 100, 123, "R1", "T1"));
            Assert.False(TextCacheLogic.IsValid(entry, "K", 101, 123, "R1", "T1"));   // 大小变
            Assert.False(TextCacheLogic.IsValid(entry, "K", 100, 124, "R1", "T1"));   // 时间变
            Assert.False(TextCacheLogic.IsValid(entry, "K", 100, 123, "R2", "T1"));   // 替换规则变
            Assert.False(TextCacheLogic.IsValid(entry, "K", 100, 123, "R1", "T2"));   // 标题规则变
            Assert.False(TextCacheLogic.IsValid(entry, "OTHER", 100, 123, "R1", "T1")); // 源键变
        }

        [Fact]
        public void IsValid_False_When_Version_Mismatch_Or_Null()
        {
            Assert.False(TextCacheLogic.IsValid(null, "K", 1, 1, "R", "T"));
            var old = new TextCacheEntry { Version = TextCacheFormat.Version + 1, SourceKey = "K" };
            Assert.False(TextCacheLogic.IsValid(old, "K", 1, 1, "R", "T"));
        }

        // ---------- 3. 窗口 ----------

        [Fact]
        public void Window_Clamps_At_Both_Ends()
        {
            Assert.Equal((0, 6), TextCacheLogic.ComputeWindow(100, 0, 2, 5));     // 开头：前2 收敛
            Assert.Equal((97, 100), TextCacheLogic.ComputeWindow(100, 99, 2, 5)); // 结尾：后5 收敛
            Assert.Equal((48, 56), TextCacheLogic.ComputeWindow(100, 50, 2, 5));  // 中间：满窗 8 章（前2+当前+后5）
            Assert.Equal((0, 0), TextCacheLogic.ComputeWindow(0, 0, 2, 5));       // 无章
        }

        [Fact]
        public void Window_Center_Clamped_When_Out_Of_Range()
        {
            Assert.Equal((0, 6), TextCacheLogic.ComputeWindow(10, -5, 2, 5));
            Assert.Equal((7, 10), TextCacheLogic.ComputeWindow(10, 99, 2, 5));
        }

        // ---------- 4. 分章 / 切片 / 装配 ----------

        [Fact]
        public void BuildChapterMap_Extracts_Chapters_With_Raw_Ranges()
        {
            var chapters = Map(BuildBook(5));

            Assert.Equal(5, chapters.Count);
            Assert.All(chapters, c => Assert.False(c.IsVolume));
            Assert.Equal("第1节 第1章标题", chapters[0].Name);

            // 第一章切片从 0 开始（卷首内容并入），其余章从各自标题行开始
            Assert.Equal(0, chapters[0].StartLine);
            Assert.Equal("第1节 第1章标题", chapters[0].Name);
            for (var i = 1; i < chapters.Count; i++)
            {
                Assert.Equal(chapters[i].HeadingLine, chapters[i].StartLine);
                Assert.Equal(chapters[i - 1].EndLine, chapters[i].HeadingLine); // 章章相接不漏行
            }

            // 切片拼回去 == 原文（去掉卷首那几行由第一章负责）
            var text = BuildBook(5);
            var slice = TextCacheLogic.SliceChapter(TextCacheLogic.NormalizeNewlines(text), chapters[1]);
            Assert.StartsWith("第2节 第2章标题", slice);
            Assert.Contains("正文ａ第2段第一行", slice);
            Assert.DoesNotContain("第3节", slice);
        }

        [Fact]
        public void BuildChapterMap_Empty_When_No_Headings()
        {
            Assert.Empty(Map("纯正文，没有任何标题\n第二行\n第三行"));
        }

        [Fact]
        public void AssembleWindow_Purifies_Each_Chapter_And_Reports_Heading_Lines()
        {
            var text = TextCacheLogic.NormalizeNewlines(BuildBook(5));
            var chapters = Map(text);

            var assembly = TextCacheLogic.AssembleWindow(text, chapters, 1, 4, slice => slice.Replace("ａ", "a"));

            // 3 章全在
            Assert.Contains("第2节 第2章标题", assembly.Text);
            Assert.Contains("第4节 第4章标题", assembly.Text);
            Assert.DoesNotContain("第1节", assembly.Text);
            Assert.DoesNotContain("第5节", assembly.Text);

            // 净化生效
            Assert.DoesNotContain("ａ", assembly.Text);
            Assert.Contains("正文a第2段第一行", assembly.Text);

            // 标题行号：第2章在装配文本第 1 行
            Assert.Equal(1, assembly.HeadingLines[0]);
            Assert.True(assembly.HeadingLines[1] > 1);
            Assert.True(assembly.HeadingLines[2] > assembly.HeadingLines[1]);

            // 行号与文本真实对应
            var lines = assembly.Text.Split('\n');
            Assert.Equal("第2节 第2章标题", lines[assembly.HeadingLines[0] - 1]);
            Assert.Equal("第3节 第3章标题", lines[assembly.HeadingLines[1] - 1]);
        }

        [Fact]
        public void AssembleWindow_Indexed_Overload_Passes_Chapter_Index_And_Reuses_Cache()
        {
            var text = TextCacheLogic.NormalizeNewlines(BuildBook(5));
            var chapters = Map(text);
            var purifiedIndices = new List<int>();

            // 带下标的净化委托：模拟"虚拟窗口按章缓存"——已缓存的章不再走净化
            var cache = new Dictionary<int, string>();
            var assembly = TextCacheLogic.AssembleWindow(text, chapters, 1, 4, (index, slice) =>
            {
                if (cache.TryGetValue(index, out var hit))
                {
                    return hit;
                }

                purifiedIndices.Add(index);
                var pure = slice.Replace("ａ", "a");
                cache[index] = pure;
                return pure;
            });

            // 只对窗口内的 3 章发起净化，且索引就是章节表下标
            Assert.Equal(new[] { 1, 2, 3 }, purifiedIndices.ToArray());
            Assert.DoesNotContain("ａ", assembly.Text);

            // 第二次装配同一窗口：全部命中缓存，一次净化都不做
            purifiedIndices.Clear();
            var again = TextCacheLogic.AssembleWindow(text, chapters, 1, 4, (index, slice) =>
                cache.TryGetValue(index, out var hit) ? hit : slice.Replace("ａ", "a"));

            Assert.Empty(purifiedIndices);
            Assert.Equal(assembly.Text, again.Text);
        }

        [Fact]
        public void BuildChapterMap_Applies_Title_Purifier_Before_Splitting()
        {
            // 标题带装饰前缀：不预净化时标题规则匹配不到
            var text = "【玄幻】第1节 甲\n正文一\n【玄幻】第2节 乙\n正文二";
            var rule = new TitleRule
            {
                Id = 1, Name = "第N节", Pattern = @"^第\s*[0-9]+\s*节.*$", Order = 10, Level = 1, IsEnabled = true,
            };

            Assert.Empty(TextCacheLogic.BuildChapterMap(text, new[] { rule }));

            var chapters = TextCacheLogic.BuildChapterMap(
                text,
                new[] { rule },
                line => line.StartsWith("【玄幻】", StringComparison.Ordinal) ? line["【玄幻】".Length..].Trim() : line);

            Assert.Equal(2, chapters.Count);
            Assert.Equal("第1节 甲", chapters[0].Name);
            Assert.Equal("第2节 乙", chapters[1].Name);
        }

        [Fact]
        public void AssembleWindow_Volume_Chapters_Are_Skipped()
        {
            // 卷规则：第N卷
            var volumeRule = new TitleRule
            {
                Id = 2, Name = "卷", Pattern = @"^\s*第[0-9]+卷.*$", Order = 5, Level = 1, IsVolume = true, IsEnabled = true,
            };
            var text = "第1卷 开篇\n第1节 甲\n正文甲\n第2节 乙\n正文乙";
            var chapters = Map(text, new[] { ChapterRule, volumeRule });

            Assert.Contains(chapters, c => c.IsVolume && c.Name == "第1卷 开篇");

            var assembly = TextCacheLogic.AssembleWindow(text, chapters, 0, chapters.Count, s => s);
            // 卷没有独立切片（标题行号 = -1，不进窗口行号表）；卷标题行作为普通文本
            // 留在其后第一章的切片里（与整本模式下"卷标题就是一行正文"一致）
            Assert.Contains(-1, assembly.HeadingLines);
            Assert.Contains("第1节 甲", assembly.Text);
            Assert.Contains("第2节 乙", assembly.Text);

            // 卷条目之后：窗口里两章各自的标题行号
            var nonVolume = assembly.HeadingLines.Where(h => h > 0).ToArray();
            Assert.Equal(2, nonVolume.Length);
            var lines = assembly.Text.Split('\n');
            Assert.Equal("第1节 甲", lines[nonVolume[0] - 1]);
            Assert.Equal("第2节 乙", lines[nonVolume[1] - 1]);
        }

        [Fact]
        public void AssembleWindow_FirstChapter_Heading_Found_After_FrontMatter_Shrinks()
        {
            // 回归：第一章切片含卷首内容，净化删掉其中若干行后标题前移，
            // 按原始偏移算行号会错位到正文里（实测踩过：行号指向"2018年的某一天中午"）
            var text = "广告第一行\n广告第二行\n广告第三行\n第1节 开局\n正文第一行\n第2节 发展\n正文第二行";
            var chapters = Map(text);
            Assert.Equal(2, chapters.Count);
            Assert.Equal(0, chapters[0].StartLine);   // 第一章并入了卷首
            Assert.Equal(3, chapters[0].HeadingLine);

            // 模拟"删掉卷首广告"的净化：切片前 3 行消失
            var assembly = TextCacheLogic.AssembleWindow(
                text, chapters, 0, 1,
                slice =>
                {
                    var lines = slice.Split('\n');
                    return string.Join("\n", lines.Skip(3));
                });

            var lines = assembly.Text.Split('\n');
            Assert.Equal("第1节 开局", lines[assembly.HeadingLines[0] - 1]);
        }

        [Fact]
        public void AssembleWindow_Heading_Line_Falls_Back_When_Purify_Deletes_The_Heading()
        {
            // 极端：净化把标题行本身删了 → 目录行号夹紧到切片开头，不崩、不错位到别的章
            var text = "第1节 开局\n正文\n第2节 发展\n正文";
            var chapters = Map(text);

            var assembly = TextCacheLogic.AssembleWindow(
                text, chapters, 0, 2,
                slice => slice.Replace("第1节 开局\n", string.Empty).Replace("第2节 发展\n", string.Empty));

            Assert.All(assembly.HeadingLines, h => Assert.True(h >= 1));
            Assert.True(assembly.HeadingLines[0] <= assembly.Text.Split('\n').Length);
        }

        [Fact]
        public void CurrentChapterInWindow_Tracks_TopLine()
        {
            var headingLines = new[] { 1, 5, 12 };
            Assert.Equal(0, TextCacheLogic.CurrentChapterInWindow(headingLines, 0, 1));
            Assert.Equal(0, TextCacheLogic.CurrentChapterInWindow(headingLines, 0, 4));
            Assert.Equal(1, TextCacheLogic.CurrentChapterInWindow(headingLines, 0, 5));
            Assert.Equal(2, TextCacheLogic.CurrentChapterInWindow(headingLines, 0, 99));
        }

        [Fact]
        public void ResolveClickTarget_Volume_Jumps_To_Next_Chapter()
        {
            var chapters = new List<TextCacheChapter>
            {
                new("第1卷", 1, true, -1, -1, -1),
                new("第1节", 1, false, 1, 1, 3),
                new("第2节", 1, false, 4, 4, 6),
            };

            Assert.Equal(1, TextCacheLogic.ResolveClickTarget(chapters, 0)); // 卷 → 下一章
            Assert.Equal(1, TextCacheLogic.ResolveClickTarget(chapters, 1)); // 章 → 自己
            Assert.Equal(-1, TextCacheLogic.ResolveClickTarget(chapters, 99));
        }

        [Fact]
        public void FindChapterIndex_By_Name_MinusOne_When_Missing()
        {
            var chapters = Map(BuildBook(3));
            Assert.Equal(1, TextCacheLogic.FindChapterIndex(chapters, "第2节 第2章标题"));
            Assert.Equal(-1, TextCacheLogic.FindChapterIndex(chapters, "不存在的章"));
            Assert.Equal(-1, TextCacheLogic.FindChapterIndex(chapters, null));
        }

        // ---------- 5. 落盘 ----------

        [Fact]
        public async Task Store_RoundTrip_PositionUpdate_Delete()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"text-cache-{Guid.NewGuid():N}");
            var store = new TextCacheStore(dir);
            var key = TextCacheLogic.ComputeSourceKey(@"C:\Books\a.txt", null, null);

            var entry = new TextCacheEntry
            {
                SourceKey = key,
                FileSize = 10,
                FileLastWriteUtcTicks = 20,
                ReplaceRulesFingerprint = "R",
                TitleRulesFingerprint = "T",
                Chapters = Map(BuildBook(3)),
                LastChapterName = "第1节 第1章标题",
                LastLineInChapter = 0,
            };

            Assert.Null(store.Load(key)); // 还没有

            await store.SaveAsync(entry);
            var loaded = store.Load(key)!;
            Assert.Equal(3, loaded.Chapters.Count);
            Assert.Equal("第1节 第1章标题", loaded.LastChapterName);

            // 只更新位置：章数与其它字段不动
            await store.UpdatePositionAsync(key, "第3节 第3章标题", 7);
            loaded = store.Load(key)!;
            Assert.Equal(3, loaded.Chapters.Count);
            Assert.Equal("第3节 第3章标题", loaded.LastChapterName);
            Assert.Equal(7, loaded.LastLineInChapter);

            store.Delete(key);
            Assert.Null(store.Load(key));
        }

        [Fact]
        public void Store_Load_Corrupt_File_Returns_Null()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"text-cache-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(dir);
            var store = new TextCacheStore(dir);
            var key = TextCacheLogic.ComputeSourceKey(@"C:\Books\a.txt", null, null);

            // 直接写一个坏文件到哈希路径（复用 Store 的命名规则不可见，就遍历目录写）
            File.WriteAllText(Path.Combine(dir, "whatever.json"), "{ 这不是 JSON");

            // 公开面：不存在的键 → null（坏文件不影响其它键的读取）
            Assert.Null(store.Load(key));
        }
    }
}
