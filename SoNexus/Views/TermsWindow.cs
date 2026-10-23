using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Janela que mostra os Termos de Uso e a Política de Privacidade (só leitura).</summary>
public sealed class TermsWindow : Window
{
    public TermsWindow()
    {
        Title = "Termos de Uso e Privacidade — SoNexus";
        Width = 760;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("BgBrush");

        var text = new TextBox
        {
            Text = Terms.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(18),
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            BorderThickness = new Thickness(0),
        };
        var close = new Button { Content = "Fechar", Padding = new Thickness(24, 6, 24, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        var dock = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(text);
        Content = dock;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1, black = 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
