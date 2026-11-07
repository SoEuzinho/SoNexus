using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>
/// Guia do que se ajusta na BIOS (EXPO/XMP, PBO/Curve Optimizer, atualização), com o que foi detectado neste PC.
/// O SoNexus não mexe na BIOS: só explica, mostra o estado atual e manda medir antes/depois.
/// </summary>
public sealed class BiosGuideView : UserControl
{
    private readonly StackPanel _cards = new();

    public BiosGuideView()
    {
        var root = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        var h1 = new TextBlock { Text = "EXPO, PBO e BIOS" };
        h1.SetResourceReference(StyleProperty, "H1");
        var sub = new TextBlock
        {
            FontSize = 13, Margin = new Thickness(0, 4, 0, 16), TextWrapping = TextWrapping.Wrap,
            Text = "Os ganhos mais seguros e “de graça” ficam na BIOS da placa-mãe. O SoNexus não mexe na BIOS: ele mostra o que vale a pena no seu PC, " +
                   "como fazer, e mede antes e depois com o Teste completo.",
        };
        sub.SetResourceReference(StyleProperty, "Muted");
        root.Children.Add(h1);
        root.Children.Add(sub);
        root.Children.Add(_cards);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Loaded += async (_, _) =>
        {
            if (_cards.Children.Count > 0) return;
            _cards.Children.Add(Muted("Lendo as peças..."));
            var info = await Task.Run(Detect);
            _cards.Children.Clear();
            Build(info);
        };
    }

    private sealed record PcInfo(bool Amd, string Cpu, uint RamRunning, uint RamNominal, int Sticks, string BiosVersion, string BiosDate, string Board);

    private static PcInfo Detect()
    {
        uint running = 0, nominal = 0;
        var sticks = 0;
        try
        {
            var layout = MemoryInfoService.Layout();
            sticks = layout.Modules.Count;
            running = layout.Speed;
            nominal = layout.Modules.Select(m => Math.Max(m.RatedMts, HardwareHealth.SpeedFromPart(m.Part))).Where(v => v > 0).DefaultIfEmpty(0u).Min();
        }
        catch { }
        string Reg(string path, string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k?.GetValue(name)?.ToString()?.Trim() ?? "";
        }
        const string bios = @"HARDWARE\DESCRIPTION\System\BIOS";
        var cpu = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        return new PcInfo(Hardware.IsAmd(), cpu, running, nominal, sticks,
            Reg(bios, "BIOSVersion"), Reg(bios, "BIOSReleaseDate"),
            $"{Reg(bios, "BaseBoardManufacturer")} {Reg(bios, "BaseBoardProduct")}".Trim());
    }

    private void Build(PcInfo pc)
    {
        // RAM: EXPO/XMP
        var profile = pc.Amd ? "EXPO (ou DOCP/A-XMP)" : "XMP";
        var (ramState, ramLevel) = pc switch
        {
            { RamRunning: 0 } => ("Não deu para ler a velocidade dos pentes.", HealthLevel.Info),
            _ when pc.RamNominal > pc.RamRunning + 100 =>
                ($"Desligado: seus pentes rodam a {pc.RamRunning} MT/s, mas aguentam {pc.RamNominal} MT/s. Ativar dá cerca de {(pc.RamNominal / (double)pc.RamRunning - 1) * 100:0}% mais banda.", HealthLevel.Warn),
            _ => ($"Já está certo: {pc.RamRunning} MT/s, a velocidade dos pentes. ✔", HealthLevel.Ok),
        };
        _cards.Children.Add(Card($"Memória RAM: perfil {profile}", ramState, ramLevel, new[]
        {
            "Rode o Teste completo com o nome \"antes do EXPO\".",
            "Reinicie e entre na BIOS (aperte Del ou F2 várias vezes logo ao ligar).",
            $"Procure {profile} (fica em AI Tweaker, Extreme Tweaker, OC ou Tweaker, conforme a marca) e escolha o Perfil 1.",
            "Salve e saia (F10). O primeiro boot pode demorar ou reiniciar 2–3 vezes: é o treino da memória, é normal.",
            "Se não ligar depois de alguns minutos: desligue da tomada e limpe o CMOS (botão Clear CMOS ou a bateria por 1 minuto). Tudo volta ao padrão.",
            "Volte aqui e rode o Teste completo de novo e o Teste de RAM (Hardware › Memória RAM) por 30 minutos.",
        }, pc.Sticks == 1 ? "Você tem 1 pente só: o maior ganho é colocar um 2º pente igual (dual channel), mais até que o EXPO." : null));

        // Processador
        if (pc.Amd)
        {
            var x3d = pc.Cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase);
            _cards.Children.Add(Card("Processador: PBO e Curve Optimizer (AMD Ryzen)", pc.Cpu + (x3d ? " · modelo X3D: o PBO é limitado de fábrica, foque no Curve Optimizer." : ""), HealthLevel.Info, new[]
            {
                "Rode o Teste completo com o nome \"antes do PBO\".",
                "Na BIOS: Advanced › AMD Overclocking › Precision Boost Overdrive = Enabled (ou Advanced). Isso deixa o processador subir mais o clock dentro de limites seguros.",
                "Curve Optimizer › All Cores › Negative › comece com 10. Ele reduz a tensão em cada clock: menos calor, mais clock sustentado.",
                "Teste: Stress test do processador por 30 minutos e jogue normalmente por alguns dias. Travou, reiniciou sozinho ou fechou jogo? Volte 5 pontos (ex.: de 15 para 10).",
                "Não passe de 30. Valores altos só dão erro, não FPS.",
                "Rode o Teste completo de novo e compare. Se não melhorou nada, volte ao padrão (Auto): o ganho tem que ser medido, não suposto.",
            }, "Alternativa sem BIOS: o AMD Ryzen Master (site oficial da AMD) faz o mesmo pelo Windows e tem botão para voltar ao padrão."));
        }
        else
        {
            var gen1314 = System.Text.RegularExpressions.Regex.IsMatch(pc.Cpu, @"\b1[34]\d{3}");
            _cards.Children.Add(Card("Processador: limites de energia (Intel)", pc.Cpu + (gen1314 ? " · 13ª/14ª geração: veja o aviso abaixo." : ""), gen1314 ? HealthLevel.Warn : HealthLevel.Info, new[]
            {
                gen1314
                    ? "13ª e 14ª geração: atualize a BIOS para a versão mais nova (correção de microcódigo da Intel para instabilidade) e use o perfil \"Intel Default Settings\". Não aumente limites nesses modelos."
                    : "Rode o Teste completo com o nome \"antes\".",
                "Só modelos com K no nome aceitam overclock. Nos outros, o ganho é manter o processador frio (pasta térmica e cooler) para ele sustentar o turbo.",
                "Na BIOS, mantenha os limites de energia (PL1/PL2) no padrão Intel, a não ser que o cooler seja muito bom e o Stress test fique abaixo de 90 °C.",
                "Undervolt costuma vir bloqueado por segurança em BIOS novas; não force.",
                "Rode o Teste completo de novo e compare.",
            }, null));
        }

        // BIOS
        _cards.Children.Add(Card("Atualizar a BIOS", $"{pc.Board} · versão {pc.BiosVersion} de {pc.BiosDate}".Trim(' ', '·'), HealthLevel.Info, new[]
        {
            "BIOS nova traz suporte a memórias mais rápidas, correções de estabilidade e de segurança.",
            "Baixe só do site do fabricante da sua placa-mãe, na página do modelo exato.",
            "Use o método do fabricante (EZ Flash, Q-Flash, M-Flash ou o botão Flashback com um pendrive).",
            "Não desligue o PC durante a atualização e evite fazer em dia de queda de energia.",
            "Depois de atualizar, a BIOS volta ao padrão: ative o EXPO/XMP de novo.",
        }, null));

        // Placa de vídeo
        var go = new Button { Content = "Abrir Teste completo", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.Navigate("NavPerf", "Teste completo");
        var gpu = Card("Placa de vídeo", "Ajuste pelo SoNexus, sem BIOS: aba Overclock (limite de energia, clocks e ventoinhas).", HealthLevel.Info, Array.Empty<string>(), null);
        ((StackPanel)gpu.Child).Children.Add(go);
        _cards.Children.Add(gpu);
    }

    private static Border Card(string title, string state, HealthLevel level, IEnumerable<string> steps, string? note)
    {
        var panel = new StackPanel();
        var h = new TextBlock { Text = title };
        h.SetResourceReference(StyleProperty, "H2");
        panel.Children.Add(h);
        var status = new TextBlock { Text = state, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), FontWeight = level >= HealthLevel.Warn ? FontWeights.SemiBold : FontWeights.Normal };
        status.SetResourceReference(TextBlock.ForegroundProperty, level >= HealthLevel.Warn ? "TextBrush" : "MutedBrush");
        panel.Children.Add(status);
        var n = 1;
        foreach (var s in steps)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var num = Muted($"{n++}.");
            var text = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            Grid.SetColumn(text, 1);
            row.Children.Add(num);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        if (note != null)
        {
            var nt = Muted(note);
            nt.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(nt);
        }
        var card = new Border { Child = panel, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(StyleProperty, "Card");
        if (level >= HealthLevel.Warn) card.BorderBrush = Brushes.White;
        return card;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        t.SetResourceReference(StyleProperty, "Muted");
        return t;
    }
}
