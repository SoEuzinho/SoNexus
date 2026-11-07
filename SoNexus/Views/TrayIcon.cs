using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>
/// Ícone do SoNexus perto do relógio: temperaturas no texto do ícone, menu escuro com atalhos e iluminação,
/// e aviso do Windows quando a CPU ou a GPU esquenta demais (funciona com a janela fechada).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _window;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _lights = new("Iluminação");
    private readonly ToolStripMenuItem _startup = new("Iniciar com o Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem _keep = new("Ficar em segundo plano ao fechar") { CheckOnClick = true };
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _lastCpuAlert, _lastGpuAlert, _lastRamAlert;
    private bool _onBattery;

    /// <summary>Notebook tirado da tomada: sensores a cada 5 s. Voltou para a tomada: o intervalo escolhido.</summary>
    private void ApplyBatterySaver(AppSettings s)
    {
        var battery = s.BatterySaver && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        if (battery == _onBattery) return;
        _onBattery = battery;
        HardwareService.Instance.IntervalMs = battery ? Math.Max(5000, s.SensorIntervalMs) : s.SensorIntervalMs;
        RgbService.Instance.BatterySaver = battery;
    }

    public TrayIcon(MainWindow window)
    {
        _window = window;
        var v = typeof(TrayIcon).Assembly.GetName().Version;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = true };
        menu.Font = new Font("Segoe UI", 9.5f);

        var title = new ToolStripMenuItem($"SoNexus {v?.Major}.{v?.Minor}") { Enabled = false, Font = new Font("Segoe UI Semibold", 9.5f) };
        menu.Items.Add(title);
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir o SoNexus", null, (_, _) => _window.ShowFromTray());

        var go = new ToolStripMenuItem("Ir para");
        foreach (var (label, nav, tab) in new (string, string, string?)[]
                 {
                     ("Início", "NavHome", null), ("Sensores", "NavHardware", "Todos os sensores"), ("FPS ao vivo", "NavGames", "FPS ao vivo"),
                     ("Overclock e ventoinhas", "NavPerf", "Overclock"), ("Iluminação RGB", "NavIntegration", "Iluminação RGB"),
                     ("Rotas de jogos", "NavNetwork", "Rotas e servidores de jogos"), ("Configurações", "NavSettings", null),
                 })
            go.DropDownItems.Add(label, null, (_, _) => _window.Navigate(nav, tab));
        menu.Items.Add(go);
        menu.Items.Add(_lights);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startup);
        menu.Items.Add(_keep);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Fechar completamente", null, (_, _) => App.ExitCompletely());
        foreach (ToolStripItem item in menu.Items) Paint(item);

        _startup.Click += async (_, _) =>
        {
            var error = await IntegrationService.SetStartupAsync(_startup.Checked);
            if (error != null) Notify("Não foi possível mudar", error, ToolTipIcon.Error);
        };
        _keep.Click += (_, _) =>
        {
            AppSettings.Current.CloseToTray = _keep.Checked;
            AppSettings.Current.Save();
            _window.SyncTrayOptions();
        };
        menu.Opening += async (_, _) =>
        {
            UpdateStatus();
            BuildLights();
            _keep.Checked = AppSettings.Current.CloseToTray;
            _startup.Checked = await IntegrationService.IsStartupEnabledAsync();
        };

        Icon? appIcon = null;
        try { if (Environment.ProcessPath is { } exe) appIcon = Icon.ExtractAssociatedIcon(exe); } catch { }
        _icon = new NotifyIcon { Text = "SoNexus", Icon = appIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _window.ShowFromTray(); };
        _icon.BalloonTipClicked += (_, _) => _window.ShowFromTray();

        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
    }

    private static void Paint(ToolStripItem item)
    {
        item.ForeColor = item.Enabled ? Color.FromArgb(0xF5, 0xF5, 0xF5) : Color.FromArgb(0x8C, 0x8C, 0x8C);
        if (item is ToolStripMenuItem m)
            foreach (ToolStripItem sub in m.DropDownItems) Paint(sub);
    }

    /// <summary>Temperaturas no texto do ícone (passar o mouse) e no topo do menu; dispara os avisos.</summary>
    private void UpdateStatus()
    {
        var r = HardwareService.Instance.Readings;
        var cpu = HardwareService.CpuNow(r).Temp;
        var gpu = HardwareService.GpuNow(r).Temp;
        var ram = MemoryService.GetStatus().Load;
        var line = string.Join(" · ", new[]
        {
            cpu is { } c ? $"CPU {c:0} °C" : null,
            gpu is { } g ? $"GPU {g:0} °C" : null,
            $"RAM {ram}%",
        }.Where(s => s != null));
        _status.Text = line;
        var tip = $"SoNexus\n{line}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        var s = AppSettings.Current;
        ApplyBatterySaver(s);

        if (s.RamAlert && ram >= s.RamAlertPct && DateTime.Now - _lastRamAlert > TimeSpan.FromMinutes(15))
        {
            _lastRamAlert = DateTime.Now;
            Notify("Memória RAM quase cheia", $"{ram}% da RAM em uso. Feche programas que não está usando (ou use Hardware › Memória › Liberar memória).", ToolTipIcon.Warning);
        }

        if (!s.TempAlerts) return;
        if (cpu >= s.CpuAlertC && DateTime.Now - _lastCpuAlert > TimeSpan.FromMinutes(10))
        {
            _lastCpuAlert = DateTime.Now;
            Notify("Processador muito quente", $"A CPU chegou a {cpu:0} °C (limite {s.CpuAlertC} °C). Confira o cooler e a ventilação.", ToolTipIcon.Warning);
        }
        if (gpu >= s.GpuAlertC && DateTime.Now - _lastGpuAlert > TimeSpan.FromMinutes(10))
        {
            _lastGpuAlert = DateTime.Now;
            Notify("Placa de vídeo muito quente", $"A GPU chegou a {gpu:0} °C (limite {s.GpuAlertC} °C). Confira as ventoinhas e a poeira.", ToolTipIcon.Warning);
        }
    }

    /// <summary>Submenu de iluminação: troca o efeito sem abrir a janela.</summary>
    private void BuildLights()
    {
        _lights.DropDownItems.Clear();
        var rgb = RgbService.Instance;
        if (!rgb.Connected)
        {
            _lights.DropDownItems.Add("Conectar ao OpenRGB", null, async (_, _) =>
            {
                if (await rgb.ConnectAsync()) await Task.Run(rgb.Apply);
                else Notify("OpenRGB não encontrado", "Abra o OpenRGB e ligue o SDK Server.", ToolTipIcon.Info);
            });
        }
        else
        {
            foreach (var (effect, name, _) in RgbService.Effects)
            {
                var item = new ToolStripMenuItem(name) { Checked = !rgb.HardwareModeActive && rgb.Settings.Effect == effect.ToString() };
                item.Click += async (_, _) =>
                {
                    rgb.Settings.Effect = effect.ToString();
                    await Task.Run(rgb.Apply);
                };
                _lights.DropDownItems.Add(item);
            }
        }
        Paint(_lights);
    }

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) => _icon.ShowBalloonTip(6000, title, text, icon);

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>Cores do menu no tema do SoNexus (preto, cinza, branco).</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x0A, 0x0A, 0x0A);
        private static readonly Color Hover = Color.FromArgb(0x26, 0x26, 0x26);
        private static readonly Color Line = Color.FromArgb(0x2A, 0x2A, 0x2A);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

/// <summary>Devolve ao Windows a memória que o app não está usando (útil quando fica só na bandeja).</summary>
internal static class MemoryTrim
{
    public static void Now()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
