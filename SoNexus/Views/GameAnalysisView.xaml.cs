using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GameAnalysisView : UserControl
{
    private PcProfile? _pc;
    private bool _loaded;

    public GameAnalysisView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _pc = await Task.Run(GamesService.Profile);
        Show();
        var problems = await Task.Run(() => GamesService.Problems(GamesService.ScanLibrary()));
        ProblemList.ItemsSource = problems;
        ProblemStatus.Text = problems.Count == 0 ? "Nenhum problema encontrado. ✔" : "";
    }

    private void Show()
    {
        var pc = _pc!;
        GpuName.Text = pc.Gpu;
        GpuBar.Value = pc.GpuScore;
        GpuText.Text = $"{pc.GpuScore / 100.0:0.0}x uma GTX 1060. " + pc.GpuScore switch
        {
            >= 400 => "Topo de linha: 4K e ray tracing.",
            >= 300 => "Muito forte: 1440p no Ultra com folga, 4K com DLSS/FSR.",
            >= 200 => "Forte: 1440p no Alto, 1080p no Ultra.",
            >= 130 => "Boa para 1080p no Alto.",
            >= 80 => "Entrada: 1080p no Médio/Baixo.",
            _ => "Fraca para jogos atuais: 720p ou 1080p no Baixo.",
        };
        CpuName.Text = pc.Cpu;
        CpuBar.Value = pc.CpuScore;
        CpuText.Text = $"{pc.CpuScore / 100.0:0.0}x um Ryzen 5 3600 em jogos.";
        MonitorText.Text = $"{pc.Resolution} · {pc.RefreshHz} Hz";
        RamText.Text = $"{Format.Bytes(pc.RamBytes)} de RAM · {pc.RamSticks} pente(s) · {pc.RamMts} MT/s · " +
                       (pc.RamSticks >= 2 ? "dual channel" : "canal único") +
                       (pc.GamesDiskType.Length > 0 ? $" · jogos em {pc.GamesDiskType}" : "");
        BottleneckList.ItemsSource = GamesService.Bottlenecks(pc);
        var tips = GamesService.Tips(pc);
        TipList.ItemsSource = tips;
        TipStatus.Text = tips.Count == 0 ? "Nada segurando o seu PC. Tudo no lugar." : "";
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PcTip { Fix: { } fix } }) return;
        try
        {
            TipStatus.Text = await fix() ?? "";
        }
        catch (Exception ex)
        {
            TipStatus.Text = $"Não foi possível: {ex.Message}";
        }
        if (_pc != null) TipList.ItemsSource = GamesService.Tips(_pc);
    }
}
