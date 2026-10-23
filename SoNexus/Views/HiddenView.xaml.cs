using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class HiddenView : UserControl
{
    private List<InstalledProgram> _programs = new();
    private List<TaskInfo> _hiddenTasks = new();
    private bool _loaded;

    public HiddenView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        _programs = await Task.Run(HiddenItemsService.GetPrograms);
        ApplyProgramFilter();

        var devices = LoadDevicesAsync();
        var tasks = LoadTasksAsync();
        await Task.WhenAll(devices, tasks);
    }

    // ---------- Programas ----------

    private void ApplyProgramFilter()
    {
        var q = ProgramFilter.Text.Trim();
        var onlyHidden = OnlyHidden.IsChecked == true;
        var shown = _programs
            .Where(p => !onlyHidden || p.Hidden)
            .Where(p => q.Length == 0
                        || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ProgramGrid.ItemsSource = shown;
        ProgramStatus.Text = $"{_programs.Count(p => p.Hidden)} ocultos de {_programs.Count} programas";
    }

    private void ProgramFilter_Changed(object sender, RoutedEventArgs e)
    {
        ProgramHint.Visibility = ProgramFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loaded) ApplyProgramFilter();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramGrid.SelectedItem is not InstalledProgram p) return;
        if (p.UninstallString.Length == 0)
        {
            MessageBox.Show("Este item não tem desinstalador registrado.", "SoNexus");
            return;
        }
        var warning = p.Hidden ? "\n\nAtenção: este item está oculto porque faz parte de outro programa ou do sistema." : "";
        if (MessageBox.Show($"Abrir o desinstalador de \"{p.Name}\"?{warning}", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            HiddenItemsService.Uninstall(p);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível abrir o desinstalador.\n\n{ex.Message}", "SoNexus");
        }
    }

    // ---------- Dispositivos ----------

    private async void ReloadDevices_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async Task LoadDevicesAsync()
    {
        DeviceBar.IsEnabled = false;
        DeviceStatus.Text = "Procurando dispositivos desconectados...";
        var devices = await HiddenItemsService.GetGhostDevicesAsync();
        DeviceGrid.ItemsSource = devices.OrderBy(d => d.Class).ThenBy(d => d.Display).ToList();
        DeviceStatus.Text = $"{devices.Count} dispositivos que não estão mais conectados. Removê-los só apaga o registro; se o aparelho voltar, o Windows reinstala.";
        DeviceBar.IsEnabled = true;
    }

    private async void RemoveDevices_Click(object sender, RoutedEventArgs e)
    {
        var selected = DeviceGrid.SelectedItems.Cast<GhostDevice>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Selecione um ou mais dispositivos (Ctrl ou Shift + clique).", "SoNexus");
            return;
        }
        if (MessageBox.Show($"Remover {selected.Count} dispositivo(s) fantasma?", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        DeviceBar.IsEnabled = false;
        var failed = new List<string>();
        foreach (var d in selected)
        {
            DeviceStatus.Text = $"Removendo {d.Display}...";
            if (await HiddenItemsService.RemoveDeviceAsync(d) is { } error) failed.Add($"{d.Display}: {error}");
        }
        await LoadDevicesAsync();
        if (failed.Count > 0)
            MessageBox.Show("Alguns não foram removidos:\n\n" + string.Join("\n", failed.Take(10)), "SoNexus");
    }

    // ---------- Tarefas ----------

    private async Task LoadTasksAsync()
    {
        TaskStatus.Text = "Lendo o Agendador de Tarefas...";
        _hiddenTasks = (await StartupService.LoadTasksAsync()).Where(t => t.Hidden).ToList();
        ApplyTaskFilter();
    }

    private void ApplyTaskFilter()
    {
        var showMs = TaskShowMicrosoft.IsChecked == true;
        var shown = _hiddenTasks
            .Where(t => showMs || !t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Path).ThenBy(t => t.Name)
            .ToList();
        TaskGrid.ItemsSource = shown;
        TaskStatus.Text = $"{shown.Count} tarefas ocultas exibidas ({_hiddenTasks.Count} no total, incluindo as do Windows).";
    }

    private void TaskFilter_Changed(object sender, RoutedEventArgs e) => ApplyTaskFilter();
}
