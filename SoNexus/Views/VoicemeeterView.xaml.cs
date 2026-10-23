using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class VoicemeeterView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private VoicemeeterRemote? _vm;
    private int _tick;

    public VoicemeeterView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Status.Text = VoicemeeterRemote.FindDll() == null
            ? "O Voicemeeter não está instalado. É uma mesa de som virtual gratuita (vb-audio.com)."
            : "Abra o Voicemeeter e clique em Conectar para ver os canais e níveis aqui.";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm?.Connected == true) _timer.Start();
        else if (VoicemeeterRemote.FindDll() != null) Connect();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private void Connect()
    {
        _timer.Stop();
        _vm?.Dispose();
        _vm = new VoicemeeterRemote();
        var ok = _vm.Connect();
        Status.Text = _vm.Status;
        Mixer.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (!ok) return;
        StripList.ItemsSource = _vm.Strips;
        BusList.ItemsSource = _vm.Buses;
        ConnectButton.Content = "Reconectar";
        _timer.Start();
    }

    private void Tick()
    {
        if (_vm?.Connected != true) return;
        try
        {
            _vm.RefreshLevels();
            if (++_tick % 15 == 0) _vm.RefreshParameters();
        }
        catch (Exception ex)
        {
            _timer.Stop();
            Status.Text = $"Conexão perdida: {ex.Message}. Clique em Reconectar.";
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VmChannel c }) _vm?.ToggleMute(c);
    }
}
