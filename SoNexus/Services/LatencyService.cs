using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace SoNexus.Services;

public sealed class DriverLatency
{
    public required string Driver { get; init; }
    public string Description { get; init; } = "";
    public long DpcCount { get; set; }
    public double DpcMaxUs { get; set; }
    public double DpcTotalUs { get; set; }
    public long IsrCount { get; set; }
    public double IsrMaxUs { get; set; }

    public string DpcMaxText => DpcCount > 0 ? $"{DpcMaxUs:0} µs" : "—";
    public string DpcTotalText => $"{DpcTotalUs / 1000:0.0} ms";
    public string IsrMaxText => IsrCount > 0 ? $"{IsrMaxUs:0} µs" : "—";
    public string Level => Math.Max(DpcMaxUs, IsrMaxUs) switch { >= 1000 => "Problema", >= 500 => "Atenção", _ => "OK" };
}

public sealed record LatencySnapshot(
    double TimerMaxUs, double TimerAvgUs, double TimerLastSecondMaxUs, long TimerSamples,
    double DpcMaxUs, string DpcMaxDriver, double IsrMaxUs, string IsrMaxDriver,
    List<DriverLatency> Drivers, string? EtwError);

/// <summary>
/// Medição de latência no estilo LatencyMon:
/// 1) um timer de 1 ms de alta prioridade mede quanto o Windows atrasa para acordar um processo;
/// 2) um rastreamento ETW do kernel mede o tempo de cada DPC/ISR e descobre qual driver o executou.
/// </summary>
public sealed class LatencyMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DriverLatency> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private (ulong Base, string Name)[] _modules = Array.Empty<(ulong, string)>();
    private TraceEventSession? _session;
    private Thread? _timerThread;
    private volatile bool _running;

    private double _timerMax, _timerSum, _secondMax;
    private long _timerCount;
    private double _dpcMax, _isrMax;
    private string _dpcMaxDriver = "—", _isrMaxDriver = "—";
    private string? _etwError;

    public void Start()
    {
        _running = true;
        _modules = LoadDriverModules();

        _timerThread = new Thread(() =>
        {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
            TimerLoop();
        }) { IsBackground = true };
        _timerThread.Start();

        try
        {
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
            var kernel = _session.Source.Kernel;
            kernel.PerfInfoDPC += OnDpc;
            kernel.PerfInfoTimerDPC += OnDpc;
            kernel.PerfInfoThreadedDPC += OnDpc;
            kernel.PerfInfoISR += OnIsr;
            var source = _session.Source;
            new Thread(() => { try { source.Process(); } catch { } }) { IsBackground = true }.Start();
        }
        catch (Exception ex)
        {
            _etwError = $"Rastreamento de drivers indisponível: {ex.Message}";
        }
    }

    public LatencySnapshot Snapshot()
    {
        lock (_lock)
        {
            var second = _secondMax;
            _secondMax = 0;
            var drivers = _drivers.Values
                .Select(d => new DriverLatency
                {
                    Driver = d.Driver, Description = d.Description, DpcCount = d.DpcCount, DpcMaxUs = d.DpcMaxUs,
                    DpcTotalUs = d.DpcTotalUs, IsrCount = d.IsrCount, IsrMaxUs = d.IsrMaxUs,
                })
                .OrderByDescending(d => Math.Max(d.DpcMaxUs, d.IsrMaxUs))
                .ToList();
            return new LatencySnapshot(_timerMax, _timerCount > 0 ? _timerSum / _timerCount : 0, second, _timerCount,
                _dpcMax, _dpcMaxDriver, _isrMax, _isrMaxDriver, drivers, _etwError);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _session?.Dispose(); } catch { }
        _timerThread?.Join(500);
    }

    // ---------- Lacunas de execução (atraso causado por interrupções e drivers) ----------

    /// <summary>
    /// Uma thread de prioridade máxima lê o relógio sem parar. Qualquer buraco entre duas leituras
    /// é tempo em que o Windows tirou a CPU dela (ISR, DPC ou algo mais prioritário).
    /// Usa 1 núcleo enquanto mede.
    /// </summary>
    private void TimerLoop()
    {
        var freq = Stopwatch.Frequency;
        var threshold = freq * 10 / 1_000_000; // lacunas acima de 10 µs contam como atraso
        var warmUntil = Stopwatch.GetTimestamp() + freq / 2;
        var gcCount = GC.CollectionCount(0);
        var previous = Stopwatch.GetTimestamp();
        while (_running)
        {
            var now = Stopwatch.GetTimestamp();
            var gap = now - previous;
            previous = now;
            if (gap <= threshold || now < warmUntil) continue;

            // Pausa do coletor de lixo do próprio SoNexus não é culpa do Windows: ignora.
            var gc = GC.CollectionCount(0);
            if (gc != gcCount)
            {
                gcCount = gc;
                continue;
            }

            var us = gap * 1_000_000.0 / freq;
            lock (_lock)
            {
                _timerCount++;
                _timerSum += us;
                if (us > _timerMax) _timerMax = us;
                if (us > _secondMax) _secondMax = us;
            }
        }
    }

    // ---------- ETW (DPC / ISR por driver) ----------

    private void OnDpc(DPCTraceData d) => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: false);

    private void OnIsr(ISRTraceData d) => Record(d.Routine, d.ElapsedTimeMSec * 1000, isr: true);

    private void Record(ulong routine, double us, bool isr)
    {
        var name = ModuleFor(routine);
        lock (_lock)
        {
            if (!_drivers.TryGetValue(name, out var drv))
                _drivers[name] = drv = new DriverLatency { Driver = name, Description = Describe(name) };
            if (isr)
            {
                drv.IsrCount++;
                drv.IsrMaxUs = Math.Max(drv.IsrMaxUs, us);
                if (us > _isrMax) { _isrMax = us; _isrMaxDriver = name; }
            }
            else
            {
                drv.DpcCount++;
                drv.DpcTotalUs += us;
                drv.DpcMaxUs = Math.Max(drv.DpcMaxUs, us);
                if (us > _dpcMax) { _dpcMax = us; _dpcMaxDriver = name; }
            }
        }
    }

    private string ModuleFor(ulong address)
    {
        var mods = _modules;
        int lo = 0, hi = mods.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (mods[mid].Base <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? mods[found].Name : "desconhecido";
    }

    private static string Describe(string driver)
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", driver);
            if (!File.Exists(path)) path = Path.Combine(Environment.SystemDirectory, driver);
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static (ulong, string)[] LoadDriverModules()
    {
        EnumDeviceDrivers(null, 0, out var needed);
        var bases = new UIntPtr[needed / (uint)UIntPtr.Size];
        if (!EnumDeviceDrivers(bases, needed, out _)) return Array.Empty<(ulong, string)>();
        var sb = new StringBuilder(260);
        return bases
            .Where(b => b != UIntPtr.Zero)
            .Select(b =>
            {
                sb.Clear();
                GetDeviceDriverBaseNameW(b, sb, (uint)sb.Capacity);
                return ((ulong)b, sb.ToString());
            })
            .OrderBy(m => m.Item1)
            .ToArray();
    }

    // ---------- Win32 ----------

    private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers([Out] UIntPtr[]? addresses, uint cb, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDeviceDriverBaseNameW(UIntPtr imageBase, StringBuilder name, uint size);
}
