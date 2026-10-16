using Microsoft.Graphics.Canvas.Text;
using System;
using Uviewer.Models;

namespace Uviewer.Services;

internal static class HorizontalHeadingLayout
{
    public static void Configure(CanvasTextFormat format, AozoraBindingModel block)
    {
        if (block.HeadingLevel <= 0) return;
        // Let DirectWrite include the selected font and any CJK/emoji fallback
        // metrics instead of squeezing large headings into a fixed em ratio.
        format.LineSpacingMode = CanvasLineSpacingMode.Default;
        format.LineSpacing = -1;
        format.LineSpacingBaseline = 0;
    }

    public static (float topInset, float height) Measure(CanvasTextLayout layout, AozoraBindingModel block)
    {
        if (block.HeadingLevel <= 0) return (0, (float)layout.LayoutBounds.Height);
        var ink = layout.DrawBounds;
        var lines = layout.LayoutBounds;
        float topInset = (float)Math.Max(0, -Math.Min(lines.Top, ink.Top));
        double decoration = block.BorderThickness.Bottom > 0 ? 4 + block.BorderThickness.Bottom / 2 : 0;
        float height = (float)Math.Ceiling(topInset + Math.Max(lines.Bottom, ink.Bottom + decoration));
        return (topInset, height);
    }

    public static float BottomTolerance(AozoraBindingModel block, float fontSize) =>
        block.HeadingLevel > 0 ? 0 : fontSize * 1.2f;
}
