using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Windows.Storage;
using Windows.Storage.Streams;
using Uviewer.Models;

namespace Uviewer.Services
{
    public sealed class TextDocumentLoadService
    {
        private readonly ArchiveSession _archiveSession;

        public TextDocumentLoadService(ArchiveSession archiveSession)
        {
            _archiveSession = archiveSession;
        }

        public async Task<string> ReadLocalFileAsync(
            StorageFile file,
            string encodingName,
            CancellationToken token = default)
        {
            string content = await ReadAsync(file, encodingName);
            token.ThrowIfCancellationRequested();
            return content;
        }

        public async Task<string> ReadArchiveEntryAsync(
            ImageEntry entry,
            string encodingName,
            CancellationToken token = default)
        {
            if (entry.ArchiveEntryKey == null)
            {
                return string.Empty;
            }

            var bytes = await _archiveSession.ReadEntryBytesAsync(entry.ArchiveEntryKey, token);
            token.ThrowIfCancellationRequested();
            return bytes == null
                ? string.Empty
                : Decode(bytes, encodingName);
        }

        private static async Task<string> ReadAsync(StorageFile file, string encodingName)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            using var dataReader = DataReader.FromBuffer(buffer);
            byte[] bytes = new byte[buffer.Length];
            dataReader.ReadBytes(bytes);

            return Decode(bytes, encodingName);
        }

        private static string Decode(byte[] bytes, string encodingName)
        {
            Encoding encoding = TextEncodingService.GetTextEncoding(bytes, encodingName);
            // 读入即归一换行（CRLF/CR → LF）：全应用（分章 / 标题规则 / 替换净化 / 章节缓存 /
            // 搜索行号 / 阅读进度）都假定 LF。在这里归一一次，下游不必各自兼容两种换行，
            // 也不会出现"正文是 CRLF、章节缓存坐标是 LF"的错位。
            return Uviewer.Services.Legado.BookSources.TextNormalization.ToLf(encoding.GetString(bytes));
        }
    }
}
