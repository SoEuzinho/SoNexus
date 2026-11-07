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
    private GameSession? _session;
    private bool _building;
    // Texto da prévia: o mesmo tipo de painel do jogo, montado pelo tema escolhido.
    private readonly TextBlock PreviewText = OverlayWindow.NewText();
    private int _refreshHz;

    public FpsView()
    {
        InitializeComponent();
        FrameChart.Stroke = Metric.Warn;
        _timer.Tick += (_, _) => Refresh();
        BuildOverlayOptions();
        // Prévia do painel: atualiza só com a página aberta (ou com medição/painel ligados).
        IsVisibleChanged += (_, _) =>
        {
            ApplyOverlayLook(); // liga/desliga a animação da prévia
            if (IsVisible) { Refresh(); _timer.Start(); }
            else if (_monitor == null && _overlay == null) _timer.Stop();
        };
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
        EndSession();
        if (!IsVisible) _timer.Stop();
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
            ApplyOverlayLook();
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
            UpdateOverlay(Sample(null), null);
            if (!IsVisible && _overlay == null) _timer.Stop();
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
            EndSession();
            _currentPid = game.Pid;
            _session = new GameSession(game.Name);
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

        if (_refreshHz == 0) _refreshHz = GamesService.PrimaryRefreshHz();
        var second = new GameSecond(game.Fps, s.CpuLoad, s.CoreMax, s.CpuTemp, s.GpuLoad, s.GpuTemp,
            s.VramUsedMb is { } u && s.VramTotalMb is { } t && t > 0 ? u / t * 100 : null, s.RamPct);
        var limit = GameDiagnosis.Classify(second, _refreshHz);
        var (title, detail) = GameDiagnosis.Explain(limit, second, _refreshHz);
        DiagnosisTitle.Text = title;
        DiagnosisText.Text = detail + (stats == null ? "" : $" Fluidez: {stats.Smoothness.ToLowerInvariant()} (1% low {stats.Low1:0} de média {stats.AvgFps:0}" +
                                                         (stats.Stutters > 0 ? $", {stats.Stutters} engasgo(s) na sessão" : "") + ").");
        _session?.Add(second, limit);
        ShowSession();
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
        _building = true;
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
        ColorBox.SelectedIndex = Math.Clamp(AppSettings.Current.OverlayColor, 0, 3);
        FlowBox.SelectedIndex = Math.Clamp(AppSettings.Current.OverlayFlow, 0, 1);
        OpacitySlider.Value = Math.Clamp(AppSettings.Current.OverlayOpacity, 0, 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        ThemeBox.ItemsSource = OverlaySkin.Names;
        ThemeBox.SelectedIndex = Math.Clamp(AppSettings.Current.OverlayTheme, 0, OverlaySkin.Names.Length - 1);
        AnimateBox.IsChecked = AppSettings.Current.OverlayAnimate;
        _building = false;
        ApplyOverlayLook();
    }

    private void OverlayLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building || CornerBox.SelectedIndex < 0 || SizeBox.SelectedIndex < 0 || ColorBox.SelectedIndex < 0 ||
            FlowBox.SelectedIndex < 0 || ThemeBox.SelectedIndex < 0) return;
        var set = AppSettings.Current;
        set.OverlayTheme = ThemeBox.SelectedIndex;
        set.OverlayCorner = CornerBox.SelectedIndex;
        set.OverlaySize = SizeBox.SelectedIndex;
        set.OverlayColor = ColorBox.SelectedIndex;
        set.OverlayFlow = FlowBox.SelectedIndex;
        set.Save();
        ApplyOverlayLook();
        UpdateOverlay(Sample(null), null);
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_building) return;
        AppSettings.Current.OverlayOpacity = (int)OpacitySlider.Value;
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        AppSettings.Current.Save();
        ApplyOverlayLook();
    }

    /// <summary>Aplica tamanho, fundo, canto e formato no painel do jogo e na prévia.</summary>
    private void ApplyOverlayLook()
    {
        var set = AppSettings.Current;
        // Animação só na prévia visível e no painel do jogo; esconder a página para a da prévia.
        PreviewHost.Content = OverlaySkin.Build(set.OverlayTheme, PreviewText, set.OverlaySize, set.OverlayOpacity, set.OverlayAnimate && IsVisible);
        _overlay?.SetLayout(set.OverlayCorner, set.OverlaySize, set.OverlayOpacity, set.OverlayFlow == 1, set.OverlayTheme, set.OverlayAnimate);
        UpdateOverlay(Sample(null), null);
    }

    private void Animate_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.OverlayAnimate = AnimateBox.IsChecked == true;
        AppSettings.Current.Save();
        ApplyOverlayLook();
    }

    private void AntiCheat_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        "Como o SoNexus mede o FPS\n\n" +
        "• Lê os eventos de desempenho que o próprio Windows publica para qualquer programa (ETW), o mesmo método do PresentMon e do CapFrameX.\n" +
        "• Não abre, não lê e não altera a memória do jogo, não injeta DLL e não mexe nos arquivos do jogo.\n" +
        "• O painel sobre o jogo é uma janela separada e transparente do Windows, como a do Discord: não desenha dentro do jogo.\n\n" +
        "Por isso ele não funciona como um cheat e não dá vantagem a ninguém.\n\n" +
        "Mesmo assim, nenhum programa pode garantir 100%: cada anti-cheat tem regras próprias e elas mudam. " +
        "Em jogos competitivos, se o jogo reclamar ou você preferir não arriscar, desligue o painel e a medição. " +
        "O SoNexus não tem ligação com nenhum jogo, empresa ou anti-cheat, e o uso é por sua conta (veja os Termos de Uso).",
        "SoNexus — é seguro com anti-cheat?", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Fecha a partida atual e guarda no histórico (aba "Seu PC e gargalo").</summary>
    private void EndSession()
    {
        if (_session == null) return;
        var stats = _monitor != null && _currentPid is { } pid ? _monitor.SessionStats(pid) : null;
        var session = _session;
        _session = null;
        Task.Run(() => SessionHistory.Save(session, stats));
    }

    private List<HealthTip>? _ramTips;

    /// <summary>Veredito da partida e a barra de quanto tempo cada coisa segurou o FPS.</summary>
    private void ShowSession()
    {
        if (_session == null) return;
        if (_session.Seconds >= GameSession.MinSeconds && _ramTips == null)
        {
            _ramTips = new();
            Task.Run(() => HardwareHealth.Check().Where(t => t.Part == HardwareHealth.Ram).ToList())
                .ContinueWith(t => _ramTips = t.IsCompletedSuccessfully ? t.Result : new(), TaskScheduler.FromCurrentSynchronizationContext());
        }
        var (title, detail) = _session.Verdict(_ramTips);
        SessionTitle.Text = title;
        SessionText.Text = detail;
        SessionBar.ColumnDefinitions.Clear();
        SessionBar.Children.Clear();
        SessionLegend.Children.Clear();
        if (_session.Seconds < GameSession.MinSeconds) return;
        var shares = _session.Shares();
        for (var i = 0; i < shares.Count; i++)
        {
            var (l, share) = shares[i];
            SessionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            var brush = LimitBrush(i);
            var part = new Border { Background = brush, Margin = new Thickness(0, 0, i < shares.Count - 1 ? 2 : 0, 0) };
            Grid.SetColumn(part, i);
            SessionBar.Children.Add(part);
            var legend = new TextBlock { Margin = new Thickness(0, 0, 12, 0), FontSize = 11, Foreground = (Brush)FindResource("MutedBrush") };
            legend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = brush });
            legend.Inlines.Add(new System.Windows.Documents.Run($"{GameDiagnosis.Name(l)} {share * 100:0}%"));
            SessionLegend.Children.Add(legend);
        }
    }

    private static readonly Brush[] RankBrushes =
    {
        Brushes.White,
        Frozen(Color.FromRgb(0xA0, 0xA0, 0xA0)),
        Frozen(Color.FromRgb(0x70, 0x70, 0x70)),
        Frozen(Color.FromRgb(0x48, 0x48, 0x48)),
    };

    /// <summary>Tons de cinza (tema preto e branco): o maior pedaço é o mais claro.</summary>
    private static Brush LimitBrush(int rank) => RankBrushes[Math.Min(rank, RankBrushes.Length - 1)];

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _currentPid is not { } pid || _monitor.SessionStats(pid) is not { } s) return;
        var text = $"SoNexus · {GameName.Text}\n" +
                   $"Duração {s.Duration:hh\\:mm\\:ss} · {s.Frames:N0} quadros\n" +
                   $"FPS médio {s.AvgFps:0} · 1% low {s.Low1:0} · 0,1% low {s.Low01:0}\n" +
                   $"Engasgos {s.Stutters} · pior quadro {s.MaxFrameTime:0} ms · fluidez: {s.Smoothness}\n" +
                   $"{CpuText.Text}\n{GpuText.Text}\nAgora: {DiagnosisTitle.Text}\nNa partida: {SessionTitle.Text}";
        try { Clipboard.SetText(text); } catch { }
        DiagnosisText.Text = "Relatório copiado. Cole no Discord, WhatsApp ou num fórum.";
    }

    /// <summary>Monta as linhas do painel sobre o jogo só com o que o usuário marcou (e o que este PC tem).</summary>
    private void UpdateOverlay(Snapshot s, (FpsProcess Game, FpsStats? Stats)? play)
    {
        if (_overlay == null && !IsVisible) return;
        var on = AppSettings.Current.OverlayItems.ToHashSet();
        var value = OverlaySkin.ValueBrush(AppSettings.Current.OverlayTheme, AppSettings.Current.OverlayColor);
        var accent = value;
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
        if (cpu.Length > 0) lines.Add(("CPU", cpu, TempBrush(s.CpuTemp, value)));

        var gpu = Join(new[]
        {
            on.Contains("gpu") ? Fmt(s.GpuLoad, "%") : null,
            on.Contains("gputemp") && s.GpuTemp != null ? Fmt(s.GpuTemp, "°C") : null,
            on.Contains("gpuclock") && s.GpuClock is { } g ? $"{g:0} MHz" : null,
            on.Contains("gpupower") && s.GpuPower != null ? Fmt(s.GpuPower, "W") : null,
        });
        if (gpu.Length > 0) lines.Add(("GPU", gpu, TempBrush(s.GpuTemp, value)));
        if (on.Contains("vram") && s.VramUsedMb is { } vu)
            lines.Add(("VRAM", s.VramTotalMb is { } vt and > 0 ? $"{vu / 1024:0.0}/{vt / 1024:0} GB" : $"{vu / 1024:0.0} GB",
                s.VramTotalMb is { } t2 and > 0 && vu / t2 > 0.95 ? Metric.Danger : value));

        var ram = Join(new[]
        {
            on.Contains("ram") ? $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB" : null,
            on.Contains("gameram") && s.GameRamGb is { } gr ? $"jogo {gr:0.0} GB" : null,
        });
        if (ram.Length > 0) lines.Add(("RAM", ram, s.RamPct >= 90 ? Metric.Danger : value));
        if (on.Contains("time")) lines.Add(("HORA", DateTime.Now.ToString("HH:mm"), value));

        if (lines.Count == 0) lines.Add(("", "Nada marcado em \"Mostrar sobre o jogo\"", Brushes.Gray));
        _overlay?.SetLines(lines);
        OverlayWindow.Render(PreviewText, lines, AppSettings.Current.OverlayFlow == 1);
    }

    /// <summary>Quente demais fica vermelho (perigo de verdade); o resto na cor escolhida.</summary>
    private static Brush TempBrush(float? t, Brush normal) => t is >= 85 ? Metric.Danger : normal;

    private static string Fmt(float? v, string unit) => v is { } x ? $"{x:0}{(unit == "%" ? "%" : " " + unit)}" : "—";
}
