using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GameLibraryView : UserControl
{
    private List<Game> _games = new();
    private PcProfile? _pc;
    private bool _loaded;

    public GameLibraryView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        _games = await Task.Run(GamesService.ScanLibrary);
        LauncherList.ItemsSource = await Task.Run(() => GamesService.DetectLaunchers(_games));
        ApplyFilter();
        _pc = await Task.Run(GamesService.Profile);
        Status.Text = $"{_games.Count} jogos · {_pc.Gpu} · {_pc.Cpu} · monitor {_pc.Resolution} {_pc.RefreshHz} Hz. Buscando requisitos...";

        // Busca os requisitos um por um (a loja Steam limita o número de consultas); o cache deixa as próximas aberturas instantâneas.
        var done = 0;
        foreach (var game in _games.ToList())
        {
            game.Requirements = await GamesService.RequirementsAsync(game);
            GamesService.Estimate(game, _pc);
            if (game.Requirements?.Image is { } img) game.Image = img;
            done++;
            if (done % 5 == 0) Status.Text = $"{_games.Count} jogos · analisando {done}/{_games.Count}...";
        }
        var heavy = _games.Count(g => g.Order <= 1);
        Status.Text = $"{_games.Count} jogos instalados · {_games.Count(g => g.Order >= 3)} rodam bem ou liso · {heavy} pesado(s) para o seu PC · {_pc.Gpu}, monitor {_pc.Resolution}";
        if (SortBox.SelectedIndex == 1) ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (FilterHint == null) return;
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (GameList == null) return;
        var q = FilterBox.Text.Trim();
        IEnumerable<Game> shown = _games.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        shown = SortBox.SelectedIndex switch
        {
            1 => shown.OrderBy(g => g.Order).ThenBy(g => g.Name),
            2 => shown.OrderByDescending(g => g.SizeBytes),
            _ => shown.OrderBy(g => g.Name),
        };
        GameList.ItemsSource = shown.ToList();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game g }) return;
        try
        {
            if (g.LaunchUri != null) Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
            else Folder_Click(sender, e);
        }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir: {ex.Message}", "SoNexus"); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Game { InstallPath: { } path } } && Directory.Exists(path))
            Process.Start("explorer.exe", $"\"{path}\"");
    }
}
