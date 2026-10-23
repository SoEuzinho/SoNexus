using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class SettingsView : UserControl
{
    private static readonly int[] CpuLimits = { 80, 85, 90, 95 };
    private static readonly int[] GpuLimits = { 75, 80, 85, 90 };
    private static readonly int[] RamLimits = { 80, 85, 90, 95 };
    private static readonly double[] Scales = { 0.9, 1, 1.1, 1.25 };
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
        CpuAlertBox.ItemsSource = CpuLimits.Select(c => $"{c} °C").ToList();
        GpuAlertBox.ItemsSource = GpuLimits.Select(c => $"{c} °C").ToList();
        RamAlertPctBox.ItemsSource = RamLimits.Select(c => $"{c}%").ToList();
        var v = typeof(SettingsView).Assembly.GetName().Version;
        AboutTitle.Text = $"SoNexus {v?.Major}.{v?.Minor}.{v?.Build}";
    }

    private static AppSettings S => AppSettings.Current;
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reload();
        ShowUsage();
        TermsStatus.Text = Terms.Accepted
            ? $"Termos de Uso e Privacidade v{S.TermsVersion} aceitos em {S.TermsAcceptedAt}."
            : "Termos ainda não aceitos.";
        ShowHotkeyStatus(Main?.ApplyHotkeys() ?? new());
        StartupBox.IsChecked = await IntegrationService.IsStartupEnabledAsync();
    }

    /// <summary>Relê as opções (podem ter mudado pelo menu da bandeja).</summary>
    public void Reload()
    {
        _syncing = true;
        CloseBox.IsChecked = S.CloseToTray;
        MinimizeBox.IsChecked = S.MinimizeToTray;
        AlertBox.IsChecked = S.TempAlerts;
        RamAlertBox.IsChecked = S.RamAlert;
        BatteryBox.IsChecked = S.BatterySaver;
        OnlineBox.IsChecked = S.AllowOnline;
        HotkeyBox.IsChecked = S.GlobalHotkeys;
        CpuAlertBox.SelectedIndex = Math.Max(0, Array.IndexOf(CpuLimits, S.CpuAlertC));
        GpuAlertBox.SelectedIndex = Math.Max(0, Array.IndexOf(GpuLimits, S.GpuAlertC));
        RamAlertPctBox.SelectedIndex = Math.Max(0, Array.IndexOf(RamLimits, S.RamAlertPct));
        IntervalBox.SelectedIndex = S.SensorIntervalMs switch { >= 5000 => 2, >= 2000 => 1, _ => 0 };
        ScaleBox.SelectedIndex = Math.Max(0, Array.FindIndex(Scales, k => Math.Abs(k - S.UiScale) < 0.01));
        StartPageBox.SelectedIndex = S.OpenLastPage ? 0 : 1;
        UpdateEnabled();
        _syncing = false;
    }

    private void UpdateEnabled()
    {
        CpuAlertBox.IsEnabled = GpuAlertBox.IsEnabled = S.TempAlerts;
        RamAlertPctBox.IsEnabled = S.RamAlert;
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        S.CloseToTray = CloseBox.IsChecked == true;
        S.MinimizeToTray = MinimizeBox.IsChecked == true;
        S.TempAlerts = AlertBox.IsChecked == true;
        S.RamAlert = RamAlertBox.IsChecked == true;
        S.BatterySaver = BatteryBox.IsChecked == true;
        S.AllowOnline = OnlineBox.IsChecked == true;
        UpdateEnabled();
        S.Save();
    }

    // ---------- Aparência ----------

    private void Scale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ScaleBox.SelectedIndex < 0) return;
        S.UiScale = Scales[ScaleBox.SelectedIndex];
        S.Save();
        Main?.ApplyScale();
    }

    private void StartPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        S.OpenLastPage = StartPageBox.SelectedIndex == 0;
        S.Save();
    }

    // ---------- Atalhos ----------

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        S.GlobalHotkeys = HotkeyBox.IsChecked == true;
        S.Save();
        ShowHotkeyStatus(Main?.ApplyHotkeys() ?? new());
    }

    private void ShowHotkeyStatus(List<string> failed)
    {
        HotkeyStatus.Text = !S.GlobalHotkeys ? "Atalhos desligados."
            : failed.Count == 0 ? "✔ Atalhos ativos."
            : $"✖ {string.Join(" e ", failed)} já é usado por outro programa (feche-o ou desligue o atalho dele).";
        HotkeyStatus.Foreground = failed.Count > 0 && S.GlobalHotkeys ? Metric.Warn : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    // ---------- Avisos ----------

    private void Alert_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CpuAlertBox.SelectedIndex < 0 || GpuAlertBox.SelectedIndex < 0 || RamAlertPctBox.SelectedIndex < 0) return;
        S.CpuAlertC = CpuLimits[CpuAlertBox.SelectedIndex];
        S.GpuAlertC = GpuLimits[GpuAlertBox.SelectedIndex];
        S.RamAlertPct = RamLimits[RamAlertPctBox.SelectedIndex];
        S.Save();
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e) => Main?.TestNotification();

    // ---------- Segundo plano ----------

    private async void Startup_Click(object sender, RoutedEventArgs e)
    {
        var enable = StartupBox.IsChecked == true;
        var error = await IntegrationService.SetStartupAsync(enable);
        if (error != null)
        {
            StartupBox.IsChecked = !enable;
            StartupHint.Text = $"✖ {error}";
            StartupHint.Foreground = Metric.Danger;
            return;
        }
        StartupHint.Text = enable ? "✔ O SoNexus vai abrir sozinho no próximo login, direto no segundo plano." : "✔ O SoNexus não abre mais com o Windows.";
    }

    // ---------- Desempenho ----------

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || IntervalBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        HardwareService.Instance.IntervalMs = S.SensorIntervalMs = int.Parse(tag);
        S.Save();
    }

    private void ShowUsage()
    {
        using var p = Process.GetCurrentProcess();
        UsageText.Text = $"O SoNexus está usando {Format.Bytes((ulong)p.WorkingSet64)} de RAM agora.";
    }

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        MemoryTrim.Now();
        await Task.Delay(500);
        ShowUsage();
    }

    // ---------- Dados ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "sonexus-configuracoes.json", Filter = "Configurações do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        S.Save();
        File.Copy(AppSettings.Path_, dialog.FileName, overwrite: true);
        DataStatus.Text = $"✔ Exportado para {dialog.FileName}";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Configurações do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            // Confere se é mesmo um arquivo de configurações antes de trocar.
            var text = File.ReadAllText(dialog.FileName);
            _ = JsonSerializer.Deserialize<AppSettings>(text) ?? throw new InvalidDataException();
            File.WriteAllText(AppSettings.Path_, text);
            AppSettings.DisableSave(); // não regravar as antigas por cima até reabrir
        }
        catch
        {
            DataStatus.Text = "✖ Esse arquivo não é uma configuração do SoNexus.";
            return;
        }
        if (MessageBox.Show("Configurações importadas. Reabrir o SoNexus agora para aplicar tudo?", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            SensorDriver.RestartApp();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(AppSettings.Path_)!;
        long freed = 0;
        // Só o que o SoNexus recria sozinho: requisitos dos jogos (baixa de novo) e registros (log).
        foreach (var name in new[] { "games-cache.json", "sonexus.log", "sonexus.log.old", "PawnIO_setup.exe" })
        {
            var file = Path.Combine(folder, name);
            try
            {
                if (!File.Exists(file)) continue;
                freed += new FileInfo(file).Length;
                File.Delete(file);
            }
            catch { }
        }
        DataStatus.Text = $"✔ {Format.Bytes((ulong)freed)} liberados. Suas configurações e os backups dos ajustes do Windows foram mantidos.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Voltar todas as configurações do SoNexus ao padrão (iluminação, painel de FPS, avisos, janela...)?\n\nOs ajustes que você fez no Windows (Otimização) não são desfeitos. O SoNexus será reaberto.",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { File.Delete(AppSettings.Path_); } catch { }
        // Sem salvar de novo ao sair (senão as configurações antigas voltariam).
        AppSettings.DisableSave();
        SensorDriver.RestartApp();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e) => new UninstallWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    // ---------- Sobre ----------

    private void Terms_Click(object sender, RoutedEventArgs e) => new TermsWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(AppLog.Path_)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Notices_Click(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
