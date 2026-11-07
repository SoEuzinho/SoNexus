using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SoNexus.Services;

/// <summary>
/// Faz o que o Process Lasso faz para jogos:
/// - Jogo na frente: prioridade "Alta" para ele.
/// - Equilíbrio (ProBalance): enquanto o jogo está na frente, programas em segundo plano que comem CPU
///   descem para "Abaixo do normal" e voltam ao normal quando o jogo sai da frente ou fecha.
/// Nunca mexe em processos do Windows, de outros usuários, nem em programas de stream/voz.
/// Roda a cada 3 s só quando ligado: quase nada de CPU.
/// </summary>
public sealed class GameBoostService
{
    public static GameBoostService Instance { get; } = new();

    /// <summary>Processos que nunca são rebaixados (Windows, áudio, captura, voz).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "audiodg", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "MsMpEng", "NisSrv", "SecurityHealthService", "SoNexus",
        "obs64", "obs32", "Streamlabs OBS", "Discord", "Teamspeak", "ts3client_win64", "nvcontainer", "NVIDIA Overlay", "Medal",
    };

    private readonly object _lock = new();
    private Timer? _timer;
    private HashSet<string> _libraryDirs = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _libraryRead = DateTime.MinValue;
    private int? _gamePid;
    private ProcessPriorityClass? _gameOriginal;
    private readonly Dictionary<int, (ProcessPriorityClass Original, string Name)> _lowered = new();
    private Dictionary<int, TimeSpan> _cpuBefore = new();
    private DateTime _sampleAt;

    /// <summary>Últimas ações, para a tela mostrar (mais nova primeiro).</summary>
    public List<string> Log { get; } = new();
    public string? CurrentGame { get; private set; }
    public int LoweredCount { get { lock (_lock) return _lowered.Count; } }
    public bool Running => _timer != null;

    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;
            _timer = new Timer(_ => Tick(), null, 1000, 3000);
        }
    }

    /// <summary>Para e devolve as prioridades originais a todo mundo.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            RestoreAll();
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_timer == null) return;
            try { Step(); }
            catch (Exception ex) { AppLog.Write($"Prioridade dos jogos: {ex.Message}"); }
        }
    }

    private void Step()
    {
        if (DateTime.Now - _libraryRead > TimeSpan.FromMinutes(15))
        {
            _libraryRead = DateTime.Now;
            try
            {
                _libraryDirs = GamesService.ScanLibrary().Select(g => g.InstallPath).Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => Path.GetFullPath(p!).TrimEnd('\\') + "\\").ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        var fg = ForegroundGame();
        if (fg == null)
        {
            if (_gamePid != null || _lowered.Count > 0) RestoreAll();
            return;
        }

        var (pid, name) = fg.Value;
        if (_gamePid != pid)
        {
            RestoreAll();
            try
            {
                using var p = Process.GetProcessById(pid);
                _gameOriginal = p.PriorityClass;
                if (p.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.AboveNormal)
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    Add($"{name}: prioridade Alta");
                }
                _gamePid = pid;
                CurrentGame = name;
            }
            catch { return; }
        }

        if (AppSettings.Current.ProBalance) Balance(pid);
    }

    /// <summary>Rebaixa quem usou mais de 8% da CPU do PC nos últimos 3 s (em segundo plano).</summary>
    private void Balance(int gamePid)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _sampleAt).TotalMilliseconds;
        var session = Process.GetCurrentProcess().SessionId;
        var cpu = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == gamePid || p.Id == Environment.ProcessId || p.SessionId != session || Protected.Contains(p.ProcessName)) continue;
                    var t = p.TotalProcessorTime;
                    cpu[p.Id] = t;
                    if (_lowered.ContainsKey(p.Id) || elapsed is < 1000 or > 10000 || !_cpuBefore.TryGetValue(p.Id, out var before)) continue;
                    var share = (t - before).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                    if (share < 8 || p.PriorityClass != ProcessPriorityClass.Normal) continue;
                    _lowered[p.Id] = (p.PriorityClass, p.ProcessName);
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    Add($"{p.ProcessName}: usava {share:0}% da CPU em segundo plano → Abaixo do normal");
                }
                catch { } // processo protegido ou que acabou de fechar
            }
        }
        _cpuBefore = cpu;
        _sampleAt = now;
    }

    private void RestoreAll()
    {
        foreach (var (pid, (original, name)) in _lowered)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName == name && p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = original;
            }
            catch { }
        }
        if (_lowered.Count > 0) Add($"{_lowered.Count} programa(s) de volta ao normal");
        _lowered.Clear();
        if (_gamePid is { } g && _gameOriginal is { } orig)
        {
            try
            {
                using var p = Process.GetProcessById(g);
                if (p.PriorityClass == ProcessPriorityClass.High) p.PriorityClass = orig;
            }
            catch { }
        }
        _gamePid = null;
        _gameOriginal = null;
        CurrentGame = null;
    }

    /// <summary>
    /// A janela da frente é de um jogo? Sim se o .exe está numa pasta de jogo da biblioteca (Steam, Epic...)
    /// ou se a janela cobre a tela inteira (tela cheia ou janela sem borda).
    /// </summary>
    private (int Pid, string Name)? ForegroundGame()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (Protected.Contains(p.ProcessName)) return null;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null && _libraryDirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return ((int)pid, p.ProcessName);
            if (CoversMonitor(hwnd)) return ((int)pid, p.ProcessName);
        }
        catch { }
        return null;
    }

    private static bool CoversMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    private void Add(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        if (Log.Count > 40) Log.RemoveAt(Log.Count - 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
