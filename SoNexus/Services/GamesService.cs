using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Requisitos de um jogo (vindos da loja Steam) guardados em cache.</summary>
public sealed class GameRequirements
{
    public int SteamId { get; set; }
    public int? RecGpu { get; set; }
    public int? MinGpu { get; set; }
    public int? RecCpu { get; set; }
    public int? RecRamGb { get; set; }
    public bool SsdRequired { get; set; }
    public bool Vr { get; set; }
    public bool VrOnly { get; set; }
    public string? Image { get; set; }
    public string RecText { get; set; } = "";
    public bool Light { get; set; }
    public DateTime Fetched { get; set; }
}

public sealed class Game : ObservableObject
{
    private string _estimate = "Calculando...";
    private string _verdict = "";
    private string _quality = "";
    private string _detail = "";
    private string? _image;
    private int _order = 9;

    public required string Name { get; init; }
    public required string Launcher { get; init; }
    public string? InstallPath { get; init; }
    public ulong SizeBytes { get; init; }
    public int? SteamId { get; init; }
    public string? LaunchUri { get; init; }
    public GameRequirements? Requirements { get; set; }

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : "";
    public string Info => string.Join(" · ", new[] { Launcher, SizeText }.Where(s => s.Length > 0));
    public string Estimate { get => _estimate; set => Set(ref _estimate, value); }
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string? Image { get => _image; set => Set(ref _image, value); }
    /// <summary>0 = mais pesado; usado para ordenar.</summary>
    public int Order { get => _order; set => Set(ref _order, value); }
    public bool IsVr => Requirements?.Vr == true;
    /// <summary>Estimativa bruta: quantos FPS a placa desenharia em 1080p e quantos o processador prepararia.</summary>
    public double GpuFps1080 { get; set; }
    public double CpuFps { get; set; }
}

public sealed record Launcher(string Name, string Status, int Games);

public sealed record PcProfile(
    string Gpu, int GpuScore, string Cpu, int CpuScore, ulong RamBytes, int RamSticks, int RamMts,
    int Width, int Height, int RefreshHz, int MaxRefreshHz, string GamesDiskType)
{
    public string Resolution => $"{Width}x{Height}";
    public double PixelFactor => Width * Height / (1920.0 * 1080);
}

/// <summary>GpuUse/CpuUse: quanto cada peça fica ocupada (%). Lost: quanto a peça mais forte desperdiça (%).</summary>
public sealed record Bottleneck(string Resolution, string Limit, string Text, double GpuUse, double CpuUse, double Lost)
{
    public string GpuUseText => $"Placa {GpuUse:0}%";
    public string CpuUseText => $"Processador {CpuUse:0}%";
}

/// <summary>Problema encontrado num jogo ou no PC que atrapalha jogar.</summary>
public sealed record GameProblem(string Title, string Detail, string Severity);

public sealed record PcTip(string Title, string Detail, string Impact, Func<Task<string?>>? Fix);

/// <summary>Biblioteca de jogos de todos os launchers e estimativa de desempenho para o seu PC.</summary>
public static partial class GamesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "games-cache.json");

    // ---------- Biblioteca ----------

    private static readonly string[] NotGames =
    {
        "Redistributable", "Steamworks", "SDK", "Dedicated Server", "Proton", "Steam Linux Runtime", "SteamVR", "Wallpaper Engine",
        "Soundtrack", "Benchmark", "Launcher", "Anti-Cheat", "EasyAntiCheat", "Uninstall", "Tool",
    };

    /// <summary>Jogos que o launcher diz estarem instalados, mas cuja pasta não existe mais (instalação quebrada).</summary>
    public static List<Game> BrokenInstalls { get; private set; } = new();

    public static List<Game> ScanLibrary()
    {
        var games = new List<Game>();
        // Cada launcher isolado: se um falhar (formato mudou, pasta sem permissão), os outros continuam.
        foreach (var (name, source) in new (string, Func<IEnumerable<Game>>)[]
                 {
                     ("Steam", Steam), ("Epic", Epic), ("GOG", Gog), ("Ubisoft", Ubisoft), ("EA/Battle.net/Riot", FromUninstall), ("Xbox", XboxGames),
                 })
        {
            try { games.AddRange(source().ToList()); }
            catch (Exception ex) { AppLog.Write($"Biblioteca: falha ao ler {name}: {ex.Message}"); }
        }
        var list = games
            .Where(g => g.Name.Length > 0 && !NotGames.Any(n => g.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(g => g.Name.ToLowerInvariant())
            .ToList();
        BrokenInstalls = list.Where(g => !string.IsNullOrWhiteSpace(g.InstallPath) && !Directory.Exists(g.InstallPath)).ToList();
        return list.Except(BrokenInstalls).OrderBy(g => g.Name).ToList();
    }

    public static string? SteamPath()
    {
        // HKCU da conta que está usando o PC; se a Steam foi instalada por outra conta, o caminho do HKLM.
        using (var k = Reg.Open(RegistryHive.CurrentUser, @"Software\Valve\Steam"))
            if ((k?.GetValue("SteamPath") as string)?.Replace('/', '\\') is { } p && Directory.Exists(p)) return p;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
            if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"(.+?)\"")]
    private static partial Regex VdfPath();

    private static string? Acf(string text, string key) =>
        Regex.Match(text, $"\"{key}\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    public static IEnumerable<string> SteamLibraries()
    {
        var steam = SteamPath();
        if (steam == null) yield break;
        yield return Path.Combine(steam, "steamapps");
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in VdfPath().Matches(TryRead(vdf) ?? ""))
        {
            var lib = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
            if (!lib.Equals(Path.Combine(steam, "steamapps"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(lib)) yield return lib;
        }
    }

    private static IEnumerable<Game> Steam()
    {
        foreach (var lib in SteamLibraries())
        {
            foreach (var acf in Directory.GetFiles(lib, "appmanifest_*.acf"))
            {
                var text = TryRead(acf);
                if (text == null || !int.TryParse(Acf(text, "appid"), out var id)) continue;
                var name = Acf(text, "name") ?? $"App {id}";
                ulong.TryParse(Acf(text, "SizeOnDisk"), out var size);
                yield return new Game
                {
                    Name = name, Launcher = "Steam", SteamId = id, SizeBytes = size,
                    InstallPath = Path.Combine(lib, "common", Acf(text, "installdir") ?? ""),
                    LaunchUri = $"steam://rungameid/{id}",
                };
            }
        }
    }

    private static string? TryRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch { return null; } // a Steam pode estar gravando o arquivo nesse momento
    }

    public static bool SteamAppInstalled(int appId) =>
        SteamLibraries().Any(l => File.Exists(Path.Combine(l, $"appmanifest_{appId}.acf")));

    private static IEnumerable<Game> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.item"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch { continue; } // um manifesto corrompido não derruba a lista
            using var _ = doc;
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array && !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
            var size = r.TryGetProperty("InstallSize", out var sz) && sz.TryGetUInt64(out var s) ? s : 0;
            yield return new Game
            {
                Name = S("DisplayName"), Launcher = "Epic Games", InstallPath = S("InstallLocation"), SizeBytes = size,
                LaunchUri = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=launch&silent=true",
            };
        }
    }

    private static IEnumerable<Game> Gog()
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root == null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("gameName") is not string name) continue;
                var exe = k.GetValue("exe") as string;
                yield return new Game { Name = name, Launcher = "GOG", InstallPath = k.GetValue("path") as string, LaunchUri = exe };
            }
        }
    }

    private static IEnumerable<Game> Ubisoft()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir) continue;
            yield return new Game
            {
                Name = Path.GetFileName(dir.TrimEnd('/', '\\')), Launcher = "Ubisoft Connect", InstallPath = dir,
                LaunchUri = $"uplay://launch/{id}/0",
            };
        }
    }

    // EA, Battle.net, Riot e Rockstar não têm arquivo de biblioteca simples: os jogos aparecem como programas instalados.
    private static readonly (string Publisher, string Launcher, string[] Skip)[] Publishers =
    {
        ("Electronic Arts", "EA app", new[] { "EA app", "Origin" }),
        ("Blizzard Entertainment", "Battle.net", new[] { "Battle.net" }),
        ("Riot Games", "Riot", new[] { "Riot Client", "Riot Vanguard" }),
        ("Rockstar Games", "Rockstar", new[] { "Rockstar Games Launcher", "Social Club" }),
    };

    private static IEnumerable<Game> FromUninstall()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = Reg.Open(hive, path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || k.GetValue("Publisher") is not string pub) continue;
                var match = Publishers.FirstOrDefault(p => pub.Contains(p.Publisher, StringComparison.OrdinalIgnoreCase));
                if (match.Launcher == null || match.Skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                var size = k.GetValue("EstimatedSize") is int kb ? (ulong)kb * 1024 : 0;
                yield return new Game { Name = name, Launcher = match.Launcher, InstallPath = k.GetValue("InstallLocation") as string, SizeBytes = size };
            }
        }
    }

    private static IEnumerable<Game> XboxGames()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            var dir = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(dir)) continue;
            foreach (var game in Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith("GameSave")))
                yield return new Game { Name = Path.GetFileName(game), Launcher = "Xbox / Game Pass", InstallPath = game };
        }
    }

    // ---------- Launchers ----------

    private static readonly (string Name, string[] Processes, string[] Uninstall, Func<bool>? Extra)[] Launchers =
    {
        ("Steam", new[] { "steam" }, Array.Empty<string>(), () => SteamPath() != null),
        ("Epic Games", new[] { "EpicGamesLauncher" }, new[] { "Epic Games Launcher" }, null),
        ("EA app", new[] { "EADesktop" }, new[] { "EA app" }, null),
        ("Ubisoft Connect", new[] { "upc", "UbisoftConnect" }, new[] { "Ubisoft Connect" }, null),
        ("GOG Galaxy", new[] { "GalaxyClient" }, new[] { "GOG GALAXY" }, null),
        ("Battle.net", new[] { "Battle.net" }, new[] { "Battle.net" }, null),
        ("Riot Client", new[] { "RiotClientServices" }, new[] { "Riot Client", "VALORANT", "League of Legends" }, null),
        ("Rockstar Games Launcher", new[] { "Launcher", "RockstarService" }, new[] { "Rockstar Games Launcher" }, null),
        ("Xbox", new[] { "XboxPcApp" }, Array.Empty<string>(),
            () => DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Fixed && Directory.Exists(Path.Combine(d.Name, "XboxGames")))),
        ("Amazon Games", new[] { "Amazon Games" }, new[] { "Amazon Games" }, null),
        ("itch", new[] { "itch" }, new[] { "itch" }, null),
    };

    public static List<Launcher> DetectLaunchers(List<Game> games)
    {
        var running = Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = HiddenItemsService.GetPrograms().Select(p => p.Name).ToList();
        var list = new List<Launcher>();
        foreach (var (name, processes, keys, extra) in Launchers)
        {
            var isInstalled = keys.Any(k => installed.Any(i => i.StartsWith(k, StringComparison.OrdinalIgnoreCase))) || (extra?.Invoke() ?? false);
            var isRunning = processes.Any(running.Contains) && (name != "Rockstar Games Launcher" || running.Contains("RockstarService"));
            if (!isInstalled && !isRunning) continue;
            var count = games.Count(g => g.Launcher.StartsWith(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
            list.Add(new Launcher(name, isRunning ? "Aberto" : "Instalado", count));
        }
        return list;
    }

    // ---------- Requisitos (loja Steam) ----------

    // Jogos fora da Steam: requisitos recomendados oficiais (placa, processador).
    private static readonly Dictionary<string, (string Gpu, string Cpu, bool Light)> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VALORANT"] = ("GTX 1050 Ti", "i3-4150", true),
        ["League of Legends"] = ("GTX 960", "i5-3300", true),
        ["Fortnite"] = ("GTX 960", "i5-7300", false),
        ["Minecraft"] = ("GTX 1060", "i5-4690", true),
        ["Minecraft Launcher"] = ("GTX 1060", "i5-4690", true),
        ["Roblox"] = ("GTX 750 Ti", "i5-4460", true),
        ["Genshin Impact"] = ("GTX 1060", "i7-4770", false),
        ["Honkai: Star Rail"] = ("GTX 1060", "i7-7700", false),
        ["Overwatch"] = ("GTX 1060", "i7-4770", true),
        ["Call of Duty"] = ("RTX 3060", "i7-8700K", false),
        ["World of Warcraft"] = ("GTX 1080", "i7-6700K", false),
    };

    private static Dictionary<string, GameRequirements>? _cache;

    private static Dictionary<string, GameRequirements> Cache()
    {
        if (_cache != null) return _cache;
        try { _cache = File.Exists(CachePath) ? JsonSerializer.Deserialize<Dictionary<string, GameRequirements>>(File.ReadAllText(CachePath)) : null; }
        catch { }
        return _cache ??= new();
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Cache()));
        }
        catch { }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    private static string Section(string text, string label)
    {
        // "Graphics: ... DirectX:" -> só o trecho da placa. Exige ":" para não pegar "64-bit processor and...".
        var m = Regex.Match(text, label + @"\s*:\s*(.+?)(?=(?:Memory|Graphics|DirectX|Storage|Sound Card|Additional Notes|Network|OS|Processor|VR Support)\s*:|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : "";
    }

    public static async Task<GameRequirements?> RequirementsAsync(Game game)
    {
        var key = game.SteamId?.ToString() ?? game.Name.ToLowerInvariant();
        lock (Cache())
            if (Cache().TryGetValue(key, out var cached) && (DateTime.Now - cached.Fetched).TotalDays < 30) return cached;

        GameRequirements? req = null;
        var known = KnownGames.FirstOrDefault(k => game.Name.StartsWith(k.Key, StringComparison.OrdinalIgnoreCase));
        if (known.Key != null)
        {
            req = new GameRequirements
            {
                RecGpu = PerformanceTables.GpuScore(known.Value.Gpu), RecCpu = PerformanceTables.CpuScore(known.Value.Cpu),
                Light = known.Value.Light, RecText = $"Placa recomendada: {known.Value.Gpu}", Fetched = DateTime.Now,
            };
        }
        else
        {
            if (!AppSettings.Current.AllowOnline) return null; // usuário desligou as consultas na internet
            var id = game.SteamId ?? await SteamSearchAsync(game.Name);
            if (id != null) req = await SteamDetailsAsync(id.Value);
        }
        if (req == null) return null;
        lock (Cache())
        {
            Cache()[key] = req;
            SaveCache();
        }
        return req;
    }

    private static async Task<int?> SteamSearchAsync(string name)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=BR";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var items = doc.RootElement.GetProperty("items");
            return items.GetArrayLength() > 0 ? items[0].GetProperty("id").GetInt32() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GameRequirements?> SteamDetailsAsync(int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l=english"));
            var root = doc.RootElement.GetProperty(id.ToString());
            if (!root.GetProperty("success").GetBoolean()) return null;
            var data = root.GetProperty("data");
            string Req(string which)
            {
                if (!data.TryGetProperty("pc_requirements", out var pc) || pc.ValueKind != JsonValueKind.Object) return "";
                return pc.TryGetProperty(which, out var v) ? Tags().Replace(v.GetString() ?? "", " ") : "";
            }
            var rec = Req("recommended");
            var min = Req("minimum");
            var recGpu = PerformanceTables.FindGpus(Section(rec, "Graphics"));
            var minGpu = PerformanceTables.FindGpus(Section(min, "Graphics"));
            var recCpu = PerformanceTables.FindCpus(Section(rec.Length > 0 ? rec : min, "Processor"));
            var ram = Regex.Match(Section(rec.Length > 0 ? rec : min, "Memory"), @"(\d+)\s*GB", RegexOptions.IgnoreCase);
            var categories = data.TryGetProperty("categories", out var cats)
                ? cats.EnumerateArray().Select(c => c.GetProperty("description").GetString() ?? "").ToList()
                : new List<string>();
            return new GameRequirements
            {
                SteamId = id,
                RecGpu = recGpu.Count > 0 ? (int)recGpu.Average() : null,
                MinGpu = minGpu.Count > 0 ? (int)minGpu.Average() : null,
                RecCpu = recCpu.Count > 0 ? (int)recCpu.Average() : null,
                RecRamGb = ram.Success ? int.Parse(ram.Groups[1].Value) : null,
                SsdRequired = (rec + min).Contains("SSD", StringComparison.OrdinalIgnoreCase),
                Vr = categories.Any(c => c.Contains("VR", StringComparison.OrdinalIgnoreCase)),
                VrOnly = categories.Any(c => c.Contains("VR Only", StringComparison.OrdinalIgnoreCase)),
                Image = data.TryGetProperty("header_image", out var img) ? img.GetString() : null,
                RecText = Section(rec.Length > 0 ? rec : min, "Graphics").Trim(),
                Light = recGpu.Count == 0 && minGpu.Count == 0,
                Fetched = DateTime.Now,
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------- Seu PC ----------

    public static PcProfile Profile()
    {
        var gpus = DriverService.GetGpuDrivers();
        var main = gpus.FirstOrDefault(g => g.Vendor == "NVIDIA") ?? gpus.FirstOrDefault(g => g.Vendor == "AMD" && !g.Name.Contains("(TM) Graphics"))
                   ?? gpus.FirstOrDefault(g => g.Vendor is "AMD" or "Intel") ?? gpus.FirstOrDefault();
        var gpuName = main?.Name ?? "Placa de vídeo";
        var cpuName = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject o in s.Get()) using (o) cpuName = o["Name"]?.ToString()?.Trim() ?? "";
        }
        catch { }

        // Monitor principal direto do Windows (funciona em notebook com duas placas, onde a dedicada não "tem" tela).
        var (w, h, hz, maxHz) = PrimaryDisplay();

        var layout = MemoryInfoService.Layout();
        var ram = (ulong)layout.Modules.Sum(m => (long)m.Capacity);
        if (ram == 0) ram = MemoryService.GetStatus().Total; // máquina virtual / BIOS sem a tabela dos pentes
        var steamDisk = StorageTypeFor(SteamLibraries().FirstOrDefault());

        // Peça fora da tabela: valor conservador pela marca, em vez de fingir que é uma placa média.
        var gpuScore = PerformanceTables.GpuScore(gpuName) ?? (main?.Vendor switch { "NVIDIA" => 90, "AMD" => 70, "Intel" => 20, _ => 40 });
        var cpuScore = PerformanceTables.CpuScore(cpuName) ?? Math.Clamp(Environment.ProcessorCount * 9, 30, 120);
        return new PcProfile(gpuName, gpuScore, cpuName, cpuScore,
            ram, Math.Max(1, layout.Modules.Count), (int)layout.Speed, w, h, hz, maxHz, steamDisk);
    }

    public static int PrimaryRefreshHz() => PrimaryDisplay().Hz;

    private static (int W, int H, int Hz, int MaxHz) PrimaryDisplay()
    {
        var mode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) || mode.dmPelsWidth <= 0) return (1920, 1080, 60, 60);
        int w = mode.dmPelsWidth, h = mode.dmPelsHeight, hz = Math.Max(1, mode.dmDisplayFrequency), max = hz;
        var other = new DEVMODE { dmSize = mode.dmSize };
        for (var i = 0; EnumDisplaySettings(null, i, ref other); i++)
            if (other.dmPelsWidth == w && other.dmPelsHeight == h) max = Math.Max(max, other.dmDisplayFrequency);
        return (w, h, hz, max);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);

    // ---------- Problemas ----------

    /// <summary>
    /// O que pode estar fazendo jogos fecharem, não abrirem ou travarem: erros registrados pelo Windows,
    /// bibliotecas que faltam, instalação quebrada, disco cheio.
    /// </summary>
    public static List<GameProblem> Problems(List<Game> games)
    {
        var list = new List<GameProblem>();

        // 1) Jogos que fecharam com erro nos últimos 30 dias (Visualizador de Eventos › Aplicativo, evento 1000).
        try
        {
            var crashes = new Dictionary<string, (int Count, DateTime Last, string Module)>(StringComparer.OrdinalIgnoreCase);
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    var props = e.Properties;
                    if (props.Count < 11) continue;
                    var exePath = props[10].Value?.ToString() ?? "";
                    var module = props[3].Value?.ToString() ?? "";
                    var game = games.FirstOrDefault(g => !string.IsNullOrEmpty(g.InstallPath) &&
                                                         exePath.StartsWith(g.InstallPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                    if (game == null) continue;
                    var when = e.TimeCreated ?? DateTime.MinValue;
                    crashes[game.Name] = crashes.TryGetValue(game.Name, out var c)
                        ? (c.Count + 1, when > c.Last ? when : c.Last, module)
                        : (1, when, module);
                }
            }
            foreach (var (name, c) in crashes.OrderByDescending(x => x.Value.Count))
                list.Add(new GameProblem($"{name} fechou sozinho {c.Count} vez(es)",
                    $"Último em {c.Last:dd/MM HH:mm}, erro em {c.Module}. " + CrashHint(c.Module), c.Count >= 3 ? "Alto" : "Médio"));
        }
        catch (Exception ex) { AppLog.Write($"Problemas em jogos: log de eventos: {ex.Message}"); }

        // 2) Bibliotecas que quase todo jogo usa.
        bool VcRuntime(string arch)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{(arch == "x86" ? "WOW6432Node\\" : "")}Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}");
            return k?.GetValue("Installed") is int i && i == 1;
        }
        foreach (var arch in new[] { "x64", "x86" })
            if (!VcRuntime(arch))
                list.Add(new GameProblem($"Falta o Visual C++ 2015-2022 ({arch})",
                    "Sem ele, muitos jogos não abrem (erro de VCRUNTIME140.dll / MSVCP140.dll). Instale pela Microsoft: aka.ms/vs/17/release/vc_redist." + arch + ".exe", "Alto"));
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "d3dx9_43.dll")))
            list.Add(new GameProblem("DirectX antigo (9/10/11 extras) não instalado",
                "Jogos mais velhos pedem d3dx9_43.dll / XINPUT1_3.dll. Instale o \"DirectX End-User Runtime\" da Microsoft.", "Baixo"));

        // 3) Instalações quebradas e discos cheios.
        foreach (var g in BrokenInstalls)
            list.Add(new GameProblem($"{g.Name}: instalação quebrada",
                $"O {g.Launcher} acha que o jogo está em \"{g.InstallPath}\", mas a pasta não existe. Verifique os arquivos ou reinstale pelo launcher.", "Médio"));
        foreach (var drive in games.Select(g => Path.GetPathRoot(g.InstallPath ?? "")).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var d = new DriveInfo(drive!);
                if (d.IsReady && d.AvailableFreeSpace < Math.Max(20L << 30, d.TotalSize / 10))
                    list.Add(new GameProblem($"Disco {d.Name.TrimEnd('\\')} quase cheio",
                        $"Só {Format.Bytes((ulong)d.AvailableFreeSpace)} livres. Atualizações de jogos falham e o Windows fica sem espaço para memória virtual.", "Médio"));
            }
            catch { }
        }
        return list;
    }

    private static string CrashHint(string module) => module.ToLowerInvariant() switch
    {
        var m when m.StartsWith("nvwgf") || m.StartsWith("nvlddmkm") || m.StartsWith("nvoglv") => "Erro no driver da NVIDIA: reinstale o driver (Drivers › Placa de vídeo) e tire overclock da placa.",
        var m when m.StartsWith("atidxx") || m.StartsWith("amdxx") || m.StartsWith("atiumd") => "Erro no driver da AMD: reinstale o driver e tire overclock/undervolt da placa.",
        var m when m.StartsWith("igd") || m.StartsWith("igc") => "Erro no driver de vídeo da Intel: atualize o driver.",
        "ntdll.dll" or "kernelbase.dll" => "Erro genérico do jogo: verifique os arquivos do jogo no launcher e teste a RAM (Diagnóstico › Teste de RAM).",
        var m when m.StartsWith("ucrtbase") || m.StartsWith("vcruntime") || m.StartsWith("msvcp") => "Biblioteca Visual C++ com problema: reinstale o Visual C++ 2015-2022.",
        var m when m.StartsWith("d3d") || m.StartsWith("dxgi") => "Erro de DirectX: atualize o driver de vídeo e o Windows.",
        _ => "Verifique a integridade dos arquivos no launcher.",
    };

    private static string StorageTypeFor(string? path)
    {
        if (path == null) return "";
        try
        {
            var letter = Path.GetPathRoot(path)?.TrimEnd('\\', ':') ?? "";
            var (_, output, _) = ProcessRunner.PowerShellAsync(
                $"(Get-Partition -DriveLetter {letter} | Get-Disk | Get-PhysicalDisk | Select-Object -First 1).MediaType").GetAwaiter().GetResult();
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    // ---------- Estimativa ----------

    /// <summary>
    /// Estimativa: os "requisitos recomendados" miram ~60 FPS em 1080p no Alto.
    /// A diferença entre a sua placa e a recomendada escala esse número; o processador limita o máximo.
    /// </summary>
    public static void Estimate(Game game, PcProfile pc)
    {
        var r = game.Requirements;
        if (r == null)
        {
            game.Estimate = "Sem dados";
            game.Verdict = "";
            game.Detail = "Não achamos os requisitos deste jogo na loja Steam.";
            game.Order = 8;
            return;
        }
        double fpsGpu;
        if (r.RecGpu is { } rec) fpsGpu = 60 * Math.Pow((double)pc.GpuScore / rec, 0.9);
        // Muitos jogos só publicam o "mínimo", que na prática costuma rodar ~45 FPS no Médio.
        else if (r.MinGpu is { } min) fpsGpu = 45 * Math.Pow((double)pc.GpuScore / min, 0.9) / 1.3;
        else fpsGpu = 60 * Math.Pow(pc.GpuScore / 60.0, 0.9); // jogo leve (requisito sem placa específica)

        // Jogo só de VR: os requisitos já são para os óculos (mínimo ≈ 80 FPS, recomendado ≈ 90 FPS em VR).
        if (r.VrOnly)
        {
            var baseFps = r.RecGpu != null ? 90 : 80;
            var req = r.RecGpu ?? r.MinGpu ?? 100;
            var vr = Math.Min(baseFps * Math.Pow((double)pc.GpuScore / req, 0.9), 144);
            game.Estimate = $"~{vr:0} FPS em VR";
            game.Quality = vr >= 120 ? "Ultra em VR" : vr >= 90 ? "Alto em VR" : vr >= 72 ? "Médio em VR" : "Baixo em VR";
            (game.Verdict, game.Order) = vr >= 90 ? ("Roda liso em VR", 4) : vr >= 72 ? ("Roda bem em VR", 3) : ("Pesado em VR", 1);
            game.Detail = "Jogo só de VR (Virtual Desktop/SteamVR)." + (r.RecText.Length > 0 ? $" Placa: {Short(r.RecText)}" : "");
            return;
        }

        // Processador: jogos competitivos/leves dependem mais dele.
        var cpuRec = r.RecCpu ?? (r.Light ? 55 : 80);
        var fpsCpu = (r.Light ? 140 : 75) * Math.Pow((double)pc.CpuScore / cpuRec, 1.1);
        if (pc.RamSticks == 1) fpsCpu *= 0.88; // canal único tira ~10-15% em jogos limitados pelo processador

        game.GpuFps1080 = fpsGpu;
        game.CpuFps = fpsCpu;
        var native = fpsGpu / Math.Pow(pc.PixelFactor, 0.85);
        var fps = Math.Min(native, fpsCpu);
        fps = Math.Min(fps, r.Light ? 600 : 400);
        var cpuLimited = fpsCpu < native;

        string quality;
        if (fps >= 120) quality = "Ultra";
        else if (fps >= 75) quality = "Alto";
        else if (fps >= 50) quality = "Médio";
        else if (fps >= 30) quality = "Baixo";
        else quality = "Mínimo";

        // Com qualidade mais baixa, o FPS sobe (Médio ~+30%, Baixo ~+60%).
        var shown = quality switch { "Médio" => fps, "Baixo" => fps * 1.3, "Mínimo" => fps * 1.6, _ => fps };
        game.Estimate = shown >= 300 ? "300+ FPS" : $"~{shown:0} FPS";
        game.Quality = $"{quality} · {pc.Resolution}";
        (game.Verdict, game.Order) = fps switch
        {
            >= 90 => ("Roda liso", 4),
            >= 60 => ("Roda bem", 3),
            >= 40 => ("Jogável", 2),
            >= 25 => ("Pesado", 1),
            _ => ("Muito pesado", 0),
        };
        var notes = new List<string>();
        if (cpuLimited && fps < 200) notes.Add("limitado pelo processador");
        if (r.RecRamGb is { } ram && pc.RamBytes / (1L << 30) + 1 < (ulong)ram) notes.Add($"pede {ram} GB de RAM");
        if (r.SsdRequired && pc.GamesDiskType.Contains("HDD", StringComparison.OrdinalIgnoreCase)) notes.Add("exige SSD");
        if (r.VrOnly) notes.Add("só em VR");
        game.Detail = (r.RecText.Length > 0 ? $"Recomendado: {Short(r.RecText)}" : "Jogo leve") + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");
    }

    private static string Short(string s) => s.Length > 80 ? s[..80] + "..." : s;

    /// <summary>
    /// Quem segura o FPS em cada resolução. Usa o mesmo modelo da estimativa dos jogos, num jogo pesado típico
    /// (requisito recomendado = GTX 1060 + Ryzen 5 2600): calcula quantos FPS a placa conseguiria desenhar e quantos
    /// o processador conseguiria preparar. O menor dos dois é o que você vê; o outro fica esperando.
    /// </summary>
    public static List<Bottleneck> Bottlenecks(PcProfile pc)
    {
        var list = new List<Bottleneck>();
        var cpuFps = 75 * Math.Pow(pc.CpuScore / 80.0, 1.1) * (pc.RamSticks == 1 ? 0.88 : 1);
        foreach (var (res, pixels) in new[] { ("1080p", 1.0), ("1440p", 1.78), ("4K", 4.0) })
        {
            var gpuFps = 60 * Math.Pow(pc.GpuScore / 100.0, 0.9) / Math.Pow(pixels, 0.85);
            var fps = Math.Min(gpuFps, cpuFps);
            var gpuUse = Math.Min(99, fps / gpuFps * 99);
            var cpuUse = Math.Min(100, fps / cpuFps * 100);
            var lost = (1 - Math.Min(gpuFps, cpuFps) / Math.Max(gpuFps, cpuFps)) * 100;
            var numbers = $"Num jogo pesado: a placa desenharia ~{gpuFps:0} FPS e o processador prepararia ~{cpuFps:0} FPS; você vê ~{fps:0}.";

            string limit, advice;
            if (cpuFps < gpuFps * 0.85)
            {
                limit = "Processador";
                advice = $"A placa de vídeo fica ~{100 - gpuUse:0}% parada esperando o processador. Subir gráficos/resolução quase não tira FPS; " +
                         (pc.RamSticks == 1 ? "colocar o 2º pente de RAM (dual channel) ajuda; " : "") + "trocar o processador dá o maior ganho.";
            }
            else if (gpuFps < cpuFps * 0.85)
            {
                limit = "Placa de vídeo";
                advice = "Este é o cenário normal e saudável: a placa trabalha a 99%. Para mais FPS, baixe sombras/efeitos ou ative DLSS/FSR.";
            }
            else
            {
                limit = "Equilibrado";
                advice = "Processador e placa estão no mesmo nível: nenhum dos dois desperdiça o outro.";
            }
            list.Add(new Bottleneck(res, limit, $"{numbers} {advice}", gpuUse, cpuUse, lost));
        }
        return list;
    }

    public static List<PcTip> Tips(PcProfile pc)
    {
        var tips = new List<PcTip>();
        if (pc.RamSticks == 1)
            tips.Add(new PcTip("Memória em canal único", "Com 1 pente só, a RAM entrega metade da banda. Em jogos isso tira 10-20% de FPS, principalmente no 1% low (engasgos). Use 2 pentes iguais.", "Alto", null));
        if (pc.RamBytes < 16UL << 30)
            tips.Add(new PcTip("Pouca memória", $"{Format.Bytes(pc.RamBytes)} de RAM. Jogos atuais pedem 16 GB; com menos, aparecem travadinhas.", "Alto", null));
        if (pc.MaxRefreshHz > pc.RefreshHz)
            tips.Add(new PcTip("Monitor abaixo da taxa máxima", $"O monitor está em {pc.RefreshHz} Hz, mas aceita {pc.MaxRefreshHz} Hz. Ajuste em Configurações > Tela > Avançado.", "Alto",
                () => { IntegrationService.Open("ms-settings:display-advancedgraphics"); return Task.FromResult<string?>(null); }));
        if (pc.GamesDiskType.Contains("HDD", StringComparison.OrdinalIgnoreCase))
            tips.Add(new PcTip("Jogos em HD", "A biblioteca principal está num HD. Jogos novos carregam devagar e travam ao carregar áreas; mova para um SSD.", "Médio", null));

        foreach (var (title, find, impact) in new[]
                 {
                     ("Gravação em segundo plano ligada", "Desativar gravação em segundo plano (Game DVR)", "Médio"),
                     ("Modo de Jogo desligado", "Ativar Modo de Jogo", "Baixo"),
                     ("Agendamento de GPU por hardware desligado", "Agendamento de GPU acelerado por hardware", "Baixo"),
                     ("Prioridade de jogos padrão", "Prioridade alta para jogos", "Baixo"),
                 })
        {
            var tweak = DebloatService.Tweaks.FirstOrDefault(t => t.Title == find);
            if (tweak == null || !tweak.IsAvailable || tweak.IsApplied) continue;
            tips.Add(new PcTip(title, tweak.Description, impact, () =>
            {
                tweak.Apply();
                return Task.FromResult<string?>(tweak.Restart == Restart.Windows ? "Aplicado. Reinicie o PC para valer." : "Aplicado.");
            }));
        }
        return tips;
    }
}
