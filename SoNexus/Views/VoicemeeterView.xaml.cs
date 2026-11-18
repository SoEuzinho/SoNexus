using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class VoicemeeterView : UserControl
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private VoicemeeterRemote? _vm;
    private Window? _host;
    private int _tick;

    public VoicemeeterView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => UpdatePolling();
        Status.Text = VoicemeeterRemote.FindDll() == null
            ? "O Voicemeeter não está instalado. É uma mesa de som virtual gratuita (vb-audio.com)."
            : "Abra o Voicemeeter e clique em Conectar para ver os canais e níveis aqui.";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Window.GetWindow(this);
        if (_host != null) _host.StateChanged += WindowStateChanged;
        if (VoicemeeterRemote.FindDll() != null) Connect();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host != null) _host.StateChanged -= WindowStateChanged;
        _host = null;
        Disconnect();
    }

    private void WindowStateChanged(object? sender, EventArgs e) => UpdatePolling();

    private void UpdatePolling()
    {
        if (IsVisible && _host is { WindowState: not WindowState.Minimized } && _vm?.Connected == true)
            _timer.Start();
        else
            _timer.Stop();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private void Connect()
    {
        Disconnect();
        _vm = new VoicemeeterRemote();
        var ok = _vm.Connect();
        Status.Text = _vm.Status;
        Mixer.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (!ok)
        {
            Disconnect();
            return;
        }
        StripList.ItemsSource = _vm.Strips;
        BusList.ItemsSource = _vm.Buses;
        ConnectButton.Content = "Reconectar";
        _tick = 0;
        UpdatePolling();
    }

    private void Disconnect()
    {
        _timer.Stop();
        _vm?.Dispose();
        _vm = null;
        Mixer.Visibility = Visibility.Collapsed;
        StripList.ItemsSource = BusList.ItemsSource = null;
    }

    private void Tick()
    {
        if (_vm?.Connected != true) return;
        try
        {
            _vm.RefreshLevels();
            if (++_tick % 10 == 0) _vm.RefreshParameters();
        }
        catch (Exception ex)
        {
            Disconnect();
            Status.Text = $"Conexão perdida: {ex.Message}. Clique em Reconectar.";
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VmChannel c } && _vm != null)
            Status.Text = _vm.ToggleMute(c)
                ? _vm.Status
                : "O Voicemeeter recusou o ajuste de mudo. Confira a conexão ou clique em Reconectar.";
    }
}
