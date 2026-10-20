using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services.Legado;
using TitleRule = BookSource.Core.Models.TitleRule;

namespace Uviewer.Services
{
    /// <summary>
    /// TXT 目录提供器（打开文件时用它做分章 + 生成目录）。
    ///
    /// <para><b>三级来源</b>（顺序不可颠倒）：
    /// <list type="number">
    /// <item>有青空解析块（Aozora）→ 直接用块里的标题，零额外开销；</item>
    /// <item>否则走 <see cref="TextTocBuilder"/>：内置标记 → 用户标题规则切分；</item>
    /// </list></para>
    ///
    /// <para><b>为什么要拆出 <see cref="TextTocBuilder"/></b>：切分逻辑本身不依赖 WinUI，
    /// 拆开后可被集成测试直接链接（WinUI 的 <c>TocItem</c> 带 <c>Thickness</c> 等 XAML 类型，
    /// 整个文件无法链接进测试工程）。</para>
    /// </summary>
    public class TextTocProvider : ITocProvider
    {
        /// <summary>卷标题目录项的 Tag（与 <c>DocumentReaderController.LegadoChapterTag</c> 同范式）。</summary>
        internal const string LegadoVolumeTocTag = TextTocBuilder.LegadoVolumeTocTag;

        private readonly string _content;
        private readonly List<AozoraBindingModel>? _blocks;
        private readonly IReadOnlyList<TitleRule>? _titleRules;

        public TextTocProvider(
            string content,
            List<AozoraBindingModel>? blocks = null,
            IReadOnlyList<TitleRule>? titleRules = null)
        {
            _content = content;
            _blocks = blocks;
            _titleRules = titleRules;
        }

        public async Task<List<TocItem>> GetTocAsync(CancellationToken token = default)
        {
            // 青空块优先：块里已有标题，无需再扫全文
            if (_blocks != null && _blocks.Count > 0)
            {
                return _blocks
                    .Where(b => b.HeadingLevel > 0)
                    .Select(b => new TocItem
                    {
                        HeadingText = b.HeadingText,
                        SourceLineNumber = b.SourceLineNumber,
                        HeadingLevel = b.HeadingLevel
                    })
                    .ToList();
            }

            var entries = await Task.Run(() => TextTocBuilder.Build(_content, _titleRules), token);

            return entries
                .Select(e => new TocItem
                {
                    HeadingText = e.Title,
                    SourceLineNumber = e.SourceLineNumber,
                    HeadingLevel = e.Level,
                    Tag = e.Tag,
                })
                .ToList();
        }
    }
}
