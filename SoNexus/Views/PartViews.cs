using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>
/// Hardware organizado por peça. Cada peça tem as mesmas abas, na mesma ordem:
/// Info · Sensores · (testes da peça) · Dicas.
/// </summary>
public static class HardwareParts
{
    public static SectionView Section() => new(
        ("Visão geral", () => new HardwareView()),
        (HardwareHealth.Cpu, Cpu),
        (HardwareHealth.Gpu, Gpu),
        (HardwareHealth.Ram, Ram),
        (HardwareHealth.Storage, Storage),
        (HardwareHealth.Board, Board),
        (HardwareHealth.Power, Power),
        ("Todos os sensores", () => new SensorsView()));

    private static UserControl Cpu() => new SectionView(true,
        ("Info", () => new PartInfoView("Processador")),
        ("Sensores", () => new SensorsView("Sensores do processador", t => t == HardwareType.Cpu)),
        ("Benchmark", () => SharedPages.Get<BenchmarkView>()),
        ("Stress test", () => SharedPages.Get<StressView>()),
        ("Dicas", () => new PartTipsView(HardwareHealth.Cpu)));

    private static UserControl Gpu() => new SectionView(true,
        ("Info", () => new PartInfoView("Placa de vídeo")),
        ("Sensores", () => new SensorsView("Sensores da placa de vídeo", t => t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)),
        ("Benchmark", () => SharedPages.Get<HardwareBenchView>()),
        ("Stress test", () => SharedPages.Get<StressView>()),
        ("Dicas", () => new PartTipsView(HardwareHealth.Gpu)));

    private static UserControl Ram() => new SectionView(true,
        ("Info", () => new PartInfoView("Memória RAM")),
        ("Uso, latência e banda", () => new MemoryView()),
        ("Teste de RAM", () => new RamTestView()),
        ("Dicas", () => new PartTipsView(HardwareHealth.Ram)));

    private static UserControl Storage() => new SectionView(true,
        ("Info", () => new PartInfoView("Armazenamento")),
        ("Sensores", () => new SensorsView("Sensores dos discos", t => t == HardwareType.Storage)),
        ("Saúde e velocidade", () => new StorageView()),
        ("Teste de discos", () => new DiskCheckView()),
        ("Dicas", () => new PartTipsView(HardwareHealth.Storage)));

    private static UserControl Board() => new SectionView(true,
        ("Info", () => new PartInfoView("Placa-mãe e BIOS", "Sistema")),
        ("Sensores", () => new SensorsView("Sensores da placa-mãe", t => t is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController)),
        ("Dicas", () => new PartTipsView(HardwareHealth.Board)));

    private static UserControl Power() => new SectionView(true,
        ("Consumo e custo", () => new EnergyView()),
        ("Sensores", () => new SensorsView("Sensores de energia", t => t is HardwareType.Psu or HardwareType.Battery)),
        ("Dicas", () => new PartTipsView(HardwareHealth.Power)));
}

/// <summary>Aba "Info" de uma peça: só a seção dela (lê só o necessário).</summary>
public sealed class PartInfoView : UserControl
{
    private readonly string[] _sections;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { FontSize = 13, Margin = new Thickness(0, 0, 0, 14) };
    private bool _loaded;

    public PartInfoView(params string[] sections)
    {
        _sections = sections;
        _status.SetResourceReference(StyleProperty, "Muted");
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        Content = root;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _status.Text = "Lendo informações...";
            var data = await Task.Run(() => _sections.Select(SystemInfoService.CollectOne).ToList());
            foreach (var s in data) _list.Children.Add(UiParts.InfoCard(s));
            _status.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}";
        };
    }
}

/// <summary>Aba "Dicas" de uma peça: o que está bom, o que dá para melhorar e o que é urgente.</summary>
public sealed class PartTipsView : UserControl
{
    private readonly string _part;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = new() { Content = "Verificar de novo", HorizontalAlignment = HorizontalAlignment.Right };

    public PartTipsView(string part)
    {
        _part = part;
        _status.SetResourceReference(StyleProperty, "Muted");
        var top = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(_status);
        top.Children.Add(_refresh);
        _refresh.Click += async (_, _) => await LoadAsync();

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        Content = root;
        Loaded += async (_, _) => { if (_list.Children.Count == 0) await LoadAsync(); };
    }

    private async Task LoadAsync()
    {
        _refresh.IsEnabled = false;
        _status.Text = "Conferindo...";
        var tips = await Task.Run(() => HardwareHealth.For(_part));
        _list.Children.Clear();
        foreach (var t in tips) _list.Children.Add(UiParts.TipCard(t, showPart: false));
        var problems = tips.Count(t => t.IsProblem);
        _status.Text = problems == 0 ? "Nenhum problema encontrado nesta peça." : $"{problems} ponto(s) de atenção.";
        _refresh.IsEnabled = true;
    }
}

/// <summary>Pedaços de interface repetidos nas telas de peça.</summary>
public static class UiParts
{
    public static Border InfoCard(InfoSection s)
    {
        var panel = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var glyph = new TextBlock { Text = s.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var title = new TextBlock { Text = s.Title, Margin = new Thickness(10, 0, 0, 0) };
        title.SetResourceReference(FrameworkElement.StyleProperty, "H2");
        head.Children.Add(glyph);
        head.Children.Add(title);
        panel.Children.Add(head);

        foreach (var i in s.Items)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var k = new TextBlock { Text = i.Key, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            k.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            var v = new TextBlock { Text = i.Value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            v.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(v, 1);
            row.Children.Add(k);
            row.Children.Add(v);
            panel.Children.Add(row);
        }

        var card = new Border { Child = panel, Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        return card;
    }

    public static Border TipCard(HealthTip t, bool showPart)
    {
        var brush = t.Level switch
        {
            HealthLevel.Critical => "DangerBrush",
            HealthLevel.Warn => "WarnBrush",
            _ => "MutedBrush",
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var icon = new TextBlock { Text = t.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, Margin = new Thickness(0, 1, 14, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        grid.Children.Add(icon);

        var text = new StackPanel();
        var label = new TextBlock { Text = showPart ? $"{t.LevelText.ToUpperInvariant()} · {t.Part.ToUpperInvariant()}" : t.LevelText.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var title = new TextBlock { Text = t.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 4), TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var detail = new TextBlock { Text = t.Detail, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(label);
        text.Children.Add(title);
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var card = new Border { Child = grid, Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        if (t.Level == HealthLevel.Critical) card.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        return card;
    }
}
