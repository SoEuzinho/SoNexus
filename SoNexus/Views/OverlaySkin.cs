using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SoNexus.Views;

/// <summary>
/// Temas do painel sobre o jogo. Todos desenhados aqui, com formas e ícones próprios do SoNexus:
/// nenhum usa logo, nome ou personagem de jogo ou empresa.
/// Animações são lentas e a 15 quadros por segundo: o painel não pode pesar no jogo.
/// </summary>
public static class OverlaySkin
{
    public static readonly string[] Names = { "Clássico", "Neon", "Imperial", "Pixel", "Arena", "Fofo", "Cristal" };

    private const string LabelKey = "OverlayLabel";

    /// <summary>Cor dos rótulos (FPS, CPU...) do tema em uso.</summary>
    public static Brush LabelBrush(TextBlock text) => text.Resources[LabelKey] as Brush ?? Brushes.Gray;

    /// <summary>Cor dos números: a escolhida, menos nos temas claros, onde branco não daria para ler.</summary>
    public static Brush ValueBrush(int theme, int color) => (theme, color) switch
    {
        (5, 0) => Solid(0x5A, 0x1F, 0x3D),
        (6, 0) => Solid(0x0D, 0x24, 0x4F),
        _ => OverlayWindow.ValueBrush(color),
    };

    /// <summary>Monta o painel do tema em volta do texto (o mesmo texto serve ao jogo e à prévia).</summary>
    public static FrameworkElement Build(int theme, TextBlock text, int size, int opacity, bool animate)
    {
        Detach(text);
        text.FontSize = size switch { 0 => 12, 2 => 20, _ => 15 };
        text.Tag = null; // rótulos mudam de cor: desenha as linhas de novo
        text.VerticalAlignment = VerticalAlignment.Center;
        var a = (byte)(Math.Clamp(opacity, 0, 100) * 2.55);
        var badge = text.FontSize * 4; // selo da direita acompanha o tamanho da letra
        var root = new Grid { Margin = new Thickness(14) }; // espaço para enfeites que saem da borda
        var fx = new Fx(animate);
        Brush label;

        switch (theme)
        {
            case 1: label = Neon(root, text, a, badge, fx); break;
            case 2: label = Imperial(root, text, a, badge, fx); break;
            case 3: label = Pixel(root, text, a, badge, fx); break;
            case 4: label = Arena(root, text, a, badge, fx); break;
            case 5: label = Cute(root, text, a, badge, fx); break;
            case 6: label = Crystal(root, text, a, badge, fx); break;
            default:
                label = Brushes.Gray;
                root.Children.Add(new ChamferBorder { Fill = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0)), Cut = 6, Padding = new Thickness(11, 6, 11, 6), Child = text });
                break;
        }

        text.Resources[LabelKey] = label;
        // Sem fundo, uma sombra deixa os números legíveis em cena clara (não nos temas claros).
        text.Effect = opacity < 30 && theme is not (5 or 6) ? new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 1 } : null;
        return root;
    }

    // ---------- Temas ----------

    /// <summary>Neon: vidro escuro, borda roxo→azul com luz correndo, brilho que respira, controle no selo.</summary>
    private static Brush Neon(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var purple = Color.FromRgb(0x8B, 0x5C, 0xF6);
        var blue = Color.FromRgb(0x3B, 0x82, 0xF6);
        var glow = new ChamferBorder { Fill = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Cut = 14, Corners = Cut.TopRight | Cut.BottomLeft, Effect = new BlurEffect { Radius = 16 }, Opacity = 0.45 };
        root.Children.Add(glow);
        fx.Pulse(glow, UIElement.OpacityProperty, 0.25, 0.75, 1.6);

        var icon = Icon(Gamepad, new SolidColorBrush(purple), badge * 0.72);
        icon.Effect = new DropShadowEffect { Color = purple, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 };
        root.Children.Add(Frame(text, Badge(icon, badge, Color.FromArgb(0x40, 0x5B, 0x3D, 0xD6)),
            new ChamferBorder
            {
                Fill = new LinearGradientBrush(Color.FromArgb(a, 0x10, 0x0C, 0x30), Color.FromArgb(a, 0x08, 0x0A, 0x22), 0),
                Stroke = new LinearGradientBrush(purple, blue, 0), StrokeThickness = 2.5, Cut = 14, Corners = Cut.TopRight | Cut.BottomLeft,
            }));
        root.Children.Add(new ChamferBorder { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0x8B, 0x5C, 0xF6)), StrokeThickness = 1, Cut = 10, Corners = Cut.TopRight | Cut.BottomLeft, Margin = new Thickness(5), IsHitTestVisible = false });
        root.Children.Add(Sweep(14, Cut.TopRight | Cut.BottomLeft, Color.FromRgb(0xE9, 0xDD, 0xFF), 2.5, 2.4, fx));

        // Três barras inclinadas no topo, acendendo uma de cada vez.
        var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, 26, 0) };
        for (var i = 0; i < 3; i++)
        {
            var bar = new Rectangle { Width = 9, Height = 5, Margin = new Thickness(2, 0, 0, 0), Fill = new SolidColorBrush(purple), RenderTransform = new SkewTransform(-35, 0) };
            bars.Children.Add(bar);
            fx.Pulse(bar, UIElement.OpacityProperty, 0.25, 1, 0.6, i * 0.2);
        }
        root.Children.Add(bars);
        return Solid(0xB4, 0xA6, 0xFF);
    }

    /// <summary>Imperial: moldura dourada pesada com rebites, brasão no topo, caveira no selo e lacre vermelho.</summary>
    private static Brush Imperial(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var gold = Gold();
        var dark = Color.FromArgb(a, 0x17, 0x14, 0x11);
        root.Children.Add(new ChamferBorder { Fill = Solid(0x2A, 0x1D, 0x10), Cut = 10, Margin = new Thickness(-4), Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.8 } });
        var icon = Icon(Skull, gold, badge * 0.6);
        root.Children.Add(Frame(text, Badge(icon, badge, Color.FromArgb(0xD0, 0x0D, 0x0B, 0x09), gold),
            new ChamferBorder { Fill = new SolidColorBrush(dark), Stroke = gold, StrokeThickness = 3.5, Cut = 10, Padding = new Thickness(15, 10, 12, 10) }));
        root.Children.Add(new ChamferBorder { Stroke = Solid(0x6B, 0x52, 0x22), StrokeThickness = 1, Cut = 7, Margin = new Thickness(6), IsHitTestVisible = false });
        root.Children.Add(Sweep(10, Cut.All, Color.FromRgb(0xFF, 0xF1, 0xC2), 3.5, 3.2, fx));

        foreach (var (h, v) in new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Left, VerticalAlignment.Bottom), (HorizontalAlignment.Right, VerticalAlignment.Top) })
            root.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = gold, Stroke = Solid(0x4A, 0x36, 0x10), StrokeThickness = 1, HorizontalAlignment = h, VerticalAlignment = v, Margin = new Thickness(5) });

        root.Children.Add(new Path
        {
            Data = Geometry.Parse("M 0,6 L 16,0 L 26,7 L 36,0 L 52,6 L 38,10 L 31,15 L 26,11 L 21,15 L 14,10 Z M 26,2 L 30,9 L 26,17 L 22,9 Z"),
            Fill = gold, Stroke = Solid(0x4A, 0x36, 0x10), StrokeThickness = 0.8, Stretch = Stretch.Uniform, Width = 92, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -18, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.8 },
        });
        var bottom = Icon(Skull, gold, 16);
        bottom.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        bottom.Margin = new Thickness(0, 0, 0, -9);
        bottom.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.8 };
        root.Children.Add(bottom);
        var seal = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -10, -10), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        seal.Children.Add(new Ellipse { Fill = new RadialGradientBrush(Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x5E, 0x0B, 0x0B)), Stroke = Solid(0x3A, 0x06, 0x06), StrokeThickness = 1.5 });
        seal.Children.Add(Icon(Skull, Solid(0x3A, 0x06, 0x06), 13));
        root.Children.Add(seal);
        fx.Beat((ScaleTransform)seal.RenderTransform, 1.08, 2.4);
        return Solid(0xD4, 0xAF, 0x62);
    }

    /// <summary>Pixel: bloco de terra com grama em cima e embaixo, pontinhos de terra e coração pixelado batendo.</summary>
    private static Brush Pixel(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(a, 0x5B, 0x3A, 0x1E)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        foreach (var (x, y) in new[] { (2, 3), (9, 1), (13, 7), (5, 10), (11, 13), (1, 14) })
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(a, 0x47, 0x2C, 0x15)), null, new RectangleGeometry(new Rect(x, y, 2, 2))));
        var dirt = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };

        var heart = Icon(PixelHeart, Solid(0xE5, 0x39, 0x35), badge * 0.55);
        heart.RenderTransformOrigin = new Point(0.5, 0.5);
        heart.RenderTransform = new ScaleTransform();
        fx.Beat((ScaleTransform)heart.RenderTransform, 1.15, 1.0);
        root.Children.Add(Frame(text, Badge(heart, badge, Color.FromArgb(0x50, 0x2E, 0x1B, 0x0B), Solid(0x3E, 0x7B, 0x27)),
            new ChamferBorder { Fill = dirt, Stroke = Solid(0x2F, 0x5E, 0x1D), StrokeThickness = 3, Cut = 0, Padding = new Thickness(12, 14, 10, 12) }));
        root.Children.Add(Grass(VerticalAlignment.Top, new Thickness(-2, -5, -2, 0), false));
        root.Children.Add(Grass(VerticalAlignment.Bottom, new Thickness(-2, 0, -2, -4), true));
        return Solid(0xE8, 0xD8, 0xB4);
    }

    /// <summary>Arena: preto com cantos cortados vermelhos, faixa branca, estilhaços girando e raio no selo.</summary>
    private static Brush Arena(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var red = Color.FromRgb(0xE5, 0x39, 0x35);
        var glow = new ChamferBorder { Fill = new SolidColorBrush(red), Cut = 16, Corners = Cut.TopLeft | Cut.BottomRight, Effect = new BlurEffect { Radius = 12 }, Opacity = 0.35 };
        root.Children.Add(glow);
        fx.Pulse(glow, UIElement.OpacityProperty, 0.15, 0.6, 0.9);
        var bolt = Icon(Bolt, Brushes.White, badge * 0.6);
        bolt.Effect = new DropShadowEffect { Color = red, BlurRadius = 10, ShadowDepth = 0 };
        root.Children.Add(Frame(text, Badge(bolt, badge, Color.FromArgb(0x60, 0x3A, 0x08, 0x08), new SolidColorBrush(red)),
            new ChamferBorder { Fill = new SolidColorBrush(Color.FromArgb(a, 0x10, 0x10, 0x14)), Stroke = new SolidColorBrush(red), StrokeThickness = 2.5, Cut = 16, Corners = Cut.TopLeft | Cut.BottomRight }));
        root.Children.Add(Sweep(16, Cut.TopLeft | Cut.BottomRight, Color.FromRgb(0xFF, 0xD0, 0xCC), 2.5, 1.8, fx));
        root.Children.Add(new Rectangle { Width = 5, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-3, 20, 0, 20), RenderTransform = new SkewTransform(0, -20) });
        root.Children.Add(new Rectangle { Width = 46, Height = 5, Fill = new SolidColorBrush(red), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -3), RenderTransform = new SkewTransform(-35, 0) });
        foreach (var (size, x, y, secs) in new[] { (9.0, -16.0, -6.0, 6.0), (6.0, -4.0, 14.0, 4.0), (7.0, -26.0, 30.0, 8.0) })
        {
            var shard = new Rectangle
            {
                Width = size, Height = size, Fill = new SolidColorBrush(red), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, y, badge - x, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(20),
            };
            root.Children.Add(shard);
            fx.Spin((RotateTransform)shard.RenderTransform, secs);
        }
        return Solid(0xFF, 0x8A, 0x80);
    }

    /// <summary>Fofo: rosa claro, corações flutuando, brilhinhos piscando, laço e coração grande batendo no selo.</summary>
    private static Brush Cute(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var pink = Color.FromRgb(0xFF, 0x5F, 0xA2);
        var bgA = Math.Max(a, (byte)235);
        root.Children.Add(new ChamferBorder { Fill = Solid(0xFF, 0xB3, 0xD1), Cut = 0, Radius = 22, Margin = new Thickness(-3), Effect = new BlurEffect { Radius = 6 }, Opacity = 0.8 });
        var heart = Icon(HeartShape, new LinearGradientBrush(Color.FromRgb(0xFF, 0x8F, 0xC0), Color.FromRgb(0xFF, 0x4F, 0x9A), 90), badge * 0.6);
        heart.RenderTransformOrigin = new Point(0.5, 0.5);
        heart.RenderTransform = new ScaleTransform();
        fx.Beat((ScaleTransform)heart.RenderTransform, 1.12, 1.2);
        root.Children.Add(Frame(text, Badge(heart, badge, Color.FromArgb(0x00, 0, 0, 0)),
            new ChamferBorder
            {
                Fill = new LinearGradientBrush(Color.FromArgb(bgA, 0xFF, 0xF5, 0xFA), Color.FromArgb(bgA, 0xFF, 0xD6, 0xE8), 90),
                Stroke = new SolidColorBrush(pink), StrokeThickness = 2.5, Radius = 20, Cut = 0, Padding = new Thickness(18, 9, 12, 9),
            }));
        foreach (var (s, h, v, m, d) in new[]
                 {
                     (17.0, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-8, -9, 0, 0), 1.8),
                     (12.0, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(-6, 0, 0, -6), 1.4),
                     (11.0, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -5, -6), 1.3),
                     (9.0, HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(30, -8, 0, 0), 2.1),
                 })
        {
            var small = Icon(HeartShape, Solid(0xFF, 0x7E, 0xB6), s);
            small.HorizontalAlignment = h;
            small.VerticalAlignment = v;
            small.Margin = m;
            small.RenderTransform = new TranslateTransform();
            root.Children.Add(small);
            fx.Pulse((TranslateTransform)small.RenderTransform, TranslateTransform.YProperty, 0, -3, d);
        }
        foreach (var (h, v, m, d) in new[] { (HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 40, 3), 0.9), (HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 6, badge + 6, 0), 1.3) })
        {
            var spark = Icon(Sparkle, Brushes.White, 10);
            spark.HorizontalAlignment = h;
            spark.VerticalAlignment = v;
            spark.Margin = m;
            spark.Effect = new DropShadowEffect { Color = pink, BlurRadius = 6, ShadowDepth = 0 };
            root.Children.Add(spark);
            fx.Pulse(spark, UIElement.OpacityProperty, 0.1, 1, d);
        }
        root.Children.Add(Bow(pink));
        return Solid(0xD6, 0x33, 0x7F);
    }

    /// <summary>Cristal: vidro azul claro com reflexo passando e estrela no selo redondo.</summary>
    private static Brush Crystal(Grid root, TextBlock text, byte a, double badge, Fx fx)
    {
        var blue = Color.FromRgb(0x1E, 0x88, 0xE5);
        var bgA = Math.Max(a, (byte)225);
        root.Children.Add(new ChamferBorder { Fill = new SolidColorBrush(blue), Cut = 12, Corners = Cut.TopLeft | Cut.BottomRight, Effect = new BlurEffect { Radius = 10 }, Opacity = 0.5 });
        var circle = new Grid { Width = badge * 0.8, Height = badge * 0.8 };
        circle.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush(Color.FromRgb(0x64, 0xB5, 0xF6), Color.FromRgb(0x0D, 0x47, 0xA1)) { GradientOrigin = new Point(0.35, 0.3) },
            Stroke = Brushes.White, StrokeThickness = 2,
        });
        var star = Icon(Star, Brushes.White, badge * 0.46);
        star.RenderTransformOrigin = new Point(0.5, 0.5);
        star.RenderTransform = new RotateTransform();
        circle.Children.Add(star);
        fx.Pulse((RotateTransform)star.RenderTransform, RotateTransform.AngleProperty, -10, 10, 2.2);
        root.Children.Add(Frame(text, Badge(circle, badge, Color.FromArgb(0, 0, 0, 0)),
            new ChamferBorder
            {
                Fill = new LinearGradientBrush(Color.FromArgb(bgA, 0xF4, 0xFA, 0xFF), Color.FromArgb(bgA, 0xC9, 0xE4, 0xFB), 90),
                Stroke = new LinearGradientBrush(Color.FromRgb(0x42, 0xA5, 0xF5), Color.FromRgb(0x15, 0x65, 0xC0), 90), StrokeThickness = 3,
                Cut = 12, Corners = Cut.TopLeft | Cut.BottomRight,
            }));
        root.Children.Add(new ChamferBorder { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.2, Cut = 9, Corners = Cut.TopLeft | Cut.BottomRight, Margin = new Thickness(4), IsHitTestVisible = false });
        root.Children.Add(Sweep(12, Cut.TopLeft | Cut.BottomRight, Colors.White, 3, 2.6, fx));
        // Faixa de reflexo do vidro passando por dentro.
        var shine = new Rectangle
        {
            Width = 26, Opacity = 0.35, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(0, 255, 255, 255), 0), new(Colors.White, 0.5), new(Color.FromArgb(0, 255, 255, 255), 1) }, 0),
            RenderTransform = new TransformGroup { Children = { new SkewTransform(-20, 0), new TranslateTransform() } },
        };
        root.Children.Add(new Border { ClipToBounds = true, Child = shine, Margin = new Thickness(3) });
        fx.Pulse((TranslateTransform)((TransformGroup)shine.RenderTransform).Children[1], TranslateTransform.XProperty, -40, 420, 3.5, reverse: false);
        return Solid(0x15, 0x65, 0xC0);
    }

    // ---------- Peças ----------

    /// <summary>Moldura com o texto à esquerda e o selo à direita, separados por um traço fino.</summary>
    private static ChamferBorder Frame(TextBlock text, FrameworkElement badge, ChamferBorder frame)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        if (frame.Padding == default) frame.Padding = new Thickness(13, 8, 10, 8);
        frame.Child = grid;
        return frame;
    }

    private static FrameworkElement Badge(FrameworkElement icon, double size, Color plate, Brush? line = null)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var box = new Border
        {
            Width = size, MinHeight = size * 0.8, Margin = new Thickness(14, 0, 0, 0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(plate), BorderBrush = line, BorderThickness = new Thickness(line == null ? 0 : 1),
            Child = icon,
        };
        return box;
    }

    /// <summary>Luz que dá a volta na borda (um traço claro passando de um lado ao outro).</summary>
    private static ChamferBorder Sweep(double cut, Cut corners, Color light, double thickness, double seconds, Fx fx)
    {
        var move = new TranslateTransform(-1, 0);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.3), RelativeTransform = move,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 0.35),
                new GradientStop(light, 0.5),
                new GradientStop(Color.FromArgb(0, light.R, light.G, light.B), 0.65),
            },
        };
        fx.Pulse(move, TranslateTransform.XProperty, -1, 1, seconds, reverse: false);
        if (!fx.On) move.X = -1; // parado: sem luz
        return new ChamferBorder { Stroke = brush, StrokeThickness = thickness, Cut = cut, Corners = corners, IsHitTestVisible = false };
    }

    private static UIElement Grass(VerticalAlignment v, Thickness margin, bool flip)
    {
        var grass = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 30, VerticalAlignment = v, Height = 13, Margin = margin };
        int[] steps = { 8, 12, 6, 12, 13, 8, 6, 11, 9, 13, 6, 9, 12 };
        for (var i = 0; i < 30; i++)
            grass.Children.Add(new Rectangle
            {
                Height = steps[(i + (flip ? 5 : 0)) % steps.Length] * (flip ? 0.7 : 1), VerticalAlignment = flip ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                Fill = i % 4 == 0 ? Solid(0x3F, 0x80, 0x26) : i % 3 == 0 ? Solid(0x6C, 0xC2, 0x45) : Solid(0x5D, 0xAE, 0x3B),
            });
        return grass;
    }

    private static FrameworkElement Bow(Color pink)
    {
        var bow = new Grid
        {
            Width = 40, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -13, 14, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(8),
        };
        bow.Children.Add(new Path
        {
            Data = Geometry.Parse("M 20,11 C 12,0 0,0 2,8 C 3,14 10,16 20,11 Z M 20,11 C 28,0 40,0 38,8 C 37,14 30,16 20,11 Z"),
            Fill = new SolidColorBrush(pink), Stroke = Brushes.White, StrokeThickness = 1.2, Stretch = Stretch.Fill,
        });
        bow.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Solid(0xE0, 0x2F, 0x7E), Stroke = Brushes.White, StrokeThickness = 1.2 });
        return bow;
    }

    private static Path Icon(string data, Brush fill, double size) => new()
    {
        Data = Geometry.Parse(data), Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size,
    };

    private static LinearGradientBrush Gold() => new(new GradientStopCollection
    {
        new(Color.FromRgb(0x7A, 0x55, 0x1A), 0), new(Color.FromRgb(0xF5, 0xD2, 0x7A), 0.45), new(Color.FromRgb(0xB8, 0x89, 0x2B), 0.7), new(Color.FromRgb(0x6E, 0x4C, 0x16), 1),
    }, 45);

    // Ícones próprios (desenhados para o SoNexus).
    private const string Gamepad = "M 7,5 H 17 C 20.5,5 23,8.5 23,12.5 C 23,16 21.5,18 19.5,18 C 17.5,18 16.5,15 14.5,15 H 9.5 C 7.5,15 6.5,18 4.5,18 C 2.5,18 1,16 1,12.5 C 1,8.5 3.5,5 7,5 Z M 6,9 V 10.5 H 4.5 V 12 H 6 V 13.5 H 7.5 V 12 H 9 V 10.5 H 7.5 V 9 Z M 17,9.2 A 1.1,1.1 0 1 0 17.01,9.2 Z M 19,11.4 A 1.1,1.1 0 1 0 19.01,11.4 Z";
    private const string Skull = "F0 M 12,2 C 6.5,2 3,6 3,10.5 C 3,13.5 4.5,15.5 7,16.5 V 21 H 17 V 16.5 C 19.5,15.5 21,13.5 21,10.5 C 21,6 17.5,2 12,2 Z M 7,10.5 A 2.2,2.2 0 1 0 11.4,10.5 A 2.2,2.2 0 1 0 7,10.5 Z M 12.6,10.5 A 2.2,2.2 0 1 0 17,10.5 A 2.2,2.2 0 1 0 12.6,10.5 Z M 11,14.5 L 12,16.2 L 13,14.5 Z M 9.5,18.5 H 10.5 V 21 H 9.5 Z M 13.5,18.5 H 14.5 V 21 H 13.5 Z";
    private const string PixelHeart = "M 2,0 H 6 V 2 H 8 V 0 H 12 V 2 H 14 V 7 H 12 V 9 H 10 V 11 H 8 V 13 H 6 V 11 H 4 V 9 H 2 V 7 H 0 V 2 H 2 Z";
    private const string Bolt = "M 13,1 L 3,14 H 10 L 8,23 L 21,8 H 13.5 Z";
    private const string HeartShape = "M 5,9 C 5,9 0,6 0,3 C 0,0.5 3.5,-0.5 5,2.2 C 6.5,-0.5 10,0.5 10,3 C 10,6 5,9 5,9 Z";
    private const string Sparkle = "M 5,0 L 6,4 L 10,5 L 6,6 L 5,10 L 4,6 L 0,5 L 4,4 Z";
    private const string Star = "M 12,2 L 14.9,8.6 L 22,9.3 L 16.6,14 L 18.2,21 L 12,17.3 L 5.8,21 L 7.4,14 L 2,9.3 L 9.1,8.6 Z";

    /// <summary>O mesmo texto vai para o painel novo: sai do anterior.</summary>
    private static void Detach(TextBlock text)
    {
        switch (text.Parent)
        {
            case Decorator d: d.Child = null; break;
            case Panel p: p.Children.Remove(text); break;
            case ContentControl c: c.Content = null; break;
        }
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Animações do tema: todas a 15 quadros por segundo; com "Animar" desligado, nada se mexe.</summary>
    private sealed class Fx(bool on)
    {
        public bool On => on;

        public void Pulse(IAnimatable target, DependencyProperty property, double from, double to, double seconds, double delay = 0, bool reverse = true)
        {
            if (!on) return;
            var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = reverse, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay),
                EasingFunction = reverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null,
            };
            Timeline.SetDesiredFrameRate(anim, 15);
            target.BeginAnimation(property, anim);
        }

        /// <summary>"Batida": cresce e volta, para corações e lacres.</summary>
        public void Beat(ScaleTransform scale, double to, double seconds)
        {
            Pulse(scale, ScaleTransform.ScaleXProperty, 1, to, seconds / 2);
            Pulse(scale, ScaleTransform.ScaleYProperty, 1, to, seconds / 2);
        }

        public void Spin(RotateTransform rotate, double seconds) =>
            Pulse(rotate, RotateTransform.AngleProperty, 0, 360, seconds, reverse: false);
    }
}

/// <summary>Quais cantos são cortados em diagonal.</summary>
[Flags]
public enum Cut { None = 0, TopLeft = 1, TopRight = 2, BottomRight = 4, BottomLeft = 8, All = 15 }

/// <summary>
/// Moldura com cantos cortados em diagonal (ou arredondados), desenhada direto: fundo, borda e o conteúdo dentro.
/// Sem filho, ela ocupa o espaço todo (usada para brilho e luz por cima da moldura principal).
/// </summary>
public sealed class ChamferBorder : Decorator
{
    public Brush? Fill { get; set; }
    public Brush? Stroke { get; set; }
    public double StrokeThickness { get; set; }
    public double Cut { get; set; } = 8;
    public double Radius { get; set; }
    public Cut Corners { get; set; } = Views.Cut.All;
    public Thickness Padding { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        var extra = new Size(Padding.Left + Padding.Right, Padding.Top + Padding.Bottom);
        if (Child == null) return extra;
        Child.Measure(new Size(Math.Max(0, available.Width - extra.Width), Math.Max(0, available.Height - extra.Height)));
        return new Size(Child.DesiredSize.Width + extra.Width, Child.DesiredSize.Height + extra.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(Padding.Left, Padding.Top,
            Math.Max(0, final.Width - Padding.Left - Padding.Right), Math.Max(0, final.Height - Padding.Top - Padding.Bottom)));
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var inset = StrokeThickness / 2;
        var r = new Rect(inset, inset, Math.Max(0, RenderSize.Width - StrokeThickness), Math.Max(0, RenderSize.Height - StrokeThickness));
        Geometry shape;
        if (Radius > 0) shape = new RectangleGeometry(r, Radius, Radius);
        else
        {
            var c = Math.Min(Cut, Math.Min(r.Width, r.Height) / 2);
            double C(Cut corner) => (Corners & corner) != 0 ? c : 0;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(r.Left + C(Views.Cut.TopLeft), r.Top), true, true);
                ctx.LineTo(new Point(r.Right - C(Views.Cut.TopRight), r.Top), true, false);
                ctx.LineTo(new Point(r.Right, r.Top + C(Views.Cut.TopRight)), true, false);
                ctx.LineTo(new Point(r.Right, r.Bottom - C(Views.Cut.BottomRight)), true, false);
                ctx.LineTo(new Point(r.Right - C(Views.Cut.BottomRight), r.Bottom), true, false);
                ctx.LineTo(new Point(r.Left + C(Views.Cut.BottomLeft), r.Bottom), true, false);
                ctx.LineTo(new Point(r.Left, r.Bottom - C(Views.Cut.BottomLeft)), true, false);
                ctx.LineTo(new Point(r.Left, r.Top + C(Views.Cut.TopLeft)), true, false);
            }
            g.Freeze();
            shape = g;
        }
        dc.DrawGeometry(Fill, Stroke != null && StrokeThickness > 0 ? new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Miter } : null, shape);
    }
}
