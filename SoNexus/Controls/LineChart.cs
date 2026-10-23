using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SoNexus.Controls;

/// <summary>Gráfico de linha simples que rola para a esquerda (últimos N valores).</summary>
public sealed class LineChart : FrameworkElement
{
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C)));
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private readonly List<double> _values = new();
    private Brush _stroke = Brushes.White;
    // Pincéis da linha e do preenchimento: criados uma vez por cor, e congelados (o WPF desenha mais rápido).
    private Pen _linePen = Freeze(new Pen(Brushes.White, 2) { LineJoin = PenLineJoin.Round });
    private Brush _fill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public int Capacity { get; set; } = 300;
    public double? FixedMin { get; set; }
    public double? FixedMax { get; set; }
    public string Unit { get; set; } = "";

    public Brush Stroke
    {
        get => _stroke;
        set
        {
            _stroke = value;
            _linePen = Freeze(new Pen(value, 2) { LineJoin = PenLineJoin.Round });
            var fill = value.Clone();
            fill.Opacity = 0.15;
            _fill = Freeze(fill);
        }
    }

    public void Push(double value)
    {
        _values.Add(value);
        // Corta o histórico em blocos, não a cada ponto (RemoveAt(0) desloca a lista inteira toda vez).
        if (_values.Count > Capacity + 128) _values.RemoveRange(0, _values.Count - Capacity);
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Escondido não redesenha; ao aparecer, desenha o que acumulou.
        if (e.Property == IsVisibleProperty && IsVisible) InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 10) return;

        // Só os últimos "Capacity" valores aparecem.
        var start = Math.Max(0, _values.Count - Capacity);
        var count = _values.Count - start;
        var visible = _values.Skip(start);
        double min = FixedMin ?? (count > 0 ? visible.Min() : 0);
        double max = FixedMax ?? (count > 0 ? visible.Max() : 1);
        if (FixedMin is null) min = Math.Max(0, min - (max - min) * 0.1);
        if (FixedMax is null) max += Math.Max((max - min) * 0.1, 1);
        if (max <= min) max = min + 1;

        var pen = GridPen;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Gráficos pequenos (miniaturas) ficam só com 2 linhas e sem números, para não embolar.
        var lines = h < 90 ? 2 : 4;
        var format = max - min >= 20 ? "0" : "0.#";
        for (var i = 0; i <= lines; i++)
        {
            var y = Math.Round(h * i / lines) + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            if (h < 90) continue;
            var label = new FormattedText($"{(max - (max - min) * i / lines).ToString(format)} {Unit}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, dpi);
            dc.DrawText(label, new Point(4, Math.Clamp(y - 13, 0, h - 13)));
        }

        if (count < 2) return;
        var step = w / (Capacity - 1);
        var x0 = w - step * (count - 1);
        Point P(int i) => new(x0 + i * step, h - (Math.Clamp(_values[start + i], min, max) - min) / (max - min) * h);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (var i = 1; i < count; i++) g.LineTo(P(i), true, false);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (var i = 0; i < count; i++) g.LineTo(P(i), true, false);
            g.LineTo(new Point(w, h), true, false);
        }
        area.Freeze();

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _linePen, line);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
