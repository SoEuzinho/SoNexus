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
/// Mede o FPS de qualquer jogo sem injetar nada nele, no estilo do PresentMon:
/// escuta pelo ETW os eventos "Present" do DirectX (DXGI e D3D9) e calcula o tempo entre quadros.
/// Jogos em Vulkan/OpenGL não passam pelo DXGI: para eles usamos o evento de "Present" do kernel de vídeo (DxgKrnl).
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int MaxSessionFrames = 1_000_000;

    private readonly object _lock = new();
    private readonly Dictionary<int, State> _processes = new();
    private readonly Dictionary<int, string> _names = new();
    private TraceEventSession? _session;

    private sealed class State
    {
        public double LastMs = -1;
        /// <summary>O processo manda eventos do DirectX: ignora os do kernel para não contar o quadro duas vezes.</summary>
        public bool HasDirectX;
        public string Api = "";
        public readonly Queue<(long Wall, double Ms)> Recent = new();
        public readonly List<double> Session = new();
        public long SessionStart = Stopwatch.GetTimestamp();
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
        if (e.ProviderGuid == Dxgi && id is 42 or 55) api = "DirectX 10/11/12";
        else if (e.ProviderGuid == D3D9 && id == 1) api = "DirectX 9";
        else if (e.ProviderGuid == DxgKrnl && id == 184) api = "Vulkan/OpenGL";
        else return;

        var t = e.TimeStampRelativeMSec;
        var wall = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (!_processes.TryGetValue(e.ProcessID, out var s)) _processes[e.ProcessID] = s = new State();
            if (api == "Vulkan/OpenGL")
            {
                if (s.HasDirectX) return;
            }
            else if (!s.HasDirectX)
            {
                // Primeiro evento do DirectX: descarta o que veio do kernel até aqui.
                s.HasDirectX = true;
                s.LastMs = -1;
                s.Recent.Clear();
                s.Session.Clear();
            }
            s.Api = api;
            if (s.LastMs >= 0)
            {
                var dt = t - s.LastMs;
                if (dt is > 0 and < 2000)
                {
                    s.Recent.Enqueue((wall, dt));
                    if (s.Session.Count < MaxSessionFrames) s.Session.Add(dt);
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
        List<double> frames;
        long start;
        lock (_lock)
        {
            if (!_processes.TryGetValue(pid, out var s) || s.Session.Count < 10) return null;
            frames = s.Session.ToList();
            start = s.SessionStart;
        }
        var sorted = frames.OrderByDescending(f => f).ToList();
        double Low(double fraction) => 1000 / sorted.Take(Math.Max(1, (int)(sorted.Count * fraction))).Average();
        var median = sorted[sorted.Count / 2];
        var stutters = frames.Count(f => f > median * 2.5 && f > 20);
        return new FpsStats(1000 / frames.Average(), Low(0.01), Low(0.001), sorted[0], frames.Count,
            Stopwatch.GetElapsedTime(start), stutters, median);
    }

    public void ResetSession(int pid)
    {
        lock (_lock)
        {
            if (!_processes.TryGetValue(pid, out var s)) return;
            s.Session.Clear();
            s.SessionStart = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Escolhe o jogo: a janela em primeiro plano, se estiver desenhando; senão o maior FPS.</summary>
    public FpsProcess? PickGame(List<FpsProcess> active)
    {
        var candidates = active.Where(p => !Ignored(p.Name)).ToList();
        GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
        return candidates.FirstOrDefault(p => p.Pid == fg) ?? candidates.FirstOrDefault();
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
