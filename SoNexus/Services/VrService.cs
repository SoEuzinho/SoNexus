using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SoNexus.Services;

public sealed record VrApp(string Name, string Status, string Detail, string? Launch);

public sealed record OpenXrRuntime(string Name, string JsonPath, bool Active)
{
    public string Display => Active ? $"{Name}  (ativo)" : Name;
    public override string ToString() => Display;
}

/// <summary>Realidade virtual: Virtual Desktop, SteamVR, Meta Quest Link, runtimes OpenXR e um "Modo VR".</summary>
public static class VrService
{
    private const string OpenXrKey = @"SOFTWARE\Khronos\OpenXR\1";
    private static readonly string[] VrProcesses = { "VirtualDesktop.Streamer", "vrserver", "vrcompositor", "vrmonitor", "OVRServer_x64", "alvr_dashboard" };

    /// <summary>
    /// Óculos conectado agora: o programa de streaming mantém uma conexão com um IP da rede local (o próprio óculos).
    /// </summary>
    public static (string App, string Ip)? HeadsetConnection()
    {
        var names = new Dictionary<int, string>();
        foreach (var c in NetworkService.Connections().Where(c => !c.Listening && IsLan(c.RemoteIp)))
        {
            if (!names.TryGetValue(c.Pid, out var name)) names[c.Pid] = name = c.Process;
            if (name.StartsWith("VirtualDesktop", StringComparison.OrdinalIgnoreCase)) return ("Virtual Desktop", c.RemoteIp);
            if (name.StartsWith("alvr", StringComparison.OrdinalIgnoreCase)) return ("ALVR", c.RemoteIp);
            if (name.StartsWith("streaming_client", StringComparison.OrdinalIgnoreCase) || name.Equals("steamlink", StringComparison.OrdinalIgnoreCase)) return ("Steam Link", c.RemoteIp);
            if (name.StartsWith("OVRServer", StringComparison.OrdinalIgnoreCase)) return ("Air Link", c.RemoteIp);
        }
        return null;
    }

    private static bool IsLan(string ip) =>
        ip.StartsWith("192.168.") || ip.StartsWith("10.") ||
        (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31);

    public static List<string> RunningVrSoftware()
    {
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        if (running.Contains("VirtualDesktop.Streamer")) list.Add("Virtual Desktop Streamer");
        if (running.Contains("vrserver")) list.Add("SteamVR");
        if (running.Contains("OVRServer_x64")) list.Add("Meta Quest Link");
        if (running.Contains("alvr_dashboard")) list.Add("ALVR");
        return list;
    }

    public sealed record Stability(double AvgMs, double P99Ms, double MaxMs, int Spikes, int LossPercent, int Samples)
    {
        public string Verdict => (LossPercent, Spikes, P99Ms) switch
        {
            ( > 1, _, _) => "Ruim: há perda de pacotes. O vídeo vai travar e pixelar.",
            (_, > 15, _) or (_, _, > 25) => "Instável: picos de atraso frequentes vão causar travadinhas e \"pulos\" na imagem.",
            (_, > 3, _) or (_, _, > 12) => "Razoável: alguns picos. Use qualidade/bitrate menor ou aproxime-se do roteador.",
            _ => "Ótimo para VR sem fio.",
        };
    }

    /// <summary>
    /// Ping a cada 50 ms por alguns segundos. Para VR sem fio importa a constância (picos), não só a média.
    /// </summary>
    public static async Task<Stability> StabilityTestAsync(string ip, int seconds, Action<double> progress, CancellationToken ct)
    {
        using var ping = new System.Net.NetworkInformation.Ping();
        var times = new List<double>();
        var sent = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(ip, 500);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch { }
            progress(sw.Elapsed.TotalSeconds / seconds * 100);
            await Task.Delay(50, CancellationToken.None);
        }
        if (times.Count == 0) return new Stability(0, 0, 0, 0, 100, sent);
        var sorted = times.OrderBy(t => t).ToList();
        var avg = times.Average();
        return new Stability(avg, sorted[(int)(sorted.Count * 0.99) - (sorted.Count >= 100 ? 1 : 0)], sorted[^1],
            times.Count(t => t > Math.Max(10, avg * 3)), (int)Math.Round(100.0 * (sent - times.Count) / sent), sent);
    }

    /// <summary>
    /// FPS em VR a partir do FPS estimado em 1080p. Quest 3 em resolução padrão desenha ~2× 2064×2208 com
    /// supersampling ≈ 4,4× os pixels de 1080p; jogos VR costumam ser mais leves, então usamos o expoente 0,8.
    /// </summary>
    public static double VrFps(double gpuFps1080, double cpuFps) => Math.Min(gpuFps1080 / Math.Pow(4.4, 0.8), cpuFps * 0.85);

    public static List<VrApp> DetectApps()
    {
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = HiddenItemsService.GetPrograms().Select(p => p.Name).ToList();
        bool Has(string n) => installed.Any(i => i.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        var list = new List<VrApp>();

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var vdExe = Path.Combine(programFiles, @"Virtual Desktop Streamer\VirtualDesktop.Streamer.exe");
        if (Has("Virtual Desktop Streamer") || File.Exists(vdExe))
            list.Add(new VrApp("Virtual Desktop", running.Contains("VirtualDesktop.Streamer") ? "Streamer aberto" : "Instalado",
                "Joga os jogos do PC no Quest/Pico pelo Wi-Fi. Use com o runtime VDXR para menos latência em jogos OpenXR.", vdExe));

        if (GamesService.SteamAppInstalled(250820))
            list.Add(new VrApp("SteamVR", running.Contains("vrserver") ? "Rodando" : "Instalado",
                "Plataforma de VR da Steam. Necessária para jogos VR da Steam que não usam OpenXR.", "steam://rungameid/250820"));

        var oculus = Path.Combine(programFiles, @"Oculus\Support\oculus-client\OculusClient.exe");
        if (Has("Oculus") || Has("Meta Quest") || File.Exists(oculus))
            list.Add(new VrApp("Meta Quest Link", running.Contains("OVRServer_x64") ? "Rodando" : "Instalado",
                "App oficial da Meta para Link (cabo) e Air Link.", File.Exists(oculus) ? oculus : null));

        if (Has("ALVR") || running.Contains("alvr_dashboard"))
            list.Add(new VrApp("ALVR", running.Contains("alvr_dashboard") ? "Rodando" : "Instalado", "Streaming de VR gratuito e de código aberto.", null));
        if (Has("PICO Connect") || Has("Streaming Assistant"))
            list.Add(new VrApp("PICO Connect", "Instalado", "App oficial da Pico para jogar no PC.", null));
        if (Has("Pimax"))
            list.Add(new VrApp("Pimax Play", "Instalado", "Software dos óculos Pimax.", null));
        return list;
    }

    public static List<OpenXrRuntime> Runtimes()
    {
        var list = new List<OpenXrRuntime>();
        using var key = Registry.LocalMachine.OpenSubKey(OpenXrKey);
        var active = key?.GetValue("ActiveRuntime") as string ?? "";
        using var available = Registry.LocalMachine.OpenSubKey(OpenXrKey + @"\AvailableRuntimes");
        var paths = available?.GetValueNames().ToList() ?? new();
        if (active.Length > 0 && !paths.Contains(active, StringComparer.OrdinalIgnoreCase)) paths.Add(active);
        foreach (var path in paths)
            list.Add(new OpenXrRuntime(RuntimeName(path), path, path.Equals(active, StringComparison.OrdinalIgnoreCase)));
        return list;
    }

    private static string RuntimeName(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                return name;
        }
        catch { }
        var file = Path.GetFileNameWithoutExtension(jsonPath).ToLowerInvariant();
        return file.Contains("virtualdesktop") ? "Virtual Desktop (VDXR)" : file.Contains("steamxr") ? "SteamVR" : file.Contains("oculus") ? "Meta Quest Link" : file;
    }

    /// <summary>Troca qual programa responde pelos jogos OpenXR (precisa de administrador).</summary>
    public static string? SetActiveRuntime(OpenXrRuntime runtime)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(OpenXrKey, true);
            key.SetValue("ActiveRuntime", runtime.JsonPath, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Qual codec usar no streaming, pelo codificador de vídeo da placa.</summary>
    public static (string Codec, string Detail) Encoder(string gpu)
    {
        var g = gpu.ToUpperInvariant();
        if (g.Contains("RTX 40") || g.Contains("RTX 50") || g.Contains("RX 7") || g.Contains("RX 9") || g.Contains("ARC"))
            return ("AV1 10-bit", "Sua placa tem codificador AV1: melhor imagem com o mesmo bitrate. No Virtual Desktop, escolha o codec AV1 e bitrate de 150-200 Mbps (Quest 3/Pro).");
        if (g.Contains("RTX") || g.Contains("GTX 16") || g.Contains("RX 6") || g.Contains("RX 5"))
            return ("HEVC 10-bit", "Use HEVC 10-bit no Virtual Desktop/Air Link com 100-150 Mbps.");
        return ("H.264", "Placa sem codificador moderno: use H.264 e qualidade média.");
    }

    // ---------- Modo VR ----------

    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>
    /// Prepara o PC para VR: plano de energia de alto desempenho, prioridade alta para o streamer/compositor
    /// e memória liberada. Pode ser desfeito.
    /// </summary>
    public static async Task<List<string>> EnableVrModeAsync()
    {
        var log = new List<string>();
        var plans = await PowerPlans.ListAsync();
        var current = plans.FirstOrDefault(p => p.Active);
        var target = plans.FirstOrDefault(p => p.Name.Contains("Máximo", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                     ?? plans.FirstOrDefault(p => p.Id == HighPerformance);
        if (target != null && current?.Id != target.Id)
        {
            AppSettings.Current.PlanBeforeVr = current?.Id;
            AppSettings.Current.Save();
            await PowerPlans.SetActiveAsync(target.Id);
            log.Add($"✔ Plano de energia: {target.Name}");
        }

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!VrProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    log.Add($"✔ Prioridade alta: {p.ProcessName}");
                }
                catch { }
            }
        }

        var before = MemoryService.GetStatus().Available;
        await Task.Run(MemoryService.TrimWorkingSets);
        await Task.Run(MemoryService.PurgeStandbyList);
        var freed = MemoryService.GetStatus().Available - before;
        log.Add($"✔ Memória liberada: {Models.Format.Bytes((ulong)Math.Max(0, (long)freed))}");
        if (log.Count == 1) log.Add("Abra o Virtual Desktop/SteamVR antes para dar prioridade a eles também.");
        return log;
    }

    public static async Task<string> DisableVrModeAsync()
    {
        if (AppSettings.Current.PlanBeforeVr is not { } previous) return "Nada para desfazer.";
        await PowerPlans.SetActiveAsync(previous);
        AppSettings.Current.PlanBeforeVr = null;
        AppSettings.Current.Save();
        return "✔ Plano de energia anterior restaurado.";
    }
}
