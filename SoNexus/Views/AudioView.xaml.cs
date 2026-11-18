using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class AudioView : UserControl
{
    private static readonly TimeSpan VolumeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DeviceInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _meters = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private List<AudioDevice> _devices = new();
    private List<AudioApp> _apps = new();
    private Window? _host;
    private long _lastVolume, _lastSessions, _lastDevices;

    public AudioView()
    {
        InitializeComponent();
        _meters.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => UpdatePolling();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Window.GetWindow(this);
        if (_host != null) _host.StateChanged += WindowStateChanged;
        UpdatePolling();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _meters.Stop();
        if (_host != null) _host.StateChanged -= WindowStateChanged;
        _host = null;
        DeviceList.ItemsSource = AppList.ItemsSource = null;
        AppStatus.Visibility = Visibility.Collapsed;
        DisposeSnapshot(_devices, _apps);
        _devices = new();
        _apps = new();
    }

    private void WindowStateChanged(object? sender, EventArgs e) => UpdatePolling();

    private void UpdatePolling()
    {
        if (!CanPoll())
        {
            _meters.Stop();
            return;
        }
        if (_meters.IsEnabled) return;
        Reload();
        _meters.Start();
    }

    private bool CanPoll() => IsLoaded && IsVisible && _host is { WindowState: not WindowState.Minimized };

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void SoundPanel_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });

    private void Reload(bool force = true)
    {
        _lastDevices = _lastSessions = _lastVolume = Stopwatch.GetTimestamp();
        try
        {
            var (devices, apps) = AudioService.Load();
            var sameDevices = devices.Select(DeviceKey).SequenceEqual(_devices.Select(DeviceKey));
            var sameApps = apps.Select(a => (a.DeviceId, a.SessionId)).ToHashSet()
                .SetEquals(_apps.Select(a => (a.DeviceId, a.SessionId)));
            if (!force && sameDevices && sameApps)
            {
                DisposeSnapshot(devices, apps);
            }
            else
            {
                DisposeSnapshot(_devices, _apps);
                (_devices, _apps) = (devices, apps);
                DeviceList.ItemsSource = _devices;
                UpdateAppList();
            }
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Não foi possível ler o áudio: {ex.Message}";
            return;
        }
        var outputs = _devices.Count(d => !d.IsInput);
        var inputs = _devices.Count(d => d.IsInput);
        var virtuals = _devices.Count(d => d.IsVirtual);
        var inactive = AudioService.InactiveCount();
        Subtitle.Text = $"{outputs} saída(s) · {inputs} entrada(s) · {virtuals} virtual(is) · {inactive} desativado(s) ou desconectado(s)";

        // Avisos: taxas de amostragem diferentes causam reamostragem e às vezes estalos.
        var rates = _devices.Where(d => !d.IsVirtual).Select(d => d.Format.Split(" · ")[0]).Distinct().ToList();
        var warnings = new List<string>();
        if (rates.Count > 1)
            warnings.Add($"Os dispositivos usam taxas diferentes ({string.Join(", ", rates)}). Se houver estalos, compare as taxas do aplicativo e da saída.");
        if (_devices.Any(d => d.IsDefault && d.IsInput && d.Volume is < 30))
            warnings.Add("O volume do microfone padrão está baixo (abaixo de 30%).");
        WarningText.Text = string.Join("\n", warnings);
        WarningCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAppList(long now)
    {
        _lastSessions = now;
        var apps = AudioService.LoadApps(_devices);
        if (apps.Select(a => (a.DeviceId, a.SessionId)).SequenceEqual(_apps.Select(a => (a.DeviceId, a.SessionId))))
        {
            foreach (var app in apps) app.Dispose();
            return;
        }
        foreach (var app in _apps) app.Dispose();
        _apps = apps;
        UpdateAppList();
    }

    private void UpdateAppList()
    {
        foreach (var app in _apps) app.RefreshVolume();
        AppList.ItemsSource = _apps;
        NoApps.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioApp app } && _apps.Contains(app))
            ShowAppResult(app, app.ToggleMute());
    }

    private void AppVolume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioApp app, Tag: string delta } && _apps.Contains(app) &&
            int.TryParse(delta, out var change))
            ShowAppResult(app, app.AdjustVolume(change));
    }

    private void ShowAppResult(AudioApp app, bool confirmed)
    {
        AppStatus.Text = confirmed
            ? $"Último ajuste confirmado: {app.Name} · {app.Device} · {app.VolumeText} · {app.MuteStatus}"
            : $"Não foi possível confirmar o ajuste de {app.Name}. Atualize a lista se a sessão tiver mudado.";
        AppStatus.Visibility = Visibility.Visible;
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
        if (!CanPoll()) return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastDevices, now) >= DeviceInterval)
        {
            Reload(force: false);
        }
        else if (Stopwatch.GetElapsedTime(_lastSessions, now) >= SessionInterval)
        {
            RefreshAppList(now);
        }

        foreach (var d in _devices) d.RefreshPeak();
        foreach (var a in _apps) a.RefreshPeak();
        if (Stopwatch.GetElapsedTime(_lastVolume, now) < VolumeInterval) return;
        _lastVolume = now;
        foreach (var d in _devices) d.RefreshVolume();
        foreach (var a in _apps) a.RefreshVolume();
    }
}
