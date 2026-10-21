using System.Collections.Generic;
using CoreTocItem = Uviewer.Services.Legado.Models.TocItem;

namespace Uviewer.Services.Legado
{
    /// <summary>
    /// 目录条目（宿主中立模型，不依赖任何 UI 类型；由控制器映射成 <c>Uviewer.Models.TocItem</c>）。
    /// </summary>
    /// <param name="Title">显示标题（章名缺失时已回退为"第 N 章"）。</param>
    /// <param name="ChapterIndex">章节下标（点击跳转用；卷为 -1 表示不可跳转）。</param>
    /// <param name="IsVolume">是否卷标题（分组用，不可点击跳转）。</param>
    public readonly record struct LegadoTocEntry(string Title, int ChapterIndex, bool IsVolume);

    /// <summary>
    /// 目录适配（阶段 5 集成的纯逻辑部分）：把库侧目录项映射成 <see cref="LegadoTocEntry"/>。
    ///
    /// <para><b>映射要点</b>：
    /// <list type="number">
    /// <item>章名缺失/空白 → 回退"第 N 章"（宿主目录不允许空标题行）；</item>
    /// <item>章 → <c>ChapterIndex = 下标</c>；这是书源目录与"按行跳转"文本目录的唯一结构差异，
    /// 由控制器把它写进 <c>TocItem.SourceLineNumber</c>；</item>
    /// <item>卷 → <c>ChapterIndex = -1</c>（不可跳转，仅作分组标题）。</item>
    /// <item>VIP/付费标记不进目录（宿主目录无对应视觉语义，丢弃以免误解）。</item>
    /// </list></para>
    /// </summary>
    public static class LegadoTocAdapter
    {
        public static List<LegadoTocEntry> Adapt(IReadOnlyList<CoreTocItem> chapters)
        {
            var result = new List<LegadoTocEntry>(chapters.Count);
            for (var i = 0; i < chapters.Count; i++)
            {
                var c = chapters[i];
                var title = string.IsNullOrWhiteSpace(c.ChapterName)
                    ? $"第 {i + 1} 章"
                    : c.ChapterName!.Trim();

                result.Add(new LegadoTocEntry(title, c.IsVolume ? -1 : i, c.IsVolume));
            }

            return result;
        }
    }
}