using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class AudioDriversView : UserControl
{
    private bool _loaded;

    public AudioDriversView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        var drivers = await Task.Run(AudioService.Drivers);
        DriverGrid.ItemsSource = drivers;
        DriverTitle.Text = $"Drivers de áudio ({drivers.Count})";

        var (devices, _) = AudioService.Load();
        var software = await Task.Run(() => AudioService.DetectSoftware(devices));
        SoftwareList.ItemsSource = software.Count > 0
            ? software
            : new List<AudioSoftware> { new("Nenhum programa de áudio extra", "", "Só o driver padrão do Windows/placa-mãe está em uso.") };
    }
}
