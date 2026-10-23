using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (long Rx, long Tx) _last;
    private long _lastTime;
    private double _peak = 1;
    private bool _loaded;

    public NetworkView()
    {
        InitializeComponent();
        UpChart.Stroke = (Brush)Application.Current.FindResource("MutedBrush");
        _timer.Tick += (_, _) => Tick();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _last = NetworkService.TotalBytes();
        _lastTime = Stopwatch.GetTimestamp();
        _timer.Start();
        if (_loaded) return;
        _loaded = true;

        var adapters = NetworkService.Adapters();
        var main = adapters.FirstOrDefault();
        if (main == null)
        {
            ConnType.Text = "Sem conexão";
            ConnDetail.Text = "Nenhuma placa de rede conectada.";
        }
        else
        {
            ConnType.Text = main.Type;
            ConnDetail.Text = $"{main.Description} · {main.SpeedText}";
            var items = new List<InfoItem>
            {
                new("IPv4 local", string.Join(", ", main.IPv4)),
                new("IPv6", main.IPv6.Count > 0 ? string.Join("\n", main.IPv6.Take(2)) : "—"),
                new("Roteador (gateway)", string.Join(", ", main.Gateways)),
                new("Servidores DNS", string.Join(", ", main.Dns)),
                new("MAC", main.Mac),
                new("Velocidade do link", main.SpeedText),
            };
            foreach (var other in adapters.Skip(1))
                items.Add(new InfoItem($"Outra: {other.Name}", $"{other.Type} · {string.Join(", ", other.IPv4)}"));
            AddressList.ItemsSource = items;
        }

        var wifi = main?.Type == "Wi-Fi" ? await NetworkService.WifiDetailsAsync() : new();
        WifiList.ItemsSource = wifi;
        WifiCard.Visibility = wifi.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var pub = await NetworkService.PublicInfoAsync();
        PublicIp.Text = pub?.Ip ?? "indisponível";
        PublicDetail.Text = pub != null ? $"{pub.Isp}\n{pub.City}, {pub.Region} · {pub.Country}"
            : !AppSettings.Current.AllowOnline ? "Consultas na internet desligadas em Configurações › Privacidade."
            : "Sem acesso à internet ou serviço fora do ar.";

        await RunPingsAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var bytes = NetworkService.TotalBytes();
        var seconds = (now - _lastTime) / (double)Stopwatch.Frequency;
        if (seconds <= 0) return;
        var down = Math.Max(0, bytes.Rx - _last.Rx) * 8 / seconds / 1e6;
        var up = Math.Max(0, bytes.Tx - _last.Tx) * 8 / seconds / 1e6;
        SpeedNow.Text = $"↓ {down:0.0}  ↑ {up:0.0} Mbps";
        SpeedDetail.Text = $"Desde que o PC ligou: ↓ {Format.Bytes((ulong)bytes.Rx)} · ↑ {Format.Bytes((ulong)bytes.Tx)}";
        DownChart.Push(down);
        UpChart.Push(up);
        // Os dois gráficos usam a mesma escala para dar para comparar.
        _peak = Math.Max(_peak * 0.995, Math.Max(1, Math.Max(down, up)));
        DownChart.FixedMin = UpChart.FixedMin = 0;
        DownChart.FixedMax = UpChart.FixedMax = Math.Ceiling(_peak * 1.15);
        _last = bytes;
        _lastTime = now;
    }

    private async void Ping_Click(object sender, RoutedEventArgs e) => await RunPingsAsync();

    private async Task RunPingsAsync()
    {
        PingButton.IsEnabled = false;
        PingButton.Content = "Testando...";
        var gateway = NetworkService.Adapters().FirstOrDefault()?.Gateways.FirstOrDefault();
        var tasks = new List<Task<PingResult>>();
        if (gateway != null) tasks.Add(NetworkService.PingAsync("Seu roteador", gateway));
        tasks.Add(NetworkService.PingAsync("Cloudflare", "1.1.1.1"));
        tasks.Add(NetworkService.PingAsync("Google DNS", "8.8.8.8"));
        tasks.Add(NetworkService.PingAsync("google.com", "google.com"));
        PingGrid.ItemsSource = await Task.WhenAll(tasks);
        PingButton.Content = "Testar conexão";
        PingButton.IsEnabled = true;
    }
}
