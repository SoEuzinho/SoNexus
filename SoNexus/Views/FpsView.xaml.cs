using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class FpsView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FpsMonitor? _monitor;
    private OverlayWindow? _overlay;
    private int? _pinnedPid;
    private int? _currentPid;
    private bool _updatingList;

    public FpsView()
    {
        InitializeComponent();
        FrameChart.Stroke = Metric.Warn;
        _timer.Tick += (_, _) => Refresh();
        BuildOverlayOptions();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _monitor = new FpsMonitor();
            _monitor.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível iniciar a medição: {ex.Message}", "SoNexus");
            return;
        }
        _pinnedPid = _currentPid = null;
        FpsChart.Clear();
        FrameChart.Clear();
        _timer.Start();
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        ResetButton.IsEnabled = true;
        GameName.Text = "Aguardando um jogo...";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _monitor?.Dispose();
        _monitor = null;
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        ResetButton.IsEnabled = false;
        CloseOverlay();
        OverlayBox.IsChecked = false;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor != null && _currentPid is { } pid) _monitor.ResetSession(pid);
        FpsChart.Clear();
        FrameChart.Clear();
    }

    private void Process_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || ProcessList.SelectedItem is not FpsProcess p) return;
        _pinnedPid = p.Pid;
        FpsChart.Clear();
        FrameChart.Clear();
    }

    /// <summary>Atalho Ctrl+Shift+F: liga/desliga o painel sobre o jogo. Retorna se ficou ligado.</summary>
    public bool ToggleOverlay()
    {
        OverlayBox.IsChecked = OverlayBox.IsChecked != true;
        Overlay_Click(this, new RoutedEventArgs());
        return OverlayBox.IsChecked == true;
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayBox.IsChecked == true)
        {
            _overlay ??= new OverlayWindow();
            _overlay.Show();
            _overlay.SetLayout(AppSettings.Current.OverlayCorner, AppSettings.Current.OverlaySize);
            UpdateOverlay(Sample(null), null);
            _timer.Start();
        }
        else
        {
            CloseOverlay();
        }
    }

    private void CloseOverlay()
    {
        _overlay?.Close();
        _overlay = null;
    }

    private void Refresh()
    {
        if (_monitor == null)
        {
            // Painel aberto sem medição: continua mostrando os sensores.
            if (_overlay != null) UpdateOverlay(Sample(null), null);
            return;
        }
        var active = _monitor.Active();

        _updatingList = true;
        ProcessList.ItemsSource = active;
        _updatingList = false;
        NoProcess.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var game = _pinnedPid is { } pinned ? active.FirstOrDefault(p => p.Pid == pinned) : _monitor.PickGame(active);
        if (game != null && game.Pid != _currentPid)
        {
            _currentPid = game.Pid;
            FpsChart.Clear();
            FrameChart.Clear();
        }
        ProcessList.SelectedItem = active.FirstOrDefault(p => p.Pid == _currentPid);

        var s = Sample(game);
        CpuText.Text = $"CPU {Fmt(s.CpuLoad, "%")}  {Fmt(s.CpuTemp, "°C")}" + (s.CpuClock is { } cc ? $"  {cc / 1000:0.0} GHz" : "") + (s.CpuPower is { } cw ? $"  {cw:0} W" : "");
        GpuText.Text = $"GPU {Fmt(s.GpuLoad, "%")}  {Fmt(s.GpuTemp, "°C")}  {Fmt(s.GpuPower, "W")}" +
                       (s.VramUsedMb is { } vu ? (s.VramTotalMb is { } vt and > 0 ? $"  ·  VRAM {vu / 1024:0.0}/{vt / 1024:0} GB" : $"  ·  VRAM {vu / 1024:0.0} GB") : "");
        RamText.Text = $"RAM {s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB ({s.RamPct}%)" + (s.GameRamGb is { } gr ? $"  ·  jogo {gr:0.0} GB" : "");

        if (game == null)
        {
            GameName.Text = _currentPid == null ? "Aguardando um jogo..." : "O jogo parou de desenhar quadros";
            FpsText.Text = "—";
            FrameTimeText.Text = "";
            UpdateOverlay(s, null);
            return;
        }

        GameName.Text = game.Name.ToUpperInvariant() + (game.Api.Length > 0 ? $"  ·  {game.Api}" : "");
        FpsText.Text = $"{game.Fps:0}";
        FrameTimeText.Text = $"{game.FrameTime:0.0} ms por quadro";
        FpsChart.Push(game.Fps);
        foreach (var ft in _monitor.RecentFrameTimes(game.Pid).TakeLast(Math.Max(1, (int)game.Fps))) FrameChart.Push(ft);

        var stats = _monitor.SessionStats(game.Pid);
        CopyButton.IsEnabled = stats != null;
        if (stats != null)
        {
            AvgText.Text = $"{stats.AvgFps:0}";
            Low1Text.Text = $"{stats.Low1:0}";
            Low01Text.Text = $"{stats.Low01:0}";
            DurationText.Text = $"{stats.Duration:hh\\:mm\\:ss} · {stats.Frames:N0} quadros";
            StutterText.Text = $"{stats.Stutters} engasgo(s) · pior quadro {stats.MaxFrameTime:0} ms";
            Low1Text.Foreground = stats.Low1 >= stats.AvgFps * 0.6 ? Metric.Ok : Metric.Warn;
        }

        var (title, detail) = Diagnose(game.Fps, stats, s.CpuLoad, s.CoreMax, s.CpuTemp, s.GpuLoad, s.GpuTemp,
            s.VramUsedMb is { } u && s.VramTotalMb is { } t && t > 0 ? u / t * 100 : null, s.RamPct);
        DiagnosisTitle.Text = title;
        DiagnosisText.Text = detail;
        BottleneckText.Text = s.CoreMax > 0 ? $"núcleo mais ocupado: {s.CoreMax:0}%" : "";

        UpdateOverlay(s, (game, stats));
    }

    /// <summary>Tudo o que os sensores e o Windows dizem agora. Cada valor é null quando este PC não tem o sensor.</summary>
    private sealed record Snapshot(
        float? CpuLoad, float? CpuTemp, float? CpuClock, float? CpuPower, float CoreMax,
        float? GpuLoad, float? GpuTemp, float? GpuClock, float? GpuPower, float? VramUsedMb, float? VramTotalMb,
        double RamUsedGb, double RamTotalGb, uint RamPct, double? GameRamGb);

    private static Snapshot Sample(FpsProcess? game)
    {
        var r = HardwareService.Instance.Readings;
        var (cpuTemp, cpuPower, cpuClock) = HardwareService.CpuNow(r);
        var cpuLoad = HardwareService.Find(r, HardwareService.IsCpu, LibreHardwareMonitor.Hardware.SensorType.Load, false, "CPU Total")?.Value;
        var (gpuTemp, gpuLoad, gpuPower, gpuClock) = HardwareService.GpuNow(r);
        // Núcleo mais ocupado: um jogo pode travar num núcleo a 100% com o "CPU total" em 30%.
        var coreMax = r.Where(x => x.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu &&
                                   x.Type == LibreHardwareMonitor.Hardware.SensorType.Load && x.Name.StartsWith("CPU Core #") && x.Value.HasValue)
                       .Select(x => x.Value!.Value).DefaultIfEmpty().Max();
        var gpu = HardwareService.GpuFilter(r);
        var vramUsed = HardwareService.Find(r, gpu, LibreHardwareMonitor.Hardware.SensorType.SmallData, false, "GPU Memory Used", "D3D Dedicated Memory Used")?.Value;
        var vramTotal = HardwareService.Find(r, gpu, LibreHardwareMonitor.Hardware.SensorType.SmallData, false, "GPU Memory Total", "D3D Dedicated Memory Total")?.Value;

        // RAM direto do Windows (vale para qualquer PC, sem depender de sensor).
        var ram = MemoryService.GetStatus();
        double? gameRam = null;
        if (game != null)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(game.Pid);
                gameRam = p.WorkingSet64 / 1073741824.0;
            }
            catch { }
        }
        return new Snapshot(cpuLoad, cpuTemp, cpuClock, cpuPower, coreMax, gpuLoad, gpuTemp, gpuClock, gpuPower, vramUsed, vramTotal,
            (ram.Total - ram.Available) / 1073741824.0, Math.Round(ram.Total / 1073741824.0), ram.Load, gameRam);
    }

    /// <summary>Itens que dá para mostrar sobre o jogo: chave salva nas configurações e texto da caixinha.</summary>
    public static readonly (string Key, string Label)[] OverlayItems =
    {
        ("fps", "FPS"), ("low", "1% low"), ("frametime", "Tempo de quadro"), ("stutter", "Engasgos"),
        ("cpu", "Uso da CPU"), ("cputemp", "Temp. CPU"), ("cpuclock", "Clock CPU"), ("cpupower", "Consumo CPU"), ("core", "Maior núcleo"),
        ("gpu", "Uso da GPU"), ("gputemp", "Temp. GPU"), ("gpuclock", "Clock GPU"), ("gpupower", "Consumo GPU"), ("vram", "VRAM"),
        ("ram", "RAM do PC"), ("gameram", "RAM do jogo"), ("time", "Hora"),
    };

    private void BuildOverlayOptions()
    {
        var chosen = AppSettings.Current.OverlayItems.ToHashSet();
        foreach (var (key, label) in OverlayItems)
        {
            var box = new CheckBox { Content = label, Tag = key, IsChecked = chosen.Contains(key), Margin = new Thickness(0, 0, 10, 6), MinWidth = 110 };
            box.Click += (_, _) =>
            {
                AppSettings.Current.OverlayItems = OverlayItemsPanel.Children.OfType<CheckBox>()
                    .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                AppSettings.Current.Save();
            };
            OverlayItemsPanel.Children.Add(box);
        }
        CornerBox.SelectedIndex = Math.Clamp(AppSettings.Current.OverlayCorner, 0, 3);
        SizeBox.SelectedIndex = Math.Clamp(AppSettings.Current.OverlaySize, 0, 2);
    }

    private void OverlayLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CornerBox == null || SizeBox == null || CornerBox.SelectedIndex < 0 || SizeBox.SelectedIndex < 0) return;
        AppSettings.Current.OverlayCorner = CornerBox.SelectedIndex;
        AppSettings.Current.OverlaySize = SizeBox.SelectedIndex;
        AppSettings.Current.Save();
        _overlay?.SetLayout(CornerBox.SelectedIndex, SizeBox.SelectedIndex);
    }

    private static int? _refreshHz;

    /// <summary>Diz em português quem está segurando o FPS agora, a partir dos sensores.</summary>
    private static (string Title, string Detail) Diagnose(double fps, FpsStats? stats, float? cpuLoad, float coreMax, float? cpuTemp,
        float? gpuLoad, float? gpuTemp, double? vramPct, uint ramPct)
    {
        _refreshHz ??= GamesService.PrimaryRefreshHz();
        var smooth = stats == null ? "" : $" Fluidez: {stats.Smoothness.ToLowerInvariant()} (1% low {stats.Low1:0} de média {stats.AvgFps:0}" +
                                         (stats.Stutters > 0 ? $", {stats.Stutters} engasgo(s) na sessão" : "") + ").";
        var caps = new[] { 30, 60, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 360, _refreshHz.Value };
        var cap = caps.FirstOrDefault(c => Math.Abs(fps - c) <= Math.Max(1.5, c * 0.02));

        if (gpuTemp >= 87 || cpuTemp >= 95)
            return ("Esquentando demais", $"{(gpuTemp >= 87 ? $"Placa a {gpuTemp:0} °C" : $"Processador a {cpuTemp:0} °C")}: acima disso as peças baixam o clock sozinhas (thermal throttling) e o FPS cai. Limpe a poeira, melhore o fluxo de ar ou deixe as ventoinhas mais fortes." + smooth);
        if (vramPct >= 95)
            return ("Memória de vídeo cheia", $"A VRAM está em {vramPct:0}%. Quando enche, a placa usa a RAM do PC e aparecem engasgos. Baixe a qualidade das texturas." + smooth);
        if (ramPct >= 90)
            return ("RAM do PC quase cheia", $"{ramPct}% da memória em uso: o Windows começa a usar o disco e o jogo engasga. Feche navegador e programas em segundo plano." + smooth);
        if (gpuLoad >= 95)
            return ("Limite: placa de vídeo", "A placa está trabalhando a 99%, o cenário normal em jogos pesados. Para mais FPS: baixe sombras, reflexos e efeitos, ou ative DLSS/FSR/XeSS." + smooth);
        if (cap > 0 && gpuLoad is < 90)
            return ($"FPS travado em {cap}", (cap == _refreshHz ? "É a taxa do monitor: V-Sync, G-Sync/FreeSync ou o limitador do jogo estão segurando. Isso é bom para fluidez." :
                                              "Há um limitador de FPS ativo (no jogo, no painel da NVIDIA/AMD ou no RivaTuner). Tire-o se quiser mais FPS.") + smooth);
        if (coreMax >= 90 && gpuLoad is < 90)
            return ("Limite: processador", $"Um núcleo do processador está a {coreMax:0}% e a placa está só a {gpuLoad:0}%: o processador não consegue preparar quadros mais rápido. Suba a resolução/gráficos (é \"de graça\"), feche programas em segundo plano." + smooth);
        if (gpuLoad is < 80)
            return ("Nem placa nem processador no limite", $"Placa a {gpuLoad:0}% e núcleo mais ocupado a {coreMax:0}%. O motor do jogo, o servidor online, a leitura do disco ou um limitador estão segurando o FPS." + smooth);
        return ("Equilibrado", $"Placa a {gpuLoad:0}% e processador a {cpuLoad:0}%: as duas peças estão bem aproveitadas." + smooth);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _currentPid is not { } pid || _monitor.SessionStats(pid) is not { } s) return;
        var text = $"SoNexus · {GameName.Text}\n" +
                   $"Duração {s.Duration:hh\\:mm\\:ss} · {s.Frames:N0} quadros\n" +
                   $"FPS médio {s.AvgFps:0} · 1% low {s.Low1:0} · 0,1% low {s.Low01:0}\n" +
                   $"Engasgos {s.Stutters} · pior quadro {s.MaxFrameTime:0} ms · fluidez: {s.Smoothness}\n" +
                   $"{CpuText.Text}\n{GpuText.Text}\nDiagnóstico: {DiagnosisTitle.Text}";
        try { Clipboard.SetText(text); } catch { }
        DiagnosisText.Text = "Relatório copiado. Cole no Discord, WhatsApp ou num fórum.";
    }

    /// <summary>Monta as linhas do painel sobre o jogo só com o que o usuário marcou (e o que este PC tem).</summary>
    private void UpdateOverlay(Snapshot s, (FpsProcess Game, FpsStats? Stats)? play)
    {
        if (_overlay == null) return;
        var on = AppSettings.Current.OverlayItems.ToHashSet();
        var accent = (Brush)FindResource("AccentBrush");
        var lines = new List<(string, string, Brush)>();
        static string Join(IEnumerable<string?> parts) => string.Join("  ", parts.Where(p => !string.IsNullOrEmpty(p)));

        var fps = Join(new[]
        {
            on.Contains("fps") ? (play is { } p ? $"{p.Game.Fps:0}" : "—") : null,
            on.Contains("low") && play?.Stats is { } st ? $"1% {st.Low1:0}" : null,
            on.Contains("frametime") && play is { } p2 ? $"{p2.Game.FrameTime:0.0} ms" : null,
            on.Contains("stutter") && play?.Stats is { } st2 ? $"{st2.Stutters} engasgos" : null,
        });
        if (fps.Length > 0) lines.Add(("FPS", fps, accent));

        var cpu = Join(new[]
        {
            on.Contains("cpu") ? Fmt(s.CpuLoad, "%") : null,
            on.Contains("cputemp") && s.CpuTemp != null ? Fmt(s.CpuTemp, "°C") : null,
            on.Contains("cpuclock") && s.CpuClock is { } c ? $"{c / 1000:0.0} GHz" : null,
            on.Contains("cpupower") && s.CpuPower != null ? Fmt(s.CpuPower, "W") : null,
            on.Contains("core") && s.CoreMax > 0 ? $"núcleo {s.CoreMax:0}%" : null,
        });
        if (cpu.Length > 0) lines.Add(("CPU", cpu, TempBrush(s.CpuTemp)));

        var gpu = Join(new[]
        {
            on.Contains("gpu") ? Fmt(s.GpuLoad, "%") : null,
            on.Contains("gputemp") && s.GpuTemp != null ? Fmt(s.GpuTemp, "°C") : null,
            on.Contains("gpuclock") && s.GpuClock is { } g ? $"{g:0} MHz" : null,
            on.Contains("gpupower") && s.GpuPower != null ? Fmt(s.GpuPower, "W") : null,
        });
        if (gpu.Length > 0) lines.Add(("GPU", gpu, TempBrush(s.GpuTemp)));
        if (on.Contains("vram") && s.VramUsedMb is { } vu)
            lines.Add(("VRAM", s.VramTotalMb is { } vt and > 0 ? $"{vu / 1024:0.0}/{vt / 1024:0} GB" : $"{vu / 1024:0.0} GB",
                s.VramTotalMb is { } t2 and > 0 && vu / t2 > 0.95 ? Metric.Warn : Brushes.White));

        var ram = Join(new[]
        {
            on.Contains("ram") ? $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB" : null,
            on.Contains("gameram") && s.GameRamGb is { } gr ? $"jogo {gr:0.0} GB" : null,
        });
        if (ram.Length > 0) lines.Add(("RAM", ram, s.RamPct >= 90 ? Metric.Warn : Brushes.White));
        if (on.Contains("time")) lines.Add(("HORA", DateTime.Now.ToString("HH:mm"), Brushes.White));

        if (lines.Count == 0) lines.Add(("", "Nada marcado em \"Mostrar sobre o jogo\"", Brushes.Gray));
        _overlay.SetLines(lines);
    }

    private static Brush TempBrush(float? t) => t switch { null or < 75 => Brushes.White, < 85 => Metric.Warn, _ => Metric.Danger };

    private static string Fmt(float? v, string unit) => v is { } x ? $"{x:0}{(unit == "%" ? "%" : " " + unit)}" : "—";
}
