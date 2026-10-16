using System;

namespace Uviewer.Services;

// CSharpMath does not implement \boxed. Keep its contents in the math engine,
// and reserve the frame's space in the same layout used for drawing/pagination.
internal readonly record struct LatexBoxLayout(string Content, int BoxCount, float Inset, float StrokeThickness)
{
    public float Padding => BoxCount * Inset;

    public static LatexBoxLayout Parse(string latex, float fontSize)
    {
        string content = latex.Trim();
        int boxes = 0;
        while (TryUnwrap(content, out string inner))
        {
            content = inner.Trim();
            boxes++;
        }
        float stroke = Math.Max(0.5f, fontSize * 0.04f);
        return new(content, boxes, fontSize * 0.2f + stroke, stroke);
    }

    private static bool TryUnwrap(string latex, out string content)
    {
        content = latex;
        const string command = @"\boxed";
        if (!latex.StartsWith(command, StringComparison.Ordinal)) return false;
        int start = command.Length;
        while (start < latex.Length && char.IsWhiteSpace(latex[start])) start++;
        if (start == latex.Length || latex[start] != '{') return false;
        int depth = 1;
        for (int i = start + 1; i < latex.Length; i++)
        {
            // Escaped braces and TeX comments do not delimit the argument.
            if (latex[i] == '\\') { i++; continue; }
            if (latex[i] == '%')
            {
                while (i < latex.Length && latex[i] != '\n') i++;
                continue;
            }
            if (latex[i] == '{') depth++;
            else if (latex[i] == '}' && --depth == 0)
            {
                if (i != latex.Length - 1) return false;
                content = latex[(start + 1)..i];
                return true;
            }
        }
        return false;
    }
}
