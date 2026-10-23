using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class DiagnosticsView : UserControl
{
    private bool _loaded;
    private bool _toolRunning;

    public sealed record Card(string Value, string Title, Brush Brush);

    public DiagnosticsView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await AnalyzeAsync();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        var days = int.Parse((string)((ComboBoxItem)PeriodBox.SelectedItem).Tag);
        AnalyzeBar.IsEnabled = false;
        VerdictText.Text = "Lendo os registros do Windows...";
        VerdictText.Foreground = (Brush)FindResource("TextBrush");

        var r = await Task.Run(() => DiagnosticsService.Analyze(days));

        Cards.ItemsSource = new[]
        {
            Count(r.Bluescreens, "Telas azuis", 1),
            Count(r.Shutdowns, "Desligamentos inesperados", 1),
            Count(r.Whea, "Erros de hardware (WHEA)", 1),
            Count(r.GpuResets, "Falhas do driver de vídeo", 1),
            Count(r.AppCrashes.Sum(a => a.Count), "Programas que travaram", 10),
            Count(r.Devices.Count, "Dispositivos com problema", 1),
        };
        CrashGrid.ItemsSource = r.Crashes;
        HardwareGrid.ItemsSource = r.Hardware;
        AppGrid.ItemsSource = r.AppCrashes;
        DeviceGrid.ItemsSource = r.Devices;
        SystemGrid.ItemsSource = r.SystemErrors;

        var period = $"nos últimos {days} dias";
        var (text, color) = (r.Bluescreens, r.Whea, r.Shutdowns, r.GpuResets) switch
        {
            ( > 0, _, _, _) => ($"{r.Bluescreens} tela(s) azul(is) {period}. Veja a causa provável na primeira aba.", Metric.Danger),
            (_, > 0, _, _) => ($"{r.Whea} erro(s) de hardware {period}: sinal de overclock/undervolt instável ou peça com defeito. Rode o Stress test e o Teste de RAM.", Metric.Danger),
            (_, _, > 0, _) => ($"{r.Shutdowns} desligamento(s) inesperado(s) {period}. Pode ser energia, fonte ou superaquecimento.", Metric.Warn),
            (_, _, _, > 0) => ($"O driver de vídeo falhou {r.GpuResets} vez(es) {period}. Considere reinstalar o driver da placa de vídeo.", Metric.Warn),
            _ when r.Devices.Count > 0 => ($"Nenhuma falha grave {period}, mas há {r.Devices.Count} dispositivo(s) com problema.", Metric.Warn),
            _ => ($"Tudo certo: nenhuma tela azul, erro de hardware ou falha de vídeo {period}.", Metric.Ok),
        };
        VerdictText.Text = text;
        VerdictText.Foreground = color;
        Verdict.BorderBrush = color;
        AnalyzeBar.IsEnabled = true;
    }

    private static Card Count(int n, string title, int warnAt) =>
        new($"{n}", title, n == 0 ? Metric.Ok : n >= warnAt ? Metric.Danger : Metric.Warn);

    // ---------- Ferramentas ----------

    private async void Sfc_Click(object sender, RoutedEventArgs e) => await RunToolAsync("sfc.exe", "/scannow");
    private async void Dism_Click(object sender, RoutedEventArgs e) => await RunToolAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth");
    private async void Chkdsk_Click(object sender, RoutedEventArgs e) => await RunToolAsync("chkdsk.exe", "C: /scan");

    private async Task RunToolAsync(string exe, string args)
    {
        if (_toolRunning) return;
        _toolRunning = true;
        ToolBar.IsEnabled = false;
        ToolLog.AppendText($"\n> {exe} {args}\n");
        ToolProgress.Text = "Executando... não feche o SoNexus.";
        try
        {
            var code = await DiagnosticsService.RunToolAsync(exe, args, (line, isProgress) => Dispatcher.Invoke(() =>
            {
                if (isProgress && line.Contains('%'))
                {
                    ToolProgress.Text = line;
                }
                else
                {
                    ToolLog.AppendText(line + "\n");
                    ToolLog.ScrollToEnd();
                }
            }));
            ToolProgress.Text = code == 0 ? "Concluído." : $"Terminou com código {code}. Veja o registro abaixo.";
        }
        catch (Exception ex)
        {
            ToolProgress.Text = $"Erro: {ex.Message}";
        }
        ToolBar.IsEnabled = true;
        _toolRunning = false;
    }
}
