using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 章节缓存的落盘存储（对标 <see cref="ReplaceRuleManager"/> 的同构策略：
    /// 原子替换 + 写串行化，但**不做内存快照**——缓存是一次性写、整份读的小文件，
    /// 目录量级为几百条，没有并发读的热点）。
    ///
    /// <para>布局：<c>%LocalAppData%/Uviewer/textCache/&lt;sha1(sourceKey)[0:32]&gt;.json</c>，
    /// 一个源一个文件。文件名用哈希：源键含路径分隔符/中文/压缩包条目名，
    /// 直接当文件名会踩非法字符与 260 长度上限。</para>
    /// </summary>
    internal sealed class TextCacheStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        private readonly string _directory;

        /// <summary>写串行化：位置更新（切章即写）与缓存保存可能并发，共用同一文件不能踩。</summary>
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public TextCacheStore(string? directory = null)
        {
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Uviewer",
                "textCache");
        }

        /// <summary>缓存目录（菜单提示 / 测试断言用）。</summary>
        public string Directory => _directory;

        private string PathFor(string sourceKey)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey ?? string.Empty));
            var sb = new StringBuilder(32);
            foreach (var b in hash.AsSpan(0, 16))
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return Path.Combine(_directory, sb + ".json");
        }

        /// <summary>读一个源的缓存（不存在 / 损坏 / 版本不符均返回 null，由调用方判无效）。</summary>
        public TextCacheEntry? Load(string sourceKey)
        {
            if (string.IsNullOrEmpty(sourceKey))
            {
                return null;
            }

            var path = PathFor(sourceKey);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                var entry = JsonSerializer.Deserialize<TextCacheEntry>(json, JsonOptions);
                // 版本不符按"没有缓存"处理（下一行保存时自然覆盖成新版本）
                if (entry is not null && entry.Version != TextCacheFormat.Version)
                {
                    return null;
                }

                return entry;
            }
            catch
            {
                // 缓存损坏：当作没有（功能降级为整本路径），不阻断打开文件
                return null;
            }
        }

        /// <summary>
        /// 按文件 UUID 找缓存条目（路径 key 未命中时的回退查找：
        /// 文件被改名/移动后，凭文件头标记里的 UUID 找回原缓存）。
        /// <para>缓存目录内条目数有限（每文件一个），逐个反序列化代价可忽略；
        /// 损坏文件按"没有"跳过（与 <see cref="Load"/> 同口径）。</para>
        /// </summary>
        public TextCacheEntry? FindByUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || !System.IO.Directory.Exists(_directory))
            {
                return null;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TextCacheEntry>(
                        File.ReadAllText(file), JsonOptions);
                    if (entry is not null
                        && entry.Version == TextCacheFormat.Version
                        && string.Equals(entry.Uuid, uuid, StringComparison.Ordinal))
                    {
                        return entry;
                    }
                }
                catch
                {
                    // 单个缓存损坏：跳过继续找
                }
            }

            return null;
        }

        /// <summary>保存（覆盖写）。原子替换：tmp + File.Move，半截文件不会出现。</summary>
        public async Task SaveAsync(TextCacheEntry entry)
        {
            if (entry is null || string.IsNullOrEmpty(entry.SourceKey))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(_directory);
            var path = PathFor(entry.SourceKey);
            var temp = path + ".tmp";

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                _writeGate.Release();
                TryDelete(temp);
            }
        }

        /// <summary>删除一个源的缓存（再次点选菜单项 = 删缓存）。不存在则静默返回。</summary>
        public void Delete(string sourceKey)
        {
            if (string.IsNullOrEmpty(sourceKey))
            {
                return;
            }

            TryDelete(PathFor(sourceKey));
        }

        /// <summary>
        /// 只更新阅读位置（load-modify-save）。缓存本体可能很大（几百章目录），
        /// 但相对纯文本仍然很小（几十 KB），切章时整份重写可接受，且避免双文件不一致。
        /// </summary>
        public async Task UpdatePositionAsync(string sourceKey, string chapterName, int lineInChapter)
        {
            if (string.IsNullOrEmpty(sourceKey))
            {
                return;
            }

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var entry = Load(sourceKey);
                if (entry is null)
                {
                    return;
                }

                entry.LastChapterName = chapterName ?? string.Empty;
                entry.LastLineInChapter = Math.Max(0, lineInChapter);
                var path = PathFor(entry.SourceKey);
                var temp = path + ".tmp";
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                _writeGate.Release();
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
                // 清理失败不影响主流程
            }
        }
    }
}
