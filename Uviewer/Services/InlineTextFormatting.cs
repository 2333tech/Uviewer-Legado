using Microsoft.Graphics.Canvas.Text;
using Uviewer.Models;

namespace Uviewer.Services;

internal static class InlineTextFormatting
{
    public static void Apply(CanvasTextLayout layout, int start, int length, AozoraStyledText text)
    {
        if (length <= 0) return;
        if (text.Bold) layout.SetFontWeight(start, length, Microsoft.UI.Text.FontWeights.Bold);
        if (text.Italic) layout.SetFontStyle(start, length, Windows.UI.Text.FontStyle.Italic);
        if (text.Underline) layout.SetUnderline(start, length, true);
        if (text.Strikethrough) layout.SetStrikethrough(start, length, true);
        if (text.Code) layout.SetFontFamily(start, length, "Consolas");
        if (text.Script != 0)
        {
            using var typography = new CanvasTypography();
            typography.AddFeature(text.Script > 0 ? CanvasTypographyFeatureName.Superscript : CanvasTypographyFeatureName.Subscript, 1);
            layout.SetTypography(start, length, typography);
        }
    }
}
