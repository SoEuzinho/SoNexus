using System.Windows;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Primeira vez que o SoNexus abre: apresenta o app e pergunta como a pessoa quer usar.</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        // Nunca maior que a área útil da tela (com zoom de 150-200% a janela passaria da borda).
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        // "Iniciar com o Windows" só na versão instalada (a portátil fica numa pasta que outros programas alteram).
        if (!IntegrationService.IsProtectedLocation(Environment.ProcessPath ?? ""))
        {
            StartupBox.IsEnabled = false;
            StartupHint.Text = "Disponível na versão instalada (instalador do SoNexus).";
        }
        else StartupHint.Text = "Recomendado para as ventoinhas e a iluminação funcionarem desde o login.";

        // Já usava o app e só os termos mudaram: mantém as escolhas que a pessoa já tinha feito.
        if (AppSettings.Current.WelcomeShown)
        {
            TrayBox.IsChecked = AppSettings.Current.CloseToTray;
            AlertBox.IsChecked = AppSettings.Current.TempAlerts;
            OnlineBox.IsChecked = AppSettings.Current.AllowOnline;
        }
    }

    private bool _started;

    private void ReadTerms_Click(object sender, RoutedEventArgs e) => new TermsWindow { Owner = this }.ShowDialog();

    private void Accept_Click(object sender, RoutedEventArgs e) => StartButton.IsEnabled = AcceptBox.IsChecked == true;

    /// <summary>Fechar sem aceitar os termos = não usar o programa.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_started || Terms.Accepted) return;
        if (MessageBox.Show(this, "Para usar o SoNexus é preciso aceitar os Termos de Uso e a Política de Privacidade.\n\nFechar o SoNexus?",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _started = true;
            App.ExitCompletely();
        }
        else e.Cancel = true;
    }

    /// <summary>Barra de título escura, igual à janela principal.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, black = 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptBox.IsChecked != true) return;
        var s = AppSettings.Current;
        s.CloseToTray = TrayBox.IsChecked == true;
        s.TempAlerts = AlertBox.IsChecked == true;
        s.AllowOnline = OnlineBox.IsChecked == true;
        s.WelcomeShown = true;
        Terms.Accept(); // também salva
        _started = true;
        if (StartupBox.IsChecked == true && await IntegrationService.SetStartupAsync(true) is { } error)
            MessageBox.Show(error, "SoNexus");
        Close();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
