using System.Management;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Um dispositivo do PC e seu driver (Bluetooth, Wi-Fi, áudio, chipset...).</summary>
public sealed class DeviceEntry : ObservableObject
{
    private string _update = "";
    private string _status = "";

    public required string Category { get; init; }
    public required string Name { get; init; }
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string DeviceId { get; init; } = "";
    public bool Generic { get; init; }
    internal DriverUpdate? Update { get; set; }

    public string DateText => Date?.ToString("dd/MM/yyyy") ?? "";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string UpdateText { get => _update; set { if (Set(ref _update, value)) Raise(nameof(HasUpdate)); } }
    public bool HasUpdate => Update != null;
}

public sealed record BoardSupport(string Board, string Maker, string Url);

public static class DeviceDriverService
{
    public static List<DeviceEntry> Load()
    {
        var problems = DriverService.GetProblemDevices().ToDictionary(p => p.InstanceId, p => p.Explanation, StringComparer.OrdinalIgnoreCase);
        var list = new List<DeviceEntry>();
        using var s = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, DeviceID FROM Win32_PnPSignedDriver");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = o["DeviceName"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var cls = o["DeviceClass"]?.ToString()?.ToUpperInvariant() ?? "";
                var provider = o["DriverProviderName"]?.ToString() ?? "";
                var category = Categorize(cls, name, provider);
                if (category == null) continue;
                DateTime? date = null;
                if (o["DriverDate"]?.ToString() is { Length: > 0 } raw)
                    try { date = ManagementDateTimeConverter.ToDateTime(raw); } catch { }
                var id = o["DeviceID"]?.ToString() ?? "";
                list.Add(new DeviceEntry
                {
                    Category = category,
                    Name = name,
                    Provider = provider,
                    Version = o["DriverVersion"]?.ToString() ?? "",
                    Date = date,
                    DeviceId = id,
                    Generic = provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
                    Status = problems.TryGetValue(id, out var p) ? p : "OK",
                });
            }
        }
        return list.DistinctBy(d => (d.Name, d.Version)).OrderBy(d => d.Category).ThenBy(d => d.Name).ToList();
    }

    private static string? Categorize(string cls, string name, string provider)
    {
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
        // Adaptadores virtuais do próprio Windows não interessam.
        if (Has("WAN Miniport") || Has("Kernel Debug") || Has("Teredo") || Has("Microsoft ISATAP") || cls == "PRINTQUEUE" || cls == "SOFTWAREDEVICE") return null;
        return cls switch
        {
            "BLUETOOTH" => "Bluetooth",
            "NET" when Has("Bluetooth") => "Bluetooth",
            "NET" when Has("Wi-Fi") || Has("Wireless") || Has("WLAN") || Has("802.11") || Has("WiFi") => "Wi-Fi",
            "NET" => "Rede (cabo e virtual)",
            "MEDIA" or "AUDIOENDPOINT" => "Áudio",
            "DISPLAY" => "Placa de vídeo",
            "MONITOR" => "Monitores",
            "SYSTEM" => "Chipset e placa-mãe",
            "USB" => "USB",
            "HIDCLASS" or "KEYBOARD" or "MOUSE" or "XNACOMPOSITE" or "XBOXCOMPOSITE" => "Teclado, mouse e controles",
            "SCSIADAPTER" or "HDC" or "DISKDRIVE" or "SCSIADAPTERS" => "Armazenamento",
            "CAMERA" or "IMAGE" => "Câmeras",
            "BIOMETRIC" => "Biometria",
            "PRINTER" => "Impressoras",
            "SECURITYDEVICES" => "Segurança (TPM)",
            "FIRMWARE" => "Firmware",
            _ => "Outros",
        };
    }

    /// <summary>Liga cada atualização do Windows Update ao dispositivo certo (pelo ID de hardware).</summary>
    public static List<DriverUpdate> Match(List<DeviceEntry> devices, List<DriverUpdate> updates)
    {
        var unmatched = new List<DriverUpdate>();
        foreach (var u in updates)
        {
            string hwId = "", model = "";
            try { hwId = (string)u.Com.DriverHardwareID ?? ""; } catch { }
            try { model = (string)u.Com.DriverModel ?? ""; } catch { }
            var device = devices.FirstOrDefault(d => hwId.Length > 0 && d.DeviceId.StartsWith(hwId, StringComparison.OrdinalIgnoreCase))
                         ?? devices.FirstOrDefault(d => model.Length > 3 && d.Name.Contains(model, StringComparison.OrdinalIgnoreCase));
            if (device == null)
            {
                unmatched.Add(u);
                continue;
            }
            device.Update = u;
            device.UpdateText = $"{u.DateText} · {u.Title}";
        }
        return unmatched;
    }

    public static BoardSupport Board()
    {
        string maker = "", product = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                using (o) { maker = o["Manufacturer"]?.ToString() ?? ""; product = o["Product"]?.ToString() ?? ""; }
        }
        catch { }
        var m = maker.ToUpperInvariant();
        var (name, url) =
            m.Contains("ASUS") ? ("ASUS", "https://www.asus.com/br/support/download-center/") :
            m.Contains("MICRO-STAR") || m.Contains("MSI") ? ("MSI", "https://br.msi.com/support/download") :
            m.Contains("GIGABYTE") ? ("Gigabyte", "https://www.gigabyte.com/br/Support") :
            m.Contains("ASROCK") ? ("ASRock", "https://www.asrock.com/support/index.pt.asp") :
            m.Contains("DELL") ? ("Dell", "https://www.dell.com/support/home/pt-br") :
            m.Contains("LENOVO") ? ("Lenovo", "https://pcsupport.lenovo.com/br/pt") :
            m.Contains("HP") || m.Contains("HEWLETT") ? ("HP", "https://support.hp.com/br-pt/drivers") :
            m.Contains("ACER") ? ("Acer", "https://www.acer.com/br-pt/support") :
            (maker, "");
        return new BoardSupport(product, name, url);
    }
}
