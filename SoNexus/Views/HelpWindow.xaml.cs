using System.IO;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class HelpWindow : Window
{
    private List<ComponentInventory.Component> _components = new();

    public HelpWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream("SoNexus.Guia.txt");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                GuideText.Text = await reader.ReadToEndAsync();
            }
            _components = await Task.Run(() => ComponentInventory.Read(AppContext.BaseDirectory));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            InventoryStatus.Text = "O SoNexus não conseguiu ler o inventário desta instalação.";
            AppLog.Write($"Documentação: {ex}");
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (InventoryList != null) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text.Trim();
        var shown = query.Length == 0 ? _components : _components.Where(c =>
            c.File.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Package.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        InventoryList.ItemsSource = shown;
        InventoryStatus.Text = $"{shown.Count} de {_components.Count} DLL(s). Digite um nome, pacote ou descrição para filtrar.";
    }
}
