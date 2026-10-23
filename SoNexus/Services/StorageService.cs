using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record SmartRow(string Id, string Name, string Current, string Worst, string Raw, bool Important);

public sealed class DiskHealth
{
    public int Index { get; init; }
    public string Model { get; init; } = "";
    public string Type { get; init; } = "";
    public ulong Size { get; init; }
    public string Firmware { get; set; } = "";
    public string Serial { get; init; } = "";
    public string WindowsHealth { get; init; } = "";
    public int? Temperature { get; set; }
    public ulong? PowerOnHours { get; set; }
    public ulong? PowerCycles { get; set; }
    public int? LifeUsedPercent { get; set; }
    public int? SparePercent { get; set; }
    public double? WrittenTB { get; set; }
    public double? ReadTB { get; set; }
    public ulong? UnsafeShutdowns { get; set; }
    public ulong? MediaErrors { get; set; }
    public long? Reallocated { get; set; }
    public long? Pending { get; set; }
    public long? Uncorrectable { get; set; }
    public long? CrcErrors { get; set; }
    public string CriticalWarning { get; set; } = "";
    public List<SmartRow> Smart { get; } = new();
    public List<string> Volumes { get; } = new();
    public List<string> Issues { get; } = new();
    public string Verdict { get; set; } = "Saudável";
    public bool SmartAvailable { get; set; }

    public string Title => $"{Model}";
    public string Subtitle => $"{Type} · {Format.Bytes(Size)}" + (Volumes.Count > 0 ? $" · {string.Join(" ", Volumes.Select(v => v.Split(' ')[0]))}" : "");
    public string LifeText => LifeUsedPercent is { } u ? $"{Math.Max(0, 100 - u)}% de vida" : "";
}

public sealed record ScanProgress(double Percent, double MBps, int Errors, int Slow, string Position, TimeSpan Eta);

public sealed record BadBlock(long Offset, string Kind)
{
    public string OffsetText => $"{Offset / (1024.0 * 1024 * 1024):0.000} GB (byte {Offset:N0})";
}

/// <summary>Saúde real dos discos: SMART do SSD/HD, log de saúde do NVMe e varredura de setores (só leitura).</summary>
public static class StorageService
{
    public static List<DiskHealth> Disks()
    {
        var disks = new List<DiskHealth>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, SerialNumber, FirmwareVersion FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var media = Convert.ToInt32(o["MediaType"] ?? 0);
                    var bus = Convert.ToInt32(o["BusType"] ?? 0);
                    var type = bus == 17 ? "SSD NVMe" : media == 4 ? "SSD SATA" : media == 3 ? "HD" : bus == 7 ? "USB" : "Disco";
                    disks.Add(new DiskHealth
                    {
                        Index = Convert.ToInt32(o["DeviceId"] ?? -1),
                        Model = o["FriendlyName"]?.ToString()?.Trim() ?? "Disco",
                        Type = type,
                        Size = Convert.ToUInt64(o["Size"] ?? 0),
                        Serial = o["SerialNumber"]?.ToString()?.Trim() ?? "",
                        Firmware = o["FirmwareVersion"]?.ToString()?.Trim() ?? "",
                        WindowsHealth = Convert.ToInt32(o["HealthStatus"] ?? 0) switch { 0 => "Saudável", 1 => "Atenção", 2 => "Com defeito", _ => "Desconhecido" },
                    });
                }
            }
        }
        catch { }

        AddVolumes(disks);

        // SMART lido direto pelo Windows (NVMe por IOCTL, SATA/HD pelo WMI); precisa de administrador.
        foreach (var disk in disks)
        {
            try
            {
                if (disk.Type == "SSD NVMe" && SmartReader.ReadNvme(disk.Index) is { } nvme) ReadNvme(nvme, disk);
                else if (SmartReader.ReadAta(disk.Index) is { Count: > 0 } ata) ReadAta(ata, disk);
                if (SmartReader.PredictsFailure(disk.Index) == true) disk.CriticalWarning = "o SMART prevê falha do disco";

                // Complemento genérico do Windows (serve também para discos USB e controladoras RAID).
                if (!disk.SmartAvailable || disk.Temperature == null)
                {
                    if (SmartReader.Counters(disk.Index) is { } c)
                    {
                        disk.Temperature ??= c.Temperature;
                        disk.PowerOnHours ??= c.PowerOnHours;
                        disk.LifeUsedPercent ??= c.Wear;
                        if (c.ReadErrorsUncorrected > 0) disk.MediaErrors ??= c.ReadErrorsUncorrected;
                        disk.SmartAvailable |= c.Temperature != null || c.PowerOnHours != null;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"SMART do disco {disk.Index} falhou: {ex.Message}");
            }
        }

        foreach (var d in disks) Judge(d);
        return disks.OrderBy(d => d.Index).ToList();
    }

    /// <summary>Partições de cada disco com o espaço livre.</summary>
    private static void AddVolumes(List<DiskHealth> disks)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var letter = Convert.ToChar(o["DriveLetter"] ?? '\0');
                    if (letter == '\0') continue;
                    var disk = disks.FirstOrDefault(d => d.Index == Convert.ToInt32(o["DiskNumber"] ?? -1));
                    if (disk == null) continue;
                    var di = new DriveInfo($"{letter}:\\");
                    if (di.IsReady)
                        disk.Volumes.Add($"{letter}: {Format.Bytes((ulong)di.AvailableFreeSpace)} livres de {Format.Bytes((ulong)di.TotalSize)}");
                }
            }
        }
        catch { }
    }

    private static void ReadNvme(SmartReader.NvmeHealth h, DiskHealth d)
    {
        d.SmartAvailable = true;
        d.LifeUsedPercent = h.PercentageUsed;
        d.SparePercent = h.AvailableSpare;
        // Unidade de dados NVMe = 1000 blocos de 512 bytes.
        d.WrittenTB = h.DataUnitsWritten * 512_000.0 / 1e12;
        d.ReadTB = h.DataUnitsRead * 512_000.0 / 1e12;
        d.PowerOnHours = h.PowerOnHours;
        d.PowerCycles = h.PowerCycles;
        d.UnsafeShutdowns = h.UnsafeShutdowns;
        d.MediaErrors = h.MediaErrors;
        if (h.TemperatureC is > 0 and < 150) d.Temperature = h.TemperatureC;
        d.CriticalWarning = NvmeWarning(h.CriticalWarning);

        d.Smart.Add(new SmartRow("—", "Vida útil usada", $"{h.PercentageUsed}%", "", "", true));
        d.Smart.Add(new SmartRow("—", "Reserva disponível", $"{h.AvailableSpare}%", $"mín. {h.SpareThreshold}%", "", true));
        d.Smart.Add(new SmartRow("—", "Erros de mídia (dados ilegíveis)", $"{h.MediaErrors}", "", "", true));
        d.Smart.Add(new SmartRow("—", "Aviso crítico", d.CriticalWarning.Length == 0 ? "nenhum" : d.CriticalWarning, "", "", true));
        d.Smart.Add(new SmartRow("—", "Total escrito", $"{d.WrittenTB:0.00} TB", "", "", false));
        d.Smart.Add(new SmartRow("—", "Total lido", $"{d.ReadTB:0.00} TB", "", "", false));
        d.Smart.Add(new SmartRow("—", "Horas ligado", $"{h.PowerOnHours:N0} h", "", "", false));
        d.Smart.Add(new SmartRow("—", "Vezes ligado", $"{h.PowerCycles:N0}", "", "", false));
        d.Smart.Add(new SmartRow("—", "Desligamentos sem aviso", $"{h.UnsafeShutdowns:N0}", "", "", false));
        d.Smart.Add(new SmartRow("—", "Entradas no log de erros", $"{h.ErrorLogEntries:N0}", "", "", false));
        d.Smart.Add(new SmartRow("—", "Tempo acima da temperatura de alerta", $"{h.WarningTempMinutes} min", "", "", false));
    }

    private static string NvmeWarning(byte bits)
    {
        if (bits == 0) return "";
        var list = new List<string>();
        if ((bits & 1) != 0) list.Add("reserva abaixo do mínimo");
        if ((bits & 2) != 0) list.Add("temperatura fora do limite");
        if ((bits & 4) != 0) list.Add("confiabilidade degradada");
        if ((bits & 8) != 0) list.Add("disco em modo somente leitura");
        if ((bits & 16) != 0) list.Add("falha no backup de memória");
        return string.Join(", ", list);
    }

    // IDs SMART importantes para SATA/HD.
    private static readonly Dictionary<byte, string> AtaNames = new()
    {
        [1] = "Taxa de erros de leitura", [5] = "Setores realocados (defeituosos)", [9] = "Horas ligado", [12] = "Vezes ligado",
        [177] = "Desgaste (wear leveling)", [187] = "Erros incorrigíveis reportados", [188] = "Timeouts de comando",
        [194] = "Temperatura", [196] = "Eventos de realocação", [197] = "Setores pendentes (instáveis)",
        [198] = "Setores incorrigíveis (offline)", [199] = "Erros de cabo/interface (CRC)", [202] = "Vida restante (%)",
        [231] = "Vida restante do SSD (%)", [233] = "Indicador de desgaste", [241] = "Total escrito (LBAs)", [242] = "Total lido (LBAs)",
        [169] = "Vida restante (%)", [173] = "Contagem de desgaste", [174] = "Desligamentos sem aviso",
    };

    private static readonly HashSet<byte> Critical = new() { 5, 187, 196, 197, 198, 199 };

    private static void ReadAta(List<SmartReader.AtaAttribute> attributes, DiskHealth d)
    {
        d.SmartAvailable = true;
        foreach (var a in attributes)
        {
            var (id, current, worst, raw) = (a.Id, a.Current, a.Worst, a.Raw);
            var low32 = raw & 0xFFFFFFFF;
            // Valor atual abaixo do limite do fabricante = atributo reprovado.
            if (a.Threshold > 0 && current <= a.Threshold && id != 194)
                d.CriticalWarning = $"atributo {id:X2} abaixo do limite do fabricante";

            switch (id)
            {
                case 5: d.Reallocated = low32; break;
                case 9: d.PowerOnHours = (ulong)low32; break;
                case 12: d.PowerCycles = (ulong)low32; break;
                case 187: d.Uncorrectable = (d.Uncorrectable ?? 0) + low32; break;
                case 197: d.Pending = low32; break;
                case 198: d.Uncorrectable = (d.Uncorrectable ?? 0) + low32; break;
                case 199: d.CrcErrors = low32; break;
                case 194: d.Temperature ??= (int)(raw & 0xFF); break;
                case 174: d.UnsafeShutdowns = (ulong)low32; break;
                case 241: d.WrittenTB = raw * 512.0 / 1e12; break;
                case 231 or 202 or 169: d.LifeUsedPercent = 100 - current; break;
                case 177 or 233 when d.LifeUsedPercent == null: d.LifeUsedPercent = 100 - Math.Min((byte)100, current); break;
            }
            var name = AtaNames.TryGetValue(id, out var n) ? n : $"Atributo {id} ({id:X2})";
            d.Smart.Add(new SmartRow($"{id:X2}", name, $"{current}", $"{worst}", $"{raw:N0}", Critical.Contains(id) && low32 > 0));
        }
    }

    /// <summary>Regras de diagnóstico (as mesmas que ferramentas como o CrystalDiskInfo usam).</summary>
    private static void Judge(DiskHealth d)
    {
        var critical = false;
        var warning = false;
        void Crit(string s) { d.Issues.Add(s); critical = true; }
        void Warn(string s) { d.Issues.Add(s); warning = true; }

        if (d.WindowsHealth == "Com defeito") Crit("O próprio Windows marcou o disco como defeituoso.");
        if (d.CriticalWarning.Length > 0) Crit($"O SSD está emitindo aviso crítico ({d.CriticalWarning}).");
        if (d.MediaErrors > 0) Crit($"{d.MediaErrors} erro(s) de mídia: houve dados que o SSD não conseguiu ler.");
        if (d.Pending > 0) Crit($"{d.Pending} setor(es) pendente(s): partes do disco que estão falhando.");
        if (d.Uncorrectable > 0) Crit($"{d.Uncorrectable} erro(s) incorrigível(is) de leitura.");
        if (d.Reallocated > 50) Crit($"{d.Reallocated} setores já foram substituídos por defeito.");
        else if (d.Reallocated > 0) Warn($"{d.Reallocated} setor(es) defeituoso(s) já substituído(s). Acompanhe: se o número subir, troque o disco.");
        if (d.SparePercent is { } sp && sp < 10) Crit($"Reserva do SSD quase no fim ({sp}%).");
        if (d.LifeUsedPercent >= 100) Crit("Vida útil do SSD esgotada (100% usada).");
        else if (d.LifeUsedPercent >= 80) Warn($"SSD com {d.LifeUsedPercent}% da vida útil usada.");
        if (d.CrcErrors > 0) Warn($"{d.CrcErrors} erro(s) de comunicação: troque o cabo SATA ou mude de porta.");
        if (d.Temperature >= 70) Warn($"Temperatura alta ({d.Temperature} °C). Melhore a ventilação ou use dissipador no NVMe.");
        if (d.WindowsHealth == "Atenção") Warn("O Windows indica atenção na saúde do disco.");
        if (d.UnsafeShutdowns > 100) d.Issues.Add($"{d.UnsafeShutdowns} desligamentos sem aviso (quedas de energia, travamentos). Não é defeito, mas aumenta o risco de corromper arquivos.");
        if (!d.SmartAvailable) d.Issues.Add("Dados SMART indisponíveis (abra o SoNexus como administrador ou o disco é USB).");

        d.Verdict = critical ? "Crítico" : warning ? "Atenção" : d.SmartAvailable ? "Saudável" : "Sem SMART";
    }

    // ---------- Varredura de setores (somente leitura) ----------

    /// <summary>
    /// Lê o disco do início ao fim (ou em amostras) procurando partes ilegíveis ou muito lentas.
    /// Só leitura: nenhum dado é alterado.
    /// </summary>
    public static unsafe List<BadBlock> Scan(int diskIndex, ulong size, bool full, bool isHdd, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        const int block = 1 << 20; // 1 MB
        var slowMs = isHdd ? 400 : 150;
        var bad = new List<BadBlock>();
        using var handle = CreateFileW($@"\\.\PhysicalDrive{diskIndex}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (handle.IsInvalid) throw new InvalidOperationException("Sem acesso ao disco. Execute o SoNexus como administrador.");

        var blocks = (long)(size / block);
        IEnumerable<long> plan = full
            ? Enumerable.Range(0, (int)Math.Min(blocks, int.MaxValue)).Select(i => (long)i)
            : SamplePlan(blocks, 3000);
        var total = full ? blocks : Math.Min(blocks, 3000);

        var buffer = (byte*)NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            var sw = Stopwatch.StartNew();
            long done = 0, bytes = 0;
            var slow = 0;
            var lastReport = 0L;
            foreach (var b in plan)
            {
                if (ct.IsCancellationRequested) break;
                var offset = b * block;
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    var read = RandomAccess.Read(handle, new Span<byte>(buffer, block), offset);
                    bytes += read;
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (ms > slowMs)
                    {
                        slow++;
                        if (bad.Count < 500) bad.Add(new BadBlock(offset, $"Lento ({ms:0} ms)"));
                    }
                }
                catch (IOException)
                {
                    // Lê esse bloco em pedaços de 4 KB para achar exatamente os setores ruins.
                    for (var sub = 0; sub < block; sub += 4096)
                    {
                        try { RandomAccess.Read(handle, new Span<byte>(buffer, 4096), offset + sub); }
                        catch (IOException) { if (bad.Count < 500) bad.Add(new BadBlock(offset + sub, "Ilegível")); }
                    }
                }
                done++;
                if (sw.ElapsedMilliseconds - lastReport > 250 || done == total)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    var rate = bytes / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1e6;
                    var eta = done > 0 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / done * (total - done)) : TimeSpan.Zero;
                    progress.Report(new ScanProgress(100.0 * done / total, rate, bad.Count(x => x.Kind == "Ilegível"), slow,
                        $"{offset / 1e9:0.0} GB de {size / 1e9:0} GB", eta));
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
        return bad;
    }

    private static IEnumerable<long> SamplePlan(long blocks, int count)
    {
        // Começo e fim do disco (onde ficam o sistema e as tabelas) + pontos espalhados por todo o resto.
        var set = new SortedSet<long>();
        for (long i = 0; i < Math.Min(256, blocks); i++) set.Add(i);
        for (long i = Math.Max(0, blocks - 64); i < blocks; i++) set.Add(i);
        var rnd = new Random(42);
        while (set.Count < Math.Min(count, blocks)) set.Add((long)(rnd.NextDouble() * blocks));
        return set;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_NO_BUFFERING = 0x20000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
