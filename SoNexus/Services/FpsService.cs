using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SoNexus.Services;

/// <summary>Stutters: quadros que demoraram mais de 2,5× o normal (engasgos que você sente na tela).</summary>
public sealed record FpsStats(double AvgFps, double Low1, double Low01, double MaxFrameTime, int Frames, TimeSpan Duration, int Stutters, double MedianMs)
{
    /// <summary>Consistência: 1% low perto da média = jogo liso; longe = travadinhas.</summary>
    public string Smoothness => (Low1 / AvgFps) switch
    {
        >= 0.75 => "Muito liso",
        >= 0.6 => "Liso",
        >= 0.45 => "Algumas travadinhas",
        _ => "Engasgando bastante",
    };
}

public sealed record FpsProcess(int Pid, string Name, double Fps, double FrameTime)
{
    public string FpsText => $"{Fps:0} FPS";
    public string Api { get; init; } = "";
}

/// <summary>
/// Estima a cadência de apresentação por processo a partir de eventos ETW do DirectX ou do kernel gráfico.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int FrameTimeBucketsPerMs = 10;

    private readonly object _lock = new();
    private readonly Dictionary<int, State> _processes = new();
    private readonly Dictionary<int, string> _names = new();
    private TraceEventSession? _session;
    private SessionFrames? _frames;

    private sealed class State
    {
        public double LastMs = -1;
        /// <summary>O processo manda eventos do DirectX: ignora os do kernel para não contar o quadro duas vezes.</summary>
        public bool HasDirectX;
        public string Api = "";
        public readonly Queue<(long Wall, double Ms)> Recent = new();
    }

    private sealed class SessionFrames(int pid)
    {
        public readonly int Pid = pid;
        public readonly long Started = Stopwatch.GetTimestamp();
        public readonly SortedDictionary<int, (int Count, double Total)> Histogram = new();
        public int Count;
        public double Total;
        public double Worst;

        public void Add(double milliseconds)
        {
            var bucket = (int)(milliseconds * FrameTimeBucketsPerMs);
            var current = Histogram.GetValueOrDefault(bucket);
            Histogram[bucket] = (current.Count + 1, current.Total + milliseconds);
            Count++;
            Total += milliseconds;
            Worst = Math.Max(Worst, milliseconds);
        }

        public FpsStats? Stats()
        {
            if (Count < 10) return null;

            var middle = Count / 2;
            var median = 0d;
            foreach (var (count, total) in Histogram.Values)
            {
                if (middle < count)
                {
                    median = total / count;
                    break;
                }
                middle -= count;
            }

            double Low(double fraction)
            {
                var requested = Math.Max(1, (int)(Count * fraction));
                var skip = Count - requested;
                var slowest = 0d;
                foreach (var (count, total) in Histogram.Values)
                {
                    var skipped = Math.Min(count, skip);
                    skip -= skipped;
                    slowest += total / count * (count - skipped);
                }
                return 1000 * requested / slowest;
            }

            // O tamanho dos grupos é de 0,1 ms, mantendo o custo estável em sessões longas.
            var stutters = Histogram.Values.Where(b => b.Total / b.Count > Math.Max(20, median * 2.5)).Sum(b => b.Count);
            return new FpsStats(1000 * Count / Total, Low(0.01), Low(0.001), Worst, Count,
                Stopwatch.GetElapsedTime(Started), stutters, median);
        }
    }

    public void Start()
    {
        // Uma sessão que ficou aberta (app fechado à força) impediria a nova de começar.
        try { TraceEventSession.GetActiveSession("SoNexus-FPS")?.Stop(true); } catch { }
        _session = new TraceEventSession("SoNexus-FPS") { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Verbose);
        _session.EnableProvider(D3D9, TraceEventLevel.Verbose);
        try { _session.EnableProvider(DxgKrnl, TraceEventLevel.Verbose, 0x8000001 /* Base | Present */); } catch { }
        _session.Source.AllEvents += OnEvent;
        var source = _session.Source;
        new Thread(() => { try { source.Process(); } catch { } }) { IsBackground = true }.Start();
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        // DXGI: 42 = Present_Start, 55 = PresentMultiplaneOverlay_Start. D3D9: 1 = Present_Start.
        // DxgKrnl: 184 = Present_Info (chamado por OpenGL/Vulkan e pelo DirectX também).
        string api;
        if (e.ProviderGuid == Dxgi && id is 42 or 55) api = "DXGI";
        else if (e.ProviderGuid == D3D9 && id == 1) api = "Direct3D 9";
        else if (e.ProviderGuid == DxgKrnl && id == 184) api = "Kernel gráfico";
        else return;

        var t = e.TimeStampRelativeMSec;
        var wall = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (!_processes.TryGetValue(e.ProcessID, out var s)) _processes[e.ProcessID] = s = new State();
            if (e.ProviderGuid == DxgKrnl)
            {
                if (s.HasDirectX) return;
            }
            else if (!s.HasDirectX)
            {
                // Primeiro evento do DirectX: descarta o que veio do kernel até aqui.
                s.HasDirectX = true;
                s.LastMs = -1;
                s.Recent.Clear();
                if (_frames?.Pid == e.ProcessID)
                    _frames = new SessionFrames(e.ProcessID);
            }
            s.Api = api;
            if (s.LastMs >= 0)
            {
                var dt = t - s.LastMs;
                if (dt is > 0 and < 2000)
                {
                    s.Recent.Enqueue((wall, dt));
                    if (_frames?.Pid == e.ProcessID) _frames.Add(dt);
                }
            }
            s.LastMs = t;
        }
    }

    /// <summary>Processos que estão desenhando quadros agora, do maior FPS para o menor.</summary>
    public List<FpsProcess> Active()
    {
        var limit = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2; // janela de 2 s
        var list = new List<FpsProcess>();
        lock (_lock)
        {
            foreach (var (pid, s) in _processes)
            {
                while (s.Recent.Count > 0 && s.Recent.Peek().Wall < limit) s.Recent.Dequeue();
                if (s.Recent.Count < 2) continue;
                var avg = s.Recent.Average(r => r.Ms);
                list.Add(new FpsProcess(pid, NameOf(pid), 1000 / avg, avg) { Api = s.Api });
            }
        }
        return list.OrderByDescending(p => p.Fps).ToList();
    }

    /// <summary>Tempos de quadro (ms) dos últimos 2 segundos de um processo.</summary>
    public List<double> RecentFrameTimes(int pid)
    {
        lock (_lock) return _processes.TryGetValue(pid, out var s) ? s.Recent.Select(r => r.Ms).ToList() : new();
    }

    public FpsStats? SessionStats(int pid)
    {
        lock (_lock)
            return _frames?.Pid == pid ? _frames.Stats() : null;
    }

    public void ResetSession(int pid)
    {
        lock (_lock)
        {
            if (_processes.TryGetValue(pid, out var s)) s.LastMs = -1;
            _frames = new SessionFrames(pid);
        }
    }

    /// <summary>Seleciona o processo ativo em primeiro plano ou mantém a medição ao focalizar o SoNexus.</summary>
    public FpsProcess? PickGame(List<FpsProcess> active, int? currentPid)
    {
        var candidates = active.Where(p => !Ignored(p.Name)).ToList();
        GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
        return fg == Environment.ProcessId ? candidates.FirstOrDefault(p => p.Pid == currentPid)
            : candidates.FirstOrDefault(p => p.Pid == fg);
    }

    private static bool Ignored(string name) =>
        name.Equals("dwm", StringComparison.OrdinalIgnoreCase) || name.Equals("SoNexus", StringComparison.OrdinalIgnoreCase);

    private string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try { using var p = Process.GetProcessById(pid); n = p.ProcessName; }
        catch { n = $"PID {pid}"; }
        return _names[pid] = n;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
