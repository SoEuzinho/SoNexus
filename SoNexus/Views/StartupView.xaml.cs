using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class StartupView : UserControl
{
    private readonly ObservableCollection<StartupEntry> _entries = new();
    private readonly ListCollectionView _view;
    private bool _loaded;

    public StartupView()
    {
        InitializeComponent();
        _view = new ListCollectionView(_entries) { Filter = Matches };
        _view.SortDescriptions.Add(new SortDescription(nameof(StartupEntry.Kind), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(StartupEntry.Name), ListSortDirection.Ascending));
        EntryGrid.ItemsSource = _view;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        Status.Text = "Lendo itens de inicialização (tarefas agendadas podem levar alguns segundos)...";
        try
        {
            var list = await StartupService.LoadAsync();
            _entries.Clear();
            foreach (var entry in list) _entries.Add(entry);
            _loaded = true;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Status.Text = $"Erro: {ex.Message}";
        }
        RefreshButton.IsEnabled = true;
    }

    private void UpdateStatus()
    {
        var shown = _view.Count;
        var active = _view.Cast<StartupEntry>().Count(x => x.Enabled);
        var missing = _view.Cast<StartupEntry>().Count(x => !x.FileExists);
        Status.Text = $"{shown} itens exibidos · {active} ativos" + (missing > 0 ? $" · {missing} com arquivo ausente" : "");
    }

    private bool Matches(object o)
    {
        if (o is not StartupEntry e) return false;
        if (ShowMicrosoft.IsChecked != true && e.IsMicrosoft) return false;
        if (KindBox.SelectedIndex > 0 && (int)e.Kind != KindBox.SelectedIndex - 1) return false;
        var q = FilterBox.Text.Trim();
        return q.Length == 0
            || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Command.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        if (_loaded) UpdateStatus();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not StartupEntry entry) return;
        var want = cb.IsChecked == true;
        var error = await StartupService.SetEnabledAsync(entry, want);
        if (error is null)
        {
            entry.Enabled = want;
            UpdateStatus();
        }
        else
        {
            entry.ResyncEnabled();
            MessageBox.Show($"Não foi possível alterar \"{entry.Name}\".\n\n{error}", "SoNexus",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is not StartupEntry { ImagePath: { } path }) return;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
        else
            MessageBox.Show("O arquivo deste item não existe mais.", "SoNexus");
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is not StartupEntry entry) return;
        var term = entry.ImagePath is { } p ? Path.GetFileName(p) : entry.Name;
        Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(term + " inicialização")}")
        {
            UseShellExecute = true,
        });
    }
}
