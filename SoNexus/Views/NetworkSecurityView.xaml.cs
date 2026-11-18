using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class NetworkSecurityView : UserControl
{
    public NetworkSecurityView() => InitializeComponent();

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        AnalyzeButton.IsEnabled = false;
        VerdictText.Text = "Analisando conexões, processos e inicialização...";
        VerdictText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        AlertList.ItemsSource = null;
        try
        {
            var (connections, processes) = await Task.Run(() =>
            {
                var snapshot = NetworkService.Connections();
                return (snapshot, ProcessReviewService.InspectProcesses(snapshot));
            });
            var alerts = processes.Alerts;
            var monitor = NetworkMonitor.Instance;
            var traffic = monitor.Running ? monitor.Sample(connections) : new List<AppTraffic>();
            alerts.AddRange(NetworkService.Analyze(connections, traffic, processes.Names));
            var startupStatus = "";
            try
            {
                var startup = await ProcessReviewService.InspectStartupAsync();
                alerts.AddRange(startup.Alerts);
                startupStatus = $" · {startup.Examined} item(ns) de inicialização revisado(s)" +
                    (startup.TasksAvailable ? "" : " · tarefas agendadas indisponíveis");
            }
            catch (Exception ex)
            {
                startupStatus = " · consulta de inicialização incompleta";
                AppLog.Write($"Segurança: inicialização: {ex.Message}");
            }

            AlertList.ItemsSource = alerts.OrderBy(a => a.Order).ToList();
            var count = alerts.Count;
            var skipped = processes.Unavailable == 0 ? "" : $" · {processes.Unavailable} caminho(s) indisponível(is)";
            VerdictText.Text = $"{(count == 0 ? "Nenhum sinal pelas regras atuais" : $"{count} sinal(is) para revisar")}. " +
                $"{processes.Examined} processo(s) inspecionado(s){skipped}{startupStatus}.";
            VerdictText.Foreground = count == 0 ? Metric.Ok : Metric.Warn;
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Análise interrompida. Confira o registro do SoNexus para ver o erro.";
            VerdictText.Foreground = Metric.Warn;
            AppLog.Write($"Segurança: {ex}");
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void WindowsSecurity_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Abra Segurança do Windows pelo menu Iniciar. {ex.Message}", "SoNexus",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
