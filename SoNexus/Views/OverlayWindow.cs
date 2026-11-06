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
        Content = _box = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _text,
        };
    }

    private readonly Border _box;
    private int _corner;
    private bool _oneLine;

    /// <summary>Canto da tela (0-3), tamanho da letra (0-2), fundo (0-100%) e se fica tudo numa linha.</summary>
    public void SetLayout(int corner, int size, int opacity, bool oneLine)
    {
        _corner = corner;
        _oneLine = oneLine;
        ApplyLook(_box, _text, size, opacity);
        Place();
    }

    /// <summary>Mesmo visual no painel do jogo e na prévia da tela de configuração.</summary>
    public static void ApplyLook(Border box, TextBlock text, int size, int opacity)
    {
        text.FontSize = size switch { 0 => 12, 2 => 20, _ => 15 };
        // Sem fundo, uma sombra deixa os números legíveis em cena clara.
        box.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0, 100) * 2.55), 0, 0, 0));
        text.Effect = opacity < 30 ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 1 } : null;
    }

    /// <summary>Cor dos números escolhida nas configurações.</summary>
    public static Brush ValueBrush(int color) => color switch
    {
        1 => Brushes.LightGreen,
        2 => Brushes.Gold,
        3 => Brushes.Cyan,
        _ => Brushes.White,
    };

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
        Render(_text, lines, _oneLine);
        // Alguns jogos tomam o "sempre por cima": reafirma a cada atualização.
        Topmost = false;
        Topmost = true;
    }

    public static void Render(TextBlock text, IEnumerable<(string Label, string Value, Brush Color)> lines, bool oneLine)
    {
        text.Inlines.Clear();
        var first = true;
        foreach (var (label, value, color) in lines)
        {
            if (!first) text.Inlines.Add(oneLine ? new System.Windows.Documents.Run("   ") : new System.Windows.Documents.LineBreak());
            first = false;
            if (label.Length > 0)
                text.Inlines.Add(new System.Windows.Documents.Run(oneLine ? label + " " : $"{label,-5} ") { Foreground = Brushes.Gray });
            text.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = color });
        }
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
