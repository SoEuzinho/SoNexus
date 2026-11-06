using System.Management;
using Microsoft.Win32;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Coleta as peças do PC via WMI e registro.</summary>
public static class SystemInfoService
{
    private static readonly (string Title, string Glyph, Func<List<InfoItem>> Build)[] Builders =
    {
        ("Sistema", "", SystemItems),
        ("Processador", "", CpuItems),
        ("Placa-mãe e BIOS", "", BoardItems),
        ("Memória RAM", "", RamItems),
        ("Placa de vídeo", "", GpuItems),
        ("Armazenamento", "", StorageItems),
        ("Rede", "", NetworkItems),
        ("Áudio", "", AudioItems),
    };

    public static List<InfoSection> Collect() => Builders.Select(b => Section(b.Title, b.Glyph, b.Build)).ToList();

    /// <summary>Só uma seção (usado na aba "Info" de cada peça, para não ler o PC inteiro).</summary>
    public static InfoSection CollectOne(string title)
    {
        var b = Builders.First(x => x.Title == title);
        return Section(b.Title, b.Glyph, b.Build);
    }

    // Placas "de mentira" criadas por apps (VR, streaming, tela remota): não são a sua placa de vídeo.
    private static readonly string[] VirtualAdapterNames =
    {
        "Virtual Desktop", "Virtual Display", "Virtual Monitor", "Parsec", "Microsoft Basic Display", "Microsoft Remote Display",
        "IddSample", "Indirect Display", "spacedesk", "Meta Virtual", "Oculus Virtual", "Citrix", "Duet Display",
        "Sunshine", "Moonlight", "GameViewer", "Splashtop", "AnyDesk", "TeamViewer", "Mirage Driver", "DisplayLink",
    };

    /// <summary>Adaptador de vídeo virtual (Virtual Desktop, Parsec, tela remota...). PNP "ROOT\..." = criado por software.</summary>
    public static bool IsVirtualAdapter(string name, string? pnpId = null) =>
        VirtualAdapterNames.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase))
        || (pnpId?.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ?? false);

    public static string ToText(IEnumerable<InfoSection> sections)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SoNexus — Relatório de hardware ({DateTime.Now:dd/MM/yyyy HH:mm})");
        foreach (var s in sections)
        {
            sb.AppendLine().AppendLine($"== {s.Title} ==");
            foreach (var i in s.Items) sb.AppendLine($"{i.Key}: {i.Value}");
        }
        return sb.ToString();
    }

    private static InfoSection Section(string title, string glyph, Func<List<InfoItem>> build)
    {
        List<InfoItem> items;
        try { items = build(); }
        catch (Exception ex) { items = new() { new("Erro", ex.Message) }; }
        if (items.Count == 0) items.Add(new("—", "Nada encontrado"));
        return new InfoSection(title, glyph, items);
    }

    // ---------- Seções ----------

    private static List<InfoItem> SystemItems()
    {
        var list = new List<InfoItem>();
        var os = Query("SELECT Caption, OSArchitecture, InstallDate, CSName FROM Win32_OperatingSystem").FirstOrDefault();
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = $"{cv?.GetValue("CurrentBuild")}.{cv?.GetValue("UBR")}";
        if (os != null)
        {
            list.Add(new("Windows", $"{S(os, "Caption")} {cv?.GetValue("DisplayVersion")}".Trim()));
            list.Add(new("Build", build));
            list.Add(new("Arquitetura", S(os, "OSArchitecture")));
            list.Add(new("Nome do PC", S(os, "CSName")));
            var install = S(os, "InstallDate");
            if (install.Length > 0) list.Add(new("Instalado em", ManagementDateTimeConverter.ToDateTime(install).ToString("dd/MM/yyyy")));
        }
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        list.Add(new("Ligado há", $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}min"));
        return list;
    }

    private static List<InfoItem> CpuItems()
    {
        var list = new List<InfoItem>();
        foreach (var c in Query("SELECT * FROM Win32_Processor"))
        {
            list.Add(new("Modelo", S(c, "Name")));
            list.Add(new("Fabricante", S(c, "Manufacturer")));
            list.Add(new("Núcleos / Threads", $"{S(c, "NumberOfCores")} / {S(c, "NumberOfLogicalProcessors")}"));
            list.Add(new("Clock base", $"{U(c, "MaxClockSpeed") / 1000.0:0.00} GHz"));
            list.Add(new("Cache L2 / L3", $"{U(c, "L2CacheSize") / 1024.0:0.#} MB / {U(c, "L3CacheSize") / 1024.0:0.#} MB"));
            list.Add(new("Soquete", S(c, "SocketDesignation")));
            list.Add(new("Virtualização", S(c, "VirtualizationFirmwareEnabled") == "True" ? "Ativada" : "Desativada"));
        }
        return list;
    }

    private static List<InfoItem> BoardItems()
    {
        var list = new List<InfoItem>();
        foreach (var b in Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
        {
            list.Add(new("Fabricante", S(b, "Manufacturer")));
            list.Add(new("Modelo", S(b, "Product")));
            var ver = S(b, "Version");
            if (ver.Length > 0 && !ver.Contains("Default", StringComparison.OrdinalIgnoreCase)) list.Add(new("Revisão", ver));
        }
        foreach (var b in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            var date = S(b, "ReleaseDate");
            var when = date.Length > 0 ? ManagementDateTimeConverter.ToDateTime(date).ToString("dd/MM/yyyy") : "";
            list.Add(new("BIOS", $"{S(b, "Manufacturer")} {S(b, "SMBIOSBIOSVersion")}  {when}".Trim()));
        }
        return list;
    }

    private static List<InfoItem> RamItems()
    {
        var list = new List<InfoItem>();
        var modules = Query("SELECT * FROM Win32_PhysicalMemory");
        ulong total = 0;
        var speed = 0UL;
        var type = "";
        foreach (var m in modules)
        {
            var cap = U(m, "Capacity");
            total += cap;
            var configured = U(m, "ConfiguredClockSpeed");
            var rated = U(m, "Speed");
            speed = Math.Max(speed, configured > 0 ? configured : rated);
            type = MemoryType(U(m, "SMBIOSMemoryType"));
            var parts = new[] { Format.Bytes(cap), S(m, "Manufacturer"), S(m, "PartNumber"), $"{(configured > 0 ? configured : rated)} MT/s" }
                .Where(p => p.Length > 0);
            list.Add(new(S(m, "DeviceLocator") is { Length: > 0 } loc ? loc : S(m, "BankLabel"), string.Join(" · ", parts)));
        }
        var slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => (long)U(a, "MemoryDevices"));
        list.Insert(0, new("Total", $"{Format.Bytes(total)} {type} @ {speed} MT/s".Replace("  ", " ")));
        if (slots > 0) list.Insert(1, new("Slots usados", $"{modules.Count} de {slots}"));
        return list;
    }

    private static List<InfoItem> GpuItems()
    {
        var list = new List<InfoItem>();
        var all = Query("SELECT * FROM Win32_VideoController");
        var real = all.Where(g => !IsVirtualAdapter(S(g, "Name"), S(g, "PNPDeviceID"))).ToList();
        // Máquina virtual só tem adaptador virtual: aí mostra o que houver.
        foreach (var g in real.Count > 0 ? real : all)
        {
            var name = S(g, "Name");
            var vram = GpuMemory(name) ?? U(g, "AdapterRAM");
            list.Add(new("Modelo", name));
            if (vram > 0) list.Add(new("Memória de vídeo", Format.Bytes(vram)));
            var date = S(g, "DriverDate");
            var when = date.Length > 0 ? ManagementDateTimeConverter.ToDateTime(date).ToString("dd/MM/yyyy") : "";
            list.Add(new("Driver", $"{S(g, "DriverVersion")}  ({when})"));
            var w = U(g, "CurrentHorizontalResolution");
            if (w > 0) list.Add(new("Resolução atual", $"{w} x {U(g, "CurrentVerticalResolution")} @ {U(g, "CurrentRefreshRate")} Hz"));
        }
        return list;
    }

    private static List<InfoItem> StorageItems()
    {
        var list = new List<InfoItem>();
        var disks = Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        if (disks.Count > 0)
        {
            foreach (var d in disks)
            {
                var media = U(d, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                var bus = U(d, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", _ => "" };
                var health = U(d, "HealthStatus") switch { 0 => "Saudável", 1 => "Atenção", 2 => "Com problemas", _ => "?" };
                list.Add(new(S(d, "FriendlyName"), $"{Format.Bytes(U(d, "Size"))} · {$"{bus} {media}".Trim()} · {health}"));
            }
        }
        else
        {
            foreach (var d in Query("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive"))
                list.Add(new(S(d, "Model"), $"{Format.Bytes(U(d, "Size"))} · {S(d, "InterfaceType")}"));
        }
        foreach (var v in Query("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            var size = U(v, "Size");
            var free = U(v, "FreeSpace");
            var pct = size > 0 ? 100.0 * (size - free) / size : 0;
            list.Add(new($"Unidade {S(v, "DeviceID")} {S(v, "VolumeName")}".Trim(),
                $"{Format.Bytes(free)} livres de {Format.Bytes(size)} ({pct:0}% usado)"));
        }
        return list;
    }

    private static List<InfoItem> NetworkItems()
    {
        var list = new List<InfoItem>();
        foreach (var n in Query("SELECT Name, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True"))
        {
            var bps = U(n, "Speed");
            var speed = bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";
            list.Add(new(S(n, "Name"), bps > 0 ? $"Conectado · {speed}" : "Conectado"));
        }
        return list;
    }

    private static List<InfoItem> AudioItems() =>
        Query("SELECT Name, Manufacturer FROM Win32_SoundDevice")
            .Select(a => new InfoItem(S(a, "Name"), S(a, "Manufacturer")))
            .ToList();

    // ---------- Auxiliares ----------

    private static string MemoryType(ulong smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5",
        _ => "",
    };

    /// <summary>WMI limita AdapterRAM a 4 GB; o registro do driver tem o valor real.</summary>
    private static ulong? GpuMemory(string name)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") as string != name) continue;
                switch (k.GetValue("HardwareInformation.qwMemorySize"))
                {
                    case long l: return (ulong)l;
                    case byte[] b when b.Length >= 8: return BitConverter.ToUInt64(b, 0);
                }
                switch (k.GetValue("HardwareInformation.MemorySize"))
                {
                    case int i: return (uint)i;
                    case byte[] b when b.Length >= 4: return BitConverter.ToUInt32(b, 0);
                }
            }
        }
        catch { }
        return null;
    }

    private static List<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch
        {
            return new();
        }
    }

    private static string S(ManagementObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }

    private static ulong U(ManagementObject o, string prop)
    {
        try { return Convert.ToUInt64(o[prop] ?? 0); } catch { return 0; }
    }
}
