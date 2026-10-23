using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>
/// Aviso que aparece quando falta o driver de sensores (PawnIO) ou o SoNexus está sem administrador.
/// Some sozinho quando está tudo certo.
/// </summary>
public sealed class SensorDriverBanner : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Button _button = new() { VerticalAlignment = VerticalAlignment.Center };

    public SensorDriverBanner()
    {
        Style = (Style)Application.Current.FindResource("Card");
        Margin = new Thickness(0, 0, 12, 12);
        Padding = new Thickness(16, 12, 16, 12);
        BorderBrush = System.Windows.Media.Brushes.White;
        _button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _button.Click += OnClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_text);
        Grid.SetColumn(_button, 1);
        grid.Children.Add(_button);
        Child = grid;
        Update();
    }

    private void Update()
    {
        if (!SensorDriver.IsAdmin)
        {
            _text.Text = "O SoNexus está sem permissão de administrador: temperatura e consumo da CPU, tensões e ventoinhas não aparecem.";
            _button.Content = "Reabrir como administrador";
            Visibility = Visibility.Visible;
        }
        else if (!SensorDriver.IsInstalled)
        {
            _text.Text = "Falta o driver de sensores (PawnIO). Sem ele, temperatura e consumo do processador, tensões e ventoinhas da placa-mãe não aparecem. " +
                         "É o driver oficial e assinado usado pelo LibreHardwareMonitor (substitui o antigo WinRing0, que o Windows Defender bloqueia).";
            _button.Content = "Instalar driver de sensores";
            Visibility = Visibility.Visible;
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (!SensorDriver.IsAdmin)
        {
            SensorDriver.RestartApp();
            return;
        }
        _button.IsEnabled = false;
        _button.Content = "Instalando...";
        try
        {
            var code = await SensorDriver.InstallAsync();
            AppLog.Write($"PawnIO setup terminou com código {code}");
            if (SensorDriver.IsInstalled)
            {
                if (MessageBox.Show("Driver de sensores instalado. Reabrir o SoNexus agora para ler a temperatura do processador?",
                        "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    SensorDriver.RestartApp();
            }
            else
            {
                MessageBox.Show($"A instalação não foi concluída (código {code}).", "SoNexus");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Falha ao instalar PawnIO: {ex}");
            MessageBox.Show($"Não foi possível instalar: {ex.Message}", "SoNexus");
        }
        _button.IsEnabled = true;
        Update();
    }
}
