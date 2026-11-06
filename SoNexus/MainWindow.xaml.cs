using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;
using SoNexus.Views;

namespace SoNexus;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();

    public MainWindow()
    {
        InitializeComponent();

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AdminText.Text = admin ? "● Administrador" : "● Sem administrador — sensores limitados";
        AdminText.Foreground = (System.Windows.Media.Brush)FindResource(admin ? "OkBrush" : "WarnBrush");

        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = $"por SoStudio · v{v?.Major}.{v?.Minor}";
        LoadBounds();
        ApplyScale();
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

    private void LoadBounds()
    {
        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        // Só usa a posição salva se ainda estiver numa tela (monitor pode ter sido desligado).
        if (!double.IsNaN(s.WindowLeft) && s.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 &&
            s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200 &&
            s.WindowTop >= SystemParameters.VirtualScreenTop - 10 &&
            s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var s = AppSettings.Current;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.Width > 0)
        {
            (s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight) = (b.Left, b.Top, b.Width, b.Height);
            s.WindowMaximized = WindowState == WindowState.Maximized;
        }
        s.Save();
    }

    // ---------- Segundo plano (bandeja do Windows) ----------

    private TrayIcon? _tray;

    public void HideToTray(bool showHint)
    {
        SaveBounds();
        Hide();
        HardwareService.Instance.Minimized = true;
        if (showHint && !AppSettings.Current.TrayHintShown && _tray != null)
        {
            _tray.Notify("O SoNexus continua rodando",
                "Ventoinhas, RGB, avisos de temperatura e sensores seguem funcionando. Clique no ícone para abrir; botão direito para mais opções.");
            AppSettings.Current.TrayHintShown = true;
            AppSettings.Current.Save();
        }
        // Na bandeja ninguém vê a tela: devolve ao Windows a memória das páginas abertas.
        Dispatcher.BeginInvoke(MemoryTrim.Now, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        HardwareService.Instance.Minimized = false;
        Activate();
        Topmost = true;  // traz para frente mesmo com outra janela em foco
        Topmost = false;
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
        if (!App.Exiting && AppSettings.Current.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(showHint: true);
            return;
        }
        SaveBounds();
        App.ExitCompletely();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        SaveBounds();
        App.ExitCompletely();
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
        HardwareService.Instance.Minimized = WindowState == WindowState.Minimized;
        if (WindowState == WindowState.Minimized && AppSettings.Current.MinimizeToTray)
        {
            WindowState = WindowState.Normal; // volta ao normal escondido, para abrir certo depois
            HideToTray(showHint: true);
        }
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
                    ("FPS ao vivo", () => new FpsView()),
                    ("Overclock", () => new OverclockView()),
                    ("Realidade virtual", () => new VrView())),
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
