using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class PerfTestView : UserControl
{
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _cts;
    private PerfTest? _test;
    private PerfRun? _shown;
    private bool _loading;

    public PerfTestView()
    {
        InitializeComponent();
        _ui.Tick += (_, _) => ShowProgress();
        Loaded += (_, _) => LoadHistory();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (StressBusy()) return;
        var r = HardwareService.Instance.Readings;
        SensorWarn.Visibility = HardwareService.CpuNow(r).Temp == null ? Visibility.Visible : Visibility.Collapsed;

        _cts = new CancellationTokenSource();
        _test = new PerfTest();
        StartButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        RunPanel.Visibility = Visibility.Visible;
        LabelBox.IsEnabled = DiskBox.IsEnabled = false;
        _ui.Start();
        try
        {
            var run = await _test.RunAsync(DiskBox.IsChecked == true, LabelBox.Text, _cts.Token);
            // Medição interrompida não vira referência (os números estariam pela metade).
            if (run.StopReason.Length == 0) await Task.Run(() => PerfHistory.Add(run));
            LoadHistory();
            Show(run);
            StepText.Text = run.StopReason.Length > 0 ? run.StopReason : "Pronto. Resultado salvo.";
        }
        catch (Exception ex)
        {
            StepText.Text = $"Não foi possível terminar: {ex.Message}";
        }
        finally
        {
            _ui.Stop();
            Bar.Value = _test?.Progress ?? 0;
            _cts.Dispose();
            _cts = null;
            StartButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            LabelBox.IsEnabled = DiskBox.IsEnabled = true;
        }
    }

    /// <summary>Teste completo e stress test ao mesmo tempo dariam números errados (e calor demais).</summary>
    private static bool StressBusy()
    {
        if (!SharedPages.Get<StressView>().IsRunning) return false;
        MessageBox.Show("O stress test está rodando. Pare o stress test antes de medir.", "SoNexus");
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ShowProgress()
    {
        if (_test == null) return;
        StepText.Text = _test.Step;
        Bar.Value = _test.Progress;
        TempText.Text = string.Join("  ·  ", new[]
        {
            _test.MaxCpuTemp is { } c ? $"CPU máx {c:0} °C" : null,
            _test.MaxGpuTemp is { } g ? $"GPU máx {g:0} °C" : null,
        }.Where(x => x != null));
    }

    private void LoadHistory()
    {
        var runs = PerfHistory.Load();
        HistoryList.ItemsSource = runs;
        HistoryEmpty.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shown == null && runs.Count > 0) Show(runs[0]);
    }

    /// <summary>Mostra uma medição comparada, por padrão, com a anterior a ela.</summary>
    private void Show(PerfRun run)
    {
        _shown = run;
        ResultCard.Visibility = Visibility.Visible;
        ResultTitle.Text = run.Title;
        var others = PerfHistory.Load().Where(r => r.Date != run.Date).ToList();
        _loading = true;
        BaseBox.ItemsSource = others;
        BaseBox.SelectedItem = others.FirstOrDefault(r => r.Date < run.Date) ?? others.FirstOrDefault();
        _loading = false;
        ShowRows();
    }

    private void Base_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ShowRows();
    }

    private void ShowRows()
    {
        if (_shown is not { } run) return;
        var before = BaseBox.SelectedItem as PerfRun;
        Rows.ItemsSource = PerfTest.Compare(run, before).Where(r => r.Now != null || r.Before != null).ToList();
        var info = new List<string>();
        if (run.Cpu.Length > 0) info.Add(run.Cpu);
        if (run.GpuDriver.Length > 0) info.Add($"driver {run.GpuDriver}");
        if (run.RamConfig.Length > 0) info.Add($"RAM {run.RamConfig}");
        if (run.MaxCpuTemp is { } c) info.Add($"CPU máx {c:0} °C");
        if (run.MaxGpuTemp is { } g) info.Add($"GPU máx {g:0} °C");
        info.Add($"SoNexus {run.AppVersion}");
        var text = string.Join(" · ", info);
        if (before != null && before.GpuDriver != run.GpuDriver && before.GpuDriver.Length > 0)
            text += $"\nO driver de vídeo mudou desde a medição comparada ({before.GpuDriver}).";
        if (before != null && before.RamConfig != run.RamConfig && before.RamConfig.Length > 0)
            text += $"\nA RAM mudou desde a medição comparada (antes: {before.RamConfig}).";
        if (before == null) text += "\nRode de novo depois de uma mudança para ver a comparação aqui.";
        if (run.StopReason.Length > 0) text += $"\n{run.StopReason}";
        ResultInfo.Text = text;
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PerfRun run }) Show(run);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PerfRun run }) return;
        if (MessageBox.Show($"Apagar a medição de {run.Title}?", "SoNexus", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        PerfHistory.Remove(run.Date);
        if (_shown?.Date == run.Date)
        {
            _shown = null;
            ResultCard.Visibility = Visibility.Collapsed;
        }
        LoadHistory();
    }
}
