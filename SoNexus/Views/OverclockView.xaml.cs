using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class OverclockView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Nvml.Gpu? _gpu;
    private NvLimits? _limits;
    private bool _loaded;

    public OverclockView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshGpu();
        FanControlService.Instance.Ready += (_, _) => Dispatcher.BeginInvoke(ShowFans);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        ShowFans();
        if (_loaded) return;
        _loaded = true;
        LoadGpu();
        await LoadPlansAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    // ---------- GPU ----------

    private void Rules_Click(object sender, RoutedEventArgs e)
    {
        var accepted = RulesBox.IsChecked == true;
        AppSettings.Current.OverclockRulesAccepted = accepted;
        AppSettings.Current.Save();
        OcPanel.IsEnabled = accepted && _gpu != null;
        if (_gpu != null) OcStatus.Text = accepted ? "Controles liberados. Suba aos poucos e teste a cada passo." : "Controles de overclock bloqueados.";
    }

    private void LoadGpu()
    {
        var gpus = Nvml.Init(out var error);
        _gpu = gpus.FirstOrDefault();
        if (_gpu == null)
        {
            GpuName.Text = "Placa de vídeo";
            OcPanel.IsEnabled = false;
            OcStatus.Text = (error ?? "Nenhuma placa NVIDIA encontrada.") +
                            " Por enquanto o overclock funciona só em placas NVIDIA; para AMD use o AMD Software.";
            return;
        }

        GpuName.Text = _gpu.Name;
        RulesBox.IsChecked = AppSettings.Current.OverclockRulesAccepted;
        OcPanel.IsEnabled = AppSettings.Current.OverclockRulesAccepted;
        _limits = Nvml.Limits(_gpu);
        var l = _limits;

        PowerSlider.Minimum = Math.Round(l.PowerMinW);
        PowerSlider.Maximum = Math.Round(l.PowerMaxW);
        PowerSlider.Value = Math.Round(l.PowerCurrentW);
        PowerSlider.IsEnabled = l.PowerMaxW > l.PowerMinW;

        CoreSlider.Minimum = l.CoreMin;
        CoreSlider.Maximum = l.CoreMax;
        CoreSlider.Value = l.CoreCurrent;
        CoreSlider.IsEnabled = l.CoreSupported;

        MemSlider.Minimum = l.MemMin;
        MemSlider.Maximum = l.MemMax;
        MemSlider.Value = l.MemCurrent;
        MemSlider.IsEnabled = l.MemSupported;

        UpdateLabels();
        OcStatus.Text = (l.CoreSupported ? "" : "Offset de clock não suportado por este driver. ") +
                        (OcPanel.IsEnabled ? "Suba aos poucos (+25 MHz no núcleo, +100 MHz na memória) e teste a cada passo."
                                           : "Leia as regras de segurança acima para liberar os controles.");
        RefreshGpu();
    }

    private void RefreshGpu()
    {
        if (_gpu == null) return;
        try
        {
            var s = Nvml.Status(_gpu);
            GpuLive.Text = $"{s.Temp} °C · {s.PowerW:0} W · núcleo {s.CoreMHz} MHz · memória {s.MemMHz} MHz" +
                           (s.FanPercent is { } f ? $" · ventoinha {f}%" : "");
        }
        catch (Exception ex)
        {
            GpuLive.Text = ex.Message;
        }
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_limits != null) UpdateLabels();
    }

    private void UpdateLabels()
    {
        var l = _limits!;
        var percent = l.PowerDefaultW > 0 ? PowerSlider.Value / l.PowerDefaultW * 100 : 100;
        PowerValue.Text = $"{PowerSlider.Value:0} W ({percent:0}%)";
        CoreValue.Text = l.CoreSupported ? $"{CoreSlider.Value:+0;-0;0} MHz" : "não suportado";
        MemValue.Text = l.MemSupported ? $"{MemSlider.Value:+0;-0;0} MHz" : "não suportado";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_gpu == null || _limits == null || !AppSettings.Current.OverclockRulesAccepted) return;
        // Regra 4: placa já quente não recebe overclock.
        try
        {
            if (Nvml.Status(_gpu).Temp >= 85)
            {
                OcStatus.Text = "✖ A placa está a 85 °C ou mais. Melhore a refrigeração antes de fazer overclock.";
                OcStatus.Foreground = Metric.Danger;
                return;
            }
        }
        catch { }
        var core = _limits.CoreSupported ? (int?)Math.Round(CoreSlider.Value / 5) * 5 : null;
        var mem = _limits.MemSupported ? (int?)Math.Round(MemSlider.Value / 10) * 10 : null;
        if ((core > 150 || mem > 1000) && MessageBox.Show(
                "Valores altos podem causar travamentos, artefatos na tela ou tela azul. Continuar?",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var error = Nvml.Apply(_gpu, PowerSlider.Value, core, mem);
        OcStatus.Text = error is null
            ? $"✔ Aplicado às {DateTime.Now:HH:mm:ss}. Vale até reiniciar o PC ou restaurar o padrão."
            : $"✖ {error}";
        OcStatus.Foreground = error is null ? Metric.Ok : Metric.Danger;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_gpu == null) return;
        var error = Nvml.Reset(_gpu);
        LoadGpu();
        OcStatus.Text = error is null ? "✔ Valores de fábrica restaurados." : $"✖ {error}";
        OcStatus.Foreground = error is null ? Metric.Ok : Metric.Danger;
    }

    // ---------- Ventoinhas ----------

    private void ShowFans()
    {
        var service = FanControlService.Instance;
        if (!service.Started)
        {
            FanStatus.Text = "Carregando sensores...";
            return;
        }
        FanList.ItemsSource = service.Channels;
        if (service.Channels.Count == 0)
            FanStatus.Text = "Nenhuma ventoinha controlável encontrada. Algumas placas-mãe só deixam controlar pela BIOS ou pelo software do fabricante.";
    }

    // ---------- Plano de energia ----------

    private async Task LoadPlansAsync()
    {
        var plans = await PowerPlans.ListAsync();
        PlanBox.ItemsSource = plans;
        PlanBox.SelectedItem = plans.FirstOrDefault(p => p.Active);
    }

    private async void ActivatePlan_Click(object sender, RoutedEventArgs e)
    {
        if (PlanBox.SelectedItem is not PowerPlan p) return;
        await PowerPlans.SetActiveAsync(p.Id);
        await LoadPlansAsync();
    }

    private async void AddUltimate_Click(object sender, RoutedEventArgs e)
    {
        await PowerPlans.AddUltimateAsync();
        await LoadPlansAsync();
    }
}
