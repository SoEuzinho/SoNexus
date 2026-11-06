using System.Management;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record InstalledDriver(string Device, string Class, string Provider, string Version, DateTime? Date, string Inf)
{
    public string DateText => Date?.ToString("dd/MM/yyyy") ?? "";

    public string AgeText => Date is { } d ? ((DateTime.Now - d).TotalDays / 365) switch
    {
        < 1 => "menos de 1 ano",
        var y => $"{(int)y} ano(s)",
    } : "";

    /// <summary>Drivers de terceiros com mais de 3 anos. Os genéricos da Microsoft têm datas antigas de propósito.</summary>
    public bool IsOld => Date is { } d && d < DateTime.Now.AddYears(-3) && !Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    public string Status => IsOld ? "Antigo" : "";
}

public sealed record ProblemDevice(string Name, string Class, int Code, string InstanceId)
{
    public string Explanation => DriverService.ExplainCode(Code);
}

public sealed record GpuDriver(string Name, string Version, string Date, string Vendor, string DownloadUrl);

/// <summary>Uma atualização de driver oferecida pelo Windows Update.</summary>
public sealed class DriverUpdate : ObservableObject
{
    private bool _isSelected = true;
    private string _status = "";

    internal DriverUpdate(dynamic com)
    {
        Com = com;
        Title = (string)com.Title;
        Manufacturer = Safe(() => (string)com.DriverManufacturer);
        Class = Safe(() => (string)com.DriverClass);
        DateText = Safe(() => ((DateTime)com.DriverVerDate).ToString("dd/MM/yyyy"));
        Size = Safe(() => Format.Bytes((ulong)Convert.ToDouble(com.MaxDownloadSize)));
    }

    internal dynamic Com { get; }
    public string Title { get; }
    public string Manufacturer { get; }
    public string Class { get; }
    public string DateText { get; }
    public string Size { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    private static string Safe(Func<string> get)
    {
        try { return get(); } catch { return ""; }
    }
}

public static class DriverService
{
    public static List<InstalledDriver> GetInstalled()
    {
        var list = new List<InstalledDriver>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver");
        foreach (ManagementObject d in searcher.Get())
        {
            using (d)
            {
                var name = d["DeviceName"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                DateTime? date = null;
                if (d["DriverDate"]?.ToString() is { Length: > 0 } raw)
                {
                    try { date = ManagementDateTimeConverter.ToDateTime(raw); } catch { }
                }
                list.Add(new InstalledDriver(name.Trim(), d["DeviceClass"]?.ToString() ?? "",
                    d["DriverProviderName"]?.ToString() ?? "", d["DriverVersion"]?.ToString() ?? "", date,
                    d["InfName"]?.ToString() ?? ""));
            }
        }
        return list.OrderBy(d => d.Class).ThenBy(d => d.Device).ToList();
    }

    /// <summary>Dispositivos com ponto de exclamação no Gerenciador de Dispositivos.</summary>
    public static List<ProblemDevice> GetProblemDevices()
    {
        var list = new List<ProblemDevice>();
        try
        {
            // 22 = desativado pelo usuário, 45 = não conectado (fantasma): não são defeitos.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity " +
                "WHERE ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 22 AND ConfigManagerErrorCode <> 45");
            foreach (ManagementObject d in searcher.Get())
            {
                using (d)
                {
                    list.Add(new ProblemDevice(d["Name"]?.ToString() ?? "Dispositivo desconhecido", d["PNPClass"]?.ToString() ?? "",
                        Convert.ToInt32(d["ConfigManagerErrorCode"]), d["PNPDeviceID"]?.ToString() ?? ""));
                }
            }
        }
        catch { }
        return list;
    }

    public static string ExplainCode(int code) => code switch
    {
        1 => "Código 1: não está configurado corretamente. Reinstale o driver.",
        3 => "Código 3: driver corrompido ou memória insuficiente.",
        10 => "Código 10: o dispositivo não consegue iniciar. Atualize ou reinstale o driver.",
        12 => "Código 12: conflito de recursos com outro dispositivo.",
        14 => "Código 14: reinicie o PC para o dispositivo funcionar.",
        18 => "Código 18: reinstale o driver.",
        19 => "Código 19: informação de configuração no registro danificada.",
        24 => "Código 24: dispositivo ausente ou não funcionando direito.",
        28 => "Código 28: nenhum driver instalado. Procure o driver no Windows Update ou no site do fabricante.",
        31 => "Código 31: o Windows não conseguiu carregar o driver.",
        32 => "Código 32: o serviço do driver está desativado.",
        37 => "Código 37: o driver falhou ao iniciar.",
        39 => "Código 39: driver corrompido ou ausente.",
        43 => "Código 43: o Windows parou o dispositivo porque ele relatou problemas (comum em USB e placa de vídeo).",
        48 => "Código 48: driver bloqueado por problemas conhecidos.",
        52 => "Código 52: assinatura digital do driver inválida.",
        _ => $"Código {code}: o dispositivo está com problema.",
    };

    public static List<GpuDriver> GetGpuDrivers()
    {
        var list = new List<GpuDriver>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
        foreach (ManagementObject g in searcher.Get())
        {
            using (g)
            {
                var name = g["Name"]?.ToString() ?? "";
                // Placa virtual (Virtual Desktop, Parsec...) não tem driver de vídeo para baixar.
                if (SystemInfoService.IsVirtualAdapter(name, g["PNPDeviceID"]?.ToString())) continue;
                var compat = g["AdapterCompatibility"]?.ToString() ?? "";
                var text = name + " " + compat;
                var (vendor, url) =
                    text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? ("NVIDIA", "https://www.nvidia.com/pt-br/drivers/")
                    : text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                        ? ("AMD", "https://www.amd.com/pt/support/download/drivers.html")
                    : text.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                        ? ("Intel", "https://www.intel.com.br/content/www/br/pt/support/detect.html")
                    : (compat, "");
                var date = "";
                if (g["DriverDate"]?.ToString() is { Length: > 0 } raw)
                {
                    try { date = ManagementDateTimeConverter.ToDateTime(raw).ToString("dd/MM/yyyy"); } catch { }
                }
                list.Add(new GpuDriver(name, g["DriverVersion"]?.ToString() ?? "", date, vendor, url));
            }
        }
        return list;
    }

    // ---------- Windows Update ----------

    private const int ServerSelectionWindowsUpdate = 2;

    /// <summary>Pergunta ao Windows Update quais drivers novos existem para este PC (pode levar 1-2 minutos).</summary>
    public static Task<List<DriverUpdate>> SearchUpdatesAsync() => Task.Run(() =>
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        session.ClientApplicationID = "SoNexus";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerSelectionWindowsUpdate;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
        var list = new List<DriverUpdate>();
        for (var i = 0; i < (int)result.Updates.Count; i++) list.Add(new DriverUpdate(result.Updates.Item(i)));
        return list;
    });

    /// <summary>Baixa e instala as atualizações uma por uma. Retorna se precisa reiniciar.</summary>
    public static Task<bool> InstallUpdatesAsync(IReadOnlyList<DriverUpdate> updates, Action<DriverUpdate, string> status) => Task.Run(() =>
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        session.ClientApplicationID = "SoNexus";
        var reboot = false;
        foreach (var u in updates)
        {
            try
            {
                dynamic com = u.Com;
                if (!(bool)com.EulaAccepted) com.AcceptEula();
                dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
                collection.Add(com);

                status(u, "Baixando...");
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = collection;
                downloader.Download();

                status(u, "Instalando...");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = collection;
                dynamic result = installer.Install();
                int code = result.ResultCode; // 2 = ok, 3 = ok com erros, 4 = falhou, 5 = cancelado
                if ((bool)result.RebootRequired) reboot = true;
                status(u, code switch { 2 => "Instalado", 3 => "Instalado com avisos", _ => $"Falhou (código {code})" });
            }
            catch (Exception ex)
            {
                status(u, $"Falhou: {ex.Message}");
            }
        }
        return reboot;
    });
}
