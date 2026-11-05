using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Pages;

/// Two-line FPS history (output = orange, source = grey).
public sealed class FpsChart : Control
{
    private (double src, double outp)[] _data = Array.Empty<(double, double)>();
    private static readonly IPen OutPen = new Pen(new SolidColorBrush(Color.Parse("#FF7A1A")), 3);
    private static readonly IPen SrcPen = new Pen(new SolidColorBrush(Color.Parse("#6B6A70")), 2);
    private static readonly IBrush Legend = new SolidColorBrush(Color.Parse("#8B8A88"));

    public void SetData(IEnumerable<(double src, double outp)> d)
    {
        _data = d.ToArray();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var face = new Typeface(FontFamily.Default);
        ctx.DrawText(new FormattedText("— 출력 FPS   — 원본 FPS", System.Globalization.CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight, face, 12, Legend), new Point(12, 8));
        if (_data.Length < 2) return;
        double max = Math.Max(30, _data.Max(x => Math.Max(x.src, x.outp)) * 1.15);
        double top = 28, h = b.Height - top - 10;
        Point P(int i, double v) => new(b.Width * i / (_data.Length - 1), top + h * (1 - v / max));
        for (int i = 1; i < _data.Length; i++)
        {
            ctx.DrawLine(SrcPen, P(i - 1, _data[i - 1].src), P(i, _data[i].src));
            ctx.DrawLine(OutPen, P(i - 1, _data[i - 1].outp), P(i, _data[i].outp));
        }
    }
}
