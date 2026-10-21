using System;
using System.IO;
using Uviewer.Services.Legado;
using Xunit;

namespace Uviewer.Services.Legado.Tests
{
    /// <summary>
    /// 下载目录规则验收（设置 →「书源管理」→「下载设置」）。
    /// <para>要点：默认值必须是 Windows 的"下载"文件夹；用户输入要能容忍
    /// 首尾空白 / 资源管理器"复制路径"带的引号 / <c>%VAR%</c> / 末尾分隔符，
    /// 而**相对路径**与**指向文件**的输入必须被拒绝——否则文件会落到用户找不到的地方，
    /// 或者写到一半失败。</para>
    /// </summary>
    public class DownloadPathTests
    {
        [Fact]
        public void DefaultDirectory_IsDownloadsUnderUserProfile()
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

            Assert.Equal(expected, DownloadPathRules.DefaultDirectory);
            Assert.True(Path.IsPathRooted(DownloadPathRules.DefaultDirectory));
            Assert.Equal("Downloads", Path.GetFileName(DownloadPathRules.DefaultDirectory));
        }

        [Theory]
        [InlineData("  D:\\Books  ", @"D:\Books")]
        [InlineData("\"D:\\Books\"", @"D:\Books")]
        [InlineData("D:\\Books\\", @"D:\Books")]
        [InlineData("D:\\Books\\Legado\\..\\Legado2", @"D:\Books\Legado2")]
        public void Normalize_HandlesWhitespaceQuotesAndSeparators(string input, string expected)
        {
            Assert.Equal(expected, DownloadPathRules.Normalize(input));
        }

        [Fact]
        public void Normalize_ExpandsEnvironmentVariables()
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Uviewer", "books");

            Assert.Equal(expected, DownloadPathRules.Normalize(@"%LocalAppData%\Uviewer\books\"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Books")]            // 相对路径：会被解释成"进程工作目录下的 Books"
        [InlineData(@".\Books")]
        public void Normalize_RejectsEmptyAndRelative(string input)
        {
            Assert.Throws<ArgumentException>(() => DownloadPathRules.Normalize(input));
        }

        [Fact]
        public void Normalize_RejectsExistingFile()
        {
            var file = Path.Combine(Path.GetTempPath(), $"uviewer-dl-{Guid.NewGuid():N}.txt");
            File.WriteAllText(file, "x");
            try
            {
                var ex = Assert.Throws<ArgumentException>(() => DownloadPathRules.Normalize(file));
                Assert.Contains("文件", ex.Message);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Normalize_AllowsNotYetExistingDirectory()
        {
            // 允许先填后建：下载时才 CreateDirectory
            var dir = Path.Combine(Path.GetTempPath(), $"uviewer-dl-{Guid.NewGuid():N}", "books");
            Assert.False(Directory.Exists(dir));
            Assert.Equal(dir, DownloadPathRules.Normalize(dir));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Books")]
        public void Resolve_FallsBackToDefault_WhenConfigUnusable(string? configured)
        {
            Assert.Equal(DownloadPathRules.DefaultDirectory, DownloadPathRules.Resolve(configured));
        }

        [Fact]
        public void Resolve_KeepsValidConfig()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"uviewer-dl-{Guid.NewGuid():N}");
            Assert.Equal(dir, DownloadPathRules.Resolve(dir + Path.DirectorySeparatorChar));
        }
    }
}
