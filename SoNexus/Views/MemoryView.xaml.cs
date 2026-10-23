using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;
using HW = LibreHardwareMonitor.Hardware;

namespace SoNexus.Views;

public partial class MemoryView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private MemoryLayout? _layout;
    private bool _busy;

    public MemoryView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_layout == null)
        {
            _layout = await Task.Run(MemoryInfoService.Layout);
            ShowLayout(_layout);
        }
        await RefreshAsync();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void ShowLayout(MemoryLayout l)
    {
        var total = (ulong)l.Modules.Sum(m => (long)m.Capacity);
        var type = l.Modules.FirstOrDefault()?.Type ?? "";
        var channel = l.Channels >= 2 ? "dual channel" : "canal único (single channel)";
        Subtitle.Text = $"{Format.Bytes(total)} {type} · {l.Modules.Count} pente(s) · {channel} · {l.Speed} MT/s";
        TheoryText.Text = $"{l.TheoreticalGBs:0.0} GB/s";

        var items = new List<InfoItem>
        {
            new("Slots usados", l.Slots > 0 ? $"{l.Modules.Count} de {l.Slots}" : $"{l.Modules.Count}"),
            new("Capacidade máxima da placa", l.MaxCapacity > 0 ? Format.Bytes(l.MaxCapacity) : "—"),
            new("Modo", channel),
            new("Banda teórica", $"{l.TheoreticalGBs:0.0} GB/s"),
        };
        foreach (var m in l.Modules)
        {
            items.Add(new InfoItem($"— {m.Slot}", $"{Format.Bytes(m.Capacity)} {m.Type} · {m.Maker} {m.Part}"));
            items.Add(new InfoItem("   Velocidade", m.RatedMts > m.ConfiguredMts && m.ConfiguredMts > 0
                ? $"{m.ConfiguredMts} MT/s (o pente aceita {m.RatedMts})"
                : $"{m.ConfiguredMts} MT/s"));
            items.Add(new InfoItem("   Tensão · ranks · largura", $"{m.Voltage:0.00} V · {(m.Ranks > 0 ? m.Ranks.ToString() : "?")} rank(s) · {m.DataWidth} bits"));
            if (m.Serial.Length > 0 && m.Serial != "00000000") items.Add(new InfoItem("   Número de série", m.Serial));
        }
        ModuleList.ItemsSource = items;

        var advice = new List<string>();
        if (l.Modules.Count == 1)
            advice.Add("Você usa só 1 pente (canal único): metade da velocidade possível. Dois pentes iguais ativam o dual channel, que dá mais FPS em jogos.");
        if (l.Modules.Any(m => m.RatedMts > m.ConfiguredMts && m.ConfiguredMts > 0))
            advice.Add("Os pentes aceitam mais velocidade do que estão usando. Dá para ativar o perfil XMP/EXPO na BIOS, mas só depois de confirmar que a RAM é estável no Teste de RAM.");
        if (advice.Count > 0)
        {
            AdviceText.Text = string.Join("\n", advice);
            Advice.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshAsync()
    {
        var d = await Task.Run(MemoryInfoService.Details);
        var pct = d.Total > 0 ? 100.0 * d.InUse / d.Total : 0;
        UsedText.Text = $"{pct:0}%";
        UsedDetail.Text = $"{Format.Bytes(d.InUse)} de {Format.Bytes(d.Total)}";
        UsageChart.Push(pct);
        AvailText.Text = Format.Bytes(d.Available);
        AvailDetail.Text = d.Standby is { } sb ? $"{Format.Bytes(sb)} disso é cache que o Windows libera sozinho" : "inclui o cache do Windows";
        CommitText.Text = Format.Bytes(d.Committed);
        CommitDetail.Text = $"limite {Format.Bytes(d.CommitLimit)} (RAM + arquivo de paginação) · pico {Format.Bytes(d.CommitPeak)}";

        // Barra de composição
        var standby = d.Standby ?? 0;
        var modified = d.Modified ?? 0;
        var free = d.Free ?? (d.Available > standby ? d.Available - standby : d.Available);
        ColUsed.Width = new GridLength(d.InUse, GridUnitType.Star);
        ColModified.Width = new GridLength(modified, GridUnitType.Star);
        ColStandby.Width = new GridLength(standby, GridUnitType.Star);
        ColFree.Width = new GridLength(free, GridUnitType.Star);
        LegendUsed.Text = $"■ Em uso {Format.Bytes(d.InUse)}";
        LegendModified.Text = d.Modified is null ? "" : $"■ Modificada {Format.Bytes(modified)}";
        LegendStandby.Text = d.Standby is null ? "" : $"■ Em espera {Format.Bytes(standby)}";
        LegendFree.Text = $"■ Livre {Format.Bytes(free)}";

        var temps = HardwareService.Instance.Readings
            .Where(r => r.HardwareType == HW.HardwareType.Memory && r.Type == HW.SensorType.Temperature && r.Value > 0)
            .Select(r => $"{r.Name} {r.Value:0} °C").ToList();

        var items = new List<InfoItem>
        {
            new("Total utilizável", Format.Bytes(d.Total)),
            new("Em uso", Format.Bytes(d.InUse)),
            new("Disponível", Format.Bytes(d.Available)),
            new("Em espera (cache)", d.Standby is null ? "precisa de administrador" : Format.Bytes(standby)),
            new("Modificada", d.Modified is null ? "—" : Format.Bytes(modified)),
            new("Livre (zerada)", Format.Bytes(free)),
            new("Cache do sistema", Format.Bytes(d.SystemCache)),
            new("Pool paginado / não paginado", $"{Format.Bytes(d.PagedPool)} / {Format.Bytes(d.NonPagedPool)}"),
            new("Arquivo de paginação", d.PageFileSize > 0 ? $"{Format.Bytes(d.PageFileUsed)} de {Format.Bytes(d.PageFileSize)}" : "desativado"),
            new("Processos · threads · handles", $"{d.Processes} · {d.Threads:N0} · {d.Handles:N0}"),
        };
        if (d.BadPages is { } bad)
            items.Add(new InfoItem("Páginas com defeito", bad == 0 ? "0 (nenhuma)" : $"{bad} ⚠ o Windows marcou partes da RAM como defeituosas"));
        if (temps.Count > 0) items.Add(new InfoItem("Temperatura", string.Join(" · ", temps)));
        DetailList.ItemsSource = items;

        ProcessList.ItemsSource = await Task.Run(() => MemoryService.TopProcesses(12));
    }

    // ---------- Teste de latência e velocidade ----------

    public sealed record LatencyRow(string Size, string Level, double Bar, string Text);

    private async void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        BenchButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => MemoryInfoService.Benchmark(s => Dispatcher.Invoke(() => BenchStatus.Text = s)));
            var max = result.Latencies.Max(l => l.Ns);
            LatencyList.ItemsSource = result.Latencies
                .Select(l => new LatencyRow(l.Size, l.Level, Math.Max(2, 420 * l.Ns / max), $"{l.Ns:0.0} ns"))
                .ToList();
            ReadText.Text = $"{result.ReadGBs:0.0} GB/s";
            WriteText.Text = $"{result.WriteGBs:0.0} GB/s";
            CopyText.Text = $"{result.CopyGBs:0.0} GB/s";
            LatencyText.Text = $"{result.RamNs:0} ns";
            // Medido com acesso totalmente aleatório e páginas de 4 KB: fica ~30-40% acima do AIDA64.
            LatencyDetail.Text = result.RamNs switch
            {
                < 100 => "Excelente (DDR5 com EXPO/XMP bem ajustada)",
                < 130 => "Boa",
                < 160 => "Normal para RAM sem EXPO/XMP",
                _ => "Alta: RAM lenta, canal único ou timings frouxos",
            };
            var theory = _layout?.TheoreticalGBs ?? 0;
            BenchStatus.Text = (theory > 0 ? $"A leitura chegou a {result.ReadGBs / theory * 100:0}% do máximo teórico ({theory:0.0} GB/s). " : "")
                + "A latência aqui é medida no pior caso (endereços totalmente aleatórios), por isso fica uns 30-40% acima do AIDA64. Compare sempre com medições do próprio SoNexus.";
        }
        catch (Exception ex)
        {
            BenchStatus.Text = $"Não foi possível medir: {ex.Message}";
        }
        BenchButton.IsEnabled = true;
        _busy = false;
    }

    // ---------- Liberar memória ----------

    private async void Trim_Click(object sender, RoutedEventArgs e) => await RunAsync(trim: true, standby: false);
    private async void Standby_Click(object sender, RoutedEventArgs e) => await RunAsync(trim: false, standby: true);
    private async void Full_Click(object sender, RoutedEventArgs e) => await RunAsync(trim: true, standby: true);

    private async Task RunAsync(bool trim, bool standby)
    {
        SetButtons(false);
        var before = MemoryService.GetStatus().Available;

        if (trim)
        {
            Log("Liberando memória dos processos...");
            var (ok, skipped) = await Task.Run(MemoryService.TrimWorkingSets);
            Log($"  {ok} processos liberados, {skipped} protegidos/ignorados.");
        }
        if (standby)
        {
            Log("Limpando cache (Standby List)...");
            var error = await Task.Run(MemoryService.PurgeStandbyList);
            Log(error is null ? "  Cache limpo." : $"  {error}");
        }

        await Task.Delay(500);
        var after = MemoryService.GetStatus().Available;
        var freed = after > before ? after - before : 0;
        Log($"Pronto: {Format.Bytes(freed)} liberados.");

        await RefreshAsync();
        SetButtons(true);
    }

    private void SetButtons(bool enabled) =>
        FullButton.IsEnabled = TrimButton.IsEnabled = StandbyButton.IsEnabled = enabled;

    private void Log(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        LogBox.ScrollToEnd();
    }
}
