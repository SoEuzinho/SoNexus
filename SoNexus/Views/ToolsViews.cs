using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Peças de tela montadas em código, no mesmo estilo do resto do app.</summary>
internal static class Ui
{
    public static StackPanel Page(UserControl owner, string title, string subtitle)
    {
        var root = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        root.Children.Add(Styled(new TextBlock { Text = title }, "H1"));
        root.Children.Add(Styled(new TextBlock { Text = subtitle, FontSize = 13, Margin = new Thickness(0, 4, 0, 16), TextWrapping = TextWrapping.Wrap }, "Muted"));
        owner.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        return root;
    }

    public static Border Card(UIElement child, double bottom = 12)
    {
        var card = new Border { Child = child, Margin = new Thickness(0, 0, 0, bottom) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        return card;
    }

    public static TextBlock H2(string text) => Styled(new TextBlock { Text = text }, "H2");

    public static TextBlock Muted(string text, double size = 13) =>
        Styled(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }, "Muted");

    public static TextBlock Text(string text = "", double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        b.Click += click;
        return b;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    public static StackPanel Stack(params UIElement[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    public static T Styled<T>(T e, string style) where T : FrameworkElement
    {
        e.SetResourceReference(FrameworkElement.StyleProperty, style);
        return e;
    }

    public static bool Admin(StackPanel page)
    {
        if (SensorDriver.IsAdmin) return true;
        page.Children.Add(Card(Text("Esta ferramenta precisa do SoNexus aberto como administrador (o instalador já abre assim).")));
        return false;
    }
}

/// <summary>Memória como no RAMMap: listas do Windows e botões para soltar a memória em espera.</summary>
public sealed class RamToolView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _numbers = Ui.Text("", 14);
    private readonly TextBlock _status = Ui.Muted("");
    private readonly ProgressBar _bar = new() { Maximum = 100, Height = 10, Margin = new Thickness(0, 10, 0, 0) };

    public RamToolView()
    {
        var page = Ui.Page(this, "Memória (RAM)",
            "O Windows guarda arquivos usados recentemente na RAM como \"em espera\" para abrir mais rápido. Em alguns jogos isso causa engasgos quando a RAM livre acaba. " +
            "Aqui dá para soltar essa memória na hora, como o RAMMap e o ISLC.");
        if (!Ui.Admin(page)) return;
        _bar.SetResourceReference(StyleProperty, "Meter");
        _bar.Foreground = Brushes.White;
        page.Children.Add(Ui.Card(Ui.Stack(Ui.H2("Agora"), _numbers, _bar)));
        page.Children.Add(Ui.Card(Ui.Stack(
            Ui.H2("Liberar memória"),
            Ui.Muted("\"Em espera\" é cache: soltar não fecha nada e não perde nada. Use antes de abrir um jogo ou quando ele começar a engasgar com a RAM quase cheia."),
            Ui.Row(
                Ui.Button("Liberar memória em espera", (_, _) => Run(() => MemoryCleaner.PurgeStandby(), "Memória em espera liberada."), primary: true),
                Ui.Button("Só a de prioridade baixa", (_, _) => Run(() => MemoryCleaner.PurgeStandby(true), "Memória em espera de prioridade baixa liberada.")),
                Ui.Button("Esvaziar programas parados", (_, _) => Run(MemoryCleaner.EmptyWorkingSets,
                    "Programas parados soltaram a RAM. Eles podem demorar um instante para responder na próxima vez que você mexer neles."))),
            _status)));
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _timer.Start(); } else _timer.Stop(); };
    }

    private async void Run(Func<bool> action, string done)
    {
        var before = MemoryService.GetStatus().Available;
        var ok = await Task.Run(action);
        await Task.Delay(400);
        var gained = (long)MemoryService.GetStatus().Available - (long)before;
        _status.Text = ok ? $"{done} RAM livre: +{Format.Bytes((ulong)Math.Max(0, gained))}." : "O Windows não deixou (precisa de administrador).";
        Refresh();
    }

    private void Refresh()
    {
        var s = MemoryService.GetStatus();
        var lists = MemoryCleaner.Read();
        _bar.Value = s.Load;
        _numbers.Text = $"Em uso {Format.Bytes(s.Total - s.Available)} de {Format.Bytes(s.Total)} ({s.Load}%)" +
                        (lists is { } l ? $"\nEm espera (cache) {Format.Bytes(l.Standby)} · modificada {Format.Bytes(l.Modified)} · livre de verdade {Format.Bytes(l.Free + l.Zeroed)}" : "");
    }
}

/// <summary>Prioridade automática para jogos e equilíbrio de programas em segundo plano (como o Process Lasso).</summary>
public sealed class GameBoostView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _now = Ui.Text("", 14);
    private readonly TextBlock _log = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public GameBoostView()
    {
        var page = Ui.Page(this, "Prioridade dos jogos",
            "Como o Process Lasso: quando um jogo está na frente, ele ganha prioridade alta, e programas em segundo plano que estiverem comendo CPU " +
            "descem um nível até você sair do jogo. Nada é fechado e tudo volta ao normal sozinho.");
        if (!Ui.Admin(page)) return;
        var set = AppSettings.Current;
        var on = new CheckBox { Content = "Ligar a prioridade automática para jogos", IsChecked = set.GameBoost, FontSize = 14 };
        var balance = new CheckBox { Content = "Equilíbrio: segurar programas em segundo plano que usam mais de 8% da CPU durante o jogo", IsChecked = set.ProBalance, Margin = new Thickness(0, 8, 0, 0) };
        on.Click += (_, _) =>
        {
            set.GameBoost = on.IsChecked == true;
            set.Save();
            if (set.GameBoost) GameBoostService.Instance.Start();
            else GameBoostService.Instance.Stop();
            Refresh();
        };
        balance.Click += (_, _) => { set.ProBalance = balance.IsChecked == true; set.Save(); };
        page.Children.Add(Ui.Card(Ui.Stack(on, balance,
            Ui.Muted("Jogo = programa instalado numa pasta de jogos (Steam, Epic, GOG, Xbox...) ou janela que cobre a tela inteira. " +
                     "Nunca mexe no Windows, em antivírus, em programas de voz (Discord) nem de stream (OBS). Funciona com o SoNexus aberto ou na bandeja.", 12))));
        page.Children.Add(Ui.Card(Ui.Stack(Ui.H2("Agora"), _now)));
        page.Children.Add(Ui.Card(Ui.Stack(Ui.H2("O que foi feito"), _log)));
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _timer.Start(); } else _timer.Stop(); };
    }

    private void Refresh()
    {
        var s = GameBoostService.Instance;
        _now.Text = !s.Running ? "Desligado." : s.CurrentGame is { } g ? $"Jogo na frente: {g} (prioridade alta) · {s.LoweredCount} programa(s) segurado(s)" : "Ligado, esperando um jogo.";
        _log.Text = s.Log.Count == 0 ? "Nada ainda." : string.Join("\n", s.Log.Take(15));
    }
}

/// <summary>Instala vários programas de uma vez pelo winget (instaladores oficiais), como o Ninite.</summary>
public sealed class InstallAppsView : UserControl
{
    private sealed record App(string Group, string Name, string Id);

    private static readonly App[] Catalog =
    {
        new("Navegadores", "Google Chrome", "Google.Chrome"), new("Navegadores", "Firefox", "Mozilla.Firefox"),
        new("Navegadores", "Brave", "Brave.Brave"), new("Navegadores", "Opera GX", "Opera.OperaGX"),
        new("Jogos", "Steam", "Valve.Steam"), new("Jogos", "Epic Games", "EpicGames.EpicGamesLauncher"),
        new("Jogos", "EA app", "ElectronicArts.EADesktop"), new("Jogos", "Ubisoft Connect", "Ubisoft.Connect"), new("Jogos", "GOG Galaxy", "GOG.Galaxy"),
        new("Comunicação", "Discord", "Discord.Discord"), new("Comunicação", "Telegram", "Telegram.TelegramDesktop"),
        new("Mídia", "VLC", "VideoLAN.VLC"), new("Mídia", "OBS Studio", "OBSProject.OBSStudio"),
        new("Utilitários", "7-Zip", "7zip.7zip"), new("Utilitários", "Notepad++", "Notepad++.Notepad++"),
        new("Utilitários", "Everything (busca rápida)", "voidtools.Everything"), new("Utilitários", "PowerToys", "Microsoft.PowerToys"),
        new("Utilitários", "qBittorrent", "qBittorrent.qBittorrent"), new("Utilitários", "Visual Studio Code", "Microsoft.VisualStudioCode"),
        new("Para jogos rodarem", "Visual C++ 2015-2022", "Microsoft.VCRedist.2015+.x64"), new("Para jogos rodarem", "DirectX (runtime)", "Microsoft.DirectX"),
        new("Para jogos rodarem", ".NET Desktop 8", "Microsoft.DotNet.DesktopRuntime.8"),
    };

    private readonly Dictionary<App, CheckBox> _boxes = new();
    private readonly TextBlock _status = Ui.Muted("");
    private readonly Button _install;
    private bool _busy;

    public InstallAppsView()
    {
        var page = Ui.Page(this, "Instalar programas",
            "Como o Ninite: marque o que quer e o SoNexus instala tudo de uma vez, sem propaganda nem \"avançar, avançar\". " +
            "Usa o winget, o instalador oficial da Microsoft, que baixa de cada fabricante.");
        var grid = new WrapPanel();
        foreach (var group in Catalog.GroupBy(a => a.Group))
        {
            var col = Ui.Stack(Ui.Styled(new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }, "Muted"));
            col.Width = 230;
            col.Margin = new Thickness(0, 0, 16, 14);
            foreach (var app in group)
            {
                var box = new CheckBox { Content = app.Name, Margin = new Thickness(0, 0, 0, 6), ToolTip = app.Id };
                _boxes[app] = box;
                col.Children.Add(box);
            }
            grid.Children.Add(col);
        }
        _install = Ui.Button("Instalar marcados", (_, _) => Install(), primary: true);
        page.Children.Add(Ui.Card(Ui.Stack(grid,
            Ui.Muted("Ao instalar, você aceita a licença de cada programa (o winget mostra o link de cada uma). Programas já instalados aparecem marcados como ✔.", 12),
            Ui.Row(_install, Ui.Button("Atualizar todos os programas do PC", (_, _) => UpgradeAll())), _status)));
        Loaded += async (_, _) => await MarkInstalled();
    }

    private async Task MarkInstalled()
    {
        _status.Text = "Vendo o que já está instalado...";
        var (code, output, _) = await ProcessRunner.RunAsync("winget.exe", "list --accept-source-agreements --disable-interactivity");
        if (code != 0 && output.Length == 0)
        {
            _status.Text = "O winget não está disponível neste Windows. Instale o \"Instalador de Aplicativo\" pela Microsoft Store.";
            _install.IsEnabled = false;
            return;
        }
        foreach (var (app, box) in _boxes)
            if (output.Contains(app.Id, StringComparison.OrdinalIgnoreCase))
            {
                box.Content = $"{app.Name} ✔";
                box.IsEnabled = false;
            }
        _status.Text = "";
    }

    private async void Install()
    {
        var chosen = _boxes.Where(b => b.Value.IsChecked == true && b.Value.IsEnabled).Select(b => b.Key).ToList();
        if (chosen.Count == 0 || _busy) { _status.Text = "Marque pelo menos um programa."; return; }
        _busy = true;
        _install.IsEnabled = false;
        var ok = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var app = chosen[i];
            _status.Text = $"Instalando {app.Name} ({i + 1} de {chosen.Count})... Pode levar alguns minutos.";
            var (code, output, err) = await ProcessRunner.RunAsync("winget.exe",
                $"install --id {app.Id} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
            if (code == 0 || output.Contains("já instalado", StringComparison.OrdinalIgnoreCase) || output.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            {
                ok.Add(app.Name);
                _boxes[app].Content = $"{app.Name} ✔";
                _boxes[app].IsEnabled = false;
            }
            else
            {
                failed.Add(app.Name);
                AppLog.Write($"winget {app.Id}: código {code} {err.Trim()}");
            }
        }
        _status.Text = (ok.Count > 0 ? $"Instalado: {string.Join(", ", ok)}. " : "") +
                       (failed.Count > 0 ? $"Não deu: {string.Join(", ", failed)} (veja a internet ou tente de novo)." : "");
        _install.IsEnabled = true;
        _busy = false;
    }

    private async void UpgradeAll()
    {
        if (_busy) return;
        if (MessageBox.Show("Atualizar todos os programas que o winget conhece? Programas abertos podem fechar durante a atualização.",
                "SoNexus", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _busy = true;
        _status.Text = "Atualizando programas... Pode levar vários minutos.";
        var (code, _, _) = await ProcessRunner.RunAsync("winget.exe", "upgrade --all --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        _status.Text = code == 0 ? "Programas atualizados." : "Terminou, mas algum programa não atualizou (veja de novo mais tarde).";
        _busy = false;
    }
}

/// <summary>Limpeza de temporários e caches de shader.</summary>
public sealed class CleanupView : UserControl
{
    private readonly List<(CleanupTarget Target, CheckBox Box, TextBlock Size)> _rows = new();
    private readonly TextBlock _status = Ui.Muted("");
    private readonly Button _clean;

    public CleanupView()
    {
        var page = Ui.Page(this, "Limpeza",
            "Apaga só arquivos temporários e caches que o Windows e os drivers recriam sozinhos. Nunca mexe em documentos, downloads, fotos ou na Lixeira.");
        var list = new StackPanel();
        foreach (var t in CleanupService.Targets())
        {
            var box = new CheckBox { Content = t.Name, IsChecked = !t.Name.StartsWith("Cache de shaders"), FontWeight = FontWeights.SemiBold };
            var size = Ui.Muted("calculando...");
            size.HorizontalAlignment = HorizontalAlignment.Right;
            var head = new DockPanel();
            DockPanel.SetDock(size, Dock.Right);
            head.Children.Add(size);
            head.Children.Add(box);
            var row = Ui.Stack(head, Ui.Muted(t.Detail, 12));
            row.Margin = new Thickness(0, 0, 0, 10);
            list.Children.Add(row);
            _rows.Add((t, box, size));
        }
        _clean = Ui.Button("Limpar marcados", (_, _) => Clean(), primary: true);
        page.Children.Add(Ui.Card(Ui.Stack(list, Ui.Row(_clean), _status)));
        Loaded += async (_, _) => await Measure();
    }

    private async Task Measure()
    {
        foreach (var (t, _, size) in _rows)
        {
            await Task.Run(() => CleanupService.Measure(t));
            size.Text = t.Files == 0 ? "vazio" : $"{Format.Bytes((ulong)t.Bytes)} · {t.Files:N0} arquivos";
        }
    }

    private async void Clean()
    {
        _clean.IsEnabled = false;
        long freed = 0;
        foreach (var (t, box, _) in _rows.Where(r => r.Box.IsChecked == true))
        {
            _status.Text = $"Limpando {t.Name.ToLowerInvariant()}...";
            freed += await Task.Run(() => CleanupService.Clean(t, CancellationToken.None));
        }
        _status.Text = $"Pronto: {Format.Bytes((ulong)freed)} liberados. Arquivos em uso ficaram (são apagados numa próxima vez).";
        await Measure();
        _clean.IsEnabled = true;
    }
}

/// <summary>Teste do monitor (como o UFO Test): movimento, rastro/ghosting e quadros pulados.</summary>
public sealed class MonitorTestView : UserControl
{
    public MonitorTestView()
    {
        var page = Ui.Page(this, "Teste do monitor",
            "Mostra três faixas com uma nave se movendo em velocidades diferentes, em tela cheia. Serve para ver o rastro (ghosting), " +
            "o borrão de movimento e se o monitor está mesmo na taxa de atualização (Hz) certa, sem pular quadros.");
        var hz = GamesService.PrimaryRefreshHz();
        page.Children.Add(Ui.Card(Ui.Stack(
            Ui.H2($"Monitor principal configurado em {hz} Hz"),
            Ui.Muted("Olhe a nave de cima (mais rápida): ela deve parecer nítida, sem \"fantasmas\" atrás. Se a contagem de Hz medida não bater com a do monitor, " +
                     "confira o cabo (DisplayPort ou HDMI 2.0+) e a taxa em Configurações do Windows › Vídeo › Avançado."),
            Ui.Row(Ui.Button("Abrir o teste em tela cheia", (_, _) => new MotionTestWindow().Show(), primary: true)),
            Ui.Muted("Esc ou clique fecha o teste.", 12))));
    }
}

/// <summary>Janela do teste de movimento: três faixas a 960, 480 e 240 pixels por segundo.</summary>
public sealed class MotionTestWindow : Window
{
    private static readonly double[] Speeds = { 960, 480, 240 };
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)) };
    private readonly List<FrameworkElement> _ships = new();
    private readonly TextBlock _info = new() { Foreground = Brushes.White, FontSize = 16, FontFamily = new FontFamily("Consolas") };
    private TimeSpan _last;
    private double _t;
    private int _frames, _skips;
    private double _windowStart;
    private readonly double _expected = 1.0 / Math.Max(30, GamesService.PrimaryRefreshHz());

    public MotionTestWindow()
    {
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Brushes.Black;
        Content = _canvas;
        _canvas.Children.Add(_info);
        Canvas.SetLeft(_info, 24);
        Canvas.SetTop(_info, 20);
        for (var i = 0; i < Speeds.Length; i++)
        {
            var ship = Ship();
            _ships.Add(ship);
            _canvas.Children.Add(ship);
            var label = new TextBlock { Text = $"{Speeds[i]:0} pixels/s", Foreground = Brushes.Gray, FontSize = 14 };
            _canvas.Children.Add(label);
            Canvas.SetLeft(label, 24);
            label.Tag = i;
        }
        Loaded += (_, _) => Layout();
        SizeChanged += (_, _) => Layout();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        MouseDown += (_, _) => Close();
        CompositionTarget.Rendering += OnFrame;
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Nave própria: casco com faixas e janelas (bordas nítidas mostram bem o rastro).</summary>
    private static FrameworkElement Ship()
    {
        var g = new Grid { Width = 120, Height = 50 };
        g.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Brushes.LightGray, Height = 22, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) });
        g.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), Width = 46, Height = 30, VerticalAlignment = VerticalAlignment.Top });
        var lights = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var c in new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Yellow, Colors.Red })
            lights.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 6, Margin = new Thickness(3, 0, 3, 0), Fill = new SolidColorBrush(c) });
        g.Children.Add(lights);
        return g;
    }

    private void Layout()
    {
        var lane = (ActualHeight - 80) / Speeds.Length;
        for (var i = 0; i < _ships.Count; i++) Canvas.SetTop(_ships[i], 80 + lane * i + lane / 2 - 25);
        foreach (var label in _canvas.Children.OfType<TextBlock>().Where(t => t.Tag is int))
            Canvas.SetTop(label, 80 + lane * (int)label.Tag + 10);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;
        var dt = _last == TimeSpan.Zero ? 0 : (now - _last).TotalSeconds;
        _last = now;
        _t += dt;
        _frames++;
        if (_t > 1 && dt > _expected * 1.6) _skips++; // o 1º segundo é a janela abrindo
        for (var i = 0; i < _ships.Count; i++)
            Canvas.SetLeft(_ships[i], (_t * Speeds[i]) % (ActualWidth + 120) - 120);
        if (_t - _windowStart >= 1)
        {
            _info.Text = $"Medido: {_frames / (_t - _windowStart):0} Hz · quadros pulados: {_skips}   (Esc fecha)";
            _frames = 0;
            _windowStart = _t;
        }
    }
}
