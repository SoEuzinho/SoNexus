using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class AudioView : UserControl
{
    // Medidores de nível precisam de atualização rápida para parecerem "vivos".
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private List<AudioDevice> _devices = new();
    private List<AudioApp> _apps = new();
    private int _tick;

    public AudioView()
    {
        InitializeComponent();
        _meters.Tick += (_, _) => Tick();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reload();
        _meters.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _meters.Stop();
        DeviceList.ItemsSource = AppList.ItemsSource = null;
        DisposeSnapshot(_devices, _apps);
        _devices = new();
        _apps = new();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void SoundPanel_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });

    private void Reload(bool force = true)
    {
        try
        {
            var (devices, apps) = AudioService.Load();
            var sameDevices = devices.Select(DeviceKey).SequenceEqual(_devices.Select(DeviceKey));
            var sameApps = apps.Select(a => (a.DeviceId, a.SessionId)).ToHashSet()
                .SetEquals(_apps.Select(a => (a.DeviceId, a.SessionId)));
            if (!force && sameDevices && sameApps)
            {
                DisposeSnapshot(devices, apps);
                return;
            }
            DisposeSnapshot(_devices, _apps);
            (_devices, _apps) = (devices, apps);
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Não foi possível ler o áudio: {ex.Message}";
            return;
        }
        DeviceList.ItemsSource = _devices;
        AppList.ItemsSource = _apps;
        NoApps.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var outputs = _devices.Count(d => !d.IsInput);
        var inputs = _devices.Count(d => d.IsInput);
        var virtuals = _devices.Count(d => d.IsVirtual);
        var inactive = AudioService.InactiveCount();
        Subtitle.Text = $"{outputs} saída(s) · {inputs} entrada(s) · {virtuals} virtual(is) · {inactive} desativado(s) ou desconectado(s)";

        // Avisos: taxas de amostragem diferentes causam reamostragem e às vezes estalos.
        var rates = _devices.Where(d => !d.IsVirtual).Select(d => d.Format.Split(" · ")[0]).Distinct().ToList();
        var warnings = new List<string>();
        if (rates.Count > 1)
            warnings.Add($"Seus dispositivos usam taxas diferentes ({string.Join(", ", rates)}). Deixar todos em 48 kHz evita conversões e estalos, principalmente com Voicemeeter.");
        if (_devices.Any(d => d.IsDefault && d.IsInput && d.Volume < 30))
            warnings.Add("O volume do microfone padrão está baixo (abaixo de 30%).");
        WarningText.Text = string.Join("\n", warnings);
        WarningCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static (string Id, string Name, bool Default, string Format, string Latency) DeviceKey(AudioDevice device) =>
        (device.Id, device.Name, device.IsDefault, device.Format, device.Latency);

    private static void DisposeSnapshot(IEnumerable<AudioDevice> devices, IEnumerable<AudioApp> apps)
    {
        foreach (var app in apps) app.Dispose();
        foreach (var device in devices) device.Dispose();
    }

    private void Tick()
    {
        if (!IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
        foreach (var d in _devices) d.Refresh();
        foreach (var a in _apps) a.Refresh();
        // Programas abrem e fecham: recarrega a lista a cada 5 s.
        if (++_tick % 60 == 0) Reload(force: false);
    }
}
