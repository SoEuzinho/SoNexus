using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class VrView : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loaded;
    private string? _headsetIp;
    private string? _routerIp;

    public VrView()
    {
        InitializeComponent();
        _live.Tick += async (_, _) => await RefreshLiveAsync();
        Unloaded += (_, _) => _live.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live.Start();
        await RefreshLiveAsync();
        if (_loaded) return;
        _loaded = true;

        var apps = await Task.Run(VrService.DetectApps);
        AppList.ItemsSource = apps;
        NoApps.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadRuntimes();

        // Prontidão para streaming: rede do PC, roteador e codificador da placa.
        var pc = await Task.Run(GamesService.Profile);
        var adapter = NetworkService.Adapters().FirstOrDefault();
        var checks = new List<InfoItem>();
        if (adapter != null)
        {
            var wired = adapter.Type.StartsWith("Cabo");
            checks.Add(new InfoItem(wired ? "✔ PC no cabo" : "✖ PC no Wi-Fi",
                wired ? $"Ótimo: {adapter.SpeedText} até o roteador. Deixe só os óculos no Wi-Fi."
                      : "Para VR sem fio, ligue o PC ao roteador por cabo. PC e óculos no Wi-Fi dividem a banda e causam travadas."));
            if (adapter.Gateways.FirstOrDefault() is { } gw)
            {
                _routerIp = gw;
                var ping = await NetworkService.PingAsync("roteador", gw, 6);
                checks.Add(new InfoItem(ping.AvgMs is < 3 && ping.LossPercent == 0 ? "✔ Roteador respondendo rápido" : "• Roteador",
                    $"Ping {ping.AvgText}, jitter {ping.JitterText}, perda {ping.LossText}. Os óculos devem ficar em Wi-Fi 6/6E de 5 ou 6 GHz, na mesma sala do roteador."));
            }
        }
        var (codec, codecDetail) = VrService.Encoder(pc.Gpu);
        checks.Add(new InfoItem($"✔ Codec recomendado: {codec}", codecDetail));
        checks.Add(new InfoItem(pc.GpuScore >= 300 ? "✔ Placa de vídeo forte para VR" : pc.GpuScore >= 170 ? "• Placa de vídeo boa para VR" : "✖ Placa de vídeo no limite para VR",
            pc.GpuScore >= 300
                ? "Dá para usar qualidade Ultra/Godlike no Virtual Desktop em 90 Hz na maioria dos jogos."
                : pc.GpuScore >= 170 ? "Use qualidade Alta em 72-90 Hz; nos jogos pesados, ative o SSW." : "Use qualidade Média em 72 Hz e reduza a resolução nos jogos."));
        if (pc.RamSticks == 1)
            checks.Add(new InfoItem("• Memória em canal único", "VR é sensível a engasgos; dois pentes de RAM deixam o tempo de quadro mais estável."));
        CheckList.ItemsSource = checks;

        await LoadVrGamesAsync(pc);
    }

    private void LoadRuntimes()
    {
        var runtimes = VrService.Runtimes();
        RuntimeBox.ItemsSource = runtimes;
        RuntimeBox.SelectedItem = runtimes.FirstOrDefault(r => r.Active);
        if (runtimes.Count == 0) RuntimeStatus.Text = "Nenhum runtime OpenXR instalado.";
    }

    private async Task LoadVrGamesAsync(PcProfile pc)
    {
        var games = await Task.Run(GamesService.ScanLibrary);
        var vr = new List<Game>();
        foreach (var g in games.Where(g => g.SteamId != null))
        {
            g.Requirements = await GamesService.RequirementsAsync(g);
            if (g.Requirements?.Vr != true) continue;
            GamesService.Estimate(g, pc);
            if (!g.Requirements.VrOnly)
            {
                // Jogo de monitor com modo VR: converte a estimativa para a resolução dos óculos.
                var fps = VrService.VrFps(g.GpuFps1080, g.CpuFps);
                g.Estimate = $"~{Math.Min(fps, 144):0} FPS em VR";
                g.Detail = fps switch
                {
                    >= 90 => "Roda em 90 Hz com folga.",
                    >= 72 => "Use 72-80 Hz para ficar liso.",
                    >= 45 => "Pesado: use 72 Hz com SSW/ASW (reprojeção) e qualidade menor.",
                    _ => "Muito pesado para VR nesta placa.",
                } + " (estimado para Quest 3 em resolução padrão)";
            }
            vr.Add(g);
        }
        VrGameList.ItemsSource = vr.OrderByDescending(v => v.Order).ToList();
        VrGamesStatus.Text = vr.Count == 0 ? "Nenhum jogo com suporte a VR encontrado na biblioteca da Steam." : $"{vr.Count} jogo(s) com suporte a VR.";
    }

    private async Task RefreshLiveAsync()
    {
        var (running, headset) = await Task.Run(() => (VrService.RunningVrSoftware(), VrService.HeadsetConnection()));
        _headsetIp = headset?.Ip;
        if (headset is { } h)
        {
            LiveTitle.Text = $"✔ Óculos conectado pelo {h.App}";
            LiveDetail.Text = $"Endereço do óculos na rede: {h.Ip}. Rodando: {string.Join(", ", running)}.";
        }
        else if (running.Count > 0)
        {
            LiveTitle.Text = "Programa de VR aberto, sem óculos conectado";
            LiveDetail.Text = $"Rodando: {string.Join(", ", running)}. Abra o app no óculos e conecte ao PC.";
        }
        else
        {
            LiveTitle.Text = "Nenhum programa de VR aberto";
            LiveDetail.Text = "Abra o Virtual Desktop Streamer, o Meta Quest Link ou o SteamVR.";
        }
    }

    private async void Stability_Click(object sender, RoutedEventArgs e)
    {
        var target = _headsetIp ?? _routerIp;
        if (target == null)
        {
            StabilityText.Text = "Não encontramos o roteador nem os óculos na rede.";
            return;
        }
        StabilityButton.IsEnabled = false;
        StabilityBar.Visibility = Visibility.Visible;
        StabilityText.Text = _headsetIp != null ? $"Medindo até os óculos ({target})..." : $"Medindo até o roteador ({target})... (conecte os óculos para medir até eles)";
        var s = await VrService.StabilityTestAsync(target, 20, p => Dispatcher.Invoke(() => StabilityBar.Value = p), CancellationToken.None);
        StabilityText.Text = $"{s.Verdict}\nMédia {s.AvgMs:0} ms · 99% abaixo de {s.P99Ms:0} ms · pior {s.MaxMs:0} ms · {s.Spikes} pico(s) · perda {s.LossPercent}% ({s.Samples} medições)" +
                             (_headsetIp == null ? "\nObs.: até o roteador. Com os óculos conectados o teste mede o caminho real do vídeo." : "");
        StabilityBar.Visibility = Visibility.Collapsed;
        StabilityButton.IsEnabled = true;
    }

    private void SetRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeBox.SelectedItem is not OpenXrRuntime r) return;
        var error = VrService.SetActiveRuntime(r);
        RuntimeStatus.Text = error is null ? $"✔ {r.Name} agora roda os jogos OpenXR." : $"✖ {error} (execute como administrador)";
        LoadRuntimes();
    }

    private async void VrOn_Click(object sender, RoutedEventArgs e)
    {
        VrOnButton.IsEnabled = false;
        VrLog.Text = "Preparando...";
        VrLog.Text = string.Join("\n", await VrService.EnableVrModeAsync());
        VrOnButton.IsEnabled = true;
    }

    private async void VrOff_Click(object sender, RoutedEventArgs e) => VrLog.Text = await VrService.DisableVrModeAsync();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VrApp { Launch: { } target } }) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir: {ex.Message}", "SoNexus"); }
    }

    private void PlayGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game { LaunchUri: { } uri } }) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir o jogo (o launcher está instalado?): {ex.Message}", "SoNexus"); }
    }
}
