using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class DiskCheckView : UserControl
{
    private List<DiskCheckResult> _results = new();
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public DiskCheckView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _results = await DiskCheckService.PrepareAsync();
        ResultList.ItemsSource = _results;
        Overall.Text = $"{_results.Count} disco(s) encontrado(s). Leva de 1 a 3 minutos por disco. Feche jogos e cópias de arquivo grandes para o resultado ficar fiel.";
        if (!SensorDriver.IsAdmin)
            Overall.Text += " Para ler a saúde e os setores, abra o SoNexus como administrador.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!SensorDriver.IsAdmin)
        {
            MessageBox.Show("O teste lê os setores e a saúde dos discos direto do hardware, e o Windows só permite isso para administradores.\n\nFeche o SoNexus e abra de novo como administrador.",
                "SoNexus", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cts = new CancellationTokenSource();
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = false;
        SpeedBox.IsEnabled = false;
        OverallBar.Visibility = Visibility.Visible;
        OverallBar.Value = 0;

        // Recomeça do zero (saúde relida agora, não a do carregamento da tela).
        _results = await DiskCheckService.PrepareAsync();
        ResultList.ItemsSource = _results;
        var speed = SpeedBox.IsChecked == true;
        for (var i = 0; i < _results.Count && !_cts.IsCancellationRequested; i++)
        {
            var r = _results[i];
            Overall.Text = $"Testando disco {i + 1} de {_results.Count}: {r.Title}";
            await Task.Run(() => DiskCheckService.Check(r, speed, s => Dispatcher.Invoke(() => r.Status = s), _cts.Token));
            // Listas de problemas mudaram por dentro: redesenha o cartão.
            ResultList.Items.Refresh();
            OverallBar.Value = 100.0 * (i + 1) / _results.Count;
        }

        var bad = _results.Count(r => r.Verdict == "Ruim");
        var warn = _results.Count(r => r.Verdict == "Atenção");
        Overall.Text = _cts.IsCancellationRequested ? "Teste interrompido."
            : bad > 0 ? $"⚠ {bad} disco(s) com sinais de falha. Faça backup dos seus arquivos agora."
            : warn > 0 ? $"{warn} disco(s) pedem atenção; o resto está bom."
            : "✔ Todos os discos estão bons.";
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = true;
        SpeedBox.IsEnabled = true;
        AppLog.Write("Teste geral de discos:\n" + DiskCheckService.Report(_results));
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DiskCheckService.Report(_results)); } catch { }
        Overall.Text = "Relatório copiado. Cole no WhatsApp, Discord ou num fórum de suporte.";
    }
}
