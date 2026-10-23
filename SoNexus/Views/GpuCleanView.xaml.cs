using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GpuCleanView : UserControl
{
    private List<DriverPackage> _packages = new();
    private bool _loaded;
    private bool _busy;

    public GpuCleanView() => InitializeComponent();

    private GpuVendor Vendor => Enum.Parse<GpuVendor>((string)((ComboBoxItem)VendorBox.SelectedItem).Tag);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSafeBanner();
        UpdateAutoInstall();
        if (_loaded) return;
        _loaded = true;

        var detected = await Task.Run(GpuCleanupService.DetectInstalled);
        var gpus = await Task.Run(DriverService.GetGpuDrivers);
        DetectedText.Text = gpus.Count == 0
            ? "Nenhuma placa de vídeo detectada."
            : "Instalado: " + string.Join("; ", gpus.Select(g => $"{g.Name} (driver {g.Version})"));
        // Prefere a placa dedicada quando há GPU integrada também.
        var preferred = detected.Contains(GpuVendor.Nvidia) ? GpuVendor.Nvidia
            : detected.Contains(GpuVendor.Amd) ? GpuVendor.Amd
            : GpuVendor.Intel;
        VendorBox.SelectedIndex = (int)preferred; // dispara Vendor_Changed
    }

    private async void Vendor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        await LoadVendorAsync();
    }

    private async void IncludeAudio_Click(object sender, RoutedEventArgs e) => await LoadVendorAsync();

    private async Task LoadVendorAsync()
    {
        var vendor = Vendor;
        PackageStatus.Text = "Procurando pacotes no DriverStore...";
        PackageList.ItemsSource = null;
        _packages = await GpuCleanupService.GetPackagesAsync(vendor, IncludeAudio.IsChecked == true);
        PackageList.ItemsSource = _packages;
        PackageStatus.Text = _packages.Count == 0
            ? $"Nenhum pacote de driver {GpuCleanupService.Name(vendor)} encontrado."
            : $"{_packages.Count} pacote(s) da {GpuCleanupService.Name(vendor)}. Inclui versões antigas que ficaram guardadas.";

        var programs = await Task.Run(() => GpuCleanupService.GetVendorPrograms(vendor));
        ProgramList.ItemsSource = programs;
        NoProgramsText.Visibility = programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var vendor = Vendor;
        var name = GpuCleanupService.Name(vendor);
        var selected = _packages.Where(p => p.IsSelected).ToList();
        var safe = GpuCleanupService.IsSafeMode ? "" : "\n\nVocê não está no Modo de Segurança. Funciona, mas o resultado é mais completo nele.";
        if (MessageBox.Show(
                $"Remover o driver de vídeo {name}?\n\n• {selected.Count} pacote(s) de driver\n• dispositivos de vídeo {name}" +
                $"{(RemoveServices.IsChecked == true ? "\n• serviços do fabricante" : "")}" +
                $"{(RemoveFolders.IsChecked == true ? "\n• pastas e caches" : "")}{safe}",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _busy = true;
        OptionsPanel.IsEnabled = CleanButton.IsEnabled = false;
        try
        {
            if (RestorePoint.IsChecked == true)
            {
                Log("Criando ponto de restauração...");
                var error = await DebloatService.CreateRestorePointAsync();
                Log(error is null ? "✔ Ponto de restauração criado." : $"• Sem ponto de restauração: {error}");
            }

            var options = new CleanupOptions(IncludeAudio.IsChecked == true, BlockUpdate.IsChecked == true,
                RemoveServices.IsChecked == true, RemoveFolders.IsChecked == true, RemovePanel.IsChecked == true);
            await GpuCleanupService.CleanAsync(vendor, selected, options, line => Dispatcher.Invoke(() => Log(line)));
        }
        catch (Exception ex)
        {
            Log($"✖ Erro: {ex.Message}");
        }
        finally
        {
            _busy = false;
            OptionsPanel.IsEnabled = CleanButton.IsEnabled = true;
            UpdateAutoInstall();
            await LoadVendorAsync();
        }

        if (MessageBox.Show("Limpeza concluída. Reiniciar o PC agora?", "SoNexus", MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RestartAsync();
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reiniciar o PC em 5 segundos?", "SoNexus", MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RestartAsync();
    }

    /// <summary>Se estiver no Modo de Segurança, já volta ao boot normal antes de reiniciar.</summary>
    private static async Task RestartAsync()
    {
        if (GpuCleanupService.IsSafeMode) await GpuCleanupService.LeaveSafeModeAsync(reboot: true);
        else GpuCleanupService.Restart();
    }

    private void UninstallProgram_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstalledProgram p }) return;
        try { HiddenItemsService.Uninstall(p); }
        catch (Exception ex) { Log($"✖ {p.Name}: {ex.Message}"); }
    }

    // ---------- Modo de segurança ----------

    private void UpdateSafeBanner()
    {
        Brush color;
        if (GpuCleanupService.IsSafeMode)
        {
            SafeText.Text = "Você está no Modo de Segurança. Remova o driver e depois clique para voltar ao modo normal.";
            SafeButton.Content = "Sair do Modo de Segurança e reiniciar";
            color = Metric.Ok;
        }
        else
        {
            SafeText.Text = "Recomendado: fazer a remoção no Modo de Segurança. Se o BitLocker estiver ativo, tenha a chave de recuperação em mãos. Depois de reiniciar, abra o SoNexus de novo.";
            SafeButton.Content = "Reiniciar em Modo de Segurança";
            color = Metric.Warn;
        }
        SafeText.Foreground = color;
        SafeBanner.BorderBrush = color;
        var bg = color.Clone();
        bg.Opacity = 0.1;
        SafeBanner.Background = bg;
    }

    private async void Safe_Click(object sender, RoutedEventArgs e)
    {
        if (GpuCleanupService.IsSafeMode)
        {
            var error = await GpuCleanupService.LeaveSafeModeAsync(reboot: true);
            if (error != null) Log($"✖ {error}");
            return;
        }
        if (MessageBox.Show("Reiniciar agora em Modo de Segurança?\n\nSalve seus trabalhos antes. Depois de reiniciar, abra o SoNexus e volte nesta tela.",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var err = await GpuCleanupService.RebootToSafeModeAsync();
        Log(err is null ? "Reiniciando em Modo de Segurança..." : $"✖ Não foi possível: {err}");
    }

    private void UpdateAutoInstall()
    {
        var blocked = GpuCleanupService.IsAutoDriverInstallBlocked();
        AutoInstallText.Text = blocked
            ? "Agora: o Windows NÃO instala drivers sozinho. Libere depois de instalar o driver novo."
            : "Agora: o Windows instala drivers automaticamente.";
        UnblockButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        GpuCleanupService.SetAutoDriverInstall(true);
        Log("✔ Instalação automática de drivers pelo Windows Update liberada.");
        UpdateAutoInstall();
    }

    private void Log(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        LogBox.ScrollToEnd();
    }
}
