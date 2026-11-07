// Vídeo de divulgação do SoNexus: narração (voz do Windows), cenas animadas em WPF quadro a quadro
// e timeline embaixo, na paleta do app (preto, cinzas e branco). O ffmpeg só junta quadros e áudio.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SoNexus.Views;
using Windows.Media.SpeechSynthesis;
using Path = System.IO.Path;

const int W = 1280, H = 720, Fps = 30;
var repo = args[0];
var ff = args[1];
var work = AppContext.BaseDirectory;
var media = Path.Combine(repo, "docs", "media");
Scene.MediaDir = media;

var scenes = new List<Scene>
{
    new("Início", "Intro", null, null, "Conheça o SoNexus: o seu PC inteiro, num app só.", "", Array.Empty<string>()),
    new("Hardware", "Shot", "hardware.png", new Rect(264, 208, 1006, 248),
        "Ele mostra a saúde de cada peça, e encontra sozinho o que está segurando o seu computador.",
        "Saúde do PC", new[] { "Cada peça com sensores e testes", "Acha RAM em canal único e EXPO desligado", "Explica o que fazer, em português" }),
    new("Jogos", "Shot", "gargalo.png", new Rect(264, 336, 992, 352),
        "Nos jogos, mede o FPS e o gargalo de verdade, durante a partida.",
        "Gargalo de verdade", new[] { "FPS, 1% low e engasgos", "Mede placa e processador na partida", "Sem mexer na memória do jogo" }),
    new("Painel", "Themes", null, null, "O painel sobre o jogo tem temas animados, e quase não usa o processador.",
        "Painel sobre o jogo", new[] { "Temas com animação", "Menos de 0,25% de CPU", "Desenhos próprios, sem marcas" }),
    new("Desempenho", "Shot", "expo-pbo-bios.png", new Rect(264, 204, 1008, 240),
        "Na área Desempenho, você faz um teste completo, compara antes e depois, e aprende a ativar EXPO e PBO.",
        "Desempenho", new[] { "Teste completo em 3 rodadas", "Compara antes e depois", "Guia de EXPO, PBO e BIOS" }),
    new("Ferramentas", "Shot", "instalar-programas.png", new Rect(264, 205, 1014, 330),
        "Em Ferramentas, libera memória, dá prioridade ao jogo e instala vários programas de uma vez.",
        "Ferramentas", new[] { "Libera a memória em espera", "Prioridade automática no jogo", "Instala vários programas de uma vez" }),
    new("RGB", "Shot", "rgb.png", new Rect(264, 186, 1012, 70),
        "Controla a iluminação RGB, até de fitas de LED Bluetooth.",
        "Iluminação", new[] { "OpenRGB: placa-mãe, memória e fans", "Fitas de LED Bluetooth", "Efeitos por temperatura e música" }),
    new("Windows", "Shot", "otimizacao.png", new Rect(264, 326, 1004, 360),
        "E deixa o Windows mais leve e sem propaganda, sempre com botão de desfazer.",
        "Windows mais leve", new[] { "Mais de 70 ajustes", "Sem telemetria e propaganda", "Tudo com desfazer" }),
    new("Baixar", "Outro", null, null, "SoNexus. Grátis e de código aberto. Baixe agora no GitHub.", "", Array.Empty<string>()),
};

// ---------- 1. Narração ----------
var synth = new SpeechSynthesizer { Voice = SpeechSynthesizer.AllVoices.First(v => v.DisplayName.Contains("Maria")) };
synth.Options.SpeakingRate = 1.08;
double start = 0;
for (var i = 0; i < scenes.Count; i++)
{
    var s = scenes[i];
    var wav = Path.Combine(work, $"voz{i}.wav");
    using (var stream = await synth.SynthesizeTextToStreamAsync(s.Voice))
    using (var file = File.Create(wav))
        await stream.AsStreamForRead().CopyToAsync(file);
    s.Wav = wav;
    s.VoiceSeconds = WavSeconds(wav);
    s.Start = start;
    s.Length = Math.Max(4.2, s.VoiceSeconds + 1.3) + (s.Kind == "Outro" ? 1.5 : 0);
    start += s.Length;
    Console.WriteLine($"{s.Chapter,-12} voz {s.VoiceSeconds:0.0} s · cena {s.Length:0.0} s");
}
var total = start;

// ---------- 2. Quadros ----------
var silent = Path.Combine(work, "video.mp4");
var th = new Thread(() => Render(silent));
th.SetApartmentState(ApartmentState.STA);
th.Start();
th.Join();

// ---------- 3. Áudio: vozes no tempo de cada cena + fundo suave (acorde gerado aqui, sem direito autoral) ----------
var inputs = string.Join(" ", scenes.Select(s => $"-i \"{s.Wav}\""));
var delays = string.Join(";", scenes.Select((s, i) => { var ms = (int)((s.Start + 0.45) * 1000); return $"[{i}:a]aresample=44100,adelay={ms}|{ms},volume=1.6[v{i}]"; }));
var pad = $"aevalsrc=0.035*sin(2*PI*110*t)*(0.7+0.3*sin(2*PI*0.2*t))+0.022*sin(2*PI*164.81*t)+0.018*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*0.13*t)):s=44100:d={total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}," +
          $"lowpass=f=900,afade=t=in:d=2,afade=t=out:st={(total - 2.5).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}:d=2.5[pad]";
var mix = $"{delays};{pad};{string.Concat(scenes.Select((_, i) => $"[v{i}]"))}[pad]amix=inputs={scenes.Count + 1}:normalize=0,alimiter=limit=0.9[a]";
var audio = Path.Combine(work, "audio.m4a");
var tot = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
Run(ff, $"-y -loglevel error {inputs} -filter_complex \"{mix}\" -map \"[a]\" -t {tot} -c:a aac -b:a 160k \"{audio}\"");
var output = Path.Combine(media, "divulgacao.mp4");
Run(ff, $"-y -loglevel error -i \"{silent}\" -i \"{audio}\" -c:v copy -c:a copy -shortest -movflags +faststart \"{output}\"");
Console.WriteLine($"pronto: {output} ({total:0.0} s)");

// ======================================================================

void Render(string path)
{
    var ffp = Process.Start(new ProcessStartInfo(ff,
        $"-y -loglevel error -f rawvideo -pix_fmt bgra -s {W}x{H} -r {Fps} -i - -c:v libx264 -crf 18 -preset slow -pix_fmt yuv420p \"{path}\"")
    { RedirectStandardInput = true, UseShellExecute = false })!;
    var pipe = ffp.StandardInput.BaseStream;

    var root = new Grid { Width = W, Height = H, Background = Pal.Bg };
    var stage = new Grid();
    root.Children.Add(stage);
    var timeline = new Timeline(scenes, total, W);
    root.Children.Add(timeline.Element);

    var built = scenes.Select(s => s.Build()).ToList();
    var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
    var pixels = new byte[W * H * 4];
    var frames = (int)Math.Ceiling(total * Fps);
    for (var f = 0; f < frames; f++)
    {
        var now = f / (double)Fps;
        var i = scenes.FindLastIndex(s => s.Start <= now);
        if (stage.Children.Count == 0 || stage.Children[0] != built[i])
        {
            stage.Children.Clear();
            stage.Children.Add(built[i]);
        }
        scenes[i].Animate(now - scenes[i].Start);
        timeline.Update(now, i);
        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();
        bmp.Clear();
        bmp.Render(root);
        bmp.CopyPixels(pixels, W * 4, 0);
        pipe.Write(pixels, 0, pixels.Length);
        if (f % 300 == 0) Console.WriteLine($"quadro {f}/{frames}");
    }
    pipe.Close();
    ffp.WaitForExit();
}

static double WavSeconds(string wav)
{
    var b = File.ReadAllBytes(wav);
    var byteRate = BitConverter.ToInt32(b, 28);
    for (var i = 12; i < b.Length - 8;)
    {
        var id = System.Text.Encoding.ASCII.GetString(b, i, 4);
        var size = BitConverter.ToInt32(b, i + 4);
        if (id == "data") return size / (double)byteRate;
        i += 8 + size;
    }
    return (b.Length - 44) / (double)byteRate;
}

static void Run(string exe, string args)
{
    var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"{exe} falhou ({p.ExitCode})");
}

// ======================================================================

static class Pal
{
    public static readonly Brush Bg = Make(0x0A, 0x0A, 0x0A);
    public static readonly Brush Line = Make(0x2A, 0x2A, 0x2A);
    public static readonly Brush Muted = Make(0x8A, 0x8A, 0x8A);
    public static readonly Brush Soft = Make(0xC8, 0xC8, 0xC8);
    public static readonly Brush White = Brushes.White;
    public static readonly FontFamily Font = new("Segoe UI");
    public static readonly FontFamily Semi = new("Segoe UI Semibold");

    static Brush Make(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
}

static class Ease
{
    public static double Clamp(double v) => Math.Clamp(v, 0, 1);
    public static double Out(double t) => 1 - Math.Pow(1 - Clamp(t), 3);
    public static double InOut(double t) { t = Clamp(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
    public static double Back(double t) { t = Clamp(t); const double c = 1.70158; return 1 + (c + 1) * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2); }
    /// <summary>0→1 entre a e b (segundos).</summary>
    public static double Span(double now, double a, double b) => Clamp((now - a) / (b - a));
}

/// <summary>Barra de capítulos embaixo: o capítulo atual em branco e a linha de progresso enchendo.</summary>
sealed class Timeline
{
    public FrameworkElement Element { get; }
    private readonly List<TextBlock> _labels = new();
    private readonly List<Rectangle> _ticks = new();
    private readonly Rectangle _fill = new() { Height = 2, Fill = Pal.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly double _total, _width;

    public Timeline(List<Scene> scenes, double total, int screenWidth)
    {
        _total = total;
        _width = screenWidth - 80;
        var g = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Height = 40, Margin = new Thickness(40, 0, 40, 16) };
        g.Children.Add(new Rectangle { Height = 2, Fill = Pal.Line, VerticalAlignment = VerticalAlignment.Top });
        g.Children.Add(_fill);
        var canvas = new Canvas();
        foreach (var s in scenes)
        {
            var x = s.Start / total * _width;
            var tick = new Rectangle { Width = 2, Height = 8, Fill = Pal.Line };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, -3);
            canvas.Children.Add(tick);
            _ticks.Add(tick);
            var label = new TextBlock { Text = s.Chapter.ToUpperInvariant(), FontFamily = Pal.Semi, FontSize = 11, Foreground = Pal.Muted };
            Canvas.SetLeft(label, x + 6);
            Canvas.SetTop(label, 12);
            canvas.Children.Add(label);
            _labels.Add(label);
        }
        g.Children.Add(canvas);
        Element = g;
    }

    public void Update(double now, int current)
    {
        _fill.Width = Math.Max(0, now / _total * _width);
        for (var i = 0; i < _labels.Count; i++)
        {
            _labels[i].Foreground = i == current ? Pal.White : i < current ? Pal.Soft : Pal.Muted;
            _ticks[i].Fill = i <= current ? Pal.White : Pal.Line;
        }
        Element.Opacity = Ease.Out(Ease.Span(now, 0.3, 1.2)) * (1 - Ease.Span(now, _total - 1.2, _total - 0.3));
    }
}

sealed class Scene
{
    public static string MediaDir = "";
    public string Chapter, Kind, Voice, Title;
    public string? Image;
    public Rect? Focus;
    public string[] Bullets;
    public string Wav = "";
    public double VoiceSeconds, Start, Length;
    public Action<double> Animate = _ => { };

    public Scene(string chapter, string kind, string? image, Rect? focus, string voice, string title, string[] bullets)
    {
        Chapter = chapter; Kind = kind; Image = image; Focus = focus; Voice = voice; Title = title; Bullets = bullets;
    }

    public FrameworkElement Build() => Kind switch
    {
        "Intro" => Intro(),
        "Outro" => Outro(),
        "Themes" => Themes(),
        _ => Shot(),
    };

    /// <summary>Sai suave no fim da cena (cada cena entra com o seu próprio movimento).</summary>
    private double FadeOut(double t) => 1 - Ease.InOut(Ease.Span(t, Length - 0.45, Length));

    private FrameworkElement Intro()
    {
        var g = new Grid();
        var glow = new Ellipse
        {
            Width = 900, Height = 900, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Fill = new RadialGradientBrush(Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)),
            RenderTransform = new ScaleTransform(), RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var letters = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 200, 0, 0) };
        var chars = "SoNexus".Select(c => new TextBlock { Text = c.ToString(), FontFamily = Pal.Semi, FontSize = 112, Foreground = Pal.White, RenderTransform = new TranslateTransform() }).ToList();
        chars.ForEach(c => letters.Children.Add(c));
        var bar = new Rectangle { Height = 3, Fill = Pal.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 362, 0, 0) };
        var sub = Text("O seu PC inteiro num app só.", 38, Pal.Soft, 392);
        var by = Text("por SoStudio · criado por SoEuzinho", 22, Pal.Muted, 452);
        g.Children.Add(glow);
        g.Children.Add(letters);
        g.Children.Add(bar);
        g.Children.Add(sub);
        g.Children.Add(by);
        Animate = t =>
        {
            for (var i = 0; i < chars.Count; i++)
            {
                chars[i].Opacity = Ease.Span(t, 0.15 + i * 0.07, 0.45 + i * 0.07);
                ((TranslateTransform)chars[i].RenderTransform).Y = (1 - Ease.Back(Ease.Span(t, 0.15 + i * 0.07, 0.75 + i * 0.07))) * 60;
            }
            bar.Width = 420 * Ease.Out(Ease.Span(t, 0.9, 1.6));
            Slide(sub, Ease.Out(Ease.Span(t, 1.2, 1.9)), 18);
            Slide(by, Ease.Out(Ease.Span(t, 1.6, 2.3)), 18);
            var sc = (ScaleTransform)glow.RenderTransform;
            sc.ScaleX = sc.ScaleY = 0.8 + 0.25 * Ease.InOut(Ease.Span(t, 0, Length));
            g.Opacity = FadeOut(t);
        };
        return g;
    }

    private FrameworkElement Outro()
    {
        var g = new Grid();
        var title = Text("SoNexus", 92, Pal.White, 160, Pal.Semi);
        var free = Text("Grátis e de código aberto", 40, Pal.White, 292, Pal.Semi);
        var url = Text("", 32, Pal.Soft, 360);
        var button = new Border
        {
            Background = Pal.White, CornerRadius = new CornerRadius(26), Padding = new Thickness(30, 12, 30, 12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 432, 0, 0),
            Child = new TextBlock { Text = "Baixar na página Releases", FontFamily = Pal.Semi, FontSize = 22, Foreground = Pal.Bg },
            RenderTransform = new ScaleTransform(), RenderTransformOrigin = new Point(0.5, 0.5),
        };
        g.Children.Add(title);
        g.Children.Add(free);
        g.Children.Add(url);
        g.Children.Add(button);
        const string link = "github.com/SoEuzinho/SoNexus";
        Animate = t =>
        {
            Slide(title, Ease.Out(Ease.Span(t, 0.1, 0.8)), 30);
            Slide(free, Ease.Out(Ease.Span(t, 0.5, 1.1)), 24);
            url.Opacity = 1;
            url.Text = link[..(int)(link.Length * Ease.Span(t, 1.0, 2.4))] + (t % 0.8 < 0.4 && t < 3.5 ? "|" : "");
            var sc = (ScaleTransform)button.RenderTransform;
            sc.ScaleX = sc.ScaleY = Ease.Back(Ease.Span(t, 2.3, 2.9)) * (1 + 0.03 * Math.Sin(Math.Max(0, t - 2.9) * 4));
            button.Opacity = Ease.Span(t, 2.3, 2.6);
            g.Opacity = FadeOut(t);
        };
        return g;
    }

    /// <summary>Texto à esquerda e a tela do app entrando pela direita, aproximando do destaque.</summary>
    private FrameworkElement Shot()
    {
        var g = new Grid();
        var (left, tag, title, line, bullets) = LeftColumn();
        g.Children.Add(left);

        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource = new Uri(System.IO.Path.Combine(MediaDir, Image!));
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        src.Freeze();
        const double sw = 800, sh = sw * 860 / 1320;
        var k = sw / 1320;
        var f = Focus!.Value;
        var cx = (f.X + f.Width / 2) * k;
        var cy = (f.Y + f.Height / 2) * k;

        var zoom = new ScaleTransform(1, 1, cx, cy);
        var inner = new Grid { Width = sw, Height = sh, RenderTransform = zoom };
        inner.Children.Add(new Image { Source = src, Width = sw, Height = sh, Stretch = Stretch.Fill });
        var focus = new Rectangle
        {
            Width = f.Width * k + 12, Height = f.Height * k + 12, RadiusX = 10, RadiusY = 10, Stroke = Pal.White, StrokeThickness = 3,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(f.X * k - 6, f.Y * k - 6, 0, 0),
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.8 },
        };
        inner.Children.Add(focus);
        var frame = new Border { Width = sw, Height = sh, Child = inner, ClipToBounds = true, Clip = new RectangleGeometry(new Rect(0, 0, sw, sh), 12, 12) };
        var border = new Border { Width = sw, Height = sh, CornerRadius = new CornerRadius(12), BorderBrush = Pal.Line, BorderThickness = new Thickness(1) };
        var holder = new Grid
        {
            Width = sw, Height = sh, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 44, 50),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.75, Color = Colors.Black },
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } }, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        holder.Children.Add(frame);
        holder.Children.Add(border);
        g.Children.Add(holder);

        Animate = t =>
        {
            AnimateLeft(t, tag, title, line, bullets);
            var enter = Ease.Out(Ease.Span(t, 0.05, 0.85));
            var tg = (TransformGroup)holder.RenderTransform;
            ((ScaleTransform)tg.Children[0]).ScaleX = ((ScaleTransform)tg.Children[0]).ScaleY = 0.92 + 0.08 * enter;
            ((TranslateTransform)tg.Children[1]).X = (1 - enter) * 160;
            holder.Opacity = enter;
            // Aproxima do destaque e solta um pouco no fim.
            var z = 1 + 0.2 * Ease.InOut(Ease.Span(t, 1.5, 3.3)) - 0.1 * Ease.InOut(Ease.Span(t, Length - 1.6, Length - 0.4));
            zoom.ScaleX = zoom.ScaleY = z;
            focus.Opacity = Ease.Span(t, 1.2, 1.6) * (0.75 + 0.25 * Math.Sin(t * 5));
            focus.StrokeThickness = 3 / z;
            g.Opacity = FadeOut(t);
        };
        return g;
    }

    /// <summary>Temas do painel aparecendo um a um, com os números mudando (valores de exemplo).</summary>
    private FrameworkElement Themes()
    {
        var g = new Grid();
        var (left, tag, title, line, bullets) = LeftColumn();
        g.Children.Add(left);
        var grid = new UniformGrid { Columns = 2, Width = 800, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 30, 60) };
        var cards = new List<(FrameworkElement Card, TextBlock Text, int Theme)>();
        foreach (var theme in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var text = OverlayWindow.NewText();
            var skin = OverlaySkin.Build(theme, text, 1, 70, false);
            skin.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new StackPanel { Margin = new Thickness(6, 4, 6, 4), RenderTransform = new ScaleTransform(), RenderTransformOrigin = new Point(0.5, 0.5) };
            cell.Children.Add(skin);
            cell.Children.Add(new TextBlock { Text = OverlaySkin.Names[theme], FontFamily = Pal.Semi, FontSize = 13, Foreground = Pal.Muted, HorizontalAlignment = HorizontalAlignment.Center });
            grid.Children.Add(cell);
            cards.Add((cell, text, theme));
        }
        g.Children.Add(grid);
        var rnd = new Random(7);
        var lastTick = -1;
        Animate = t =>
        {
            AnimateLeft(t, tag, title, line, bullets);
            for (var i = 0; i < cards.Count; i++)
            {
                var sc = (ScaleTransform)cards[i].Card.RenderTransform;
                sc.ScaleX = sc.ScaleY = 0.6 + 0.4 * Ease.Back(Ease.Span(t, 0.3 + i * 0.18, 0.85 + i * 0.18));
                cards[i].Card.Opacity = Ease.Span(t, 0.3 + i * 0.18, 0.6 + i * 0.18);
            }
            var tick = (int)(t * 4);
            if (tick != lastTick)
            {
                lastTick = tick;
                foreach (var (_, text, theme) in cards)
                {
                    var v = OverlaySkin.ValueBrush(theme, 0);
                    var fps = 138 + rnd.Next(0, 10);
                    OverlayWindow.Render(text, new List<(string, string, Brush)>
                    {
                        ("FPS", $"{fps}  1% {fps - 36}", v), ("CPU", $"{rnd.Next(18, 31)}%  {rnd.Next(60, 64)} °C", v),
                        ("GPU", $"{rnd.Next(95, 99)}%  {rnd.Next(66, 69)} °C", v), ("RAM", "11,2/16 GB  jogo 3,1 GB", v),
                    }, false);
                }
            }
            g.Opacity = FadeOut(t);
        };
        return g;
    }

    private (StackPanel Left, TextBlock Tag, TextBlock Title, Rectangle Line, List<FrameworkElement> Bullets) LeftColumn()
    {
        var left = new StackPanel { Width = 350, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(56, 0, 0, 60) };
        var tag = new TextBlock { Text = Chapter.ToUpperInvariant(), FontFamily = Pal.Semi, FontSize = 14, Foreground = Pal.Muted, RenderTransform = new TranslateTransform() };
        var title = new TextBlock { Text = Title, FontFamily = Pal.Semi, FontSize = 42, Foreground = Pal.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), RenderTransform = new TranslateTransform() };
        var line = new Rectangle { Height = 3, Width = 0, Fill = Pal.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 22) };
        left.Children.Add(tag);
        left.Children.Add(title);
        left.Children.Add(line);
        var bullets = new List<FrameworkElement>();
        foreach (var b in Bullets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14), RenderTransform = new TranslateTransform() };
            var dot = new Rectangle { Width = 8, Height = 8, Fill = Pal.White, Margin = new Thickness(0, 10, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = b, FontFamily = Pal.Font, FontSize = 21, Foreground = Pal.Soft, TextWrapping = TextWrapping.Wrap });
            left.Children.Add(row);
            bullets.Add(row);
        }
        return (left, tag, title, line, bullets);
    }

    private static void AnimateLeft(double t, TextBlock tag, TextBlock title, Rectangle line, List<FrameworkElement> bullets)
    {
        Slide(tag, Ease.Out(Ease.Span(t, 0.1, 0.6)), 14, horizontal: true);
        Slide(title, Ease.Out(Ease.Span(t, 0.2, 0.8)), 24, horizontal: true);
        line.Width = 90 * Ease.Out(Ease.Span(t, 0.6, 1.1));
        for (var i = 0; i < bullets.Count; i++) Slide(bullets[i], Ease.Out(Ease.Span(t, 1.0 + i * 0.4, 1.5 + i * 0.4)), 22, horizontal: true);
    }

    private static TextBlock Text(string text, double size, Brush color, double top, FontFamily? font = null) => new()
    {
        Text = text, FontFamily = font ?? Pal.Font, FontSize = size, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, top, 0, 0), RenderTransform = new TranslateTransform(),
    };

    private static void Slide(FrameworkElement e, double k, double distance, bool horizontal = false)
    {
        e.Opacity = k;
        var tr = (TranslateTransform)e.RenderTransform;
        if (horizontal) tr.X = (1 - k) * -distance;
        else tr.Y = (1 - k) * distance;
    }
}
