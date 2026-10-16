using CSharpMath.Rendering.FrontEnd;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Numerics;
using DrawingColor = System.Drawing.Color;
using Color = Windows.UI.Color;

namespace Uviewer.Renderers;

// CSharpMath supplies the TeX layout and font outlines; Win2D draws them as vectors.
internal sealed class MathPainter : MathPainter<MathCanvas, Color>
{
    public override DrawingColor WrapColor(Color c) => DrawingColor.FromArgb(c.A, c.R, c.G, c.B);
    public override Color UnwrapColor(DrawingColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public override ICanvas WrapCanvas(MathCanvas canvas) => canvas;
}

internal sealed class MathCanvas(CanvasDrawingSession session) : ICanvas
{
    private readonly Stack<Matrix3x2> _transforms = new();
    public float Width => 0;
    public float Height => 0;
    public DrawingColor DefaultColor { get; set; }
    public DrawingColor? CurrentColor { get; set; }
    public PaintStyle CurrentStyle { get; set; }
    private Color Color => ToColor(CurrentColor ?? DefaultColor);
    private static Color ToColor(DrawingColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public void Save() => _transforms.Push(session.Transform);
    public void Restore() => session.Transform = _transforms.Pop();
    public void Translate(float dx, float dy) => session.Transform = Matrix3x2.CreateTranslation(dx, dy) * session.Transform;
    public void Scale(float sx, float sy) => session.Transform = Matrix3x2.CreateScale(sx, sy) * session.Transform;
    public void StrokeRect(float x, float y, float w, float h) => session.DrawRectangle(x, y, w, h, Color, 0.5f);
    public void FillRect(float x, float y, float w, float h) => session.FillRectangle(x, y, w, h, Color);
    public void DrawLine(float x1, float y1, float x2, float y2, float thickness)
    {
        if (thickness <= 0) return;
        if (CurrentStyle == PaintStyle.Fill) session.DrawLine(x1, y1, x2, y2, Color, thickness);
        else this.StrokeLineOutline(x1, y1, x2, y2, thickness);
    }
    public CSharpMath.Rendering.FrontEnd.Path StartNewPath() => new GlyphPath(this, session);

    private sealed class GlyphPath(MathCanvas owner, CanvasDrawingSession session) : CSharpMath.Rendering.FrontEnd.Path
    {
        private readonly CanvasPathBuilder _builder = new(session);
        private bool _open;
        private bool _disposed;
        public override DrawingColor? Foreground { get; set; }
        public override void MoveTo(float x, float y)
        {
            CloseContour();
            _builder.BeginFigure(x, y);
            _open = true;
        }
        public override void LineTo(float x, float y) => _builder.AddLine(x, y);
        public override void Curve3(float x1, float y1, float x2, float y2) => _builder.AddQuadraticBezier(new(x1, y1), new(x2, y2));
        public override void Curve4(float x1, float y1, float x2, float y2, float x3, float y3) => _builder.AddCubicBezier(new(x1, y1), new(x2, y2), new(x3, y3));
        public override void CloseContour()
        {
            if (!_open) return;
            _builder.EndFigure(CanvasFigureLoop.Closed);
            _open = false;
        }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseContour();
            using (_builder)
            using (var geometry = CanvasGeometry.CreatePath(_builder))
            {
                var color = Foreground.HasValue ? ToColor(Foreground.Value) : owner.Color;
                if (owner.CurrentStyle == PaintStyle.Fill) session.FillGeometry(geometry, color);
                else session.DrawGeometry(geometry, color, 0.5f);
            }
        }
    }
}
