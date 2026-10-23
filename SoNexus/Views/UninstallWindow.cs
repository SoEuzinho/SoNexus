using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Janela "Desinstalar o SoNexus": o que fazer antes de remover o programa.</summary>
public sealed class UninstallWindow : Window
{
    private readonly CheckBox _revert = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox _delete = new() { Content = "Apagar também minhas configurações, registros e backups", Margin = new Thickness(0, 12, 0, 4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _go = new() { Content = "Desinstalar", Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _cancel = new() { Content = "Cancelar", Padding = new Thickness(22, 7, 22, 7), IsCancel = true };
    private readonly bool _portable = UninstallService.Uninstaller == null;

    public UninstallWindow()
    {
        Title = "Desinstalar o SoNexus";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Style)Application.Current.FindResource("Muted");

        var tweaks = UninstallService.AppliedTweaks();
        _revert.Content = $"Desfazer os ajustes do Windows feitos pelo SoNexus ({tweaks.Count})";
        _revert.IsChecked = tweaks.Count > 0;
        _revert.IsEnabled = tweaks.Count > 0;

        var panel = new StackPanel { Margin = new Thickness(30, 26, 30, 24) };
        panel.Children.Add(new TextBlock { Text = _portable ? "Remover o SoNexus" : "Desinstalar o SoNexus", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Style = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18),
            Text = _portable
                ? "Esta é a versão portátil (sem instalador). O SoNexus vai limpar o que deixou no PC e fechar; depois é só apagar a pasta do programa."
                : "O SoNexus vai fechar e o desinstalador do Windows remove o programa, os atalhos e o \"Iniciar com o Windows\". As ventoinhas voltam para o controle da placa.",
        });
        panel.Children.Add(_revert);
        panel.Children.Add(new TextBlock
        {
            Style = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 0, 0, 0),
            Text = tweaks.Count > 0
                ? "Volta os valores originais que o SoNexus guardou (" + string.Join(", ", tweaks.Take(4).Select(t => t.Title)) + (tweaks.Count > 4 ? "..." : "") + "). Apps removidos não voltam sozinhos: reinstale pela Microsoft Store se quiser."
                : "Nenhum ajuste do Windows foi aplicado pelo SoNexus.",
        });
        panel.Children.Add(_delete);
        panel.Children.Add(new TextBlock
        {
            Style = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 0, 0, 0),
            Text = "Iluminação, painel de FPS, avisos, histórico e backups. Deixe desmarcado se pretende instalar de novo.",
        });
        panel.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_go);
        panel.Children.Add(buttons);
        Content = panel;

        _go.Background = Metric.Danger; // ação definitiva: vermelho de perigo real
        _go.Foreground = Brushes.White;
        _go.Click += async (_, _) => await RunAsync();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1, black = 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));
        };
    }

    private async Task RunAsync()
    {
        _go.IsEnabled = _cancel.IsEnabled = _revert.IsEnabled = _delete.IsEnabled = false;
        var revert = _revert.IsChecked == true;
        var delete = _delete.IsChecked == true;

        if (revert)
        {
            var failed = await Task.Run(() => UninstallService.RevertTweaks(s => Dispatcher.Invoke(() => _status.Text = s)));
            if (failed.Count > 0 && MessageBox.Show(this,
                    $"Não deu para desfazer: {string.Join(", ", failed)}.\n\nContinuar a desinstalação mesmo assim?",
                    "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                _go.IsEnabled = _cancel.IsEnabled = true;
                _status.Text = "Desinstalação cancelada.";
                return;
            }
        }

        if (_portable)
        {
            _status.Text = "Removendo \"Iniciar com o Windows\" e limpando...";
            await IntegrationService.SetStartupAsync(false);
            if (delete) UninstallService.DeleteData();
            MessageBox.Show(this, "Pronto. O SoNexus vai fechar; depois apague a pasta onde ele está:\n\n" + AppContext.BaseDirectory,
                "SoNexus", MessageBoxButton.OK, MessageBoxImage.Information);
            App.ExitCompletely();
            return;
        }

        _status.Text = "Abrindo o desinstalador...";
        try { UninstallService.RunUninstaller(delete); }
        catch (Exception ex)
        {
            _status.Text = $"✖ {ex.Message} Use Configurações do Windows › Aplicativos › SoNexus › Desinstalar.";
            _cancel.IsEnabled = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
