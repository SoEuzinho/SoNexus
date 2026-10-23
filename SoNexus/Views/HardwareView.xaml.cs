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
        _sections = await Task.Run(SystemInfoService.Collect);
        Sections.ItemsSource = _sections;
        Status.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        RefreshButton.IsEnabled = CopyButton.IsEnabled = true;
    }
}
