using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class StorageView : UserControl
{
    private CancellationTokenSource? _scan;
    private bool _loaded;

    public StorageView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var selected = (DiskList.SelectedItem as DiskHealth)?.Index;
        var disks = await Task.Run(StorageService.Disks);
        DiskList.ItemsSource = disks;
        DiskList.SelectedItem = disks.FirstOrDefault(d => d.Index == selected) ?? disks.OrderByDescending(d => d.Verdict != "Saudável").FirstOrDefault();
    }

    private void Disk_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskHealth d) return;
        Detail.Visibility = Visibility.Visible;
        VerdictTitle.Text = d.Verdict switch
        {
            "Crítico" => "Crítico: faça backup agora",
            "Atenção" => "Atenção",
            "Sem SMART" => "Saudável pelo Windows (sem dados SMART)",
            _ => "Saudável",
        };
        VerdictTitle.Foreground = d.Verdict == "Crítico" ? Metric.Danger : (System.Windows.Media.Brush)FindResource("TextBrush");
        IssueList.ItemsSource = d.Issues.Count > 0 ? d.Issues : new List<string> { "Nenhum setor defeituoso, erro de leitura ou alerta no SMART." };

        LifeText.Text = d.LifeUsedPercent is { } u ? $"{Math.Max(0, 100 - u)}%" : "—";
        TempText.Text = d.Temperature is { } t ? $"{t} °C" : "—";
        HoursText.Text = d.PowerOnHours is { } h ? (h >= 48 ? $"{h / 24.0:0} dias" : $"{h} h") : "—";
        WrittenText.Text = d.WrittenTB is { } w ? (w >= 1 ? $"{w:0.0} TB" : $"{w * 1000:0} GB") : "—";

        var info = new List<InfoItem>
        {
            new("Tipo", d.Type),
            new("Capacidade", Format.Bytes(d.Size)),
            new("Saúde pelo Windows", d.WindowsHealth),
            new("Firmware", d.Firmware.Length > 0 ? d.Firmware : "—"),
            new("Número de série", d.Serial.Length > 0 ? d.Serial : "—"),
            new("Disco nº", $"{d.Index}"),
        };
        if (d.PowerCycles is { } pc) info.Add(new InfoItem("Vezes ligado", $"{pc:N0}"));
        if (d.ReadTB is { } rd) info.Add(new InfoItem("Total lido", $"{rd:0.00} TB"));
        if (d.Reallocated is { } re) info.Add(new InfoItem("Setores realocados", $"{re}"));
        if (d.Pending is { } pe) info.Add(new InfoItem("Setores pendentes", $"{pe}"));
        if (d.CrcErrors is { } crc) info.Add(new InfoItem("Erros de cabo (CRC)", $"{crc}"));
        foreach (var v in d.Volumes) info.Add(new InfoItem("Partição", v));
        InfoList.ItemsSource = info;
        SmartGrid.ItemsSource = d.Smart;

        BadGrid.Visibility = Visibility.Collapsed;
        ScanProgressBar.Value = 0;
        ScanStatus.Text = "";
    }

    private async void QuickScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(full: false);
    private async void FullScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(full: true);
    private void Stop_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();

    private async Task ScanAsync(bool full)
    {
        if (DiskList.SelectedItem is not DiskHealth d) return;
        if (full && d.IsHdd && MessageBox.Show(
                $"A varredura completa de um HD de {Format.Bytes(d.Size)} pode levar algumas horas. O PC pode ser usado durante, mas fica mais lento. Continuar?",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _scan = new CancellationTokenSource();
        ScanBar.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        DiskList.IsEnabled = false;
        BadGrid.Visibility = Visibility.Collapsed;
        ScanStatus.Text = "Iniciando...";

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressBar.Value = p.Percent;
            ScanStatus.Text = $"{p.Percent:0.0}% · {p.Position} · {p.MBps:0} MB/s · {p.Errors} ilegível(is) · {p.Slow} lento(s) · faltam {p.Eta:hh\\:mm\\:ss}";
        });
        try
        {
            var bad = await Task.Run(() => StorageService.Scan(d.Index, d.Size, full, d.IsHdd, progress, _scan.Token));
            var unreadable = bad.Count(b => b.Kind == "Ilegível");
            var slow = bad.Count - unreadable;
            ScanStatus.Text = (_scan.IsCancellationRequested ? "Parado. " : "Concluído. ") + (unreadable, slow) switch
            {
                (> 0, _) => $"{unreadable} setor(es) ILEGÍVEL(IS). O disco está falhando: faça backup e troque.",
                (0, > 0) => $"Nenhum setor ilegível. {slow} trecho(s) lento(s): normal com o PC em uso; em HD, muitos lentos indicam desgaste.",
                _ => "Nenhum setor defeituoso ou lento encontrado.",
            };
            ScanStatus.Foreground = unreadable > 0 ? Metric.Danger : (System.Windows.Media.Brush)FindResource("MutedBrush");
            BadGrid.ItemsSource = bad;
            BadGrid.Visibility = bad.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Não foi possível ler o disco: {ex.Message}";
        }
        ScanBar.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        DiskList.IsEnabled = true;
    }
}
