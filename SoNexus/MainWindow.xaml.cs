using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;
using SoNexus.Views;
using Screen = System.Windows.Forms.Screen;

namespace SoNexus;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    private FpsView? _fpsView;

    internal void StopFpsMonitoring() => _fpsView?.StopMonitoring();

    public MainWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdateSensorPolling();

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AdminText.Text = admin ? "● Administrador" : "● Sem administrador — sensores limitados";
        AdminText.Foreground = (System.Windows.Media.Brush)FindResource(admin ? "OkBrush" : "WarnBrush");

        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = $"por SoStudio · v{v?.Major}.{v?.Minor}";
        ApplyScale();
        LoadBounds();
        HardwareService.Instance.IntervalMs = AppSettings.Current.SensorIntervalMs;
        _tray = new TrayIcon(this);

        // Volta na última página aberta (ou sempre no Início, se preferir).
        if (AppSettings.Current.OpenLastPage && FindName(AppSettings.Current.LastPage) is RadioButton last) last.IsChecked = true;
        else NavHome.IsChecked = true;
    }

    /// <summary>Tamanho da interface (Configurações › Aparência).</summary>
    public void ApplyScale()
    {
        var k = Math.Clamp(AppSettings.Current.UiScale, 0.8, 1.5);
        RootGrid.LayoutTransform = k == 1 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(k, k);
        MinWidth = 1060 * k;
        MinHeight = 660 * k;
    }

    // ---------- Atalhos de teclado globais ----------

    private const int HotkeyShow = 1, HotkeyOverlay = 2;
    private System.Windows.Interop.HwndSource? _source;

    /// <summary>Liga ou desliga Ctrl+Shift+S e Ctrl+Shift+F. Retorna o que não deu para registrar (outro programa já usa).</summary>
    public List<string> ApplyHotkeys()
    {
        var failed = new List<string>();
        if (_source == null) return failed;
        var hwnd = _source.Handle;
        UnregisterHotKey(hwnd, HotkeyShow);
        UnregisterHotKey(hwnd, HotkeyOverlay);
        if (!AppSettings.Current.GlobalHotkeys) return failed;
        const uint mods = 0x0002 | 0x0004 | 0x4000; // Ctrl + Shift, sem repetir ao segurar
        if (!RegisterHotKey(hwnd, HotkeyShow, mods, 0x53)) failed.Add("Ctrl+Shift+S");
        if (!RegisterHotKey(hwnd, HotkeyOverlay, mods, 0x46)) failed.Add("Ctrl+Shift+F");
        return failed;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmSysCommand = 0x0112, scMinimize = 0xF020, wmDisplayChange = 0x007E;
        if (msg == wmSysCommand && (wParam.ToInt64() & 0xFFF0) == scMinimize &&
            AppSettings.Current.MinimizeToTray && !App.Exiting)
        {
            handled = true;
            QueueHideToTray(requireMinimized: false);
            return IntPtr.Zero;
        }
        if (msg == wmDisplayChange)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible && WindowState != WindowState.Minimized && !App.Exiting)
                    RecoverBounds();
            }, DispatcherPriority.Background);
        }
        if (msg != 0x0312) return IntPtr.Zero; // WM_HOTKEY
        handled = true;
        if (wParam.ToInt32() == HotkeyShow)
        {
            if (IsVisible && IsActive) HideToTray(showHint: false);
            else ShowFromTray();
        }
        else if (wParam.ToInt32() == HotkeyOverlay && View(nameof(NavGames)) is SectionView games && games.Page("FPS ao vivo") is FpsView fps)
        {
            var on = fps.ToggleOverlay();
            _tray?.Notify("Painel sobre o jogo", on ? "Ligado. Ctrl+Shift+F de novo para esconder." : "Desligado.");
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ---------- Tamanho e posição ----------

    private WindowState _restoreState;

    private void LoadBounds()
    {
        var s = AppSettings.Current;
        var width = double.IsFinite(s.WindowWidth) ? Math.Max(MinWidth, s.WindowWidth) : Width;
        var height = double.IsFinite(s.WindowHeight) ? Math.Max(MinHeight, s.WindowHeight) : Height;
        Rect bounds = new(s.WindowLeft ?? double.NaN, s.WindowTop ?? double.NaN, width, height);
        ApplyBounds(VisibleBounds(bounds));
        _restoreState = s.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = _restoreState;
    }

    private Rect NormalBounds() =>
        WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

    private void RecoverBounds()
    {
        var current = NormalBounds();
        if (current.IsEmpty) return;
        var bounds = VisibleBounds(current);
        if (bounds != current) ApplyBounds(bounds);
    }

    private void ApplyBounds(Rect bounds)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private Rect VisibleBounds(Rect bounds)
    {
        var scale = 96.0 / GetDpiForSystem();
        Rect ToDips(System.Drawing.Rectangle area) =>
            new(area.Left * scale, area.Top * scale, area.Width * scale, area.Height * scale);
        var areas = Screen.AllScreens.Select(screen => ToDips(screen.WorkingArea)).ToArray();
        var primary = Screen.PrimaryScreen is { } screen ? ToDips(screen.WorkingArea) : SystemParameters.WorkArea;
        return KeepWindowVisible(bounds, areas, primary, MinWidth, MinHeight);
    }

    internal static Rect KeepWindowVisible(Rect bounds, IReadOnlyList<Rect> workAreas, Rect fallback,
        double minWidth, double minHeight)
    {
        if (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            var title = new Rect(bounds.Left, bounds.Top, bounds.Width, Math.Min(bounds.Height, 36));
            foreach (var area in workAreas)
            {
                var visible = Rect.Intersect(title, area);
                if (visible.Width >= Math.Min(160, bounds.Width) && visible.Height >= Math.Min(18, title.Height))
                    return bounds;
            }
        }

        var width = Math.Max(minWidth, Math.Min(bounds.Width, fallback.Width));
        var height = Math.Max(minHeight, Math.Min(bounds.Height, fallback.Height));
        return new Rect(fallback.Left + Math.Max(0, fallback.Width - width) / 2,
            fallback.Top + Math.Max(0, fallback.Height - height) / 2, width, height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private void SaveBounds()
    {
        var s = AppSettings.Current;
        var b = NormalBounds();
        if (b.Width > 0 && b.Height > 0 && double.IsFinite(b.Left) && double.IsFinite(b.Top))
            (s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight) = (b.Left, b.Top, b.Width, b.Height);
        s.WindowMaximized = _restoreState == WindowState.Maximized;
        s.Save();
    }

    // ---------- Segundo plano (bandeja do Windows) ----------

    private TrayIcon? _tray;

    public void HideToTray(bool showHint)
    {
        if (App.Exiting) return;
        if (IsVisible)
        {
            SaveBounds();
            Hide();
        }
        HardwareService.Instance.Minimized = true;
        if (showHint && !AppSettings.Current.TrayHintShown && _tray != null)
        {
            _tray.Notify("O SoNexus continua rodando",
                "Ventoinhas, RGB, avisos de temperatura e sensores seguem funcionando. Clique no ícone para abrir; botão direito para mais opções.");
            AppSettings.Current.TrayHintShown = true;
            AppSettings.Current.Save();
        }
    }

    public void ShowFromTray()
    {
        if (App.Exiting) return;
        RecoverBounds();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        if (!IsVisible) Show();
        Activate();
    }

    /// <summary>Abre uma página (e uma aba dela), ex.: atalhos do menu da bandeja.</summary>
    public void Navigate(string nav, string? tab)
    {
        ShowFromTray();
        if (FindName(nav) is not RadioButton rb) return;
        rb.IsChecked = true;
        if (tab != null && _views.TryGetValue(nav, out var view) && view is SectionView section) section.Select(tab);
    }

    /// <summary>A opção mudou em outro lugar (menu da bandeja): a página de Configurações relê.</summary>
    public void SyncTrayOptions()
    {
        if (_views.TryGetValue(nameof(NavSettings), out var v) && v is SettingsView s) s.Reload();
    }

    public void TestNotification() =>
        _tray?.Notify("Teste de aviso", "É assim que o SoNexus avisa quando a CPU ou a GPU esquenta demais.", System.Windows.Forms.ToolTipIcon.Warning);

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Exiting)
        {
            SaveBounds();
            return;
        }

        e.Cancel = true;
        if (AppSettings.Current.CloseToTray) HideToTray(showHint: true);
        else Dispatcher.BeginInvoke(App.ExitCompletely);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.ExitCompletely();

    protected override void OnClosed(EventArgs e)
    {
        if (_source is { IsDisposed: false })
        {
            UnregisterHotKey(_source.Handle, HotkeyShow);
            UnregisterHotKey(_source.Handle, HotkeyOverlay);
            _source.RemoveHook(WndProc);
        }
        _source = null;
        base.OnClosed(e);
    }

    /// <summary>Barra de título preta (modo escuro do Windows + cor da legenda no Windows 11).</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, black = 0x000000, border = 0x1F1F1F;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));  // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR

        _source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        ApplyHotkeys();
    }

    /// <summary>Minimizado, os sensores atualizam devagar para economizar CPU (e pode ir para a bandeja).</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var minimized = WindowState == WindowState.Minimized;
        if (!minimized) _restoreState = WindowState;
        UpdateSensorPolling();
        if (minimized && AppSettings.Current.MinimizeToTray)
            QueueHideToTray(requireMinimized: true);
    }

    private void UpdateSensorPolling() => HardwareService.Instance.Minimized = WindowState == WindowState.Minimized || !IsVisible;

    private void QueueHideToTray(bool requireMinimized)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && (!requireMinimized || WindowState == WindowState.Minimized))
                HideToTray(showHint: true);
        }, DispatcherPriority.Input);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        ContentHost.Content = View(rb.Name);
        AppSettings.Current.LastPage = rb.Name;
    }

    /// <summary>A página de um item do menu (criada só na primeira vez que alguém precisa dela).</summary>
    private UserControl View(string name)
    {
        if (!_views.TryGetValue(name, out var view))
        {
            view = name switch
            {
                // Por peça: cada uma com Info · Sensores · testes · Dicas (ver Views/PartViews.cs).
                nameof(NavHardware) => HardwareParts.Section(),
                nameof(NavOptim) => new SectionView(
                    ("Debloat do Windows", () => new DebloatView()),
                    ("Inicialização", () => new StartupView()),
                    ("Itens ocultos", () => new HiddenView())),
                nameof(NavDrivers) => new SectionView(
                    ("Placa de vídeo", () => new GpuDriverView()),
                    ("Todos os dispositivos", () => new DevicesView()),
                    ("Remover driver de vídeo", () => new GpuCleanView())),
                nameof(NavGames) => new SectionView(
                    ("Biblioteca", () => new GameLibraryView()),
                    ("Seu PC e gargalo", () => new GameAnalysisView()),
                    ("FPS ao vivo", () => _fpsView ??= new FpsView()),
                    ("Realidade virtual", () => new VrView())),
                // Tudo de medir e forçar o PC num lugar só (as mesmas telas aparecem também na peça, em Hardware).
                nameof(NavPerf) => new SectionView(
                    ("Teste completo", () => new PerfTestView()),
                    ("Processador", () => SharedPages.Get<BenchmarkView>()),
                    ("Placa de vídeo, RAM e disco", () => SharedPages.Get<HardwareBenchView>()),
                    ("Stress test", () => SharedPages.Get<StressView>()),
                    ("Overclock", () => new OverclockView()),
                    ("EXPO, PBO e BIOS", () => new BiosGuideView())),
                // Funções de programas avulsos (RAMMap, Process Lasso, Ninite, UFO Test) dentro do SoNexus.
                nameof(NavTools) => new SectionView(
                    ("Memória (RAM)", () => new RamToolView()),
                    ("Prioridade dos jogos", () => new GameBoostView()),
                    ("Limpeza", () => new CleanupView()),
                    ("Instalar programas", () => new InstallAppsView()),
                    ("Teste do monitor", () => new MonitorTestView())),
                nameof(NavNetwork) => new SectionView(
                    ("Visão geral", () => new NetworkView()),
                    ("Aplicativos e conexões", () => new NetworkAppsView()),
                    ("Rotas e servidores de jogos", () => new NetworkRoutesView()),
                    ("Segurança", () => new NetworkSecurityView()),
                    ("DNS", () => new NetworkDnsView())),
                nameof(NavAudio) => new SectionView(
                    ("Dispositivos", () => new AudioView()),
                    ("Voicemeeter", () => new VoicemeeterView()),
                    ("Drivers e programas", () => new AudioDriversView())),
                nameof(NavDiag) => new SectionView(
                    ("Erros e telas azuis", () => new DiagnosticsView()),
                    ("Latência (DPC)", () => new LatencyView())),
                nameof(NavIntegration) => new SectionView(
                    ("Visão geral", () => new IntegrationsView()),
                    ("Iluminação RGB", () => new RgbView()),
                    ("Atalhos do Windows", () => new SystemIntegrationView())),
                nameof(NavSettings) => new SettingsView(),
                _ => new HomeView(),
            };
            _views[name] = view;
        }
        return view;
    }
}
