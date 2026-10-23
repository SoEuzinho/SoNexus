using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class BenchmarkView : UserControl
{
    private static readonly TimeSpan MultiDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SingleDuration = TimeSpan.FromSeconds(20);

    private readonly WriteableBitmap _bitmap = new(BenchmarkRun.Width, BenchmarkRun.Height, 96, 96, PixelFormats.Bgra32, null);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private BenchmarkRun? _run;
    private CancellationTokenSource? _cts;
    private Stopwatch _phaseClock = new();
    private TimeSpan _phaseDuration;

    public BenchmarkView()
    {
        InitializeComponent();
        RenderImage.Source = _bitmap;
        _timer.Tick += (_, _) => DrawProgress();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => HistoryList.ItemsSource = BenchmarkHistory.Load();

    private async void Full_Click(object sender, RoutedEventArgs e) => await RunAsync(multi: true, single: true);
    private async void Multi_Click(object sender, RoutedEventArgs e) => await RunAsync(multi: true, single: false);
    private async void Single_Click(object sender, RoutedEventArgs e) => await RunAsync(multi: false, single: true);
    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task RunAsync(bool multi, bool single)
    {
        _cts = new CancellationTokenSource();
        StartBar.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        if (multi) MultiText.Text = "—";
        if (single) SingleText.Text = "—";
        RatioText.Text = "";

        double multiScore = 0, singleScore = 0;
        try
        {
            if (multi) multiScore = await PhaseAsync($"Multi-core ({Environment.ProcessorCount} threads)", Environment.ProcessorCount, MultiDuration, MultiText);
            if (single) singleScore = await PhaseAsync("Single-core (1 thread)", 1, SingleDuration, SingleText);

            PhaseText.Text = "Concluído";
            if (multiScore > 0 && singleScore > 0)
                RatioText.Text = $"Multi-core é {multiScore / singleScore:0.0}x o single-core.";
            BenchmarkHistory.Add(new BenchResult(DateTime.Now, CpuName(), Environment.ProcessorCount, singleScore, multiScore));
            HistoryList.ItemsSource = BenchmarkHistory.Load();
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Cancelado";
        }
        finally
        {
            _timer.Stop();
            LiveText.Text = "";
            StartBar.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<double> PhaseAsync(string name, int threads, TimeSpan duration, TextBlock target)
    {
        _run = new BenchmarkRun();
        _phaseDuration = duration;
        _phaseClock = Stopwatch.StartNew();
        PhaseText.Text = name;
        _timer.Start();

        var perSecond = await _run.RunAsync(threads, duration, _cts!.Token);
        _cts.Token.ThrowIfCancellationRequested();

        _timer.Stop();
        DrawProgress();
        var score = BenchmarkRun.ToScore(perSecond);
        target.Text = $"{score:0}";
        return score;
    }

    private void DrawProgress()
    {
        if (_run == null) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, BenchmarkRun.Width, BenchmarkRun.Height), _run.Pixels, BenchmarkRun.Width * 4, 0);

        var elapsed = _phaseClock.Elapsed;
        PhaseBar.Value = Math.Min(100, elapsed / _phaseDuration * 100);
        var live = elapsed.TotalSeconds > 0.5 ? BenchmarkRun.ToScore(_run.Iterations / elapsed.TotalSeconds) : 0;
        var (temp, power, clock) = HardwareService.CpuNow(HardwareService.Instance.Readings);
        var sensors = string.Join("  ·  ", new[]
        {
            temp is { } t ? $"{t:0} °C" : null,
            power is { } p ? $"{p:0} W" : null,
            clock is { } c ? $"{c / 1000:0.00} GHz" : null,
        }.Where(s => s != null));
        LiveText.Text = $"{live:0} pts  ·  {Math.Max(0, (_phaseDuration - elapsed).TotalSeconds):0} s" + (sensors.Length > 0 ? "  ·  " + sensors : "");
    }

    private static string CpuName() =>
        HardwareService.HardwareName(HardwareService.Instance.Readings, HardwareService.IsCpu) ?? "CPU";
}
