using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class StressView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StressTest? _test;
    private volatile GpuTester? _gpu;
    private StressMode _mode;
    private CancellationTokenSource? _cts;
    private Stopwatch _clock = new();
    private TimeSpan? _duration;
    private int _tempLimit;
    private string? _stopReason;
    private string? _gpuError;

    private float _maxTemp, _maxPower, _minClock = float.MaxValue, _maxClock, _maxGpuTemp;

    public StressView()
    {
        InitializeComponent();
        TempChart.Stroke = Metric.Warn;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Modos em que os números principais são os da placa de vídeo.</summary>
    private bool GpuFocus => _mode is StressMode.Gpu or StressMode.Vram;
    private bool UsesGpu => _mode is StressMode.Gpu or StressMode.Vram or StressMode.Tudo;

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stopReason ??= "Parado por você.";
        _cts?.Cancel();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var mode = (StressMode)ModeBox.SelectedIndex;
        var minutes = int.Parse((string)((ComboBoxItem)DurationBox.SelectedItem).Tag);
        var tempLimit = int.Parse((string)((ComboBoxItem)LimitBox.SelectedItem).Tag);
        if (!HardwareTest.Slot.Wait(0))
        {
            MessageBox.Show("Aguarde o teste de hardware em andamento terminar.", "SoNexus");
            return;
        }

        _mode = mode;
        _duration = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        _tempLimit = tempLimit;
        _stopReason = _gpuError = null;
        _maxTemp = _maxPower = _maxClock = _maxGpuTemp = 0;
        _minClock = float.MaxValue;
        TempChart.Clear();
        PowerChart.Clear();
        ResultBanner.Visibility = Visibility.Collapsed;

        var part = GpuFocus ? "GPU" : "CPU";
        TempLabel.Text = $"TEMPERATURA {part}";
        PowerLabel.Text = $"CONSUMO {part}";
        ClockLabel.Text = GpuFocus ? "CLOCK DA GPU" : "CLOCK MÉDIO";
        TempChartTitle.Text = GpuFocus ? "Temperatura da placa de vídeo" : "Temperatura do processador";
        PowerChartTitle.Text = GpuFocus ? "Consumo da placa de vídeo" : "Consumo do processador";

        SetupPanel.IsEnabled = false;
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;

        _test = new StressTest();
        _gpu = null;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        _clock = Stopwatch.StartNew();

        try
        {
            _timer.Start();
            Tick();
            var tasks = new List<Task>();
            var threads = Environment.ProcessorCount;
            if (!_cts.IsCancellationRequested)
            {
                switch (_mode)
                {
                    case StressMode.Cpu or StressMode.CpuMemoria:
                        tasks.Add(_test.RunAsync(_mode, threads, _cts.Token));
                        break;
                    case StressMode.Ram:
                        tasks.Add(_test.RunRamAsync(threads, _cts.Token));
                        break;
                    case StressMode.Tudo:
                        // Deixa 1 thread livre para alimentar a placa de vídeo.
                        tasks.Add(_test.RunAsync(StressMode.Cpu, Math.Max(1, threads - 1), _cts.Token));
                        break;
                }
                if (UsesGpu) tasks.Add(RunGpuAsync(_cts.Token));
            }
            await Task.WhenAll(tasks.Select(CancelOnFailure));
            _clock.Stop();
            Tick();
            ShowResult();
        }
        catch (Exception ex)
        {
            _stopReason = $"O teste não terminou: {ex.Message}";
            AppLog.Write($"Stress: {ex}");
            ShowResult();
        }
        finally
        {
            _clock.Stop();
            _timer.Stop();
            try { _gpu?.Dispose(); }
            finally
            {
                _gpu = null;
                _cts = null;
                SetupPanel.IsEnabled = true;
                StartButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
                HardwareTest.Slot.Release();
            }
        }

        async Task CancelOnFailure(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }
    }

    private Task RunGpuAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var gpu = GpuTester.Create();
            _gpu = gpu;
            if (_mode == StressMode.Vram) gpu.RunVram(ct, Limit(true));
            else gpu.RunCompute(ct, Limit(true));
        }
        catch (HardwareTestSafetyException ex)
        {
            Dispatcher.Invoke(() => StopForSafety(ex.Message));
        }
        catch (Exception ex)
        {
            // Placa "sumiu" no meio do teste (driver reiniciou) também é sinal de instabilidade.
            _gpuError = ex.Message;
            AppLog.Write($"Stress GPU: {ex}");
            Dispatcher.Invoke(() => { _stopReason ??= ""; _cts?.Cancel(); });
        }
    });

    private void Tick()
    {
        if (_test == null) return;
        var elapsed = _clock.Elapsed;
        TimeText.Text = elapsed.ToString(@"hh\:mm\:ss");
        TimeDetail.Text = _duration is { } d ? $"de {d:hh\\:mm\\:ss}" : "sem limite";

        var r = HardwareService.Instance.FreshReadings;
        var (cpuTemp, cpuPower, cpuClock) = HardwareService.CpuNow(r);
        var gpu = _gpu;
        var gpuReadings = gpu == null ? Array.Empty<SensorReading>() : HardwareService.TestGpuReadings(r, gpu.AdapterName).ToArray();
        var gpuTemp = HardwareService.TestGpuTemp(gpuReadings);
        var (_, gpuLoad, gpuPower, gpuClock) = HardwareService.GpuNow(gpuReadings);
        if (gpuTemp is { } gt) _maxGpuTemp = Math.Max(_maxGpuTemp, gt);

        var (temp, power, clock) = GpuFocus ? (gpuTemp, gpuPower, gpuClock) : (cpuTemp, cpuPower, cpuClock);
        if (temp is { } t)
        {
            _maxTemp = Math.Max(_maxTemp, t);
            TempText.Text = $"{t:0} °C";
            TempText.Foreground = t < 80 ? Metric.Ok : t < 90 ? Metric.Warn : Metric.Danger;
            TempDetail.Text = $"máx {_maxTemp:0} °C · limite {Limit(GpuFocus)} °C" + (_mode == StressMode.Tudo && gpuTemp is { } g ? $" · GPU {g:0} °C" : "");
            TempChart.Push(t);
        }
        // Segurança: para se qualquer peça em teste passar do limite.
        if (_clock.IsRunning && _stopReason == null)
        {
            if (!GpuFocus && _mode != StressMode.Ram && (cpuTemp == null || cpuTemp >= _tempLimit))
                StopForSafety(cpuTemp == null ? "sem leitura recente de temperatura do processador"
                    : $"o processador chegou a {cpuTemp:0} °C (limite {_tempLimit} °C)");
        }
        if (power is { } p)
        {
            _maxPower = Math.Max(_maxPower, p);
            PowerText.Text = $"{p:0} W";
            var total = _mode == StressMode.Tudo && gpuPower is { } gp ? $" · CPU + GPU {p + gp:0} W" : "";
            PowerDetail.Text = $"máx {_maxPower:0} W{total}";
            PowerChart.Push(p);
        }
        if (clock is { } c && elapsed.TotalSeconds > 3)
        {
            _minClock = Math.Min(_minClock, c);
            _maxClock = Math.Max(_maxClock, c);
            ClockText.Text = GpuFocus ? $"{c:0} MHz" : $"{c / 1000:0.00} GHz";
            ClockDetail.Text = GpuFocus ? $"mín {_minClock:0} · máx {_maxClock:0} MHz" + (gpuLoad is { } l ? $" · uso {l:0}%" : "")
                                        : $"mín {_minClock / 1000:0.00} · máx {_maxClock / 1000:0.00} GHz";
        }

        var errors = _test.Errors + (_gpu?.Errors ?? 0);
        ErrorsText.Text = $"{errors}";
        ErrorsText.Foreground = errors == 0 ? Metric.Ok : Metric.Danger;
        ErrorsDetail.Text = _mode switch
        {
            StressMode.Ram => $"{Format.Bytes(_test.RamTested)} · {_test.Iterations} passadas",
            StressMode.Gpu => _gpu is { } g1 ? $"{g1.Work} lotes · {g1.Rate / 1000:0.0} TFLOPS" : "iniciando a placa...",
            StressMode.Vram => _gpu is { } g2 ? (g2.VramTested > 0 ? $"{Format.Bytes(g2.VramTested)} · {g2.Work} passadas · {g2.Rate:0} GB/s" : "reservando a VRAM...") : "iniciando a placa...",
            StressMode.Tudo => $"{_test.Iterations} ciclos CPU · {_gpu?.Work ?? 0} lotes GPU",
            _ => $"{_test.Iterations} ciclos conferidos",
        };

        if (_duration is { } limit && elapsed >= limit && _stopReason == null)
        {
            _stopReason = "";
            _cts?.Cancel();
        }
    }

    /// <summary>Placa de vídeo: no máximo 90 °C (elas já reduzem o clock sozinhas perto disso).</summary>
    private int Limit(bool gpu) => gpu ? Math.Min(_tempLimit, 90) : _tempLimit;

    private void StopForSafety(string what)
    {
        _stopReason ??= $"Parado por segurança: {what}.";
        _cts?.Cancel();
    }

    private void ShowResult()
    {
        if (_test == null) return;
        var minutes = _clock.Elapsed.TotalMinutes;
        var errors = _test.Errors + (_gpu?.Errors ?? 0);
        string text;
        Brush color;
        if (_gpuError != null)
        {
            text = $"A placa de vídeo parou de responder durante o teste ({_gpuError}). Se ela estava com overclock, volte ao padrão; senão, reinstale o driver de vídeo.";
            color = Metric.Danger;
        }
        else if (errors > 0)
        {
            text = $"INSTÁVEL: {errors} erro(s) em {minutes:0.#} min. " + _mode switch
            {
                StressMode.Ram => "Desative o XMP/EXPO na BIOS ou teste os pentes um de cada vez (Diagnóstico › Teste de RAM).",
                StressMode.Gpu => "Reduza o overclock/undervolt da placa de vídeo (Jogos › Overclock › Restaurar padrão).",
                StressMode.Vram => "Reduza o overclock de memória da placa de vídeo. Se estiver no padrão, a placa pode ter defeito.",
                _ => "Reduza o overclock/undervolt ou aumente a tensão.",
            };
            color = Metric.Danger;
        }
        else if (!string.IsNullOrEmpty(_stopReason) && _stopReason.StartsWith("Parado por segurança"))
        {
            text = _stopReason + " Verifique o cooler, a pasta térmica e a ventilação do gabinete.";
            color = Metric.Warn;
        }
        else if (_stopReason?.StartsWith("O teste não terminou") == true
            || (_mode == StressMode.Ram && (_test.Iterations == 0 || _test.RamAllocationFailures > 0)))
        {
            text = _stopReason?.StartsWith("O teste não terminou") == true ? _stopReason
                : $"Teste de RAM inconclusivo: {Format.Bytes(_test.RamTested)} verificados, {_test.RamAllocationFailures} falha(s) de alocação.";
            color = Metric.Warn;
        }
        else
        {
            var temps = _mode == StressMode.Ram ? "" : $" Temperatura máxima {_maxTemp:0} °C" + (_mode == StressMode.Tudo ? $" (GPU {_maxGpuTemp:0} °C)" : "") + $", consumo máximo {_maxPower:0} W.";
            var extra = _mode == StressMode.Tudo ? " A fonte aguentou CPU e GPU juntos sem desligar." : "";
            text = $"Estável: nenhum erro em {minutes:0.#} min.{temps}{extra}" + (string.IsNullOrEmpty(_stopReason) ? "" : $" ({_stopReason})");
            color = Metric.Ok;
        }
        ResultText.Text = text;
        ResultText.Foreground = color;
        ResultBanner.BorderBrush = color;
        var bg = color.Clone();
        bg.Opacity = 0.12;
        ResultBanner.Background = bg;
        ResultBanner.Visibility = Visibility.Visible;
    }
}
