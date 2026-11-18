using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GpuCleanView : UserControl
{
    private List<DriverPackage> _packages = new();
    private bool _loaded;
    private bool _busy;
    private int _loadVersion;
    private string? _lastBackupFolder;

    public GpuCleanView() => InitializeComponent();

    private GpuVendor Vendor => Enum.Parse<GpuVendor>((string)((ComboBoxItem)VendorBox.SelectedItem).Tag);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSafeBanner();
        UpdateAutoInstall();
        if (_loaded) return;
        _loaded = true;
        RestoreDriverButton.IsEnabled = SensorDriver.IsAdmin;

        var gpus = await Task.Run(DriverService.GetGpuDrivers);
        var detected = GpuCleanupService.DetectInstalled(gpus);
        DetectedText.Text = gpus.Count == 0
            ? "Nenhuma placa de vídeo detectada."
            : "Instalado: " + string.Join("; ", gpus.Select(g => $"{g.Name} (driver {g.Version})"));
        // Prefere a placa dedicada quando há GPU integrada também.
        var preferred = detected.Contains(GpuVendor.Nvidia) ? GpuVendor.Nvidia
            : detected.Contains(GpuVendor.Amd) ? GpuVendor.Amd
            : GpuVendor.Intel;
        if (VendorBox.SelectedItem == null) VendorBox.SelectedIndex = (int)preferred;
    }

    private async void Vendor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        await LoadVendorAsync();
    }

    private async void IncludeAudio_Click(object sender, RoutedEventArgs e) => await LoadVendorAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadVendorAsync();

    private async Task LoadVendorAsync()
    {
        if (_busy || VendorBox.SelectedItem == null) return;
        var version = ++_loadVersion;
        var vendor = Vendor;
        var includeAudio = IncludeAudio.IsChecked == true;
        CleanButton.IsEnabled = RefreshButton.IsEnabled = false;
        _packages = new();
        PackageStatus.Text = "Procurando pacotes no DriverStore...";
        PackageList.ItemsSource = null;
        ProgramList.ItemsSource = null;
        NoProgramsText.Visibility = Visibility.Collapsed;
        if (!SensorDriver.IsAdmin)
        {
            PackageStatus.Text = "Abra o SoNexus como administrador para consultar e remover drivers.";
            return;
        }
        try
        {
            var packages = await GpuCleanupService.GetPackagesAsync(vendor, includeAudio);
            if (version != _loadVersion) return;
            _packages = packages;
            PackageList.ItemsSource = packages;
            PackageStatus.Text = packages.Count == 0
                ? $"Nenhum pacote compatível da {GpuCleanupService.Name(vendor)} encontrado."
                : $"{packages.Count} pacote(s) da {GpuCleanupService.Name(vendor)}. Revise a seleção antes de remover.";
            CleanButton.IsEnabled = packages.Count > 0;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            PackageStatus.Text = "Não foi possível consultar o DriverStore.";
            Log($"✖ {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion) RefreshButton.IsEnabled = true;
        }

        try
        {
            var programs = await Task.Run(() => GpuCleanupService.GetVendorPrograms(vendor));
            if (version != _loadVersion || _busy) return;
            ProgramList.ItemsSource = programs;
            NoProgramsText.Visibility = programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) Log($"✖ Não foi possível listar os programas: {ex.Message}");
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CleanButton.IsEnabled) return;
        var vendor = Vendor;
        var name = GpuCleanupService.Name(vendor);
        var selected = _packages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            PackageStatus.Text = "Selecione pelo menos um pacote para remover.";
            return;
        }
        var options = new CleanupOptions(IncludeAudio.IsChecked == true, BlockUpdate.IsChecked == true,
            RemoveServices.IsChecked == true, RemoveFolders.IsChecked == true, RemovePanel.IsChecked == true);
        var backUp = BackupDrivers.IsChecked == true;
        var safe = GpuCleanupService.IsSafeMode ? "" : "\n\nVocê não está no Modo de Segurança. Funciona, mas o resultado é mais completo nele.";
        if (MessageBox.Show(
                $"Remover {selected.Count} pacote(s) da {name}?\n\n{string.Join(", ", selected.Select(p => p.Driver))}" +
                $"{(options.RemoveServices ? "\n• Serviços gráficos" : "")}" +
                $"{(options.RemoveFolders ? "\n• Caches gráficos" : "")}" +
                $"{(options.RemoveControlPanel ? "\n• Painel de controle" : "")}" +
                $"{(backUp ? "\n• Exportar e verificar o backup antes de remover" : "")}" +
                $"{(options.BlockWindowsUpdate ? "\n• Desativar busca automática de drivers" : "")}{safe}",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (backUp)
        {
            var folder = new OpenFolderDialog { Title = "Escolha onde salvar o backup dos drivers" };
            if (Directory.Exists(_lastBackupFolder)) folder.InitialDirectory = _lastBackupFolder;
            if (folder.ShowDialog() != true) return;
            _lastBackupFolder = folder.FolderName;
            options = options with { BackupDirectory = folder.FolderName };
        }

        _busy = true;
        SetCleanupControls(false);
        GpuCleanupResult? result = null;
        try
        {
            if (RestorePoint.IsChecked == true)
            {
                Log("Criando ponto de restauração...");
                var (code, _, error) = await ProcessRunner.PowerShellAsync(
                    "Checkpoint-Computer -Description 'SoNexus - driver de vídeo' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop -WarningAction Stop");
                if (code != 0)
                {
                    Log($"✖ Sem ponto de restauração. Remoção cancelada. {error.Trim()}");
                    return;
                }
                Log("✔ Ponto de restauração criado.");
            }

            result = await GpuCleanupService.CleanAsync(vendor, selected, options, Log);
            Log($"Resultado: {result.Removed} pacote(s) removido(s), {result.Failed} falha(s)." +
                (result.RestartRequired ? " O Windows solicitou reinício." : ""));
        }
        catch (Exception ex)
        {
            Log($"✖ Erro: {ex.Message}");
        }
        finally
        {
            _busy = false;
            SetCleanupControls(true);
            UpdateAutoInstall();
            await LoadVendorAsync();
        }

        if (result is { Removed: > 0 } && MessageBox.Show(
                result.Failed > 0 ? "A remoção teve falhas. Consulte o registro. Reiniciar o PC agora?"
                    : "Pacotes selecionados removidos. Reiniciar o PC agora?", "SoNexus", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RestartAsync();
    }

    private void SetCleanupControls(bool enabled)
    {
        OptionsPanel.IsEnabled = PackageList.IsEnabled = ProgramList.IsEnabled = enabled;
        SafeButton.IsEnabled = RebootButton.IsEnabled = enabled;
        RestoreDriverButton.IsEnabled = enabled && SensorDriver.IsAdmin;
        if (!enabled) CleanButton.IsEnabled = RefreshButton.IsEnabled = false;
    }

    private async void RestoreDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !SensorDriver.IsAdmin) return;
        var picker = new OpenFileDialog
        {
            Title = "Abra a pasta do backup e selecione o arquivo INF",
            Filter = "Pacote de driver (*.inf)|*.inf",
            CheckFileExists = true,
        };
        if (Directory.Exists(_lastBackupFolder)) picker.InitialDirectory = _lastBackupFolder;
        if (picker.ShowDialog() != true) return;
        if (MessageBox.Show($"Importar e tentar aplicar este pacote?\n\n{picker.FileName}", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _busy = true;
        ++_loadVersion;
        SetCleanupControls(false);
        try
        {
            Log(await GpuCleanupService.RestorePackageAsync(picker.FileName));
        }
        catch (Exception ex)
        {
            Log($"✖ {ex.Message}");
        }
        finally
        {
            _busy = false;
            SetCleanupControls(true);
            await LoadVendorAsync();
        }
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reiniciar o PC agora? Salve seus trabalhos antes.", "SoNexus", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RestartAsync();
    }

    /// <summary>Se estiver no Modo de Segurança, já volta ao boot normal antes de reiniciar.</summary>
    private async Task RestartAsync()
    {
        var error = GpuCleanupService.IsSafeMode
            ? await GpuCleanupService.LeaveSafeModeAsync(reboot: true)
            : await GpuCleanupService.RestartAsync();
        if (error != null) Log($"✖ {error}");
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
            ? "Busca automática desativada. Você pode reativá-la após instalar o driver."
            : "Busca automática de drivers habilitada.";
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
