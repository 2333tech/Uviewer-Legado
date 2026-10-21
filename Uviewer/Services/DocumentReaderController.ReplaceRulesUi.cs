using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.Models;

namespace Uviewer
{
// EN: Host-side support for the replace-rule manager: per-rule "active in this chapter" probing and window-scoped refresh.
    /// <summary>
    /// 替换净化管理界面的宿主侧支撑：
    /// ① 探测"这一条规则是否在<b>当前章节</b>生效"（给列表打「本章生效」标记）；
    /// ② 界面里改完规则、关掉窗口后，<b>只按预处理窗口</b>重算并重绘，不重排全书。
    /// </summary>
    internal sealed partial class DocumentReaderController
    {
        /// <summary>
        /// 替换净化当前是否真的会跑：
        /// 书源阅读恒跑（再受总开关约束）；本地文本还要求阅读模式开启（关闭时按其语义不净化）。
        /// <para>标记与"关窗后刷新"都以它为前提——净化没跑，就既没有"本章生效"也没有需要重绘的内容。</para>
        /// </summary>
        private bool IsReplacePurifyEffective() => (IsLegadoMode || IsReadingMode) && BookSources.PurifyEnabled;

        /// <summary>规则作用域匹配用的书名（只有书源阅读时有值；本地文件为 null = 只命中全局规则）。</summary>
        private string? ReplaceScopeBookName => IsLegadoMode ? _legadoSession!.BookName : null;

        /// <summary>规则作用域匹配用的书籍 URL。</summary>
        private string? ReplaceScopeBookUrl => IsLegadoMode ? _legadoSession!.BookUrl : null;

        /// <summary>规则作用域匹配用的书源 URL（scope = <c>source:&lt;url&gt;</c>）。</summary>
        private string? ReplaceScopeSourceUrl => IsLegadoMode ? _legadoSession!.Source.BookSourceUrl : null;

        // EN: Raw text of the current chapter only (used to probe which replace rules take effect).
        /// <summary>
        /// 取<b>当前章节</b>的原文，供逐条规则命中探测。<b>不是</b>整本 / 整个预处理窗口。
        /// <list type="number">
        /// <item>书源阅读：会话里的本章原文 HTML（替换规则的作用对象就是它）；</item>
        /// <item>章节缓存窗口：缓存按<b>原文行号</b>记录章节范围，直接切当前章；</item>
        /// <item>无缓存整本：用标题规则在原文上重新切一次章，按"当前章名"定位切片
        /// （显示中的文本已净化、行号会漂，不能拿它的行号去切原文）；</item>
        /// <item>都拿不到：退化为显示中的文本。</item>
        /// </list>
        /// </summary>
        private string? GetCurrentChapterRawTextForRuleProbe()
        {
            if (IsLegadoMode && _legadoChapterIndex >= 0)
            {
                var raw = _legadoSession!.GetRawChapterContent(_legadoChapterIndex);
                return string.IsNullOrEmpty(raw) ? _currentTextContent : raw;
            }

            if (TextCacheActive && _textCacheEntry is { Chapters.Count: > 0 } entry &&
                _textCacheCenter >= 0 && _textCacheCenter < entry.Chapters.Count &&
                !string.IsNullOrEmpty(_textCacheNormalizedRaw))
            {
                var chapter = entry.Chapters[_textCacheCenter];
                if (chapter.StartLine >= 0 && chapter.EndLine > chapter.StartLine)
                {
                    return SliceLines(_textCacheNormalizedRaw, chapter.StartLine, chapter.EndLine);
                }
            }

            if (!string.IsNullOrEmpty(_currentTextRawContent))
            {
                var heading = GetCurrentTocHeading();
                if (!string.IsNullOrEmpty(heading))
                {
                    var entries = TextTocBuilder.Build(_currentTextRawContent, GetEnabledTitleRules());
                    for (var i = 0; i < entries.Count; i++)
                    {
                        if (!string.Equals(entries[i].Title?.Trim(), heading!.Trim(), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // 章节范围 = 本条标题行 → 下一条标题行（末条到文件尾），行号 1-based
                        var startLine = Math.Max(0, entries[i].SourceLineNumber - 1);
                        var endLine = i + 1 < entries.Count
                            ? Math.Max(startLine, entries[i + 1].SourceLineNumber - 1)
                            : int.MaxValue;
                        return SliceLines(_currentTextRawContent, startLine, endLine);
                    }
                }

                return _currentTextRawContent;
            }

            return _currentTextContent;
        }

        /// <summary>顶栏目录里"当前所在章"的标题（与目录高亮同口径，见 <see cref="FindCurrentTocItemIndex"/>）。</summary>
        private string? GetCurrentTocHeading()
        {
            var items = _tocService.CurrentToc;
            if (items.Count == 0)
            {
                return null;
            }

            var index = FindCurrentTocItemIndex(items);
            return index >= 0 && index < items.Count ? items[index].HeadingText : null;
        }

        /// <summary>按 LF 行坐标切一段文本（<paramref name="endLine"/> 不含；0-based）。</summary>
        private static string SliceLines(string text, int startLine, int endLine)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var lines = Uviewer.Services.Legado.BookSources.TextNormalization.ToLf(text).Split('\n');
            var from = Math.Clamp(startLine, 0, lines.Length);
            var to = Math.Clamp(endLine, from, lines.Length);
            return to <= from ? string.Empty : string.Join("\n", lines[from..to]);
        }

        /// <summary>
        /// 规则改动后重绘当前阅读内容，<b>只重算预处理窗口</b>（当前章前后各若干章）。
        /// <para>三种阅读形态各走自己的窗口路径：书源 = <c>RepurifyWindow</c>；
        /// 章节缓存 = 重新装配当前窗口（窗口内逐章净化）；无缓存整本 = 既有重载通路。</para>
        /// </summary>
        private async Task RefreshReadingAfterReplaceRulesChangedAsync()
        {
            try
            {
                // 净化没在跑（阅读模式关 / 总开关关）：规则改了也不会改变屏幕上的文字
                if (!IsReplacePurifyEffective())
                {
                    return;
                }

                if (IsLegadoMode && _legadoChapterIndex >= 0)
                {
                    var touched = _legadoSession!.RepurifyWindow(
                        _legadoChapterIndex, RepurifyWindowBefore, RepurifyWindowAfter);
                    if (touched.Count > 0)
                    {
                        await ShowLegadoChapterAsync(_legadoChapterIndex).ConfigureAwait(true);
                    }

                    return;
                }

                if (TextCacheActive && _textCacheCenter >= 0)
                {
                    // 旧窗口文本是按旧规则净化出来的 ⇒ 先丢内存缓存，再按新规则重新装配
                    InvalidateVirtualPurifiedChapters();

                    // 缓存窗口：按新规则重新装配 + 净化当前窗口（前 2 后 5 章），不碰全书；
                    // 顺带把"章内偏移"带过去，重绘后仍停在原来那一行附近
                    await ShowTextCacheWindowAsync(_textCacheCenter, GetCurrentChapterLineOffset()).ConfigureAwait(true);
                    return;
                }

                if (!string.IsNullOrEmpty(_currentTextRawContent))
                {
                    // 没有预处理窗口可复用：走既有重载通路（重新读盘 + 重新净化）
                    await ReloadCurrentTextAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Refresh after replace rules changed failed: {ex.Message}");
                ShowNotification($"刷新正文失败：{ex.Message}", "\uE783", "Red");
            }
        }

        /// <summary>当前阅读位置相对本章标题行的偏移（缓存窗口重装配后用它还原位置；取不到则回到章首）。</summary>
        private int GetCurrentChapterLineOffset()
        {
            try
            {
                var chapterSlot = _textCacheCenter - _textCacheWindowFrom;
                if (chapterSlot < 0 || _textCacheHeadingLines is not { Length: > 0 } headings || chapterSlot >= headings.Length)
                {
                    return 0;
                }

                var headingLine = headings[chapterSlot];
                var topLine = GetTopVisibleLineIndex();
                return headingLine > 0 && topLine > headingLine ? topLine - headingLine : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
