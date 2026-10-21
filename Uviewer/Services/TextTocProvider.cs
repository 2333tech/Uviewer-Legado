using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services.Legado;
using TitleRule = Uviewer.Services.Legado.Models.TitleRule;

namespace Uviewer.Services
{
    /// <summary>
    /// TXT 目录提供器（打开文件时用它做分章 + 生成目录）。
    ///
    /// <para><b>来源策略</b>（顺序不可颠倒）：
    /// <list type="number">
    /// <item>有青空解析块且块里带标题（［＃大見出し］等真青空文书）→ 直接用块标题，零额外开销；</item>
    /// <item>否则走 <see cref="TextTocBuilder"/>：内置标记 → 用户标题规则切分。</item>
    /// </list></para>
    ///
    /// <para><b>为什么块无标题时必须继续往下走</b>：青空模式只是<b>排版</b>，普通中文小说
    /// （"第N章 xxx"顶格行）在青空模式下同样有块、但块里没有任何标题——直接返回空列表就是
    /// "顶栏目录按钮点开只有无目录"，而用户明明导入了能用的标题规则。</para>
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
        private readonly Func<string, string>? _purifyTitleLine;

        // EN: TOC provider for TXT; headings may be pre-purified by title-scope replace rules.
        /// <param name="purifyTitleLine">
        /// 标题行预净化（作用于标题的替换规则）。识别标题前逐行调用，
        /// 让"【玄幻】第12章"这类带前缀的标题在去前缀后能被标题规则命中。
        /// </param>
        public TextTocProvider(
            string content,
            List<AozoraBindingModel>? blocks = null,
            IReadOnlyList<TitleRule>? titleRules = null,
            Func<string, string>? purifyTitleLine = null)
        {
            _content = content;
            _blocks = blocks;
            _titleRules = titleRules;
            _purifyTitleLine = purifyTitleLine;
        }

        public async Task<List<TocItem>> GetTocAsync(CancellationToken token = default)
        {
            // 青空块优先：块里已有标题（真青空文书：［＃大見出し］等）就用块，零额外开销
            if (_blocks != null && _blocks.Count > 0)
            {
                var blockEntries = _blocks
                    .Where(b => b.HeadingLevel > 0)
                    .Select(b => new TocItem
                    {
                        HeadingText = b.HeadingText,
                        SourceLineNumber = b.SourceLineNumber,
                        HeadingLevel = b.HeadingLevel
                    })
                    .ToList();

                if (blockEntries.Count > 0)
                {
                    return blockEntries;
                }

                // ⚠ 块里没有标题（绝大多数中文小说：青空模式只是排版，标题是"第N章"式顶格行）：
                // 不能返回空目录——那怕的正是"顶栏目录按钮点开只有无目录"。
                // 继续往下走标题规则切分（TextTocBuilder 内置标记 → 用户规则）。
            }

            var entries = await Task.Run(() => TextTocBuilder.Build(_content, _titleRules, _purifyTitleLine), token);

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
