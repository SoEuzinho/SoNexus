using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkSecurityView : UserControl
{
    private bool _loaded;

    public NetworkSecurityView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await AnalyzeAsync();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        AnalyzeButton.IsEnabled = false;
        VerdictText.Text = "Analisando conexões e programas...";
        VerdictText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

        NetworkMonitor.Instance.Start();
        var alerts = await Task.Run(() =>
        {
            var connections = NetworkService.Connections();
            var traffic = NetworkMonitor.Instance.Sample(connections);
            return NetworkService.Analyze(connections, traffic);
        });
        AlertList.ItemsSource = alerts;

        var high = alerts.Count(a => a.Severity == "Alta");
        var medium = alerts.Count(a => a.Severity == "Média");
        (VerdictText.Text, VerdictText.Foreground) = (high, medium) switch
        {
            ( > 0, _) => ($"{high} alerta(s) importante(s). Confira abaixo e passe o antivírus (Windows Defender: verificação completa).", Metric.Danger),
            (_, > 0) => ($"{medium} ponto(s) para conferir. Pode ser normal, mas veja se você reconhece cada programa.", Metric.Warn),
            _ => ("Nenhum sinal suspeito agora. " + (alerts.Count > 0 ? $"{alerts.Count} observação(ões) de baixo risco abaixo." : ""), Metric.Ok),
        };
        AnalyzeButton.IsEnabled = true;
    }
}
