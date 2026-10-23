using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class HardwareBenchView : UserControl
{
    private const double Gtx1060Tflops = 4.4; // referência de "placa média" usada no resto do app

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cts;
    private GpuTester? _gpu;
    private Stopwatch _phaseClock = new();
    private TimeSpan _phaseDuration;
    private Func<string>? _live;

    public HardwareBenchView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (_phaseDuration > TimeSpan.Zero) PhaseBar.Value = Math.Min(100, _phaseClock.Elapsed / _phaseDuration * 100);
            LiveText.Text = _live?.Invoke() ?? "";
        };
    }

    private async void All_Click(object sender, RoutedEventArgs e) => await RunAsync(gpu: true, ram: true, disk: true);
    private async void Gpu_Click(object sender, RoutedEventArgs e) => await RunAsync(gpu: true, ram: false, disk: false);
    private async void Ram_Click(object sender, RoutedEventArgs e) => await RunAsync(gpu: false, ram: true, disk: false);
    private async void Disk_Click(object sender, RoutedEventArgs e) => await RunAsync(gpu: false, ram: false, disk: true);
    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task RunAsync(bool gpu, bool ram, bool disk)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartBar.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        _timer.Start();
        try
        {
            if (gpu) await GpuAsync(ct);
            if (ram) await RamAsync(ct);
            if (disk) await DiskAsync(ct);
            PhaseText.Text = "Concluído";
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Cancelado";
        }
        catch (Exception ex)
        {
            PhaseText.Text = $"Erro: {ex.Message}";
            AppLog.Write($"Benchmark de hardware: {ex}");
        }
        finally
        {
            _timer.Stop();
            _live = null;
            LiveText.Text = "";
            PhaseBar.Value = 0;
            _gpu?.Dispose();
            _gpu = null;
            StartBar.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Phase(string name, TimeSpan duration)
    {
        PhaseText.Text = name;
        _phaseDuration = duration;
        _phaseClock = Stopwatch.StartNew();
    }

    private async Task GpuAsync(CancellationToken ct)
    {
        GpuScore.Text = VramScore.Text = "—";
        _gpu = await Task.Run(GpuTester.Create);
        GpuName.Text = _gpu.AdapterName + (_gpu.DedicatedVram > 0 ? $" · {Format.Bytes(_gpu.DedicatedVram)}" : "");

        // Computação: 15 s, usa a média dos últimos lotes (a placa sobe o clock nos primeiros segundos).
        Phase("Placa de vídeo: computação", TimeSpan.FromSeconds(15));
        var rates = new List<double>();
        _live = () =>
        {
            var (t, load, p, _) = HardwareService.GpuNow(HardwareService.Instance.Readings);
            if (_gpu.Rate > 0) rates.Add(_gpu.Rate);
            return $"{_gpu.Rate / 1000:0.0} TFLOPS" + (t is { } x ? $" · {x:0} °C" : "") + (p is { } w ? $" · {w:0} W" : "") + (load is { } l ? $" · {l:0}%" : "");
        };
        await Run(ct, TimeSpan.FromSeconds(15), c => _gpu.RunCompute(c));
        var tflops = (rates.Count > 4 ? rates.Skip(rates.Count / 2).Average() : _gpu.Rate) / 1000;
        GpuScore.Text = $"{tflops:0.0} TFLOPS";
        GpuDetail.Text = $"≈ {tflops / Gtx1060Tflops:0.0}× uma GTX 1060 em cálculo bruto (FP32)." +
                         (_gpu.Errors > 0 ? $" ATENÇÃO: {_gpu.Errors} erro(s) de cálculo, a placa está instável." : " Nenhum erro de cálculo.");
        GpuDetail.Foreground = _gpu.Errors > 0 ? Metric.Danger : (System.Windows.Media.Brush)FindResource("MutedBrush");

        // VRAM: 10 s escrevendo e lendo ~80% da memória de vídeo.
        var vram = GpuTester.Create();
        _gpu.Dispose();
        _gpu = vram;
        Phase("Memória de vídeo: escrevendo e conferindo", TimeSpan.FromSeconds(10));
        var vrates = new List<double>();
        _live = () =>
        {
            if (vram.Rate > 0) vrates.Add(vram.Rate);
            return vram.VramTested > 0 ? $"{vram.Rate:0} GB/s · {Format.Bytes(vram.VramTested)}" : "reservando a VRAM...";
        };
        await Run(ct, TimeSpan.FromSeconds(10), c => vram.RunVram(c));
        var gbs = vrates.Count > 2 ? vrates.Skip(vrates.Count / 2).Average() : vram.Rate;
        VramScore.Text = $"{gbs:0} GB/s";
        VramDetail.Text = $"{Format.Bytes(vram.VramTested)} testados, {vram.Work} passadas. " +
                          (vram.Errors > 0 ? $"ATENÇÃO: {vram.Errors} valor(es) errado(s) na VRAM." : "Nenhum erro na memória de vídeo.");
        VramDetail.Foreground = vram.Errors > 0 ? Metric.Danger : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private async Task RamAsync(CancellationToken ct)
    {
        RamScore.Text = "—";
        Phase("Memória RAM: latência e velocidade", TimeSpan.FromSeconds(20));
        var status = "";
        _live = () => status;
        var r = await Task.Run(() => MemoryInfoService.Benchmark(s => status = s), ct);
        RamScore.Text = $"{r.ReadGBs:0.0} GB/s";
        var layout = MemoryInfoService.Layout();
        RamDetail.Text = $"Leitura {r.ReadGBs:0.0} · escrita {r.WriteGBs:0.0} · cópia {r.CopyGBs:0.0} GB/s · latência {r.RamNs:0} ns" +
                         (layout.TheoreticalGBs > 0 ? $"\nMáximo teórico dos seus pentes: {layout.TheoreticalGBs:0} GB/s ({layout.Speed} MT/s, {(layout.Modules.Count >= 2 ? "dual channel" : "canal único")})." : "") +
                         (r.RamNs > 100 ? "\nLatência alta: confira se o XMP/EXPO está ativado na BIOS." : "");
    }

    private async Task DiskAsync(CancellationToken ct)
    {
        DiskScore.Text = "—";
        var folder = Path.GetTempPath();
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        if (drive.AvailableFreeSpace < 3L << 30)
        {
            DiskDetail.Text = "Precisa de pelo menos 3 GB livres no disco para o teste.";
            return;
        }
        Phase($"Disco {drive.Name.TrimEnd('\\')}", TimeSpan.FromSeconds(15));
        var status = "";
        _live = () => status;
        var d = await Task.Run(() => DiskBenchService.Run(folder, s => status = s, ct), ct);
        DiskScore.Text = $"{d.SeqReadMBs:0} MB/s";
        var kind = d.SeqReadMBs switch { > 2500 => "SSD NVMe rápido", > 1200 => "SSD NVMe", > 400 => "SSD SATA", > 90 => "HD", _ => "disco lento" };
        DiskDetail.Text = $"{d.Drive.TrimEnd('\\')} · leitura {d.SeqReadMBs:0} · gravação {d.SeqWriteMBs:0} MB/s · aleatório 4K {d.RandIops:N0} IOPS ({d.RandReadMBs:0} MB/s)\nDesempenho de {kind}.";
    }

    /// <summary>Roda um teste "até cancelar" por um tempo fixo.</summary>
    private static async Task Run(CancellationToken outer, TimeSpan duration, Action<CancellationToken> work)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(duration);
        await Task.Run(() => work(cts.Token));
        outer.ThrowIfCancellationRequested();
    }
}
