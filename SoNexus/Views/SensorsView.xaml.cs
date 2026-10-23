using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class SensorsView : UserControl
{
    private readonly ObservableCollection<SensorRow> _rows = new();
    private readonly Dictionary<string, SensorRow> _byId = new();
    private readonly ListCollectionView _view;

    // Mín/máx próprios, para o botão "Zerar" funcionar sem mexer na biblioteca.
    private readonly Dictionary<string, (float Min, float Max)> _range = new();

    public SensorsView()
    {
        InitializeComponent();
        _view = new ListCollectionView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Hardware)));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Hardware), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Type), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Name), ListSortDirection.Ascending));
        _view.Filter = Matches;
        SensorGrid.ItemsSource = _view;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HardwareService.Instance.Updated += OnUpdated;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HardwareService.Instance.Updated -= OnUpdated;

    // Janela escondida na bandeja: não atualiza uma lista que ninguém está vendo.
    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => { if (IsVisible) Refresh(); });

    private void Refresh()
    {
        var readings = HardwareService.Instance.Readings;
        foreach (var s in readings)
        {
            if (!_byId.TryGetValue(s.Id, out var row))
            {
                row = new SensorRow(s.Hardware, SensorFormat.TypeName(s.Type), s.Name);
                _byId[s.Id] = row;
                _rows.Add(row);
            }
            row.Value = SensorFormat.Value(s.Type, s.Value);

            if (s.Value is { } v)
            {
                var (min, max) = _range.TryGetValue(s.Id, out var r) ? r : (v, v);
                _range[s.Id] = (Math.Min(min, v), Math.Max(max, v));
                row.Min = SensorFormat.Value(s.Type, _range[s.Id].Min);
                row.Max = SensorFormat.Value(s.Type, _range[s.Id].Max);
            }
        }

        var devices = readings.Select(r => r.Hardware).Distinct().Count();
        Status.Text = HardwareService.Instance.Error is { } err
            ? $"Erro: {err}"
            : $"{readings.Count} sensores em {devices} dispositivos · atualiza a cada 1 s";
    }

    private bool Matches(object o)
    {
        var q = FilterBox.Text.Trim();
        if (q.Length == 0 || o is not SensorRow r) return true;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Type.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Hardware.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _range.Clear();
        Refresh();
    }
}
