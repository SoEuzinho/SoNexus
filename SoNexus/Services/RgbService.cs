using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoNexus.Services;

/// <summary>Efeitos que o SoNexus calcula e manda para os LEDs (continuam rodando com o app na bandeja).</summary>
public enum RgbEffect { Static, Breathing, Flash, Rainbow, Wave, Gradient, CpuTemp, GpuTemp, CpuLoad, Music, Off }

/// <summary>
/// Conexão com o OpenRGB + motor de efeitos. Um único dono da conexão (o protocolo não aceita duas threads
/// falando ao mesmo tempo), atualizando os LEDs ~30 vezes por segundo nos efeitos animados.
/// </summary>
public sealed class RgbService : IDisposable
{
    public static RgbService Instance { get; } = new();

    public static readonly (RgbEffect Effect, string Name, string Hint)[] Effects =
    {
        (RgbEffect.Static, "Cor fixa", "Uma cor parada."),
        (RgbEffect.Breathing, "Respirar", "A cor acende e apaga devagar."),
        (RgbEffect.Flash, "Piscar", "Liga e desliga no ritmo da velocidade."),
        (RgbEffect.Rainbow, "Arco-íris", "Todos os LEDs passam pelas cores juntos."),
        (RgbEffect.Wave, "Onda arco-íris", "As cores correm ao longo dos LEDs (ventoinhas e fitas)."),
        (RgbEffect.Gradient, "Duas cores", "Mistura a cor 1 e a cor 2, indo e voltando."),
        (RgbEffect.CpuTemp, "Temperatura da CPU", "Azul fria, branco no meio, vermelho acima de 80 °C."),
        (RgbEffect.GpuTemp, "Temperatura da GPU", "Azul fria, branco no meio, vermelho acima de 80 °C."),
        (RgbEffect.CpuLoad, "Uso da CPU", "Verde parado, amarelo no meio, vermelho em 100%."),
        (RgbEffect.Music, "Música", "Pulsa com o som que está tocando no PC."),
        (RgbEffect.Off, "Desligar LEDs", "Apaga tudo."),
    };

    private sealed class Target
    {
        public required RgbDevice Device;
        /// <summary>Aceita cor por LED (modo Direto). Senão, a cor vai pelo modo "Estático".</summary>
        public bool Direct;
        public RgbMode? StaticMode;
        public uint[] Last = Array.Empty<uint>();
        public long LastSent;
    }

    private readonly object _io = new();
    private OpenRgbClient? _client;
    private List<Target> _targets = new();
    private Thread? _engine;
    private volatile bool _running;
    private MusicMeter? _music;

    public event EventHandler? Changed;

    public bool Connected => _client?.Connected == true;
    public List<RgbDevice> Devices { get; private set; } = new();
    public string Status { get; private set; } = "Desconectado";
    public RgbSettings Settings => AppSettings.Current.Rgb;
    /// <summary>Um modo da própria placa está ativo: o motor deixa os LEDs com ela.</summary>
    public bool HardwareModeActive { get; private set; }
    /// <summary>O motor de efeitos já está mandando cores (mudanças de cor/brilho valem na hora).</summary>
    public bool Running => _running && (_targets.Count > 0 || HasBle) && !HardwareModeActive;

    /// <summary>Há fitas Bluetooth ligadas aos efeitos (funcionam mesmo sem o OpenRGB).</summary>
    public bool HasBle => Settings.BleDevices.Any(b => b.Enabled);

    // ---------- Conexão ----------

    public async Task<bool> ConnectAsync()
    {
        var client = new OpenRgbClient();
        if (!await client.ConnectAsync())
        {
            client.Dispose();
            Status = "OpenRGB não encontrado.";
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
        List<RgbDevice> devices;
        try { devices = await Task.Run(client.Devices); }
        catch (Exception ex)
        {
            client.Dispose();
            Status = $"Erro ao ler os dispositivos: {ex.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
        lock (_io)
        {
            _client?.Dispose();
            _client = client;
            Devices = devices;
            _targets.Clear(); // prepara de novo na próxima aplicação
        }
        Status = $"Conectado ao OpenRGB · {devices.Count} dispositivo(s)";
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Ao abrir o SoNexus: espera o OpenRGB (ele pode abrir depois) e reaplica a última iluminação.</summary>
    public void RestoreOnStartup()
    {
        if (!Settings.RestoreOnStart) return;
        Task.Run(async () =>
        {
            if (HasBle) Apply(); // fitas Bluetooth não dependem do OpenRGB
            for (var attempt = 0; attempt < 30 && !Connected; attempt++)
            {
                if (await ConnectAsync()) break;
                await Task.Delay(10_000);
            }
            if (Connected) Apply();
        });
    }

    // ---------- Aplicar ----------

    /// <summary>Liga o efeito escolhido nas configurações (cor, velocidade, brilho, dispositivos).</summary>
    public void Apply()
    {
        if (!Connected && !HasBle) return;
        HardwareModeActive = false;
        _bleOff.Clear(); // efeito novo: manda o liga/desliga de novo
        lock (_io)
        {
            var wanted = Connected ? Devices.Where(d => !Settings.Excluded.Contains(d.Name)).ToList() : new List<RgbDevice>();
            // Prepara só quem mudou (preparar = ajustar zonas ARGB + modo Direto + reler o aparelho).
            var ready = new List<Target>();
            foreach (var d in wanted)
            {
                var existing = _targets.FirstOrDefault(t => t.Device.Index == d.Index);
                ready.Add(existing ?? Prepare(d));
            }
            foreach (var t in ready) { t.Last = Array.Empty<uint>(); t.LastSent = 0; }
            _targets = ready;
        }
        AppSettings.Current.Save();
        var effect = Enum.TryParse<RgbEffect>(Settings.Effect, out var e) ? e : RgbEffect.Static;
        if (effect == RgbEffect.Music) (_music ??= new MusicMeter()).Start();
        else _music?.Stop();
        StartEngine();
    }

    private Target Prepare(RgbDevice d)
    {
        var c = _client!;
        if (d.EmptyZones > 0) c.SizeEmptyZones(d);
        c.SetCustom(d.Index);
        var now = c.Device(d.Index);
        var direct = now.Active?.ColorMode == RgbMode.PerLed && now.Leds > 0;
        var stat = direct ? null
            : now.Modes.FirstOrDefault(m => m.ColorMode == RgbMode.ModeSpecific && m.Name.Contains("Static", StringComparison.OrdinalIgnoreCase))
              ?? now.Modes.FirstOrDefault(m => m.ColorMode == RgbMode.ModeSpecific);
        return new Target { Device = now, Direct = direct || (stat == null && now.Leds > 0), StaticMode = stat };
    }

    private void StartEngine()
    {
        if (_running) return;
        _running = true;
        _engine = new Thread(Loop) { IsBackground = true, Name = "SoNexus RGB" };
        _engine.Start();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        while (_running)
        {
            var frameStart = clock.Elapsed.TotalSeconds;
            if (!HardwareModeActive && Connected)
            {
                try { Frame(frameStart); }
                catch (Exception ex)
                {
                    AppLog.Write($"RGB: {ex.Message}");
                    Status = "Conexão com o OpenRGB caiu. Clique em Conectar.";
                    lock (_io) { _client?.Dispose(); _client = null; }
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            // Fitas Bluetooth: independentes do OpenRGB (um erro numa não derruba a outra).
            // (Modo da própria placa vale só para a placa: as fitas seguem os efeitos.)
            if (HasBle)
            {
                try { BleFrame(frameStart); }
                catch (Exception ex) { AppLog.Write($"RGB Bluetooth: {ex.Message}"); }
            }
            Thread.Sleep(FrameDelayMs());
        }
    }

    /// <summary>
    /// Só efeitos que se mexem precisam de ~30 quadros por segundo. Cor fixa, desligado e os que seguem
    /// sensores (que mudam a cada segundo) rodam a 2 por segundo: quase nada de CPU parado.
    /// </summary>
    private int FrameDelayMs()
    {
        if (!HasBle && (HardwareModeActive || !Connected || _targets.Count == 0)) return 1000;
        var animated = Enum.TryParse<RgbEffect>(Settings.Effect, out var e) && e is RgbEffect.Breathing or RgbEffect.Flash or RgbEffect.Rainbow
            or RgbEffect.Wave or RgbEffect.Gradient or RgbEffect.Music;
        return !animated ? 500 : BatterySaver ? 66 : 33;
    }

    /// <summary>Na bateria (Configurações › economia): animações a 15 quadros por segundo.</summary>
    public bool BatterySaver { get; set; }

    /// <summary>Calcula as cores deste instante e manda só o que mudou (ou um "lembrete" a cada 3 s).</summary>
    private void Frame(double t)
    {
        var s = Settings;
        var effect = Enum.TryParse<RgbEffect>(s.Effect, out var e) ? e : RgbEffect.Static;
        var speed = Math.Clamp(s.Speed, 0.1, 5);
        var bright = Math.Clamp(s.Brightness, 0, 100) / 100;
        var c1 = Rgb.From(s.Color);
        var c2 = Rgb.From(s.Color2);
        var now = Stopwatch.GetTimestamp();

        lock (_io)
        {
            if (_client == null) return;
            _client.Drain();
            foreach (var target in _targets)
            {
                var n = Math.Max(1, target.Device.Leds);
                var colors = new uint[target.Direct ? n : 1];
                for (var i = 0; i < colors.Length; i++)
                    colors[i] = (ColorAt(effect, t, speed, i, colors.Length, c1, c2) * bright).ToOpenRgb();

                var changed = !colors.AsSpan().SequenceEqual(target.Last);
                var keepAlive = Stopwatch.GetElapsedTime(target.LastSent, now).TotalSeconds > 3;
                // Aparelhos sem modo Direto (cor pelo modo Estático) aceitam poucas mudanças por segundo.
                var minGap = target.Direct ? 0 : 0.12;
                if ((!changed && !keepAlive) || Stopwatch.GetElapsedTime(target.LastSent, now).TotalSeconds < minGap) continue;

                if (target.Direct) _client.SendLeds(target.Device.Index, colors);
                else if (target.StaticMode is { } m)
                    _client.SendMode(target.Device.Index, m with { Colors = Enumerable.Repeat(colors[0], (int)Math.Max(1, Math.Max(m.ColorsMin, (uint)m.Colors.Length))).ToArray() });
                target.Last = colors;
                target.LastSent = now;
            }
        }
    }

    private readonly HashSet<ulong> _bleOff = new();

    /// <summary>Fitas Bluetooth: uma cor só para a fita inteira (o protocolo BJ_LED não tem cor por LED).</summary>
    private void BleFrame(double t)
    {
        var s = Settings;
        var effect = Enum.TryParse<RgbEffect>(s.Effect, out var e) ? e : RgbEffect.Static;
        foreach (var dev in s.BleDevices.Where(b => b.Enabled).ToList())
        {
            if (effect == RgbEffect.Off)
            {
                // "Desligar LEDs": desliga de verdade (cor preta deixaria a fita ligada, gastando).
                if (!_bleOff.Contains(dev.Address) && BleLeds.Current.SetPower(dev.Address, false)) _bleOff.Add(dev.Address);
                continue;
            }
            _bleOff.Remove(dev.Address);
            var c = ColorAt(effect, t, Math.Clamp(s.Speed, 0.1, 5), 0, 1, Rgb.From(s.Color), Rgb.From(s.Color2)) * (Math.Clamp(s.Brightness, 0, 100) / 100);
            BleLeds.Current.SetColor(dev.Address, (byte)Math.Clamp(c.R, 0, 255), (byte)Math.Clamp(c.G, 0, 255), (byte)Math.Clamp(c.B, 0, 255));
        }
    }

    private Rgb ColorAt(RgbEffect effect, double t, double speed, int i, int n, Rgb c1, Rgb c2)
    {
        switch (effect)
        {
            case RgbEffect.Static: return c1;
            case RgbEffect.Off: return Rgb.Black;
            case RgbEffect.Breathing:
                return c1 * (0.08 + 0.92 * Math.Pow((Math.Sin(t * Math.PI * 0.5 * speed) + 1) / 2, 2));
            case RgbEffect.Flash:
                return (t * speed * 2 % 1) < 0.5 ? c1 : Rgb.Black;
            case RgbEffect.Rainbow:
                return Rgb.Hsv(t * 40 * speed, 1, 1);
            case RgbEffect.Wave:
                return Rgb.Hsv(t * 90 * speed + 360.0 * i / n, 1, 1);
            case RgbEffect.Gradient:
                var k = (Math.Sin(t * Math.PI * 0.4 * speed + 2 * Math.PI * i / Math.Max(n, 8)) + 1) / 2;
                return Rgb.Lerp(c1, c2, k);
            case RgbEffect.CpuTemp:
                return TempColor(HardwareService.CpuNow(HardwareService.Instance.Readings).Temp);
            case RgbEffect.GpuTemp:
                return TempColor(HardwareService.GpuNow(HardwareService.Instance.Readings).Temp);
            case RgbEffect.CpuLoad:
                var load = HardwareService.Find(HardwareService.Instance.Readings, HardwareService.IsCpu,
                    LibreHardwareMonitor.Hardware.SensorType.Load, false, "CPU Total")?.Value ?? 0;
                var x = Math.Clamp(load / 100.0, 0, 1);
                return x < 0.5 ? Rgb.Lerp(new Rgb(0, 255, 40), new Rgb(255, 210, 0), x * 2) : Rgb.Lerp(new Rgb(255, 210, 0), new Rgb(255, 0, 0), (x - 0.5) * 2);
            case RgbEffect.Music:
                var level = _music?.Level ?? 0;
                // Mais som = mais forte; nos picos, puxa para a cor 2.
                return Rgb.Lerp(c1, c2, Math.Clamp((level - 0.6) / 0.4, 0, 1)) * (0.05 + 0.95 * level);
            default: return c1;
        }
    }

    /// <summary>Cor do LED <paramref name="i"/> de <paramref name="n"/> no instante <paramref name="t"/> (prévia na tela, igual ao que vai para os LEDs).</summary>
    public Rgb Preview(int i, int n, double t)
    {
        var s = Settings;
        var effect = Enum.TryParse<RgbEffect>(s.Effect, out var e) ? e : RgbEffect.Static;
        if (effect == RgbEffect.Music && _music == null) effect = RgbEffect.Breathing; // sem captura de som ligada, simula
        return ColorAt(effect, t, Math.Clamp(s.Speed, 0.1, 5), i, n, Rgb.From(s.Color), Rgb.From(s.Color2)) * (Math.Clamp(s.Brightness, 0, 100) / 100);
    }

    /// <summary>40 °C ou menos = azul, 60 °C = branco, 80 °C ou mais = vermelho.</summary>
    private static Rgb TempColor(float? celsius)
    {
        if (celsius is not { } c) return new Rgb(255, 255, 255);
        var x = Math.Clamp((c - 40) / 40.0, 0, 1);
        return x < 0.5 ? Rgb.Lerp(new Rgb(0, 60, 255), new Rgb(255, 255, 255), x * 2) : Rgb.Lerp(new Rgb(255, 255, 255), new Rgb(255, 0, 0), (x - 0.5) * 2);
    }

    // ---------- Modos da própria placa ----------

    /// <summary>
    /// Usa um efeito que roda no próprio aparelho (não precisa do SoNexus aberto).
    /// Cor e velocidade são aplicadas quando o modo aceita. <paramref name="save"/> grava na memória do aparelho.
    /// </summary>
    public string? ApplyHardwareMode(RgbDevice device, RgbMode mode, bool save)
    {
        if (!Connected) return "Não conectado ao OpenRGB.";
        HardwareModeActive = true; // o motor para de mexer nos LEDs
        var s = Settings;
        var m = mode;
        if (m.SupportsColor)
        {
            var count = (int)Math.Clamp(Math.Max((uint)Math.Max(1, m.Colors.Length), m.ColorsMin), 1, Math.Max(1, m.ColorsMax));
            var colors = new[] { Rgb.From(s.Color).ToOpenRgb(), Rgb.From(s.Color2).ToOpenRgb() };
            m = m with { Colors = Enumerable.Range(0, count).Select(i => colors[i % 2]).ToArray() };
        }
        if (m.SupportsSpeed)
        {
            var k = Math.Clamp((s.Speed - 0.2) / 2.8, 0, 1);
            m = m with { Speed = (uint)Math.Round(m.SpeedMin + (m.SpeedMax - (double)m.SpeedMin) * k) };
        }
        try
        {
            lock (_io)
            {
                if (device.EmptyZones > 0) _client!.SizeEmptyZones(device);
                _client!.SendMode(device.Index, m);
                if (save && m.CanSave) _client.SaveMode(device.Index, m);
                _targets.Clear(); // ao voltar para um efeito do SoNexus, prepara de novo (modo Direto)
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        return save && !m.CanSave ? "Aplicado. Este aparelho não aceita gravar o modo na memória." : null;
    }

    public void Dispose()
    {
        _running = false;
        _music?.Stop();
        lock (_io) _client?.Dispose();
        BleLeds.Current.Dispose();
    }
}

/// <summary>Cor RGB com conta simples (brilho, mistura, arco-íris).</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb From(int rgb) => new((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    public int ToInt() => ((int)R << 16) | ((int)G << 8) | (int)B;

    /// <summary>O OpenRGB usa 0x00BBGGRR.</summary>
    public uint ToOpenRgb() => (uint)(Clamp(R) | (Clamp(G) << 8) | (Clamp(B) << 16));
    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    public static Rgb operator *(Rgb c, double k) => new(c.R * k, c.G * k, c.B * k);
    public static Rgb Lerp(Rgb a, Rgb b, double k) => new(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);

    public static Rgb Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60;
        var i = (int)h;
        var f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new Rgb(r * 255, g * 255, b * 255);
    }

    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255, g = G / 255, b = B / 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}

/// <summary>Nível do som que está saindo nas caixas/fone (0 a 1), com ganho automático.</summary>
public sealed class MusicMeter
{
    private WasapiLoopbackCapture? _capture;
    private double _level, _peak = 0.05;

    public double Level => _level;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            AppLog.Write($"RGB música: {ex.Message}");
            _capture = null;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format == null || e.BytesRecorded == 0) return;
        double sum = 0;
        var samples = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= e.BytesRecorded; i += 4) { var v = BitConverter.ToSingle(e.Buffer, i); sum += v * v; samples++; }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= e.BytesRecorded; i += 2) { var v = BitConverter.ToInt16(e.Buffer, i) / 32768.0; sum += v * v; samples++; }
        }
        if (samples == 0) return;
        var rms = Math.Sqrt(sum / samples);
        // Ganho automático: o pico vai caindo devagar, então música baixa também acende bem.
        _peak = Math.Max(rms, _peak * 0.995);
        var target = Math.Clamp(rms / Math.Max(_peak, 0.02), 0, 1);
        // Sobe rápido, desce devagar (fica mais bonito no ritmo).
        _level = target > _level ? target : _level * 0.85 + target * 0.15;
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
        _level = 0;
    }
}
