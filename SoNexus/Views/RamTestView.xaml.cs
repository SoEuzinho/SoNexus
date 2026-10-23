using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class RamTestView : UserControl
{
    private CancellationTokenSource? _cts;
    private RamTester? _tester;

    public RamTestView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateAmount();

    private void Percent_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AmountText != null) UpdateAmount();
    }

    private long TargetBytes() => (long)(MemoryService.GetStatus().Available * PercentSlider.Value / 100);

    private void UpdateAmount()
    {
        var s = MemoryService.GetStatus();
        AmountText.Text = $"{PercentSlider.Value:0}% da RAM livre = {Format.Bytes((ulong)TargetBytes())}  (livre: {Format.Bytes(s.Available)} de {Format.Bytes(s.Total)})";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var passes = int.Parse((string)((ComboBoxItem)PassesBox.SelectedItem).Tag);
        _cts = new CancellationTokenSource();
        _tester = new RamTester();

        SetupPanel.IsEnabled = false;
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        ErrorsText.Text = "0";
        ErrorsText.Foreground = Metric.Ok;
        VerdictText.Text = "";
        ErrorGrid.ItemsSource = null;
        ProgressBar.Value = 0;

        PhaseText.Text = "Reservando memória...";
        var target = TargetBytes();
        var allocated = await Task.Run(() => _tester.Allocate(target));
        PhaseText.Text = $"Testando {Format.Bytes((ulong)allocated)}...";

        var sw = Stopwatch.StartNew();
        var lastErrors = 0L;
        var progress = new Progress<RamTestProgress>(p =>
        {
            ProgressBar.Value = p.Percent;
            var pass = passes == 0 ? $"Passagem {p.Pass}" : $"Passagem {p.Pass} de {passes}";
            PhaseText.Text = $"{pass} · padrão {p.PatternIndex}/{p.PatternCount}: {p.Pattern} ({p.Phase}) · {p.GBps:0.0} GB/s · {sw.Elapsed:hh\\:mm\\:ss}";
            ShowErrors(p.Errors, ref lastErrors);
        });

        try
        {
            await Task.Run(() => _tester.Run(passes, progress, _cts.Token));
        }
        catch (Exception ex)
        {
            VerdictText.Text = $"Teste interrompido: {ex.Message}";
        }

        ShowErrors(_tester.ErrorCount, ref lastErrors);
        var tested = Format.Bytes((ulong)_tester.AllocatedBytes);
        var cancelled = _cts.IsCancellationRequested;
        VerdictText.Text = _tester.ErrorCount == 0
            ? $"{(cancelled ? "Parado" : "Concluído")} em {sw.Elapsed:hh\\:mm\\:ss} sem erros ({tested} testados)."
            : $"{_tester.ErrorCount} erro(s). A RAM está instável: teste um pente por vez, volte o XMP/EXPO ao padrão ou aumente a tensão.";
        PhaseText.Text = cancelled ? "Teste parado." : "Teste concluído.";

        _tester.Dispose();
        _tester = null;
        SetupPanel.IsEnabled = true;
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        UpdateAmount();
    }

    private void ShowErrors(long errors, ref long lastErrors)
    {
        ErrorsText.Text = $"{errors}";
        ErrorsText.Foreground = errors == 0 ? Metric.Ok : Metric.Danger;
        if (errors != lastErrors && _tester != null)
        {
            ErrorGrid.ItemsSource = _tester.Errors;
            lastErrors = errors;
        }
    }
}
