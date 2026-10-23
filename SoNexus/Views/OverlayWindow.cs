using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoNexus.Views;

/// <summary>
/// Painel pequeno sempre por cima (estilo OSD do Afterburner). Não recebe cliques nem foco,
/// então não atrapalha o jogo. Aparece em jogos no modo janela sem borda/tela cheia em janela.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
    };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 12;
        Top = 12;
        SizeChanged += (_, _) => Place();
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _text,
        };
    }

    private int _corner;

    /// <summary>Canto da tela (0-3) e tamanho da letra (0-2).</summary>
    public void SetLayout(int corner, int size)
    {
        _corner = corner;
        _text.FontSize = size switch { 0 => 12, 2 => 20, _ => 15 };
        Place();
    }

    /// <summary>Encosta no canto escolhido da tela principal (onde o jogo roda em tela cheia).</summary>
    private void Place()
    {
        const double margin = 12;
        var w = SystemParameters.PrimaryScreenWidth;
        var h = SystemParameters.PrimaryScreenHeight;
        Left = _corner is 1 or 3 ? w - ActualWidth - margin : margin;
        Top = _corner is 2 or 3 ? h - ActualHeight - margin - 40 : margin;
    }

    public void SetLines(IEnumerable<(string Label, string Value, Brush Color)> lines)
    {
        _text.Inlines.Clear();
        var first = true;
        foreach (var (label, value, color) in lines)
        {
            if (!first) _text.Inlines.Add(new System.Windows.Documents.LineBreak());
            first = false;
            _text.Inlines.Add(new System.Windows.Documents.Run($"{label,-5} ") { Foreground = Brushes.Gray });
            _text.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = color });
        }
        // Alguns jogos tomam o "sempre por cima": reafirma a cada atualização.
        Topmost = false;
        Topmost = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x8000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
