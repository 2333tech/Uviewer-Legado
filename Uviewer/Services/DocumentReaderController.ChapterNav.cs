using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Uviewer.Models;
using Uviewer.Services.Legado;

namespace Uviewer
{
// EN: Reading-mode chapter navigation (previous/next chapter) driven by the TOC dropdown list.
    /// <summary>
    /// 阅读模式下的「上一章 / 下一章」导航。
    ///
    /// <para><b>章节来源</b>：顶栏目录下拉栏，即 <c>_tocService.CurrentToc</c>。
    /// 跳转复用 <see cref="NavigateToTocItem(TocItem)"/>（与点击目录项同一条分发），
    /// 于是三种目录天然一致：书源章节、章节缓存窗口、标题规则分章。</para>
    ///
    /// <para><b>生效范围</b>：只在阅读模式（顶栏 A 按钮）开启的文本模式下接管；
    /// 关闭时调用方继续走原来的"上一个 / 下一个文件"。</para>
    /// </summary>
    internal sealed partial class DocumentReaderController
    {
        // EN: Moves to the previous/next TOC entry of the current chapter list.
        /// <summary>阅读模式下触发一次"上一章 / 下一章"。返回 false 表示当前不接管。</summary>
        internal async Task<bool> NavigateReadingChapterAsync(bool forward)
        {
            if (!IsReadingMode || !_isTextMode || _isEpubMode)
            {
                return false;
            }

            var items = _tocService.CurrentToc;
            if (items.Count == 0)
            {
                return true;   // 没有目录：吞掉按键，避免顺手翻到相邻文件
            }

            int current = FindCurrentTocItemIndex(items);
            int target = FindAdjacentChapterTocItemIndex(items, current, forward);
            if (target < 0)
            {
                return true;   // 已到首/末章：静默
            }

            NavigateToTocItem(items[target]);
            await Task.CompletedTask;
            return true;
        }

        /// <summary>
        /// 当前阅读位置在目录里的下标（-1 = 定位不到）。
        /// <para>与 <c>TocButton_Click</c> 里"高亮当前项"的判定保持同一套规则，
        /// 否则会出现"目录高亮在第 3 章、按下一章却跳到第 5 章"。</para>
        /// </summary>
        private int FindCurrentTocItemIndex(IReadOnlyList<TocItem> items)
        {
            if (IsLegadoMode)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Tag?.ToString() == LegadoChapterTag &&
                        items[i].SourceLineNumber >= 0 &&
                        items[i].SourceLineNumber == _legadoChapterIndex)
                    {
                        return i;
                    }
                }

                return -1;
            }

            if (TextCacheActive)
            {
                // 当前章 = 窗口内真正可见的章，而不是窗口中心章（_textCacheCenter）：
                // 窗口内跳章/翻页只渲染不重开窗口，_textCacheCenter 保持不变——
                // 若按它定位，连按"下一章"每次都解析出同一个目标章，表现为
                // "第一次能跳，之后固定跳回第一次的位置/不再响应"。
                // 可见位置按排版取：分页画布（阅读模式）看当前页首块行号，其余看滚动顶端行号。
                int topLine;
                if (_isVerticalMode)
                {
                    topLine = _currentVerticalPageInfo.StartLine;
                }
                else if (_isAozoraMode)
                {
                    topLine = _currentAozoraStartBlockIndex >= 0 &&
                               _currentAozoraStartBlockIndex < _aozoraBlocks.Count
                        ? _aozoraBlocks[_currentAozoraStartBlockIndex].SourceLineNumber
                        : -1;
                }
                else
                {
                    topLine = GetTopVisibleLineIndex();
                }

                var visible = _textCacheHeadingLines is null
                    ? -1
                    : TextCacheLogic.CurrentChapterInWindow(
                        _textCacheHeadingLines, _textCacheWindowFrom, topLine);

                // 兜底：位置解析不到（极端时序）时维持旧的中心章口径
                var visibleChapter = visible >= 0 ? visible : Math.Max(0, _textCacheCenter);

                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Tag is int index && index == visibleChapter)
                    {
                        return i;
                    }
                }

                return -1;
            }

            int currentLine;
            if (_isVerticalMode)
            {
                currentLine = _currentVerticalPageInfo.StartLine;
            }
            else if (_isAozoraMode)
            {
                currentLine = _currentAozoraStartBlockIndex >= 0 && _currentAozoraStartBlockIndex < _aozoraBlocks.Count
                    ? _aozoraBlocks[_currentAozoraStartBlockIndex].SourceLineNumber
                    : 1;
            }
            else
            {
                currentLine = GetTopVisibleLineIndex();
            }

            int found = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].SourceLineNumber > 0 && items[i].SourceLineNumber <= currentLine)
                {
                    found = i;
                }
                else if (items[i].SourceLineNumber > currentLine)
                {
                    break;
                }
            }

            return found;
        }

        /// <summary>
        /// 从 <paramref name="current"/> 出发向 <paramref name="forward"/> 方向找第一个可跳转的章节项。
        /// <para>卷标题、PDF 书签这类 <c>SourceLineNumber == 0</c>（书源目录里是 -1）的项会被跳过。</para>
        /// </summary>
        private int FindAdjacentChapterTocItemIndex(IReadOnlyList<TocItem> items, int current, bool forward)
        {
            if (current < 0)
            {
                // 定位不到当前位置：向后从头找第一个可跳项，向前从尾找
                if (forward)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (IsJumpableChapterItem(items[i])) return i;
                    }
                }
                else
                {
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        if (IsJumpableChapterItem(items[i])) return i;
                    }
                }

                return -1;
            }

            int step = forward ? 1 : -1;
            for (int i = current + step; i >= 0 && i < items.Count; i += step)
            {
                if (IsJumpableChapterItem(items[i])) return i;
            }

            return -1;
        }

        private bool IsJumpableChapterItem(TocItem item)
        {
            if (IsLegadoMode)
            {
                return item.Tag?.ToString() == LegadoChapterTag;
            }

            if (TextCacheActive)
            {
                // 缓存目录里 Tag 是章下标；卷标题自身不可跳（点击会解析到下一章）
                return item.Tag is int index &&
                       _textCacheEntry is not null &&
                       index >= 0 && index < _textCacheEntry.Chapters.Count &&
                       !_textCacheEntry.Chapters[index].IsVolume;
            }

            return item.SourceLineNumber > 0;
        }

        /// <summary>
        /// 跳转到某个目录项。<b>目录点击与"上一章/下一章"共用这一条分发</b>，
        /// 所以行为天然一致（书源跳章 / 缓存重开窗口 / 按行号滚动 / 竖排与青空排版各自处理）。
        /// </summary>
        internal void NavigateToTocItem(TocItem item)
        {
            // 书源目录优先：SourceLineNumber 在书源目录里是"章节下标"而非行号
            if (TryHandleLegadoTocClick(item))
            {
                return;
            }

            // 章节缓存（窗口模式）目录：Tag 是章下标；窗口外点击 ⇒ 重开窗口
            if (TryHandleTextCacheTocClick(item))
            {
                return;
            }

            if (_isEpubMode)
            {
                if (!string.IsNullOrEmpty(item.EpubLink))
                {
                    JumpToEpubTocItem(new EpubTocItem { Title = item.HeadingText, Link = item.EpubLink });
                }

                return;
            }

            if (item.Tag?.ToString() == "PDF")
            {
                if (item.SourceLineNumber >= 0 && item.SourceLineNumber < _imageEntries.Count)
                {
                    _currentIndex = item.SourceLineNumber;
                    _ = DisplayCurrentImageAsync();
                }

                return;
            }

            if (item.SourceLineNumber > 0)
            {
                if (_isVerticalMode)
                {
                    _ = PrepareVerticalTextAsync(item.SourceLineNumber);
                }
                else if (_isAozoraMode)
                {
                    JumpToAozoraLine(item.SourceLineNumber);
                }
                else
                {
                    ScrollToLine(item.SourceLineNumber);
                }
            }
            else if (item.SourceLineNumber == 0)
            {
                // 卷标题 / 无跳转目标的条目：给出明确反馈，而不是"点了没反应"
                ShowNotification(
                    string.IsNullOrEmpty(item.HeadingText) ? Strings.NoTocContent : $"“{item.HeadingText}”是卷标题，请选择其下的具体章节",
                    "\uE946",
                    "Gray");
            }
        }
    }
}
