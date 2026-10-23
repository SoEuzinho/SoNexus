using LibreHardwareMonitor.Hardware;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record SensorReading(
    string Id, string Hardware, HardwareType HardwareType, SensorType Type,
    string Name, float? Value, float? Min, float? Max);

/// <summary>
/// Lê todos os sensores (temperatura, uso, consumo, clocks, ventoinhas...) via LibreHardwareMonitor
/// uma vez por segundo e publica um snapshot imutável.
/// </summary>
public sealed class HardwareService : IDisposable
{
    public static HardwareService Instance { get; } = new();

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
    };

    private readonly object _hwLock = new();
    private Timer? _timer;
    private int _busy;
    private volatile IReadOnlyList<SensorReading> _readings = Array.Empty<SensorReading>();

    private HardwareService() { }

    public event EventHandler? Updated;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<SensorReading> Readings => _readings;

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _computer.Open();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }
        _timer = new Timer(_ => Tick(), null, 0, _interval);
    }

    private int _interval = 1000;

    /// <summary>De quanto em quanto tempo os sensores são lidos (Integração › Sistema).</summary>
    public int IntervalMs
    {
        get => _interval;
        set
        {
            _interval = Math.Clamp(value, 500, 10000);
            _timer?.Change(_interval, _interval);
        }
    }

    /// <summary>Com a janela minimizada, lê os sensores a cada 5 s em vez de 1 s.</summary>
    public bool Minimized { get; set; }

    private long _tick;

    private void Tick()
    {
        // Se a leitura anterior ainda não terminou, pula este ciclo.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var tick = _tick++;
            if (Minimized && tick % 5 != 0) return;
            // Discos (SMART) são lentos de ler e mudam pouco: só a cada 5 s.
            var updateStorage = tick % 5 == 0;

            var list = new List<SensorReading>(_readings.Count + 16);
            lock (_hwLock)
            {
                foreach (var hw in _computer.Hardware) Collect(hw, list, updateStorage);
            }
            _readings = list;
            Error = null; // uma falha passageira não deixa o aviso de erro preso na tela
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static void Collect(IHardware hw, List<SensorReading> list, bool updateStorage)
    {
        if (updateStorage || hw.HardwareType != HardwareType.Storage) hw.Update();
        foreach (var s in hw.Sensors)
            list.Add(new SensorReading(s.Identifier.ToString(), hw.Name, hw.HardwareType, s.SensorType, s.Name, s.Value, s.Min, s.Max));
        foreach (var sub in hw.SubHardware) Collect(sub, list, updateStorage);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, 2000);
        try { if (IsRunning) _computer.Close(); } catch { }
        IsRunning = false;
    }

    /// <summary>Acesso direto aos objetos do hardware (ex.: SMART dos discos) sem brigar com a leitura periódica.</summary>
    public T WithHardware<T>(Func<IReadOnlyList<IHardware>, T> read)
    {
        if (!IsRunning) return read(Array.Empty<IHardware>());
        lock (_hwLock) return read(_computer.Hardware.ToList());
    }

    // ---------- Controle de ventoinhas ----------

    /// <summary>Ventoinhas controláveis (placa-mãe e GPU) e o sensor de RPM correspondente.</summary>
    public List<(ISensor Control, ISensor? Rpm)> GetFanControls()
    {
        var result = new List<(ISensor, ISensor?)>();
        if (!IsRunning) return result;
        lock (_hwLock)
        {
            void Walk(IHardware hw)
            {
                var sensors = hw.Sensors;
                foreach (var s in sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
                {
                    var rpm = sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Index == s.Index)
                              ?? sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Name == s.Name);
                    result.Add((s, rpm));
                }
                foreach (var sub in hw.SubHardware) Walk(sub);
            }
            foreach (var hw in _computer.Hardware) Walk(hw);
        }
        return result;
    }

    /// <summary>null = devolve o controle para a placa/BIOS.</summary>
    public void SetControl(IControl control, float? percent)
    {
        lock (_hwLock)
        {
            if (percent is { } p) control.SetSoftware(p);
            else control.SetDefault();
        }
    }

    // ---------- Consultas ----------

    /// <summary>Temperatura, uso, consumo e clock do núcleo da placa de vídeo principal.</summary>
    public static (float? Temp, float? Load, float? Power, float? Clock) GpuNow(IReadOnlyList<SensorReading> r)
    {
        var gpu = GpuFilter(r);
        return (Find(r, gpu, SensorType.Temperature, true, "GPU Core", "GPU Hot Spot")?.Value,
                Find(r, gpu, SensorType.Load, true, "GPU Core", "D3D 3D")?.Value,
                Find(r, gpu, SensorType.Power, true, "GPU Package", "GPU Power", "GPU Total")?.Value,
                Find(r, gpu, SensorType.Clock, true, "GPU Core")?.Value);
    }

    public static bool IsCpu(HardwareType t) => t == HardwareType.Cpu;

    /// <summary>
    /// Escolhe a placa principal: NVIDIA, depois AMD, depois Intel. Ryzen com vídeo integrado
    /// também aparece como "GpuAmd", então a NVIDIA tem prioridade quando existe.
    /// </summary>
    public static Func<HardwareType, bool> GpuFilter(IReadOnlyList<SensorReading> readings)
    {
        if (readings.Any(r => r.HardwareType == HardwareType.GpuNvidia)) return t => t == HardwareType.GpuNvidia;
        if (readings.Any(r => r.HardwareType == HardwareType.GpuAmd)) return t => t == HardwareType.GpuAmd;
        return t => t == HardwareType.GpuIntel;
    }

    /// <summary>Procura o primeiro sensor com um dos nomes, na ordem dada.</summary>
    public static SensorReading? Find(IReadOnlyList<SensorReading> readings, Func<HardwareType, bool> hw,
        SensorType type, bool anyFallback, params string[] names)
    {
        // Temperatura 0 = sensor sem acesso (sem administrador), não uma leitura real.
        var candidates = readings
            .Where(r => hw(r.HardwareType) && r.Type == type && r.Value.HasValue && (type != SensorType.Temperature || r.Value is > 0 and < 150))
            .ToList();
        foreach (var n in names)
        {
            var match = candidates.FirstOrDefault(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return anyFallback ? candidates.FirstOrDefault() : null;
    }

    /// <summary>Temperatura, consumo e clock médio dos núcleos da CPU agora.</summary>
    public static (float? Temp, float? Power, float? Clock) CpuNow(IReadOnlyList<SensorReading> r)
    {
        var temp = Find(r, IsCpu, SensorType.Temperature, true,
            "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tdie", "Core Max", "Core Average")?.Value;
        var power = Find(r, IsCpu, SensorType.Power, true, "CPU Package", "Package")?.Value;
        var clocks = r.Where(x => IsCpu(x.HardwareType) && x.Type == SensorType.Clock && x.Name.Contains("Core") && x.Value > 0)
            .Select(x => x.Value!.Value).ToList();
        return (temp, power, clocks.Count > 0 ? clocks.Average() : null);
    }

    public static string? HardwareName(IReadOnlyList<SensorReading> readings, Func<HardwareType, bool> hw) =>
        readings.FirstOrDefault(r => hw(r.HardwareType))?.Hardware;
}

public static class SensorFormat
{
    public static string TypeName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Uso",
        SensorType.Power => "Consumo",
        SensorType.Clock => "Clock",
        SensorType.Voltage => "Tensão",
        SensorType.Fan => "Ventoinha",
        SensorType.Control => "Controle",
        SensorType.Level => "Nível",
        SensorType.Data => "Dados",
        SensorType.SmallData => "Dados",
        SensorType.Throughput => "Taxa",
        _ => t.ToString(),
    };

    public static string Value(SensorType t, float? v)
    {
        if (v is not { } x) return "—";
        return t switch
        {
            SensorType.Temperature => $"{x:0.0} °C",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{x:0.0} %",
            SensorType.Power => $"{x:0.0} W",
            SensorType.Clock => $"{x:0} MHz",
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Data => $"{x:0.0} GB",
            SensorType.SmallData => $"{x:0} MB",
            SensorType.Throughput => Format.Bytes((ulong)Math.Max(0, x)) + "/s",
            _ => $"{x:0.##}",
        };
    }
}
