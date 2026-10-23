using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;
using HW = LibreHardwareMonitor.Hardware;

namespace SoNexus.Views;

public partial class HomeView : UserControl
{
    private readonly ObservableCollection<SensorLine> _temps = new();
    private readonly ObservableCollection<SensorLine> _fans = new();
    private readonly ObservableCollection<SensorLine> _power = new();
    private (long Rx, long Tx)? _lastNet;
    private long _lastNetTime;

    public HomeView()
    {
        InitializeComponent();
        TempList.ItemsSource = _temps;
        FanList.ItemsSource = _fans;
        PowerList.ItemsSource = _power;
        CpuTempChart.Stroke = GpuTempChart.Stroke = System.Windows.Media.Brushes.White;
        RamChart.Stroke = NetChart.Stroke = (System.Windows.Media.Brush)Application.Current.FindResource("Accent2Brush");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HardwareService.Instance.Updated += OnUpdated;
        // A soma dos pentes vem do WMI (lento): calcula fora da tela antes da primeira atualização.
        if (_installedGb == null)
        {
            var visible = MemoryService.GetStatus().Total;
            await Task.Run(() => InstalledGb(visible));
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HardwareService.Instance.Updated -= OnUpdated;

    // Janela escondida na bandeja: não redesenha gráficos que ninguém está vendo.
    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => { if (IsVisible) Refresh(); });

    private void Refresh()
    {
        var svc = HardwareService.Instance;
        var r = svc.Readings;

        if (svc.Error != null) ShowBanner($"Não foi possível ler os sensores: {svc.Error}. Execute o SoNexus como administrador.");
        else if (r.Count == 0) ShowBanner("Iniciando sensores... (a primeira leitura leva alguns segundos)");
        else Banner.Visibility = Visibility.Collapsed;

        UpdateNetwork();
        if (r.Count == 0) return;

        var gpu = HardwareService.GpuFilter(r);
        var cpuName = HardwareService.HardwareName(r, HardwareService.IsCpu) ?? "CPU";
        var gpuName = HardwareService.HardwareName(r, gpu) ?? "GPU";
        Subtitle.Text = $"{Environment.MachineName}  ·  {cpuName}  ·  {gpuName}";

        // CPU
        var (cpuTemp, cpuPower, cpuClock) = HardwareService.CpuNow(r);
        var cpuLoad = HardwareService.Find(r, HardwareService.IsCpu, HW.SensorType.Load, false, "CPU Total")?.Value;
        CpuValue.Text = Pct(cpuLoad);
        CpuDetail.Text = Join(Num(cpuTemp, "°C"), cpuClock is { } c ? $"{c / 1000:0.00} GHz" : null, Num(cpuPower, "W"));
        if (cpuLoad is { } cl) CpuChart.Push(cl);
        if (cpuTemp is { } ct) { CpuTempChart.Push(ct); CpuTempText.Text = $"{ct:0} °C"; }

        // GPU
        var (gpuTemp, gpuLoad, gpuPower, gpuClock) = HardwareService.GpuNow(r);
        GpuValue.Text = Pct(gpuLoad);
        GpuDetail.Text = Join(Num(gpuTemp, "°C"), gpuClock is { } g ? $"{g:0} MHz" : null, Num(gpuPower, "W"));
        if (gpuLoad is { } gl) GpuChart.Push(gl);
        if (gpuTemp is { } gt) { GpuTempChart.Push(gt); GpuTempText.Text = $"{gt:0} °C"; }

        // RAM: direto do Windows. Os sensores da biblioteca misturam memória virtual (arquivo de paginação)
        // e os pentes, o que dava um total maior que a RAM instalada.
        var mem = MemoryService.GetStatus();
        var usedGb = (mem.Total - mem.Available) / 1073741824.0;
        RamValue.Text = $"{mem.Load}%";
        RamDetail.Text = $"{usedGb:0.0} de {InstalledGb(mem.Total)} GB em uso";
        RamChart.Push(mem.Load);

        // Listas de sensores
        // 255 °C (e acima de 150) é o código de "sensor não suportado", não uma leitura real.
        SensorLine.Sync(_temps, r.Where(x => x.Type == HW.SensorType.Temperature && x.Value is > 0 and < 150 && !x.Name.Contains("Distance")), Short);
        SensorLine.Sync(_fans, r.Where(x =>
            (x.Type == HW.SensorType.Fan && (x.Value > 0 || IsGpu(x.HardwareType))) ||
            (x.Type == HW.SensorType.Control && x.Value.HasValue) ||
            (x.Type == HW.SensorType.Clock && !x.Name.Contains('#') && x.Value > 0)), Short);
        SensorLine.Sync(_power, r.Where(x =>
            (x.Type == HW.SensorType.Power && !x.Name.Contains('#') && x.Value.HasValue) ||
            (x.Type == HW.SensorType.Voltage && !x.Name.Contains('#') && x.Value > 0)), Short);
    }

    private void UpdateNetwork()
    {
        var now = Stopwatch.GetTimestamp();
        var bytes = NetworkService.TotalBytes();
        if (_lastNet is { } last)
        {
            var seconds = (now - _lastNetTime) / (double)Stopwatch.Frequency;
            var down = Math.Max(0, bytes.Rx - last.Rx) * 8 / seconds / 1e6;
            var up = Math.Max(0, bytes.Tx - last.Tx) * 8 / seconds / 1e6;
            NetValue.Text = $"↓ {Mbps(down)}";
            NetDetail.Text = $"↑ {Mbps(up)}  ·  total ↓ {Format.Bytes((ulong)bytes.Rx)}";
            NetChart.Push(down);
        }
        _lastNet = bytes;
        _lastNetTime = now;
    }

    private static int? _installedGb;

    /// <summary>RAM instalada (soma dos pentes), ex.: 16. O Windows reserva um pouco para a placa de vídeo/BIOS.</summary>
    private static int InstalledGb(ulong visible)
    {
        if (_installedGb == null)
        {
            var layout = MemoryInfoService.Layout();
            var sum = layout.Modules.Sum(m => (double)m.Capacity) / 1073741824.0;
            _installedGb = sum > 0 ? (int)Math.Round(sum) : (int)Math.Ceiling(visible / 1073741824.0);
        }
        return _installedGb.Value;
    }

    private static string Mbps(double v) => v >= 100 ? $"{v:0} Mbps" : $"{v:0.0} Mbps";

    private static bool IsGpu(HW.HardwareType t) => t is HW.HardwareType.GpuNvidia or HW.HardwareType.GpuAmd or HW.HardwareType.GpuIntel;

    private static string Short(SensorReading r)
    {
        var words = r.Hardware.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 2 ? r.Hardware : string.Join(' ', words.TakeLast(2));
    }

    private static string Pct(float? v) => v is { } x ? $"{x:0}%" : "—";
    private static string? Num(float? v, string unit) => v is { } x ? $"{x:0} {unit}" : null;
    private static string Join(params string?[] parts) => string.Join("  ·  ", parts.Where(p => p != null));

    private void ShowBanner(string text)
    {
        BannerText.Text = text;
        Banner.Visibility = Visibility.Visible;
    }
}
