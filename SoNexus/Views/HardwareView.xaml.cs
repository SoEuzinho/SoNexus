using System.Windows;
using System.Windows.Controls;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class HardwareView : UserControl
{
    private List<InfoSection> _sections = new();

    public HardwareView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        Clipboard.SetText(SystemInfoService.ToText(_sections));
        Status.Text = "Relatório copiado para a área de transferência.";
    }

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = CopyButton.IsEnabled = false;
        Status.Text = "Lendo informações do sistema...";
        var sections = Task.Run(SystemInfoService.Collect);
        var health = Task.Run(HardwareHealth.Check);
        _sections = await sections;
        Sections.ItemsSource = _sections;
        ShowHealth(await health);
        Status.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        RefreshButton.IsEnabled = CopyButton.IsEnabled = true;
    }

    private void ShowHealth(List<HealthTip> tips)
    {
        Health.Children.Clear();
        var problems = tips.Where(t => t.IsProblem).ToList();
        if (problems.Count == 0)
            Health.Children.Add(UiParts.TipCard(new HealthTip("PC", HealthLevel.Ok, "Tudo certo com as peças",
                "Não encontramos nada urgente. Veja as dicas de cada peça nas abas acima."), showPart: false));
        foreach (var t in problems) Health.Children.Add(UiParts.TipCard(t, showPart: true));
        var info = tips.Count(t => t.Level == HealthLevel.Info);
        HealthHint.Text = info > 0 ? $"Mais {info} dica(s) na aba \"Dicas\" de cada peça." : "";
    }
}
