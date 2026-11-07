using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Controls.Primitives;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SoNexus.Views;

/// <summary>
/// Temas do painel sobre o jogo. Todos desenhados aqui, com formas e cores próprias do SoNexus:
/// nenhum usa logo, nome ou personagem de jogo ou empresa.
/// Animações são poucas, lentas e a 15 quadros por segundo: o painel não pode pesar no jogo.
/// </summary>
public static class OverlaySkin
{
    public static readonly string[] Names = { "Clássico", "Neon", "Imperial", "Pixel", "Arena", "Fofo" };

    private const string LabelKey = "OverlayLabel";

    /// <summary>Cor dos rótulos (FPS, CPU...) do tema em uso.</summary>
    public static Brush LabelBrush(TextBlock text) => text.Resources[LabelKey] as Brush ?? Brushes.Gray;

    /// <summary>Cor dos números: a escolhida, menos no tema claro, onde branco não daria para ler.</summary>
    public static Brush ValueBrush(int theme, int color) =>
        theme == 5 && color == 0 ? Solid(0x5A, 0x1F, 0x3D) : OverlayWindow.ValueBrush(color);

    /// <summary>Monta o painel do tema em volta do texto (o mesmo texto serve ao jogo e à prévia).</summary>
    public static FrameworkElement Build(int theme, TextBlock text, int size, int opacity, bool animate)
    {
        Detach(text);
        text.FontSize = size switch { 0 => 12, 2 => 20, _ => 15 };
        text.Tag = null; // rótulos mudam de cor: desenha as linhas de novo
        var fill = (byte)(Math.Clamp(opacity, 0, 100) * 2.55);
        var root = new Grid { Margin = new Thickness(10) }; // espaço para enfeites que saem da borda
        Brush label;

        switch (theme)
        {
            case 1: // Neon: vidro escuro com brilho roxo/azul que "respira"
            {
                label = Solid(0xA9, 0x9B, 0xFF);
                var glow = new Border { CornerRadius = new CornerRadius(10), Background = Solid(0x7C, 0x3A, 0xED), Opacity = 0.0 };
                glow.Effect = new BlurEffect { Radius = 14 };
                root.Children.Add(glow);
                root.Children.Add(Panel(text, new CornerRadius(10), Color.FromArgb(fill, 0x0C, 0x0A, 0x28),
                    Gradient(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x3B, 0x82, 0xF6)), 2));
                root.Children.Add(new TextBlock
                {
                    Text = "///", FontWeight = FontWeights.Black, FontStyle = FontStyles.Italic, FontSize = 13, Foreground = Solid(0x8B, 0x5C, 0xF6),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, 14, 0),
                });
                if (animate) Pulse(glow, UIElement.OpacityProperty, 0.25, 0.7, 1.6);
                else glow.Opacity = 0.45;
                break;
            }
            case 2: // Imperial: moldura dourada gótica com lacre vermelho
            {
                label = Solid(0xC9, 0xA5, 0x5A);
                var gold = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0x8A, 0x64, 0x20), 0), new(Color.FromRgb(0xF5, 0xD2, 0x7A), 0.5), new(Color.FromRgb(0x8A, 0x64, 0x20), 1),
                }, 0) { RelativeTransform = new TranslateTransform() };
                var panel = Panel(text, new CornerRadius(3), Color.FromArgb(fill, 0x16, 0x14, 0x12), gold, 3);
                ((Border)panel).Padding = new Thickness(14, 9, 14, 9);
                root.Children.Add(panel);
                root.Children.Add(new Border { BorderBrush = Solid(0x6B, 0x52, 0x22), BorderThickness = new Thickness(1), Margin = new Thickness(5), CornerRadius = new CornerRadius(2), IsHitTestVisible = false });
                foreach (var (h, v) in new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top), (HorizontalAlignment.Left, VerticalAlignment.Bottom) })
                    root.Children.Add(new Rectangle
                    {
                        Width = 9, Height = 9, Fill = gold, HorizontalAlignment = h, VerticalAlignment = v, Margin = new Thickness(-4),
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
                    });
                // Brasão no topo: asas em chevron com um losango (desenho próprio).
                root.Children.Add(new Path
                {
                    Data = Geometry.Parse("M 0,4 L 14,0 L 24,6 L 34,0 L 48,4 L 36,8 L 30,12 L 24,9 L 18,12 L 12,8 Z M 24,2 L 28,8 L 24,14 L 20,8 Z"),
                    Fill = gold, Stroke = Solid(0x4A, 0x36, 0x10), StrokeThickness = 0.8, Stretch = Stretch.Uniform, Width = 54, Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -9, 0, 0),
                });
                root.Children.Add(new Ellipse
                {
                    Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -9, -9),
                    Fill = new RadialGradientBrush(Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x5E, 0x0B, 0x0B)), Stroke = Solid(0x3A, 0x06, 0x06), StrokeThickness = 1.5,
                });
                if (animate) Pulse((TranslateTransform)gold.RelativeTransform, TranslateTransform.XProperty, -0.6, 0.6, 3);
                break;
            }
            case 3: // Pixel: bloco de terra com grama em degraus
            {
                label = Solid(0xE3, 0xD3, 0xB0);
                root.Children.Add(Panel(text, new CornerRadius(0), Color.FromArgb(fill, 0x5B, 0x3A, 0x1E), Solid(0x3E, 0x7B, 0x27), 3, top: 10));
                var grass = new UniformGrid { Rows = 1, Columns = 26, VerticalAlignment = VerticalAlignment.Top, Height = 14, Margin = new Thickness(0, -3, 0, 0) };
                int[] steps = { 9, 12, 7, 12, 14, 9, 7, 12, 9, 14, 7, 9, 12 };
                for (var i = 0; i < 26; i++)
                    grass.Children.Add(new Rectangle
                    {
                        Height = steps[i % steps.Length], VerticalAlignment = VerticalAlignment.Top,
                        Fill = i % 3 == 0 ? Solid(0x4A, 0x8F, 0x2E) : Solid(0x5D, 0xAE, 0x3B),
                    });
                root.Children.Add(grass);
                break;
            }
            case 4: // Arena: cantos cortados vermelhos e brilho rápido
            {
                label = Solid(0xFF, 0x8A, 0x80);
                var glow = new Border { CornerRadius = new CornerRadius(14, 3, 14, 3), Background = Solid(0xE5, 0x39, 0x35), Opacity = 0.0 };
                glow.Effect = new BlurEffect { Radius = 12 };
                root.Children.Add(glow);
                root.Children.Add(Panel(text, new CornerRadius(14, 3, 14, 3), Color.FromArgb(fill, 0x12, 0x12, 0x16), Solid(0xE5, 0x39, 0x35), 2));
                root.Children.Add(new Rectangle
                {
                    Width = 30, Height = 4, Fill = Solid(0xFF, 0x52, 0x52), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -2, 18, 0), RenderTransform = new SkewTransform(-30, 0),
                });
                if (animate) Pulse(glow, UIElement.OpacityProperty, 0.15, 0.6, 0.9);
                else glow.Opacity = 0.35;
                break;
            }
            case 5: // Fofo: rosa claro com corações e laço
            {
                label = Solid(0xD6, 0x33, 0x7F);
                var bg = new LinearGradientBrush(Color.FromArgb(Math.Max(fill, (byte)235), 0xFF, 0xF5, 0xFA), Color.FromArgb(Math.Max(fill, (byte)235), 0xFF, 0xD6, 0xE8), 90);
                var panel = Panel(text, new CornerRadius(18), Colors.Transparent, Solid(0xFF, 0x5F, 0xA2), 2);
                ((Border)panel).Background = bg;
                ((Border)panel).Padding = new Thickness(16, 8, 18, 8);
                root.Children.Add(panel);
                var heart1 = Heart(17, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-7, -8, 0, 0));
                var heart2 = Heart(13, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -5, -6));
                root.Children.Add(heart1);
                root.Children.Add(heart2);
                root.Children.Add(Bow());
                if (animate)
                {
                    Pulse((TranslateTransform)heart1.RenderTransform, TranslateTransform.YProperty, 0, -3, 1.8);
                    Pulse((TranslateTransform)heart2.RenderTransform, TranslateTransform.YProperty, 0, -2, 1.3);
                }
                break;
            }
            default: // Clássico
                label = Brushes.Gray;
                root.Children.Add(Panel(text, new CornerRadius(6), Color.FromArgb(fill, 0, 0, 0), null, 0));
                break;
        }

        text.Resources[LabelKey] = label;
        // Sem fundo, uma sombra deixa os números legíveis em cena clara (não no tema claro).
        text.Effect = opacity < 30 && theme != 5 ? new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 1 } : null;
        return root;
    }

    private static FrameworkElement Panel(TextBlock text, CornerRadius radius, Color fill, Brush? border, double thickness, double top = 0) => new Border
    {
        CornerRadius = radius,
        Background = new SolidColorBrush(fill),
        BorderBrush = border,
        BorderThickness = new Thickness(thickness),
        Padding = new Thickness(11, 6 + top, 11, 6),
        Child = text,
    };

    /// <summary>O mesmo texto vai para o painel novo: sai do anterior.</summary>
    private static void Detach(TextBlock text)
    {
        switch (text.Parent)
        {
            case Decorator d: d.Child = null; break;
            case System.Windows.Controls.Panel p: p.Children.Remove(text); break;
            case ContentControl c: c.Content = null; break;
        }
    }

    /// <summary>Vai e volta para sempre, devagar e a 15 quadros por segundo (quase nada de CPU).</summary>
    private static void Pulse(IAnimatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(anim, 15);
        target.BeginAnimation(property, anim);
    }

    private static Path Heart(double size, HorizontalAlignment h, VerticalAlignment v, Thickness margin) => new()
    {
        Data = Geometry.Parse("M 5,9 C 5,9 0,6 0,3 C 0,0.5 3.5,-0.5 5,2.2 C 6.5,-0.5 10,0.5 10,3 C 10,6 5,9 5,9 Z"),
        Fill = Solid(0xFF, 0x7E, 0xB6), Stroke = Brushes.White, StrokeThickness = 0.6, Stretch = Stretch.Uniform,
        Width = size, Height = size, HorizontalAlignment = h, VerticalAlignment = v, Margin = margin,
        RenderTransform = new TranslateTransform(),
    };

    private static FrameworkElement Bow()
    {
        var bow = new Grid
        {
            Width = 36, Height = 19, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -10, 12, 0),
        };
        bow.Children.Add(new Path
        {
            Data = Geometry.Parse("M 0,0 L 11,6 L 0,12 Z M 26,0 L 15,6 L 26,12 Z"),
            Fill = Solid(0xFF, 0x4F, 0x9A), Stroke = Brushes.White, StrokeThickness = 0.8, Stretch = Stretch.Fill,
        });
        bow.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Solid(0xE0, 0x2F, 0x7E), Stroke = Brushes.White, StrokeThickness = 0.8 });
        return bow;
    }

    private static LinearGradientBrush Gradient(Color a, Color b) => new(a, b, 0);

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
