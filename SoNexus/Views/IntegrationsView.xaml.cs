using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Um cartão: o que o SoNexus conversa no seu PC e se está funcionando.</summary>
public sealed record IntegrationCard(string Icon, string Name, string Detail, string Status, bool Ok, string Action, string Nav, string? Tab)
{
    public Brush StatusBrush => Ok ? Metric.Ok : (Brush)Application.Current.FindResource("MutedBrush");
}

public partial class IntegrationsView : UserControl
{
    private bool _loaded;

    public IntegrationsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        Summary.Text = "Procurando o que está instalado no seu PC...";
        var cards = await Task.Run(Detect);
        Cards.ItemsSource = cards;
        var ok = cards.Count(c => c.Ok);
        Summary.Text = $"{ok} de {cards.Count} integrações ativas. O SoNexus conversa com estes programas e peças para controlar tudo num lugar só.";
    }

    /// <summary>Tudo detectado no próprio PC (nada de internet), em segundo plano.</summary>
    private static List<IntegrationCard> Detect()
    {
        var cards = new List<IntegrationCard>();
        var programs = Safe(() => HiddenItemsService.GetPrograms().Select(p => p.Name).ToList()) ?? new();
        bool Has(string name) => programs.Any(p => p.Contains(name, StringComparison.OrdinalIgnoreCase));

        // Iluminação
        var rgb = RgbService.Instance;
        cards.Add(rgb.Connected
            ? new("", "Iluminação RGB", $"OpenRGB conectado: {rgb.Devices.Count} dispositivo(s), {rgb.Devices.Sum(d => d.Leds)} LEDs. Efeitos, cores e modos da placa.", "Conectado", true, "Abrir iluminação", "NavIntegration", "Iluminação RGB")
            : new("", "Iluminação RGB", Has("OpenRGB") ? "OpenRGB instalado. Abra-o como administrador e ligue o SDK Server." : "Controle os LEDs de placa-mãe, RAM, placa de vídeo e ventoinhas com o OpenRGB (gratuito).",
                Has("OpenRGB") ? "Instalado" : "Não instalado", false, "Abrir iluminação", "NavIntegration", "Iluminação RGB"));

        // Sensores
        cards.Add(SensorDriver.IsInstalled && SensorDriver.IsAdmin
            ? new("", "Sensores do processador", "Driver PawnIO ativo: temperatura, consumo e tensões da CPU e da placa-mãe.", "Ativo", true, "Ver sensores", "NavHardware", "Sensores")
            : new("", "Sensores do processador", SensorDriver.IsAdmin ? "Falta o driver PawnIO (assinado) para ler a temperatura da CPU. Instale pela tela Início." : "Abra o SoNexus como administrador para ler todos os sensores.",
                "Incompleto", false, "Ver sensores", "NavHardware", "Sensores"));

        // Placa de vídeo
        var gpus = Safe(DriverService.GetGpuDrivers) ?? new();
        var main = gpus.FirstOrDefault(g => g.Vendor == "NVIDIA") ?? gpus.FirstOrDefault();
        cards.Add(main?.Vendor == "NVIDIA"
            ? new("", "Placa de vídeo NVIDIA", $"{main.Name}: monitoramento, driver com 1 clique, overclock e ventoinhas.", "Completo", true, "Overclock e ventoinhas", "NavGames", "Overclock")
            : new("", "Placa de vídeo", main != null ? $"{main.Name}: monitoramento, benchmark e driver. Overclock só em placas NVIDIA." : "Nenhuma placa de vídeo detectada.",
                main != null ? "Monitorando" : "—", main != null, "Driver da placa", "NavDrivers", "Placa de vídeo"));

        // Jogos
        var games = Safe(GamesService.ScanLibrary) ?? new();
        var launchers = Safe(() => GamesService.DetectLaunchers(games)) ?? new();
        cards.Add(new("", "Jogos e launchers", games.Count > 0
                ? $"{games.Count} jogo(s) em {launchers.Count} launcher(s) ({string.Join(", ", launchers.Select(l => l.Name).Take(4))}). FPS estimado e gargalo."
                : "Steam, Epic, GOG, Ubisoft, EA, Battle.net, Riot e Xbox: os jogos aparecem sozinhos.",
            games.Count > 0 ? $"{games.Count} jogos" : "Nenhum jogo", games.Count > 0, "Abrir biblioteca", "NavGames", "Biblioteca"));

        // Áudio
        var voicemeeter = Has("Voicemeeter");
        cards.Add(new("", "Áudio e Voicemeeter", voicemeeter ? "Voicemeeter instalado: volumes e mixer direto do SoNexus." : "Dispositivos de som, drivers e programas de áudio (Voicemeeter, Equalizer APO...).",
            voicemeeter ? "Voicemeeter" : "Dispositivos", true, "Abrir áudio", "NavAudio", null));

        // Realidade virtual
        var vr = Safe(VrService.DetectApps) ?? new();
        cards.Add(new("", "Realidade virtual", vr.Count > 0 ? $"{string.Join(", ", vr.Select(a => a.Name))}: status dos óculos, Wi-Fi para VR e Modo VR." : "Virtual Desktop, SteamVR, Meta Quest Link, ALVR e Pico.",
            vr.Count > 0 ? $"{vr.Count} programa(s)" : "Nenhum", vr.Count > 0, "Abrir VR", "NavGames", "Realidade virtual"));

        // Windows
        cards.Add(new("", "Windows", "Atalhos para as configurações escondidas do Windows: Modo de Jogo, gráficos por jogo, energia e mais.",
            "Disponível", true, "Atalhos do Windows", "NavIntegration", "Atalhos do Windows"));
        return cards;
    }

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (Exception ex) { AppLog.Write($"Integrações: {ex.Message}"); return null; }
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IntegrationCard c } && Window.GetWindow(this) is MainWindow w) w.Navigate(c.Nav, c.Tab);
    }
}
