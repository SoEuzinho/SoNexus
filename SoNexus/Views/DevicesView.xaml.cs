using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class DevicesView : UserControl
{
    private List<DeviceEntry> _devices = new();
    private BoardSupport? _board;
    private bool _loaded;

    public DevicesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _board = await Task.Run(DeviceDriverService.Board);
        BoardTitle.Text = $"Placa-mãe: {_board.Maker} {_board.Board}";
        BoardButton.IsEnabled = _board.Url.Length > 0;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status.Text = "Lendo dispositivos...";
        _devices = await Task.Run(DeviceDriverService.Load);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var show = ShowGeneric.IsChecked == true;
        var shown = _devices.Where(d => show || !d.Generic || d.HasUpdate || d.Status != "OK").ToList();
        var view = new ListCollectionView(shown);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceEntry.Category)));
        DeviceGrid.ItemsSource = view;
        var problems = _devices.Count(d => d.Status != "OK");
        var updates = _devices.Count(d => d.HasUpdate);
        Status.Text = $"{_devices.Count} drivers · {problems} com problema · {updates} atualização(ões) encontrada(s)" +
                      (show ? "" : $" · {_devices.Count - shown.Count} genéricos do Windows escondidos");
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        Bar.IsEnabled = false;
        Status.Text = "Consultando o Windows Update (1 a 2 minutos)...";
        try
        {
            var updates = await DriverService.SearchUpdatesAsync();
            var extra = DeviceDriverService.Match(_devices, updates);
            // Atualizações que não casaram com nenhum dispositivo listado também aparecem.
            foreach (var u in extra)
                _devices.Add(new DeviceEntry { Category = "Outras atualizações", Name = u.Title, Provider = u.Manufacturer, Update = u, UpdateText = u.DateText });
            ApplyFilter();
            InstallAllButton.IsEnabled = _devices.Any(d => d.HasUpdate);
            if (updates.Count == 0) Status.Text += " · nenhuma atualização no Windows Update";
        }
        catch (Exception ex)
        {
            Status.Text = $"Não foi possível consultar o Windows Update: {ex.Message}";
        }
        Bar.IsEnabled = true;
    }

    private async void InstallOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DeviceEntry d } && d.Update != null) await InstallAsync(new[] { d });
    }

    private async void InstallAll_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_devices.Where(d => d.HasUpdate).ToList());

    private async Task InstallAsync(IReadOnlyList<DeviceEntry> devices)
    {
        if (MessageBox.Show($"Baixar e instalar {devices.Count} driver(s) pelo Windows Update?", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Bar.IsEnabled = false;
        var byUpdate = devices.ToDictionary(d => d.Update!, d => d);
        var reboot = await DriverService.InstallUpdatesAsync(byUpdate.Keys.ToList(),
            (u, status) => Dispatcher.Invoke(() => byUpdate[u].UpdateText = status));
        Status.Text = reboot ? "Instalação concluída. Reinicie o PC para terminar." : "Instalação concluída.";
        Bar.IsEnabled = true;
    }

    private void Board_Click(object sender, RoutedEventArgs e)
    {
        if (_board == null || _board.Url.Length == 0) return;
        try { Clipboard.SetText(_board.Board); } catch { }
        Process.Start(new ProcessStartInfo(_board.Url) { UseShellExecute = true });
        Status.Text = $"Modelo \"{_board.Board}\" copiado. Cole na busca do site.";
    }

    private void DeviceManager_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
}
