using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Services;
using HW = LibreHardwareMonitor.Hardware;

namespace SoNexus.Views;

public partial class EnergyView : UserControl
{
    public sealed record SplitRow(string Name, string Value, double Percent);

    private double _restW = 40;
    private double _sessionWh;
    private double _sum;
    private int _samples;
    private long _last = Stopwatch.GetTimestamp();
    private bool _loaded;

    public EnergyView()
    {
        InitializeComponent();
        GpuChart.Stroke = (Brush)Application.Current.FindResource("MutedBrush");
        CpuChart.Stroke = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));
        var s = AppSettings.Current;
        TariffBox.Text = s.TariffPerKwh.ToString("0.00", CultureInfo.CurrentCulture);
        HoursBox.Text = s.HoursPerDay.ToString("0.#", CultureInfo.CurrentCulture);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HardwareService.Instance.Updated += OnUpdated;
        _last = Stopwatch.GetTimestamp();
        if (_loaded) return;
        _loaded = true;
        _restW = await Task.Run(EnergyService.EstimateRestW);
        var budget = await Task.Run(() => EnergyService.Budget(_restW));
        PsuText.Text = $"{budget.RecommendedPsuW} W";
        PsuDetail.Text = $"Pico do processador ≈ {budget.CpuMaxW:0} W + placa de vídeo até {budget.GpuMaxW:0} W + resto ≈ {budget.RestW:0} W, com 20% de folga. " +
                         "Prefira uma fonte 80 Plus Gold ou melhor, de marca conhecida.";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HardwareService.Instance.Updated -= OnUpdated;

    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Refresh);

    private void Refresh()
    {
        var r = HardwareService.Instance.Readings;
        if (r.Count == 0) return;

        var cpu = HardwareService.CpuNow(r).Power;
        var gpu = HardwareService.GpuNow(r).Power;
        // Vídeo integrado do Ryzen: já faz parte do consumo do processador, só é mostrado à parte.
        var hasNvidia = r.Any(x => x.HardwareType == HW.HardwareType.GpuNvidia);
        var igpu = hasNvidia
            ? r.Where(x => x.HardwareType == HW.HardwareType.GpuAmd && x.Type == HW.SensorType.Power).Sum(x => x.Value ?? 0)
            : 0;
        var total = (cpu ?? 0) + (gpu ?? 0) + _restW;
        var wall = total / 0.9; // fonte ~90% eficiente

        var now = Stopwatch.GetTimestamp();
        var hours = (now - _last) / (double)Stopwatch.Frequency / 3600;
        _last = now;
        _sessionWh += wall * hours;
        _sum += wall;
        _samples++;

        TotalText.Text = $"{total:0} W";
        TotalDetail.Text = (cpu is null ? "Consumo do processador indisponível (execute como administrador). " : "")
                           + $"≈ {wall:0} W puxados da tomada (fonte ~90% eficiente).";

        var tariff = AppSettings.Current.TariffPerKwh;
        SessionText.Text = _sessionWh >= 1000 ? $"{_sessionWh / 1000:0.00} kWh" : $"{_sessionWh:0} Wh";
        SessionDetail.Text = $"R$ {_sessionWh / 1000 * tariff:0.00} até agora · R$ {wall / 1000 * tariff:0.00} por hora no ritmo atual";

        var avg = _sum / _samples;
        var month = avg / 1000 * AppSettings.Current.HoursPerDay * 30;
        MonthText.Text = $"R$ {month * tariff:0.00}";

        TotalChart.Push(total);
        GpuChart.Push(gpu ?? 0);
        CpuChart.Push(cpu ?? 0);
        var peak = Math.Max(100, Math.Ceiling(total * 1.2 / 50) * 50);
        foreach (var c in new[] { TotalChart, GpuChart, CpuChart })
        {
            c.FixedMin = 0;
            c.FixedMax = Math.Max(c.FixedMax ?? 0, peak);
        }

        var max = Math.Max(1, total);
        SplitList.ItemsSource = new[]
        {
            new SplitRow("Processador", cpu is { } c1 ? $"{c1:0} W" : "—", (cpu ?? 0) / max * 100),
            new SplitRow("Placa de vídeo", gpu is { } g1 ? $"{g1:0} W" : "—", (gpu ?? 0) / max * 100),
            new SplitRow("   vídeo integrado (no processador)", $"{igpu:0} W", igpu / max * 100),
            new SplitRow("Resto (estimado)", $"{_restW:0} W", _restW / max * 100),
        }.Concat(r.Where(x => x.Type == HW.SensorType.Power && x.Value > 0 && !x.Name.Contains('#')
                              && x.Name is not ("CPU Package" or "Package" or "GPU Package" or "GPU Power"))
                  .Select(x => new SplitRow($"   {x.Name} ({x.Hardware.Split(' ').Last()})", $"{x.Value:0.0} W", (x.Value ?? 0) / max * 100)))
         .ToList();

        var volts = EnergyService.Voltages(r);
        VoltageGrid.ItemsSource = volts;
        var bad = volts.Where(v => v.Bad).ToList();
        VoltageSummary.Text = volts.Count == 0
            ? "Nenhuma tensão disponível. As tensões da placa-mãe precisam do SoNexus como administrador."
            : bad.Count == 0
                ? $"{volts.Count} tensões lidas. As linhas da fonte e a bateria da BIOS estão dentro da faixa normal."
                : $"Atenção: {string.Join(", ", bad.Select(b => b.Name))} fora da faixa. Veja a coluna Situação.";
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        if (double.TryParse(TariffBox.Text.Replace('.', ','), NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var t) && t is > 0 and < 10)
            s.TariffPerKwh = t;
        if (double.TryParse(HoursBox.Text.Replace('.', ','), NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var h) && h is > 0 and <= 24)
            s.HoursPerDay = h;
        s.Save();
        TariffBox.Text = s.TariffPerKwh.ToString("0.00", CultureInfo.CurrentCulture);
        HoursBox.Text = s.HoursPerDay.ToString("0.#", CultureInfo.CurrentCulture);
    }
}
