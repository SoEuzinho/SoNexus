using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SoNexus.Services;

public sealed record DiagEvent(DateTime Time, string Title, string Detail, string Source)
{
    public string TimeText => Time.ToString("dd/MM/yyyy HH:mm");
}

public sealed record GroupedEvent(string Name, int Count, DateTime Last, string Detail)
{
    public string LastText => Last.ToString("dd/MM/yyyy HH:mm");
}

public sealed record DiagReport(
    List<DiagEvent> Crashes, List<DiagEvent> Hardware, List<GroupedEvent> AppCrashes,
    List<GroupedEvent> SystemErrors, List<ProblemDevice> Devices,
    int Bluescreens, int Shutdowns, int Whea, int GpuResets, int DiskErrors);

/// <summary>Lê os registros de eventos do Windows e traduz os problemas para linguagem simples.</summary>
public static partial class DiagnosticsService
{
    public static DiagReport Analyze(int days)
    {
        var since = $"TimeCreated[timediff(@SystemTime) <= {(long)days * 86_400_000}]";
        var crashes = new List<DiagEvent>();
        var hardware = new List<DiagEvent>();

        // Telas azuis
        var dumps = MiniDumps();
        var bluescreens = 0;
        foreach (var e in Query("System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and {since}]]"))
        {
            bluescreens++;
            var code = ParseBugcheck(Prop(e, 0));
            var (name, help) = Bugcheck(code);
            var time = e.TimeCreated ?? DateTime.MinValue;
            var dump = dumps.FirstOrDefault(d => Math.Abs((d.LastWriteTime - time).TotalMinutes) < 30);
            crashes.Add(new DiagEvent(time, $"Tela azul: {name} (0x{code:X})",
                help + (dump != null ? $" Arquivo: {dump.FullName}" : ""), "Tela azul"));
        }

        // Desligamentos inesperados (Kernel-Power 41)
        var shutdowns = 0;
        foreach (var e in Query("System", $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {since}]]"))
        {
            shutdowns++;
            var bug = 0L;
            try { bug = Convert.ToInt64(e.Properties[0].Value); } catch { }
            crashes.Add(new DiagEvent(e.TimeCreated ?? DateTime.MinValue, "Desligamento inesperado",
                bug == 0
                    ? "O PC desligou sem tela azul: queda de energia, fonte fraca, botão reset ou travamento total (comum com overclock instável ou superaquecimento)."
                    : $"Reiniciou por causa de uma tela azul (código 0x{bug:X}).",
                "Energia"));
        }

        // Erros de hardware WHEA
        var whea = 0;
        foreach (var e in Query("System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and {since}]]"))
        {
            whea++;
            var fatal = e.Level is 1 or 2;
            hardware.Add(new DiagEvent(e.TimeCreated ?? DateTime.MinValue,
                fatal ? "Erro de hardware grave (WHEA)" : "Erro de hardware corrigido (WHEA)",
                FirstLine(Describe(e)) + " Costuma indicar overclock/undervolt instável, RAM com XMP agressivo ou peça com defeito.",
                "Hardware"));
        }

        // Driver de vídeo travou e foi recuperado (TDR)
        var gpu = 0;
        foreach (var e in Query("System", $"*[System[((Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2)) or (Provider[@Name='amdkmdag'] and (Level=1 or Level=2))) and {since}]]"))
        {
            gpu++;
            hardware.Add(new DiagEvent(e.TimeCreated ?? DateTime.MinValue, "Driver de vídeo parou de responder",
                "A tela piscou/travou e o Windows reiniciou o driver. Causas comuns: driver com defeito, overclock da GPU ou fonte fraca.",
                "Vídeo"));
        }

        // Erros de disco
        var disk = 0;
        foreach (var e in Query("System", $"*[System[(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='stornvme'] or Provider[@Name='storahci']) and (Level=1 or Level=2 or Level=3) and {since}]]"))
        {
            disk++;
            hardware.Add(new DiagEvent(e.TimeCreated ?? DateTime.MinValue, $"Problema de disco ({e.ProviderName} {e.Id})",
                FirstLine(Describe(e)) + " Faça backup e verifique a saúde do disco.", "Disco"));
        }

        // Programas que fecharam sozinhos ou travaram
        var apps = new Dictionary<string, (int Count, DateTime Last, string Module)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Query("Application", $"*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)) and {since}]]"))
        {
            var app = Prop(e, 0);
            if (app.Length == 0) continue;
            var module = e.Id == 1000 ? Prop(e, 3) : "travou (não respondeu)";
            var time = e.TimeCreated ?? DateTime.MinValue;
            apps[app] = apps.TryGetValue(app, out var a)
                ? (a.Count + 1, a.Last > time ? a.Last : time, a.Module)
                : (1, time, module);
        }
        var appCrashes = apps
            .Select(a => new GroupedEvent(a.Key, a.Value.Count, a.Value.Last, a.Value.Module.Length > 0 ? $"Falha em: {a.Value.Module}" : ""))
            .OrderByDescending(a => a.Count).ToList();

        // Erros gerais do sistema, agrupados
        var errors = new Dictionary<string, (int Count, DateTime Last, string Message)>();
        foreach (var e in Query("System", $"*[System[(Level=1 or Level=2) and {since}]]").Take(3000))
        {
            var key = $"{e.ProviderName} · {e.Id}";
            var time = e.TimeCreated ?? DateTime.MinValue;
            errors[key] = errors.TryGetValue(key, out var x)
                ? (x.Count + 1, x.Last, x.Message)
                : (1, time, FirstLine(Describe(e)));
        }
        var systemErrors = errors
            .Select(x => new GroupedEvent(x.Key, x.Value.Count, x.Value.Last, x.Value.Message))
            .OrderByDescending(x => x.Count).ToList();

        return new DiagReport(
            crashes.OrderByDescending(c => c.Time).ToList(),
            hardware.OrderByDescending(h => h.Time).ToList(),
            appCrashes, systemErrors, DriverService.GetProblemDevices(),
            bluescreens, shutdowns, whea, gpu, disk);
    }

    // ---------- Ferramentas de reparo ----------

    /// <summary>Roda sfc/DISM/chkdsk mostrando a saída. Linhas terminadas em \r são progresso.</summary>
    public static async Task<int> RunToolAsync(string exe, string args, Action<string, bool> output)
    {
        // Cada ferramenta escreve numa codificação: sfc em UTF-16, chkdsk em ANSI, DISM em OEM.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var culture = CultureInfo.CurrentCulture.TextInfo;
        var encoding = Path.GetFileName(exe).ToLowerInvariant() switch
        {
            "sfc.exe" => Encoding.Unicode,
            "chkdsk.exe" => Encoding.GetEncoding(culture.ANSICodePage),
            _ => Encoding.GetEncoding(culture.OEMCodePage),
        };

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        using var p = Process.Start(psi)!;
        var buffer = new char[512];
        var line = new StringBuilder();
        int read;
        while ((read = await p.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    var text = line.ToString().Replace("\0", "").Trim();
                    if (text.Length > 0) output(text, c == '\r');
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (line.ToString().Replace("\0", "").Trim() is { Length: > 0 } last) output(last, false);
        var err = await p.StandardError.ReadToEndAsync();
        if (err.Trim().Length > 0) output(err.Trim(), false);
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // ---------- Auxiliares ----------

    private static IEnumerable<EventRecord> Query(string log, string xpath)
    {
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
        }
        catch
        {
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch { yield break; }
                if (record == null) yield break;
                using (record) yield return record;
            }
        }
    }

    private static string Prop(EventRecord e, int index)
    {
        try { return e.Properties.Count > index ? e.Properties[index].Value?.ToString()?.Trim() ?? "" : ""; }
        catch { return ""; }
    }

    private static string Describe(EventRecord e)
    {
        try { return e.FormatDescription() ?? ""; }
        catch { return ""; }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length > 220 ? line[..220] + "..." : line;
    }

    private static List<FileInfo> MiniDumps()
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"));
            return dir.Exists ? dir.GetFiles("*.dmp").ToList() : new();
        }
        catch
        {
            return new();
        }
    }

    [GeneratedRegex("0x([0-9a-fA-F]+)")]
    private static partial Regex HexRegex();

    private static uint ParseBugcheck(string text)
    {
        var m = HexRegex().Match(text);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private static (string Name, string Help) Bugcheck(uint code) => code switch
    {
        0x0A => ("IRQL_NOT_LESS_OR_EQUAL", "Um driver acessou memória inválida. Atualize drivers; se continuar, teste a RAM."),
        0x19 => ("BAD_POOL_HEADER", "Memória do kernel corrompida, geralmente por driver. Atualize drivers e teste a RAM."),
        0x1A => ("MEMORY_MANAGEMENT", "Problema de memória. Rode o Teste de RAM e desative o XMP/EXPO para testar."),
        0x1E => ("KMODE_EXCEPTION_NOT_HANDLED", "Um driver falhou. Atualize os drivers, principalmente vídeo e rede."),
        0x24 => ("NTFS_FILE_SYSTEM", "Erro no sistema de arquivos. Rode o chkdsk na aba Ferramentas de reparo."),
        0x3B => ("SYSTEM_SERVICE_EXCEPTION", "Falha em driver ou serviço do sistema. Atualize drivers e rode o SFC."),
        0x4E => ("PFN_LIST_CORRUPT", "Memória corrompida. Teste a RAM."),
        0x50 => ("PAGE_FAULT_IN_NONPAGED_AREA", "Acesso a memória inválida: RAM com defeito/instável ou driver ruim."),
        0x7A => ("KERNEL_DATA_INPAGE_ERROR", "Falha ao ler do disco ou da RAM. Verifique a saúde do disco e os cabos."),
        0x7B => ("INACCESSIBLE_BOOT_DEVICE", "O Windows não acessou o disco de boot. Verifique modo SATA/NVMe na BIOS."),
        0x7E => ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Um driver travou. Atualize ou remova o driver suspeito."),
        0x7F => ("UNEXPECTED_KERNEL_MODE_TRAP", "Erro de hardware ou overclock instável. Volte as configurações padrão da BIOS."),
        0x9F => ("DRIVER_POWER_STATE_FAILURE", "Um driver não respondeu ao entrar/sair da suspensão. Atualize drivers (vídeo, rede, chipset)."),
        0xBE => ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Um driver tentou escrever onde não podia. Atualize drivers."),
        0xC2 => ("BAD_POOL_CALLER", "Driver com defeito. Atualize ou remova drivers instalados recentemente."),
        0xD1 => ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Driver acessou memória inválida (muito comum com rede, Wi-Fi e vídeo). Atualize esses drivers."),
        0xEF => ("CRITICAL_PROCESS_DIED", "Um processo essencial do Windows morreu. Rode SFC e DISM."),
        0x101 => ("CLOCK_WATCHDOG_TIMEOUT", "Um núcleo da CPU parou de responder: overclock/undervolt instável ou CPU com defeito."),
        0x116 => ("VIDEO_TDR_FAILURE", "O driver de vídeo travou e não voltou. Reinstale o driver (use Remover driver de vídeo) e tire overclock da GPU."),
        0x117 => ("VIDEO_TDR_TIMEOUT_DETECTED", "O driver de vídeo demorou demais. Reinstale o driver da GPU."),
        0x119 => ("VIDEO_SCHEDULER_INTERNAL_ERROR", "Erro no driver de vídeo. Reinstale o driver da GPU."),
        0x124 => ("WHEA_UNCORRECTABLE_ERROR", "Erro de hardware: overclock/undervolt instável, temperatura alta ou peça com defeito (CPU, RAM, placa-mãe)."),
        0x133 => ("DPC_WATCHDOG_VIOLATION", "Um driver demorou demais (muito comum com SSD/armazenamento). Atualize firmware do SSD e drivers de chipset."),
        0x139 => ("KERNEL_SECURITY_CHECK_FAILURE", "Estrutura do kernel corrompida: driver ruim ou RAM instável."),
        0x13A => ("KERNEL_MODE_HEAP_CORRUPTION", "Memória do kernel corrompida por driver. Atualize drivers de vídeo e áudio."),
        0x154 => ("UNEXPECTED_STORE_EXCEPTION", "Problema de armazenamento/memória. Verifique o disco e a RAM."),
        _ => ("Código desconhecido", "Atualize drivers e o Windows. Se repetir, rode o Teste de RAM e o Stress test."),
    };
}
