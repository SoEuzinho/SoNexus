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

    private void OnUnloaded(object sender, RoutedEventArgs e) => _meters.Stop();

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void SoundPanel_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });

    private void Reload()
    {
        try
        {
            (_devices, _apps) = AudioService.Load();
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

    private void Tick()
    {
        foreach (var d in _devices) d.Refresh();
        foreach (var a in _apps) a.Refresh();
        // Programas abrem e fecham: recarrega a lista a cada 5 s.
        if (++_tick % 60 == 0)
        {
            try
            {
                var (_, apps) = AudioService.Load();
                if (apps.Count != _apps.Count || apps.Select(a => a.Pid).Except(_apps.Select(a => a.Pid)).Any())
                {
                    _apps = apps;
                    AppList.ItemsSource = _apps;
                    NoApps.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { }
        }
    }
}
