using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GameAnalysisView : UserControl
{
    private PcProfile? _pc;
    private bool _loaded;
    private bool _loadingComparison;
    private int _historyVersion;
    private List<SavedSession> _history = new();

    public GameAnalysisView()
    {
        InitializeComponent();
        // Partidas novas aparecem ao voltar para esta aba.
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowMeasured(); };
    }

    private async void ShowMeasured()
    {
        var version = ++_historyVersion;
        var sessions = await Task.Run(SessionHistory.Load);
        if (version != _historyVersion || !IsVisible) return;
        MeasuredList.ItemsSource = sessions.Take(8).ToList();
        MeasuredStatus.Text = sessions.Count == 0
            ? "Nenhuma partida registrada. Inicie a medição em Jogos › FPS ao vivo e jogue por pelo menos 30 segundos."
            : $"{sessions.Count} partida(s) registrada(s). O FPS vem do ETW. Os sinais de gargalo são inferidos da telemetria do sistema.";

        var selected = FirstSession.SelectedItem as SavedSession;
        var compared = SecondSession.SelectedItem as SavedSession;
        _history = sessions.Where(s => !string.IsNullOrWhiteSpace(s.Game)).ToList();
        _loadingComparison = true;
        FirstSession.ItemsSource = _history;
        FirstSession.SelectedItem = _history.FirstOrDefault(s => SameSession(s, selected))
            ?? _history.FirstOrDefault(s => _history.Any(other => other != s && SameGame(s, other)))
            ?? _history.FirstOrDefault();
        SelectSecond(compared);
        _loadingComparison = false;
        ShowComparison();
    }

    private static bool SameGame(SavedSession first, SavedSession second) =>
        string.Equals(first.Game, second.Game, StringComparison.OrdinalIgnoreCase);

    private static bool SameSession(SavedSession first, SavedSession? second) =>
        second != null && first.Date == second.Date && SameGame(first, second);

    private void FirstSession_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingComparison) return;
        _loadingComparison = true;
        SelectSecond(null);
        _loadingComparison = false;
        ShowComparison();
    }

    private void SelectSecond(SavedSession? preferred)
    {
        var others = FirstSession.SelectedItem is SavedSession first
            ? _history.Where(s => s != first && SameGame(s, first)).ToList()
            : new List<SavedSession>();
        SecondSession.ItemsSource = others;
        SecondSession.SelectedItem = others.FirstOrDefault(s => SameSession(s, preferred)) ?? others.FirstOrDefault();
    }

    private void Comparison_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loadingComparison) ShowComparison();
    }

    private void ShowComparison()
    {
        ComparisonControls.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FirstPlot.Data = SecondPlot.Data = null;
        ComparisonSummary.Text = TimelineScale.Text = "";
        if (FirstSession.SelectedItem is not SavedSession a || SecondSession.SelectedItem is not SavedSession b)
        {
            ComparisonStatus.Text = "Registre duas partidas do mesmo jogo para comparar.";
            return;
        }

        ComparisonStatus.Text = "Compare cenas, resolução e configurações semelhantes. Temperaturas vêm dos sensores do sistema.";
        ComparisonSummary.Text = $"A · {a.Summary}\nB · {b.Summary}  ({b.AvgFps - a.AvgFps:+0.0;-0.0;0.0} FPS médio em relação a A)";
        var first = Points(a);
        var second = Points(b);
        var all = first.Concat(second).ToList();
        if (all.Count == 0)
        {
            TimelineScale.Text = "Histórico de gráficos indisponível para a métrica escolhida. Novas partidas guardam até uma hora de amostras.";
            return;
        }

        var duration = Math.Max(1, Math.Max(a.Seconds, b.Seconds));
        var top = Math.Max(1, all.Max(p => p.Value));
        var unit = TimelineMetric.SelectedIndex switch { 1 or 2 => "°C", 3 => "engasgos", _ => "FPS" };
        TimelineScale.Text = $"Escala: 0–{top:0.#} {unit} · 0–{TimeSpan.FromSeconds(duration):h\\:mm\\:ss} de tempo medido · intervalos de até 5 s";
        FirstPlot.Data = Plot(first, duration, top);
        SecondPlot.Data = Plot(second, duration, top);
    }

    private List<(int Second, double Value)> Points(SavedSession session)
    {
        var points = new List<(int Second, double Value)>();
        foreach (var sample in session.Timeline ?? Array.Empty<SessionPoint>())
        {
            double? value = TimelineMetric.SelectedIndex switch
            {
                1 => sample.CpuTemp, 2 => sample.GpuTemp, 3 => sample.Stutters, _ => sample.MinFps,
            };
            if (sample.Second > 0 && sample.Second <= session.Seconds && value is { } v && double.IsFinite(v) && v >= 0)
                points.Add((sample.Second, v));
        }
        return points;
    }

    private static Geometry Plot(List<(int Second, double Value)> points, int duration, double top)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            int? previous = null;
            foreach (var (second, value) in points)
            {
                var point = new Point(1 + 598.0 * second / duration, 98 - 96 * value / top);
                if (previous is { } last && second - last <= 5)
                    context.LineTo(point, true, false);
                else
                {
                    context.BeginFigure(point, false, false);
                    context.LineTo(new Point(point.X + 0.5, point.Y), true, false);
                }
                previous = second;
            }
        }
        geometry.Freeze();
        return geometry;
    }

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
        GpuText.Text = pc.GpuScoreKnown ? $"Índice de referência: {pc.GpuScore / 100.0:0.0}× GTX 1060." : "Modelo sem índice na tabela.";
        GpuBar.Visibility = pc.GpuScoreKnown ? Visibility.Visible : Visibility.Collapsed;
        CpuName.Text = pc.Cpu;
        CpuBar.Value = pc.CpuScore;
        CpuText.Text = pc.CpuScoreKnown ? $"Índice de referência: {pc.CpuScore / 100.0:0.0}× Ryzen 5 3600." : "Modelo sem índice na tabela.";
        CpuBar.Visibility = pc.CpuScoreKnown ? Visibility.Visible : Visibility.Collapsed;
        MonitorText.Text = $"{pc.Resolution} · {pc.RefreshHz} Hz";
        RamText.Text = $"{Format.Bytes(pc.RamBytes)} de RAM · {pc.RamSticks} pente(s) · {pc.RamMts} MT/s · " +
                       (pc.GamesDiskType.Length > 0 ? $"jogos em {pc.GamesDiskType}" : "canal de memória não verificado");
        BottleneckList.ItemsSource = GamesService.Bottlenecks(pc);
        BottleneckStatus.Text = pc.GpuScoreKnown && pc.CpuScoreKnown ? "" : "Estimativa indisponível para um ou mais modelos deste PC.";
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
