using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

/// <summary>Linha da lista de dispositivos (a caixinha liga/desliga o dispositivo nos efeitos).</summary>
public sealed class RgbDeviceItem : ObservableObject
{
    private bool _enabled;
    public required RgbDevice Device { get; init; }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Detail => $"{Device.Leds} LEDs" + (Device.Zones.Length > 0 ? $" · {Device.Zones}" : "") +
                            (Device.Mode.Length > 0 ? $" · modo {Device.Mode}" : "");
}

public partial class RgbView : UserControl
{
    private readonly RgbService _rgb = RgbService.Instance;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loaded, _syncing;
    // Enquanto a tela é montada, os controles disparam "mudou" sozinhos (ex.: o valor inicial dos sliders).
    // Sem esta trava, a cor salva era trocada por vermelho e o app quebrava (controles ainda não criados).
    private bool _ready;

    public RgbView()
    {
        InitializeComponent();
        _save.Tick += (_, _) => { _save.Stop(); AppSettings.Current.Save(); };
        BuildEffects();
        BuildPreview();
        _ready = true;
        LoadSettings();
    }

    // ---------- Prévia animada ----------

    private const int PreviewLeds = 36;
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _previewClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(SolidColorBrush Fill, System.Windows.Media.Effects.DropShadowEffect Glow)> _leds = new();

    private void BuildPreview()
    {
        for (var i = 0; i < PreviewLeds; i++)
        {
            var fill = new SolidColorBrush(Colors.Black);
            var glow = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9, Color = Colors.Black };
            LedStrip.Children.Add(new Border
            {
                Height = 16, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(4), Background = fill, Effect = glow,
            });
            _leds.Add((fill, glow));
        }
        // Só anima com a tela visível (escondida não gasta nada).
        _preview.Tick += (_, _) => DrawPreview();
        IsVisibleChanged += (_, _) => { if (IsVisible) _preview.Start(); else _preview.Stop(); };
    }

    private void DrawPreview()
    {
        var t = _previewClock.Elapsed.TotalSeconds;
        for (var i = 0; i < _leds.Count; i++)
        {
            var c = _rgb.Preview(i, _leds.Count, t);
            var color = Color.FromRgb((byte)Math.Clamp(c.R, 0, 255), (byte)Math.Clamp(c.G, 0, 255), (byte)Math.Clamp(c.B, 0, 255));
            _leds[i].Fill.Color = color;
            _leds[i].Glow.Color = color;
        }
        PreviewName.Text = RgbService.Effects.FirstOrDefault(x => x.Effect.ToString() == S.Effect).Name ?? "";
    }

    private RgbSettings S => _rgb.Settings;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _rgb.Changed += OnServiceChanged;
        if (_loaded) { ShowDevices(); return; }
        _loaded = true;

        var apps = await Task.Run(IntegrationService.DetectRgbApps);
        AppList.ItemsSource = apps;
        var vendorRunning = apps.Count(a => a.Status == "Rodando" && a.Name != "OpenRGB");
        AppWarning.Text = apps.Count == 0
            ? "Nenhum programa de RGB instalado. Sem eles, os LEDs ficam no efeito padrão da placa."
            : vendorRunning > 0
                ? "Com programas de RGB do fabricante abertos, os LEDs podem piscar ou voltar sozinhos: eles disputam o controle com o OpenRGB."
                : "";
        if (_rgb.Connected) ShowDevices();
        else await ConnectAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _rgb.Changed -= OnServiceChanged;

    private void OnServiceChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        Status.Text = _rgb.Status;
        if (!_rgb.Connected) ControlsPanel.IsEnabled = HwCard.IsEnabled = false;
    });

    // ---------- Conexão e dispositivos ----------

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        ConnectButton.IsEnabled = false;
        Status.Text = "Procurando o OpenRGB...";
        if (await _rgb.ConnectAsync()) ShowDevices();
        else
        {
            Status.Text = "OpenRGB não encontrado. Siga os passos ao lado e clique em Conectar.";
            HowTo.Visibility = Visibility.Visible;
        }
        ConnectButton.IsEnabled = true;
    }

    private void ShowDevices()
    {
        var devices = _rgb.Devices;
        DeviceList.ItemsSource = devices.Select(d => new RgbDeviceItem { Device = d, Enabled = !S.Excluded.Contains(d.Name) }).ToList();
        HwDeviceBox.ItemsSource = devices;
        if (HwDeviceBox.SelectedIndex < 0 && devices.Count > 0) HwDeviceBox.SelectedIndex = 0;
        var leds = devices.Sum(d => d.Leds);
        Status.Text = $"Conectado ao OpenRGB · {devices.Count} dispositivo(s) · {leds} LEDs" +
                      (devices.Count == 0 ? ". Nenhum dispositivo: abra o OpenRGB como administrador e clique em \"Rescan Devices\"." : "") +
                      (devices.Any(d => d.EmptyZones > 0) ? " · saídas ARGB com 0 LEDs serão configuradas sozinhas" : "") +
                      (_rgb.HardwareModeActive ? " · usando um modo da própria placa" : "");
        HowTo.Visibility = Visibility.Collapsed;
        ControlsPanel.IsEnabled = HwCard.IsEnabled = devices.Count > 0;
    }

    private void DeviceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.ItemsSource is not List<RgbDeviceItem> items) return;
        S.Excluded = items.Where(i => !i.Enabled).Select(i => i.Device.Name).ToList();
        ApplyAsync();
    }

    /// <summary>Liga o efeito escolhido (conversa com o aparelho fora da tela para não travar).</summary>
    private bool _applying;

    private async void ApplyAsync()
    {
        if (!_rgb.Connected) return;
        _applying = true;
        try { await Task.Run(_rgb.Apply); }
        catch (Exception ex) { Status.Text = $"Erro ao aplicar: {ex.Message}. Clique em Conectar."; return; }
        finally { _applying = false; }
        ShowDevices();
    }

    // ---------- Efeitos ----------

    private void BuildEffects()
    {
        foreach (var (effect, name, _) in RgbService.Effects)
        {
            var chip = new RadioButton { Content = name, Tag = effect, GroupName = "Effect", Style = (Style)FindResource("Chip") };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                S.Effect = effect.ToString();
                EffectHint.Text = RgbService.Effects.First(x => x.Effect == effect).Hint;
                ApplyAsync();
            };
            EffectPanel.Children.Add(chip);
        }
    }

    private void LoadSettings()
    {
        _syncing = true;
        foreach (var chip in EffectPanel.Children.OfType<RadioButton>())
            chip.IsChecked = chip.Tag.ToString() == S.Effect;
        EffectHint.Text = RgbService.Effects.FirstOrDefault(x => x.Effect.ToString() == S.Effect).Hint ?? "";
        BrightSlider.Value = S.Brightness;
        SpeedSlider.Value = S.Speed;
        RestoreBox.IsChecked = S.RestoreOnStart;
        _syncing = false;
        ShowColor();
        ShowFavorites();
        UpdateTuningText();
    }

    // ---------- Cor ----------

    private bool EditingSecond => EditColor2.IsChecked == true;

    private int CurrentColor
    {
        get => EditingSecond ? S.Color2 : S.Color;
        set
        {
            if (EditingSecond) S.Color2 = value;
            else S.Color = value;
            _save.Stop();
            _save.Start();
        }
    }

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Atualiza a prévia, o código e os controles de tom para a cor que está sendo editada.</summary>
    private void ShowColor(bool moveSliders = true)
    {
        if (!_ready) return;
        var c = CurrentColor;
        Preview.Background = new SolidColorBrush(ToColor(c));
        Color1Preview.Background = new SolidColorBrush(ToColor(S.Color));
        Color2Preview.Background = new SolidColorBrush(ToColor(S.Color2));
        if (!HexBox.IsKeyboardFocused) HexBox.Text = c.ToString("X6");
        if (!moveSliders) return;
        _syncing = true;
        var (h, s, _) = Rgb.From(c).ToHsv();
        HueSlider.Value = h;
        SatSlider.Value = s;
        _syncing = false;
    }

    private void EditColor_Changed(object sender, RoutedEventArgs e) => ShowColor();

    private void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        CurrentColor = Rgb.Hsv(HueSlider.Value, SatSlider.Value, 1).ToInt();
        ShowColor(moveSliders: false);
        if (!_rgb.Running && !_applying) ApplyAsync();
    }

    private void SetColor(int rgb)
    {
        CurrentColor = rgb;
        ShowColor();
        // Escolher uma cor num efeito sem cor (arco-íris, temperatura...) volta para "Cor fixa".
        if (S.Effect is nameof(RgbEffect.Rainbow) or nameof(RgbEffect.Wave) or nameof(RgbEffect.CpuTemp) or nameof(RgbEffect.GpuTemp)
            or nameof(RgbEffect.CpuLoad) or nameof(RgbEffect.Off) && !EditingSecond)
            EffectPanel.Children.OfType<RadioButton>().First(r => (RgbEffect)r.Tag == RgbEffect.Static).IsChecked = true;
        else if (!_rgb.Running) ApplyAsync();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) SetColor(Convert.ToInt32(hex, 16));
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ReadHex();
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => ReadHex();

    private void ReadHex()
    {
        var text = HexBox.Text.Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(ch => $"{ch}{ch}")); // "F80" = "FF8800"
        if (text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) SetColor(rgb);
        else HexBox.Text = CurrentColor.ToString("X6");
    }

    // ---------- Favoritas ----------

    private void ShowFavorites()
    {
        FavoritePanel.Children.Clear();
        foreach (var fav in S.Favorites)
        {
            var b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(ToColor(fav)), Tag = fav, ToolTip = $"#{fav:X6} (botão direito remove)" };
            b.Click += (_, _) => SetColor(fav);
            b.MouseRightButtonUp += (_, _) =>
            {
                S.Favorites.Remove(fav);
                AppSettings.Current.Save();
                ShowFavorites();
            };
            FavoritePanel.Children.Add(b);
        }
        FavoriteHint.Visibility = S.Favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (S.Favorites.Contains(CurrentColor)) return;
        S.Favorites.Add(CurrentColor);
        if (S.Favorites.Count > 16) S.Favorites.RemoveAt(0);
        AppSettings.Current.Save();
        ShowFavorites();
    }

    // ---------- Brilho, velocidade, opções ----------

    private void Tuning_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        S.Brightness = Math.Round(BrightSlider.Value);
        S.Speed = Math.Round(SpeedSlider.Value, 1);
        UpdateTuningText();
        _save.Stop();
        _save.Start();
    }

    private void UpdateTuningText()
    {
        if (BrightText == null || SpeedText == null) return;
        BrightText.Text = $"{S.Brightness:0}%";
        SpeedText.Text = S.Speed switch { < 0.6 => "lenta", < 1.4 => "normal", < 2.2 => "rápida", _ => "muito rápida" } + $" ({S.Speed:0.0}×)";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        S.RestoreOnStart = RestoreBox.IsChecked == true;
        AppSettings.Current.Save();
    }

    private async void StopVendor_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Fechar agora os programas de RGB do fabricante (Armoury Crate/Aura, iCUE, Mystic Light, RGB Fusion...) para o OpenRGB controlar os LEDs?\n\nEles voltam a abrir quando você reiniciar o PC.",
                "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var closed = await Task.Run(IntegrationService.StopVendorRgb);
        Status.Text = closed.Count == 0 ? "Nenhum programa do fabricante estava aberto." : $"✔ Fechados: {string.Join(", ", closed)}.";
        AppList.ItemsSource = await Task.Run(IntegrationService.DetectRgbApps);
        ApplyAsync();
    }

    // ---------- Modos da própria placa ----------

    private void HwDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HwDeviceBox.SelectedItem is not RgbDevice d) return;
        // "Direto" é o modo que o próprio SoNexus usa; aqui ficam só os efeitos da placa.
        var modes = d.Modes.Where(m => m.Name != "Direct").ToList();
        HwModeBox.ItemsSource = modes;
        HwModeBox.SelectedIndex = 0;
        if (modes.Count == 0) HwModeInfo.Text = "Este aparelho não tem efeitos próprios: ele só muda de cor pelos efeitos do SoNexus.";
    }

    private void HwMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HwModeBox.SelectedItem is not RgbMode m) { HwModeInfo.Text = ""; return; }
        var parts = new List<string>();
        parts.Add(m.SupportsColor ? (m.ColorsMax >= 2 ? "usa a Cor 1 e a Cor 2" : "usa a Cor 1") : "cores do próprio efeito");
        if (m.SupportsSpeed) parts.Add("velocidade ajustável");
        parts.Add(m.CanSave ? "dá para salvar na placa" : "não grava na placa");
        HwModeInfo.Text = string.Join(" · ", parts) + ".";
        HwSaveButton.IsEnabled = m.CanSave;
    }

    private void HwApply_Click(object sender, RoutedEventArgs e) => ApplyHardware(save: false);

    private void HwSave_Click(object sender, RoutedEventArgs e) => ApplyHardware(save: true);

    private async void ApplyHardware(bool save)
    {
        if (HwDeviceBox.SelectedItem is not RgbDevice d || HwModeBox.SelectedItem is not RgbMode m) return;
        var error = await Task.Run(() => _rgb.ApplyHardwareMode(d, m, save));
        Status.Text = error ?? $"✔ {d.Name}: {m.Display}" + (save ? " salvo na placa (fica mesmo com o SoNexus fechado)." : ".") +
                      " Os efeitos do SoNexus estão pausados; clique em um efeito para voltar.";
        foreach (var chip in EffectPanel.Children.OfType<RadioButton>()) { _syncing = true; chip.IsChecked = false; _syncing = false; }
    }

    private void BackToSoftware_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        ApplyAsync();
    }
}
