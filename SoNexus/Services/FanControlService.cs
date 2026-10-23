using System.IO;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using SoNexus.Models;

namespace SoNexus.Services;

public enum FanMode { Automatico, Fixo, Curva }

/// <summary>Uma ventoinha controlável (placa-mãe ou placa de vídeo).</summary>
public sealed class FanChannel : ObservableObject
{
    public static readonly int[] CurveTemps = { 30, 45, 60, 75, 90 };

    private FanMode _mode;
    private double _fixed = 50;
    private int _sourceIndex;
    private readonly double[] _curve = { 30, 40, 60, 80, 100 };
    private string _percent = "—";
    private string _rpm = "";
    private string _status = "";

    internal FanChannel(ISensor control, ISensor? rpm)
    {
        Control = control.Control!;
        RpmSensor = rpm;
        ControlSensor = control;
        Id = control.Identifier.ToString();
        Name = control.Name;
        Hardware = control.Hardware.Name;
        IsGpu = control.Hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        _sourceIndex = IsGpu ? 1 : 0;
    }

    internal IControl Control { get; }
    internal ISensor ControlSensor { get; }
    internal ISensor? RpmSensor { get; }
    internal bool Dirty { get; set; }
    internal bool Applied { get; set; }

    public string Id { get; }
    public string Name { get; }
    public string Hardware { get; }
    public bool IsGpu { get; }
    public string Title => $"{Hardware} · {Name}";

    public FanMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { Changed(); Raise(nameof(ModeIndex)); Raise(nameof(IsFixed)); Raise(nameof(IsCurve)); } }
    }

    public int ModeIndex { get => (int)_mode; set => Mode = (FanMode)value; }
    public bool IsFixed => _mode == FanMode.Fixo;
    public bool IsCurve => _mode == FanMode.Curva;

    public double Fixed { get => _fixed; set { if (Set(ref _fixed, Math.Round(value))) Changed(); } }

    /// <summary>0 = temperatura da CPU, 1 = temperatura da GPU.</summary>
    public int SourceIndex { get => _sourceIndex; set { if (Set(ref _sourceIndex, value)) Changed(); } }

    public double P30 { get => _curve[0]; set => SetPoint(0, value); }
    public double P45 { get => _curve[1]; set => SetPoint(1, value); }
    public double P60 { get => _curve[2]; set => SetPoint(2, value); }
    public double P75 { get => _curve[3]; set => SetPoint(3, value); }
    public double P90 { get => _curve[4]; set => SetPoint(4, value); }

    public string Percent { get => _percent; set => Set(ref _percent, value); }
    public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    internal double[] Curve => _curve;

    private void SetPoint(int i, double value)
    {
        value = Math.Round(value);
        if (_curve[i] == value) return;
        _curve[i] = value;
        Raise(CurveTemps[i] switch { 30 => nameof(P30), 45 => nameof(P45), 60 => nameof(P60), 75 => nameof(P75), _ => nameof(P90) });
        Changed();
    }

    private void Changed()
    {
        Dirty = true;
        Applied = false;
    }

    internal double Interpolate(double temp)
    {
        var t = CurveTemps;
        if (temp <= t[0]) return _curve[0];
        for (var i = 1; i < t.Length; i++)
        {
            if (temp <= t[i]) return _curve[i - 1] + (_curve[i] - _curve[i - 1]) * (temp - t[i - 1]) / (t[i] - t[i - 1]);
        }
        return _curve[^1];
    }

    internal FanConfig ToConfig() => new(Id, _mode, _fixed, _sourceIndex, _curve.ToArray());

    internal void Load(FanConfig c)
    {
        _mode = c.Mode;
        _fixed = c.Fixed;
        _sourceIndex = c.Source;
        if (c.Curve.Length == _curve.Length) c.Curve.CopyTo(_curve, 0);
    }
}

public sealed record FanConfig(string Id, FanMode Mode, double Fixed, int Source, double[] Curve);

/// <summary>
/// Controla as ventoinhas enquanto o SoNexus está aberto: velocidade fixa ou curva por temperatura.
/// Ao fechar o programa, tudo volta para o controle automático da placa.
/// </summary>
public sealed class FanControlService
{
    /// <summary>Nunca deixa uma ventoinha controlada abaixo disso.</summary>
    public const double MinimumPercent = 20;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "fans.json");

    public static FanControlService Instance { get; } = new();

    private Timer? _timer;
    private int _busy;

    public List<FanChannel> Channels { get; private set; } = new();
    public bool Started { get; private set; }
    public event EventHandler? Ready;

    public void Start()
    {
        if (Started) return;
        Channels = HardwareService.Instance.GetFanControls().Select(c => new FanChannel(c.Control, c.Rpm)).ToList();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<List<FanConfig>>(File.ReadAllText(ConfigPath)) ?? new();
                foreach (var ch in Channels)
                    if (saved.FirstOrDefault(s => s.Id == ch.Id) is { } cfg) ch.Load(cfg);
            }
        }
        catch { }
        Started = true;
        _timer = new Timer(_ => Tick(), null, 1000, 2000);
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var r = HardwareService.Instance.Readings;
            var cpuTemp = HardwareService.CpuNow(r).Temp;
            var gpuTemp = HardwareService.GpuNow(r).Temp;
            var save = false;

            // Proteção: com o PC muito quente, qualquer ventoinha controlada pelo SoNexus vai a 100%,
            // não importa a escolha (evita superaquecer com "fixo baixo" ou curva mal feita).
            var emergency = cpuTemp >= 90 || gpuTemp >= 88;

            foreach (var ch in Channels)
            {
                save |= ch.Dirty;
                ch.Dirty = false;
                try
                {
                    if (emergency && ch.Mode != FanMode.Automatico)
                    {
                        HardwareService.Instance.SetControl(ch.Control, (float)ch.Control.MaxSoftwareValue);
                        ch.Status = $"Proteção: 100% (CPU {cpuTemp:0} °C · GPU {gpuTemp:0} °C)";
                        ch.Applied = true;
                        ch.Percent = ch.ControlSensor.Value is { } pv ? $"{pv:0}%" : "—";
                        continue;
                    }
                    switch (ch.Mode)
                    {
                        case FanMode.Automatico:
                            if (!ch.Applied) HardwareService.Instance.SetControl(ch.Control, null);
                            ch.Status = "Automático (placa)";
                            break;
                        case FanMode.Fixo:
                            HardwareService.Instance.SetControl(ch.Control, (float)Clamp(ch, ch.Fixed));
                            ch.Status = $"Fixo em {Clamp(ch, ch.Fixed):0}%";
                            break;
                        case FanMode.Curva:
                            var temp = ch.SourceIndex == 0 ? cpuTemp : gpuTemp;
                            if (temp is { } t)
                            {
                                var target = Clamp(ch, ch.Interpolate(t));
                                HardwareService.Instance.SetControl(ch.Control, (float)target);
                                ch.Status = $"Curva: {t:0} °C → {target:0}%";
                            }
                            else
                            {
                                HardwareService.Instance.SetControl(ch.Control, null);
                                ch.Status = "Sem sensor de temperatura: automático";
                            }
                            break;
                    }
                    ch.Applied = true;
                }
                catch (Exception ex)
                {
                    ch.Status = $"Erro: {ex.Message}";
                }
                ch.Percent = ch.ControlSensor.Value is { } v ? $"{v:0}%" : "—";
                ch.Rpm = ch.RpmSensor?.Value is { } rpm ? $"{rpm:0} RPM" : "";
            }

            if (save) Save();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static double Clamp(FanChannel ch, double percent) =>
        Math.Clamp(percent, Math.Max(MinimumPercent, ch.Control.MinSoftwareValue), ch.Control.MaxSoftwareValue);

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Channels.Select(c => c.ToConfig())));
        }
        catch { }
    }

    /// <summary>Devolve todas as ventoinhas para o controle automático (ao fechar o programa).</summary>
    public void RestoreAll()
    {
        _timer?.Dispose();
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, 2000);
        foreach (var ch in Channels)
        {
            try { HardwareService.Instance.SetControl(ch.Control, null); } catch { }
        }
    }
}
