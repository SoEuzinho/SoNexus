using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkDnsView : UserControl
{
    private List<DnsCacheEntry> _cache = new();
    private AdapterInfo? _adapter;
    private bool _loaded;

    public NetworkDnsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        ShowCurrent();
        await LoadCacheAsync();
    }

    private void ShowCurrent()
    {
        _adapter = NetworkService.Adapters().FirstOrDefault();
        var servers = _adapter?.Dns.Where(d => !d.StartsWith("fec0")).ToList() ?? new();
        var known = NetworkService.PublicDns.FirstOrDefault(p => servers.Contains(p.Primary));
        CurrentDns.Text = known.Name ?? (servers.Count > 0 ? string.Join(", ", servers) : "—");
        CurrentAdapter.Text = known.Name != null
            ? $"{string.Join(", ", servers)} · {_adapter?.Name}"
            : servers.Any(s => _adapter?.Gateways.Contains(s) == true)
                ? $"DNS do roteador/operadora · {_adapter?.Name}"
                : _adapter?.Name ?? "";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        DnsBar.IsEnabled = false;
        DnsStatus.Text = "Medindo...";
        var targets = new List<(string Name, string Server, string? Secondary)>();
        foreach (var s in _adapter?.Dns.Where(d => System.Net.IPAddress.TryParse(d, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? Enumerable.Empty<string>())
            targets.Add(("Atual", s, null));
        targets.AddRange(NetworkService.PublicDns.Select(p => (p.Name, p.Primary, (string?)p.Secondary)));

        var results = await Task.WhenAll(targets.Select(async t =>
            (Test: new DnsTest(t.Name, t.Server, await NetworkService.DnsQueryMsAsync(t.Server)), t.Secondary)));
        TestList.ItemsSource = results.OrderBy(r => r.Test.Ms ?? double.MaxValue).Select(r => r.Test).ToList();
        var best = results.Where(r => r.Test.Ms != null).MinBy(r => r.Test.Ms);
        DnsStatus.Text = best.Test != null ? $"Mais rápido: {best.Test.Name} ({best.Test.MsText})." : "Nenhum servidor respondeu.";
        DnsBar.IsEnabled = true;
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DnsTest t } || _adapter == null) return;
        var pair = NetworkService.PublicDns.FirstOrDefault(p => p.Primary == t.Server);
        if (pair.Primary == null)
        {
            DnsStatus.Text = "Esse já é o DNS atual.";
            return;
        }
        if (MessageBox.Show($"Usar o DNS {pair.Name} ({pair.Primary} e {pair.Secondary}) em \"{_adapter.Name}\"?", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var error = await NetworkService.SetDnsAsync(_adapter.Index, pair.Primary, pair.Secondary);
        DnsStatus.Text = error is null ? $"✔ DNS alterado para {pair.Name}." : $"✖ {error}";
        await NetworkService.FlushDnsAsync();
        ShowCurrent();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_adapter == null) return;
        var error = await NetworkService.SetDnsAsync(_adapter.Index, null, null);
        DnsStatus.Text = error is null ? "✔ DNS voltou para o automático (do roteador/operadora)." : $"✖ {error}";
        ShowCurrent();
    }

    private async void Flush_Click(object sender, RoutedEventArgs e)
    {
        await NetworkService.FlushDnsAsync();
        DnsStatus.Text = "✔ Cache DNS limpo.";
        await LoadCacheAsync();
    }

    private async Task LoadCacheAsync()
    {
        CacheStatus.Text = "Lendo...";
        _cache = await NetworkService.DnsCacheAsync();
        ApplyFilter();
    }

    private void CacheFilter_Changed(object sender, TextChangedEventArgs e)
    {
        CacheHint.Visibility = CacheFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = CacheFilter.Text.Trim();
        var shown = _cache.Where(c => q.Length == 0 || c.Entry.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        CacheGrid.ItemsSource = shown;
        CacheStatus.Text = _cache.Count == 0
            ? "Cache vazio (acabou de ser limpo) ou sem permissão de administrador para ler."
            : $"{_cache.Select(c => c.Entry).Distinct().Count()} domínios guardados pelo Windows.";
    }
}
