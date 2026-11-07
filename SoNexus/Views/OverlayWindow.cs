using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoNexus.Views;

/// <summary>
/// Painel pequeno sempre por cima (estilo OSD do Afterburner). Não recebe cliques nem foco,
/// então não atrapalha o jogo. Aparece em jogos no modo janela sem borda/tela cheia em janela.
/// É uma janela comum do Windows: não entra no processo do jogo nem desenha dentro dele.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _text = NewText();
    private readonly DispatcherTimer _onTop = new() { Interval = TimeSpan.FromSeconds(3) };
    private IntPtr _hwnd;
    private int _corner;
    private bool _oneLine;

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
        // Alguns jogos tomam o "sempre por cima". Antes o painel saía e voltava para cima a cada
        // atualização (Topmost false/true), e nesse instante o jogo cobria o painel: era o pisca-pisca.
        // Agora só reafirma a posição na pilha, sem sair dela.
        _onTop.Tick += (_, _) => KeepOnTop();
        Closed += (_, _) => _onTop.Stop();
    }

    public static TextBlock NewText() => new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
    };

    /// <summary>Canto (0-3), tamanho da letra (0-2), fundo (0-100%), uma linha, tema e animações.</summary>
    public void SetLayout(int corner, int size, int opacity, bool oneLine, int theme, bool animate)
    {
        _corner = corner;
        _oneLine = oneLine;
        _text.MinWidth = 0; // novo tamanho de letra: mede de novo
        Content = OverlaySkin.Build(theme, _text, size, opacity, animate);
        Place();
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

    public void SetLines(IReadOnlyList<(string Label, string Value, Brush Color)> lines) =>
        Render(_text, lines, _oneLine);

    /// <summary>
    /// Escreve as linhas. Se são as mesmas linhas de antes, só troca os números (sem recriar nada);
    /// e o painel nunca encolhe por causa de um número mais curto, então não fica pulando de tamanho.
    /// </summary>
    public static void Render(TextBlock text, IReadOnlyList<(string Label, string Value, Brush Color)> lines, bool oneLine)
    {
        var key = (oneLine ? "1|" : "0|") + string.Join("|", lines.Select(l => l.Label));
        if (text.Tag is (string oldKey, List<Run> values) && oldKey == key && values.Count == lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (values[i].Text != lines[i].Value) values[i].Text = lines[i].Value;
                if (values[i].Foreground != lines[i].Color) values[i].Foreground = lines[i].Color;
            }
        }
        else
        {
            text.Inlines.Clear();
            var runs = new List<Run>();
            var labelBrush = OverlaySkin.LabelBrush(text);
            for (var i = 0; i < lines.Count; i++)
            {
                var (label, value, color) = lines[i];
                if (i > 0) text.Inlines.Add(oneLine ? new Run("   ") : new LineBreak());
                if (label.Length > 0) text.Inlines.Add(new Run(oneLine ? label + " " : $"{label,-5} ") { Foreground = labelBrush });
                var run = new Run(value) { Foreground = color };
                runs.Add(run);
                text.Inlines.Add(run);
            }
            text.Tag = (key, runs);
            text.MinWidth = 0;
        }
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.MinWidth = Math.Max(text.MinWidth, Math.Ceiling(text.DesiredSize.Width));
    }

    private void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        _onTop.Start();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
