using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class GpuDriverView : UserControl
{
    private bool _loaded;
    private bool _busy;

    public GpuDriverView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await CheckAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        CheckButton.IsEnabled = false;
        CheckButton.Content = "Consultando...";
        GpuList.ItemsSource = await GpuDriverService.CheckAllAsync();
        CheckButton.Content = "Verificar de novo";
        CheckButton.IsEnabled = true;
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GpuDriverStatus s }) return;
        var url = s.Vendor switch
        {
            "NVIDIA" => "https://www.nvidia.com/pt-br/drivers/",
            "AMD" => "https://www.amd.com/pt/support/download/drivers.html",
            "Intel" => "https://www.intel.com.br/content/www/br/pt/support/detect.html",
            _ => "",
        };
        if (url.Length > 0) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: GpuDriverStatus s } button) return;
        if (s.Vendor is not ("NVIDIA" or "AMD" or "Intel")) return;
        if (s.UpToDate && MessageBox.Show("Você já tem a versão mais recente. Reinstalar mesmo assim?", "SoNexus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _busy = true;
        var original = button.Content;
        try
        {
            if (s.Vendor == "Intel")
            {
                button.Content = "Instalando assistente...";
                var code = await GpuDriverService.InstallIntelAssistantAsync();
                MessageBox.Show(code == 0
                    ? "Assistente de Driver e Suporte da Intel instalado. Ele vai abrir no navegador e mostrar os drivers da Intel."
                    : $"O winget retornou o código {code}. Tente pela página oficial.", "SoNexus");
                return;
            }
            if (s.DownloadUrl == null)
            {
                Site_Click(sender, e);
                return;
            }
            var size = s.Size != null ? $" ({s.Size})" : "";
            if (MessageBox.Show($"Baixar o driver {s.Vendor} {s.Latest}{size} do site oficial e instalar?", "SoNexus",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var progress = new Progress<DownloadProgress>(p => button.Content = $"{p.Percent:0}%");
            var file = await GpuDriverService.DownloadAsync(s.DownloadUrl, progress, CancellationToken.None);

            if (s.Vendor == "NVIDIA")
            {
                button.Content = "Instalando...";
                var code = await GpuDriverService.InstallNvidiaAsync(file, CleanBox.IsChecked == true);
                MessageBox.Show(code == 0
                    ? $"Driver NVIDIA {s.Latest} instalado. Reinicie o PC para concluir."
                    : $"O instalador da NVIDIA terminou com o código {code}. Se a versão não mudou, rode o arquivo manualmente: {file}", "SoNexus");
                await CheckAsync();
            }
            else
            {
                GpuDriverService.RunInteractive(file);
                MessageBox.Show("O instalador oficial da AMD foi aberto. Siga os passos na janela dele (ele detecta a placa sozinho).", "SoNexus");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível concluir: {ex.Message}", "SoNexus");
        }
        finally
        {
            button.Content = original;
            _busy = false;
        }
    }
}
