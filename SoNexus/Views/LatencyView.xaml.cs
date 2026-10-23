using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class LatencyView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LatencyMonitor? _monitor;
    private Stopwatch _clock = new();

    public LatencyView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        LatencyChart.Clear();
        _monitor = new LatencyMonitor();
        _monitor.Start();
        _clock = Stopwatch.StartNew();
        _timer.Start();
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        SetVerdict("Medindo... use o PC normalmente (ou abra um jogo) por pelo menos 1 minuto.", null);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Refresh();
        _monitor?.Dispose();
        _monitor = null;
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
    }

    private void Refresh()
    {
        if (_monitor == null) return;
        var s = _monitor.Snapshot();

        TimerMaxText.Text = $"{s.TimerMaxUs:0} µs";
        TimerMaxText.Foreground = LevelBrush(s.TimerMaxUs, 1000, 2000);
        TimerAvgText.Text = $"{s.TimerAvgUs:0} µs";
        TimerDetail.Text = $"{s.TimerSamples:N0} atrasos · {_clock.Elapsed:hh\\:mm\\:ss}";
        LatencyChart.Push(s.TimerLastSecondMaxUs);

        if (s.EtwError is null)
        {
            DpcText.Text = $"{s.DpcMaxUs:0} µs";
            DpcText.Foreground = LevelBrush(s.DpcMaxUs, 500, 1000);
            DpcDriver.Text = s.DpcMaxDriver;
            IsrText.Text = $"{s.IsrMaxUs:0} µs";
            IsrText.Foreground = LevelBrush(s.IsrMaxUs, 500, 1000);
            IsrDriver.Text = s.IsrMaxDriver;
            DriverGrid.ItemsSource = s.Drivers;
        }
        else
        {
            DpcText.Text = IsrText.Text = "—";
            DpcDriver.Text = IsrDriver.Text = "indisponível";
        }

        if (_clock.Elapsed.TotalSeconds < 10) return;
        var worst = Math.Max(s.TimerMaxUs, Math.Max(s.DpcMaxUs * 2, s.IsrMaxUs * 2));
        var culprit = s.DpcMaxUs >= s.IsrMaxUs ? s.DpcMaxDriver : s.IsrMaxDriver;
        if (worst < 1000)
            SetVerdict("Ótimo: o PC responde rápido. Bom para jogos, áudio e streaming em tempo real.", Metric.Ok);
        else if (worst < 2000)
            SetVerdict($"Atenção: houve atrasos moderados. Driver mais suspeito: {culprit}. Pode causar pequenos engasgos.", Metric.Warn);
        else
            SetVerdict($"Problema: atrasos altos que podem causar estalos no áudio, travadinhas e input lag. Principal suspeito: {culprit}. Atualize ou reinstale esse driver.", Metric.Danger);
        if (s.EtwError is { } err) VerdictText.Text += $"\n{err}";
    }

    private void SetVerdict(string text, Brush? color)
    {
        VerdictText.Text = text;
        VerdictText.Foreground = color ?? (Brush)FindResource("TextBrush");
        Verdict.BorderBrush = color ?? (Brush)FindResource("LineBrush");
    }

    private static Brush LevelBrush(double us, double warn, double bad) =>
        us < warn ? Metric.Ok : us < bad ? Metric.Warn : Metric.Danger;
}
