using System.Windows;
using System.Windows.Controls;
using SoNexus.Services;

namespace SoNexus.Views;

public sealed record WindowsShortcut(string Icon, string Title, string Detail, string Target);

public partial class SystemIntegrationView : UserControl
{
    // Só alvos fixos do próprio Windows (nada vindo de fora do app).
    private static readonly WindowsShortcut[] Shortcuts =
    {
        new("", "Modo de Jogo", "Prioriza o jogo aberto e segura atualizações durante a partida.", "ms-settings:gaming-gamemode"),
        new("", "Gráficos", "Escolher a placa de vídeo por jogo e o agendamento de GPU (HAGS).", "ms-settings:display-advancedgraphics"),
        new("", "Iluminação dinâmica", "RGB nativo do Windows 11 (teclados e periféricos compatíveis).", "ms-settings:personalization-lighting"),
        new("", "Tela e taxa (Hz)", "Resolução e taxa de atualização do monitor.", "ms-settings:display"),
        new("", "Som (painel clássico)", "Formatos, exclusividade e dispositivos padrão.", "mmsys.cpl"),
        new("", "Opções de energia", "Planos de energia e o que o botão de desligar faz.", "powercfg.cpl"),
        new("", "Gerenciador de Dispositivos", "Drivers e peças reconhecidas pelo Windows.", "devmgmt.msc"),
        new("", "Monitor de Recursos", "Quem está usando CPU, disco, rede e memória agora.", "resmon.exe"),
        new("", "Visualizador de Eventos", "Registro completo de erros do Windows.", "eventvwr.msc"),
        new("", "Windows Update", "Atualizações do sistema e drivers opcionais.", "ms-settings:windowsupdate"),
        new("", "Segurança do Windows", "Antivírus, firewall e proteção contra ransomware.", "windowsdefender:"),
        new("", "Sistema avançado", "Memória virtual, restauração do sistema e desempenho visual.", "sysdm.cpl"),
    };

    public SystemIntegrationView()
    {
        InitializeComponent();
        ShortcutList.ItemsSource = Shortcuts;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) { }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        try { IntegrationService.Open(target); }
        catch (Exception ex) { MessageBox.Show($"Não foi possível abrir: {ex.Message}", "SoNexus"); }
    }
}
