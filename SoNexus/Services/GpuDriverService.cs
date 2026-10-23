using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SoNexus.Services;

public sealed record GpuDriverStatus(
    string Vendor, string Gpu, string Installed, string? Latest, string? DownloadUrl, string? Size,
    bool UpToDate, string Note);

public sealed record DownloadProgress(double Percent, double MBps, string Text);

/// <summary>Descobre o driver mais recente da NVIDIA/AMD/Intel nos sites oficiais e instala com um clique.</summary>
public static partial class GpuDriverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return c;
    }

    // Pasta protegida: o instalador roda como administrador, ninguém sem admin pode trocá-lo aqui.
    public static string DownloadFolder => SecureStore.Folder("Drivers");

    public static async Task<List<GpuDriverStatus>> CheckAllAsync()
    {
        var list = new List<GpuDriverStatus>();
        // Só placas de verdade: monitores virtuais (Virtual Desktop, Parsec...) não têm driver de jogos.
        foreach (var g in DriverService.GetGpuDrivers().Where(g => g.Vendor is "NVIDIA" or "AMD" or "Intel"))
        {
            try
            {
                list.Add(g.Vendor switch
                {
                    "NVIDIA" => await NvidiaAsync(g),
                    "AMD" => await AmdAsync(g),
                    "Intel" => new GpuDriverStatus("Intel", g.Name, g.Version, null, null, null, false,
                        "A Intel atualiza pelo Assistente de Driver e Suporte oficial. O botão instala o assistente e ele cuida do resto."),
                    _ => new GpuDriverStatus(g.Vendor, g.Name, g.Version, null, null, null, true, "Dispositivo virtual ou de outro fabricante: não precisa de driver de jogos."),
                });
            }
            catch (Exception ex)
            {
                list.Add(new GpuDriverStatus(g.Vendor, g.Name, g.Version, null, null, null, false, $"Não foi possível consultar o site oficial: {ex.Message}"));
            }
        }
        return list;
    }

    // ---------- NVIDIA ----------

    /// <summary>"32.0.16.2101" (versão do Windows) -> "621.01" (versão da NVIDIA).</summary>
    public static string NvidiaVersion(string windowsVersion)
    {
        var digits = string.Concat(windowsVersion.Split('.').TakeLast(2)).Replace(".", "");
        if (digits.Length < 5) return windowsVersion;
        var v = digits[^5..];
        return $"{v[..3]}.{v[3..]}";
    }

    private static async Task<GpuDriverStatus> NvidiaAsync(GpuDriver g)
    {
        var installed = NvidiaVersion(g.Version);
        if (g.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase))
            return new GpuDriverStatus("NVIDIA", g.Name, installed, null, null, null, false,
                "Placas de notebook usam um pacote próprio. Use a página oficial ou o NVIDIA App.");
        // Busca o driver Game Ready DCH mais recente (o mesmo pacote serve para todas as GeForce de mesa).
        var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
                  "&psid=131&pfid=1066&osID=57&languageCode=1046&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        var latest = info.GetProperty("Version").GetString() ?? "";
        var download = info.GetProperty("DownloadURL").GetString();
        var size = info.GetProperty("DownloadURLFileSize").GetString();
        var upToDate = CompareVersions(installed, latest) >= 0;
        return new GpuDriverStatus("NVIDIA", g.Name, installed, latest, download, size, upToDate,
            upToDate ? "Você já tem o driver Game Ready mais recente." : "Há um driver Game Ready mais novo.");
    }

    // ---------- AMD ----------

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/installer/[^""'\s]+minimalsetup[^""'\s]+\.exe")]
    private static partial Regex AmdInstallerRegex();

    [GeneratedRegex(@"adrenalin-edition-(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex AmdVersionRegex();

    private static async Task<GpuDriverStatus> AmdAsync(GpuDriver g)
    {
        var installed = AmdInstalledVersion() ?? g.Version;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.amd.com/en/support/download/drivers.html");
        request.Headers.Referrer = new Uri("https://www.amd.com/");
        var html = await (await Http.SendAsync(request)).Content.ReadAsStringAsync();
        var link = AmdInstallerRegex().Match(html).Value;
        if (link.Length == 0)
            return new GpuDriverStatus("AMD", g.Name, installed, null, null, null, false, "O site da AMD não respondeu como esperado. Use o botão para abrir a página oficial.");
        var m = AmdVersionRegex().Match(link);
        var latest = m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : "mais recente";
        var upToDate = m.Success && installed.StartsWith("20") && CompareAmd(installed, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) >= 0;
        return new GpuDriverStatus("AMD", g.Name, installed, latest, link, null, upToDate,
            upToDate ? "Você já tem o AMD Software mais recente." : "O instalador oficial da AMD detecta a placa e baixa o driver certo (inclui o vídeo integrado do Ryzen).");
    }

    private static string? AmdInstalledVersion()
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string n && n.StartsWith("AMD Software", StringComparison.OrdinalIgnoreCase))
                    return k.GetValue("DisplayVersion") as string;
            }
        }
        return null;
    }

    /// <summary>AMD Software "2026.04.15" contra Adrenalin 26.10: compara ano e mês.</summary>
    private static int CompareAmd(string installed, int yy, int month)
    {
        var p = installed.Split('.');
        if (p.Length < 2 || !int.TryParse(p[0], out var y) || !int.TryParse(p[1], out var mth)) return -1;
        return (y % 100, mth).CompareTo((yy, month));
    }

    private static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    // ---------- Download e instalação ----------

    /// <summary>Só baixa de servidores oficiais por HTTPS (o endereço da NVIDIA vem de uma resposta da internet).</summary>
    private static bool IsOfficial(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("drivers.amd.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Quem pode ter assinado cada instalador.</summary>
    private static string[] Signers(string file) =>
        file.Contains("amd", StringComparison.OrdinalIgnoreCase) || file.Contains("adrenalin", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Advanced Micro Devices" }
            : new[] { "NVIDIA Corporation" };

    /// <summary>Confere a assinatura antes de executar como administrador; arquivo adulterado é apagado.</summary>
    private static void EnsureSigned(string file)
    {
        if (Authenticode.Verify(file, Signers(file)) is not { } problem) return;
        try { File.Delete(file); } catch { }
        AppLog.Write($"Driver recusado ({file}): {problem}");
        throw new InvalidOperationException($"O instalador baixado não passou na verificação de segurança ({problem}) e foi apagado. Tente de novo ou baixe pelo site oficial.");
    }

    public static async Task<string> DownloadAsync(string url, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (!IsOfficial(url)) throw new InvalidOperationException("Endereço de download fora dos sites oficiais da NVIDIA/AMD. Baixe pelo site oficial.");
        Directory.CreateDirectory(DownloadFolder);
        var file = Path.Combine(DownloadFolder, Path.GetFileName(new Uri(url).LocalPath));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Contains("amd.com")) request.Headers.Referrer = new Uri("https://www.amd.com/");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        if (File.Exists(file) && total > 0 && new FileInfo(file).Length == total)
        {
            progress.Report(new DownloadProgress(100, 0, "Já baixado antes."));
            return file;
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(file);
        var buffer = new byte[1 << 20];
        long received = 0;
        var sw = Stopwatch.StartNew();
        var last = 0L;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            if (sw.ElapsedMilliseconds - last > 300)
            {
                last = sw.ElapsedMilliseconds;
                var mbps = received / 1e6 / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                progress.Report(new DownloadProgress(total > 0 ? 100.0 * received / total : 0, mbps,
                    $"Baixando {received / 1e6:0} de {total / 1e6:0} MB · {mbps:0.0} MB/s"));
            }
        }
        progress.Report(new DownloadProgress(100, 0, "Download concluído."));
        return file;
    }

    /// <summary>NVIDIA: instalação silenciosa (sem janelas), opcionalmente "limpa" (zera as configurações do painel).</summary>
    public static async Task<int> InstallNvidiaAsync(string file, bool clean)
    {
        EnsureSigned(file);
        var args = "-s -noreboot" + (clean ? " -clean" : "");
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>AMD: abre o instalador oficial (ele mostra a própria janela e detecta a placa).</summary>
    public static void RunInteractive(string file)
    {
        EnsureSigned(file);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>Intel: instala o Assistente de Driver e Suporte oficial pelo winget.</summary>
    public static async Task<int> InstallIntelAssistantAsync()
    {
        var (code, _, _) = await ProcessRunner.RunAsync("winget.exe",
            "install --id Intel.IntelDriverAndSupportAssistant -e --silent --accept-package-agreements --accept-source-agreements");
        return code;
    }
}
