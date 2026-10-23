using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkAppsView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<AppTraffic> _apps = new();
    private int _tick;

    public NetworkAppsView()
    {
        InitializeComponent();
        var view = new ListCollectionView(_apps) { IsLiveSorting = true };
        view.SortDescriptions.Add(new SortDescription(nameof(AppTraffic.Total), ListSortDirection.Descending));
        view.LiveSortingProperties.Add(nameof(AppTraffic.Total));
        AppGrid.ItemsSource = view;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        NetworkMonitor.Instance.Start();
        if (NetworkMonitor.Instance.Error is { } err)
            Status.Text = $"Não foi possível medir por aplicativo: {err}. Execute como administrador.";
        else
            Status.Text = $"Medindo desde {NetworkMonitor.Instance.Since:HH:mm}. Os valores somam enquanto o SoNexus estiver aberto.";
        _tick = 0;
        await TickAsync();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void ShowAll_Click(object sender, RoutedEventArgs e) => _tick = 0;

    private async Task TickAsync()
    {
        var connections = await Task.Run(NetworkService.Connections);
        foreach (var app in NetworkMonitor.Instance.Sample(connections))
            if (!_apps.Contains(app)) _apps.Add(app);

        // A lista de conexões muda menos: atualiza a cada 3 s.
        if (_tick++ % 3 != 0) return;
        var all = ShowAll.IsChecked == true;
        var shown = connections
            .Where(c => all || (!c.Listening && !c.RemoteIp.StartsWith("127.") && c.RemoteIp != "::1" && c.RemoteIp != "0.0.0.0"))
            .OrderBy(c => c.Process).ToList();
        foreach (var c in shown.Where(c => !c.Listening))
            c.Host = await NetworkService.HostNameAsync(c.RemoteIp);
        ConnGrid.ItemsSource = shown;
    }
}
