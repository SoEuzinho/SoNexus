using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkRoutesView : UserControl
{
    private const int Rounds = 30; // ~20 s de medição
    private readonly List<(string Name, string Host, int Port)> _targets = new();
    private CancellationTokenSource? _cts;
    private List<RouteHop> _hops = new();
    private bool _loaded;

    public NetworkRoutesView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _targets.AddRange(RouteService.Presets);
        RefreshTargets(0);
        // CS2/Dota 2: relés atuais da Valve, logo depois do Minecraft na lista.
        var valve = await RouteService.ValveRelaysAsync();
        var at = _targets.FindIndex(t => t.Name.StartsWith("Fortnite"));
        _targets.InsertRange(at < 0 ? _targets.Count : at, valve);
        RefreshTargets(TargetBox.SelectedIndex < 0 ? 0 : TargetBox.SelectedIndex);
    }

    private void RefreshTargets(int select)
    {
        TargetBox.ItemsSource = _targets.Select(t => $"{t.Name}  ·  {t.Host}").ToList();
        TargetBox.SelectedIndex = select;
    }

    private void Target_Selected(object sender, SelectionChangedEventArgs e) { }

    private async void FindGame_Click(object sender, RoutedEventArgs e)
    {
        TargetHint.Text = "Procurando conexões dos jogos abertos...";
        var servers = await Task.Run(RouteService.OpenGameServers);
        if (servers.Count == 0)
        {
            TargetHint.Text = "Nenhum jogo da sua biblioteca está conectado a um servidor agora. Abra o jogo, entre numa partida e tente de novo. (Alguns jogos usam só UDP, que o Windows não mostra; nesse caso use um destino da lista.)";
            return;
        }
        _targets.RemoveAll(t => t.Name.StartsWith("🎮"));
        foreach (var s in servers) _targets.Insert(0, ($"🎮 {s.Game}", s.Ip, s.Port));
        RefreshTargets(0);
        TargetHint.Text = $"{servers.Count} servidor(es) encontrado(s). Clique em Traçar rota.";
    }

    private async void Trace_Click(object sender, RoutedEventArgs e)
    {
        var (host, port) = CurrentTarget();
        if (host.Length == 0) return;
        _cts = new CancellationTokenSource();
        TraceButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        VerdictCard.Visibility = Visibility.Visible;
        VerdictTitle.Text = "Descobrindo o caminho...";
        VerdictText.Text = $"Até {host}. Leva uns 20 segundos.";
        HopList.ItemsSource = null;
        try
        {
            await RouteService.TraceAsync(host, port, hops => Dispatcher.Invoke(() =>
            {
                _hops = hops;
                HopList.ItemsSource = hops;
                VerdictTitle.Text = $"{hops.Count} saltos até o destino. Medindo...";
            }), Rounds, _cts.Token);
            var (title, detail) = RouteService.Diagnose(_hops);
            VerdictTitle.Text = title;
            VerdictText.Text = detail;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VerdictTitle.Text = "Não foi possível traçar a rota";
            VerdictText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            if (_hops.Count > 0)
            {
                var (title, detail) = RouteService.Diagnose(_hops);
                VerdictTitle.Text = title;
                VerdictText.Text = detail + " (medição interrompida)";
            }
        }
        TraceButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>Item da lista ou o que o usuário digitou (site, IP, "ip:porta").</summary>
    private (string Host, int Port) CurrentTarget()
    {
        if (TargetBox.SelectedIndex >= 0 && TargetBox.SelectedIndex < _targets.Count &&
            TargetBox.Text == (string)TargetBox.SelectedItem)
        {
            var t = _targets[TargetBox.SelectedIndex];
            return (t.Host, t.Port);
        }
        var text = TargetBox.Text.Trim();
        if (text.Contains("://") && Uri.TryCreate(text, UriKind.Absolute, out var uri)) text = uri.Host;
        text = text.Trim('/', ' ');
        var parts = text.Split(':');
        return parts.Length == 2 && int.TryParse(parts[1], out var p) ? (parts[0], p) : (text, 443);
    }
}
