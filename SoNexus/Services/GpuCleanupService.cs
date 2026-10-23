using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SoNexus.Models;

namespace SoNexus.Services;

public enum GpuVendor { Nvidia, Amd, Intel }

/// <summary>Um pacote de driver guardado no Windows (DriverStore), ex.: oem42.inf.</summary>
public sealed class DriverPackage : ObservableObject
{
    private bool _isSelected = true;

    public string Driver { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string Version { get; set; } = "";

    public string InfName => Path.GetFileName(OriginalFileName);
    public string ClassText => ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ? "Áudio HDMI/DP" : "Vídeo";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed record CleanupOptions(bool IncludeAudio, bool BlockWindowsUpdate, bool RemoveServices, bool RemoveFolders, bool RemoveControlPanel);

/// <summary>
/// Remoção completa do driver de vídeo no estilo DDU: dispositivos, pacotes do DriverStore,
/// serviços, painel de controle e pastas/caches do fabricante.
/// </summary>
public static class GpuCleanupService
{
    private const string DriverSearching = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";

    public static string Name(GpuVendor v) => v switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", _ => "Intel" };

    private static bool IsVendor(GpuVendor v, string text) => v switch
    {
        GpuVendor.Nvidia => text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Amd => text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("AMD", StringComparison.Ordinal) || text.Contains("ATI Technologies", StringComparison.OrdinalIgnoreCase),
        _ => text.Contains("Intel", StringComparison.OrdinalIgnoreCase),
    };

    public static List<GpuVendor> DetectInstalled() =>
        DriverService.GetGpuDrivers()
            .SelectMany(g => Enum.GetValues<GpuVendor>().Where(v => IsVendor(v, g.Name + " " + g.Vendor)))
            .Distinct()
            .ToList();

    public static async Task<List<DriverPackage>> GetPackagesAsync(GpuVendor vendor, bool includeAudio)
    {
        var all = await ProcessRunner.PowerShellJsonAsync<DriverPackage>(
            """Get-WindowsDriver -Online | Select-Object @{n='Driver';e={"$($_.Driver)"}}, @{n='OriginalFileName';e={"$($_.OriginalFileName)"}}, @{n='ClassName';e={"$($_.ClassName)"}}, @{n='ProviderName';e={"$($_.ProviderName)"}}, @{n='Version';e={"$($_.Version)"}}""");
        return all
            .Where(p => IsVendor(vendor, p.ProviderName))
            .Where(p => p.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase)
                        || (includeAudio && vendor != GpuVendor.Intel && p.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.ClassName).ThenBy(p => p.Driver)
            .ToList();
    }

    public static List<InstalledProgram> GetVendorPrograms(GpuVendor vendor)
    {
        string[] keywords = vendor switch
        {
            GpuVendor.Nvidia => new[] { "NVIDIA" },
            GpuVendor.Amd => new[] { "AMD Software", "Radeon", "AMD Settings", "AMD Catalyst" },
            _ => new[] { "Intel(R) Graphics", "Intel Graphics", "Intel Arc", "Intel(R) Arc" },
        };
        return HiddenItemsService.GetPrograms()
            .Where(p => !p.Hidden && keywords.Any(k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ---------- Limpeza ----------

    public static async Task CleanAsync(GpuVendor vendor, IReadOnlyList<DriverPackage> packages, CleanupOptions o, Action<string> log)
    {
        if (o.BlockWindowsUpdate)
        {
            SetAutoDriverInstall(false);
            log("✔ Windows Update não vai reinstalar drivers sozinho.");
        }

        if (o.RemoveServices)
        {
            foreach (var svc in Services(vendor))
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                if (key == null) continue;
                await Task.Run(() => ProcessRunner.Sc($"stop \"{svc}\""));
                var code = await Task.Run(() => ProcessRunner.Sc($"delete \"{svc}\""));
                log(code == 0 ? $"✔ Serviço removido: {svc}" : $"✖ Serviço {svc}: sc.exe código {code}");
            }
        }

        log("Removendo dispositivos de vídeo...");
        var devices = await ProcessRunner.PowerShellJsonAsync<PnpDto>(
            """Get-PnpDevice -Class Display, MEDIA -ErrorAction SilentlyContinue | Select-Object @{n='InstanceId';e={"$($_.InstanceId)"}}, @{n='Name';e={"$($_.FriendlyName)"}}, @{n='Manufacturer';e={"$($_.Manufacturer)"}}, @{n='Class';e={"$($_.Class)"}}""");
        foreach (var d in devices.Where(d => IsVendor(vendor, d.Manufacturer + " " + d.Name)))
        {
            if (d.Class.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) && !o.IncludeAudio) continue;
            var (code, _, _) = await ProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{d.InstanceId}\"");
            log(code == 0 ? $"✔ Dispositivo removido: {d.Name}" : $"✖ {d.Name}: pnputil código {code}");
        }

        foreach (var p in packages)
        {
            var (code, output, _) = await ProcessRunner.RunAsync("pnputil.exe", $"/delete-driver {p.Driver} /uninstall /force");
            log(code == 0 ? $"✔ Pacote removido: {p.Driver} ({p.InfName} {p.Version})"
                          : $"✖ {p.Driver}: {output.Trim().Split('\n').LastOrDefault()?.Trim()}");
        }

        if (o.RemoveControlPanel)
        {
            foreach (var app in ControlPanelApps(vendor))
            {
                if (await DebloatService.RemoveAppAsync(app)) log($"✔ Painel removido: {app}");
            }
        }

        if (o.RemoveFolders)
        {
            foreach (var dir in Folders(vendor).Where(Directory.Exists))
            {
                try
                {
                    await Task.Run(() => Directory.Delete(dir, recursive: true));
                    log($"✔ Pasta apagada: {dir}");
                }
                catch (Exception ex)
                {
                    log($"• {dir}: parte ficou (arquivo em uso: {ex.GetType().Name}). Some após reiniciar.");
                }
            }
        }

        log("Concluído. Reinicie o PC para terminar e depois instale o driver novo.");
    }

    private static IEnumerable<string> Services(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => new[] { "NVDisplay.ContainerLocalSystem", "NvContainerLocalSystem", "NvContainerNetworkService", "NvTelemetryContainer", "FvSvc", "nvagent" },
        GpuVendor.Amd => new[] { "AMD External Events Utility", "AUEPLauncher", "amdfendr", "amdfendrmgr", "AMD Crash Defender Service" },
        _ => new[] { "igfxCUIService2.0.0.0", "igccservice", "cplspcon" },
    };

    private static IEnumerable<string> ControlPanelApps(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => new[] { "NVIDIACorp.NVIDIAControlPanel" },
        GpuVendor.Amd => new[] { "AdvancedMicroDevicesInc-2.AMDRadeonSoftware" },
        _ => new[] { "AppUp.IntelGraphicsExperience", "AppUp.IntelGraphicsControlPanel" },
    };

    private static IEnumerable<string> Folders(GpuVendor v)
    {
        string E(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var pf = E(Environment.SpecialFolder.ProgramFiles);
        var pf86 = E(Environment.SpecialFolder.ProgramFilesX86);
        var data = E(Environment.SpecialFolder.CommonApplicationData);
        var local = E(Environment.SpecialFolder.LocalApplicationData);
        var common = new[] { Path.Combine(local, "D3DSCache") }; // cache de shaders do DirectX
        string[] vendor = v switch
        {
            GpuVendor.Nvidia => new[]
            {
                Path.Combine(pf, "NVIDIA Corporation"), Path.Combine(pf86, "NVIDIA Corporation"),
                Path.Combine(data, "NVIDIA Corporation"), Path.Combine(data, "NVIDIA"),
                Path.Combine(local, "NVIDIA"), Path.Combine(local, "NVIDIA Corporation"),
            },
            // Só pastas do driver de vídeo: Program Files\AMD também guarda chipset e Ryzen Master.
            GpuVendor.Amd => new[]
            {
                // O instalador da AMD extrai sempre em <disco do Windows>\AMD.
                Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "AMD"), Path.Combine(pf, "AMD", "CNext"),
                Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "GLCache"), Path.Combine(local, "AMD", "VkCache"), Path.Combine(local, "AMD", "CN"),
            },
            _ => new[] { Path.Combine(local, "Intel", "ShaderCache"), Path.Combine(data, "Intel", "ShaderCache") },
        };
        return vendor.Concat(common);
    }

    // ---------- Windows Update / Modo de Segurança ----------

    public static bool IsAutoDriverInstallBlocked()
    {
        using var k = Registry.LocalMachine.OpenSubKey(DriverSearching);
        return k?.GetValue("SearchOrderConfig") is int v && v == 0;
    }

    public static void SetAutoDriverInstall(bool enabled)
    {
        using var k = Registry.LocalMachine.CreateSubKey(DriverSearching, true);
        k.SetValue("SearchOrderConfig", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public static bool IsSafeMode => GetSystemMetrics(SM_CLEANBOOT) != 0;

    /// <summary>Configura o próximo boot em Modo de Segurança e reinicia.</summary>
    public static async Task<string?> RebootToSafeModeAsync()
    {
        var (code, output, error) = await ProcessRunner.RunAsync("bcdedit.exe", "/set {current} safeboot minimal");
        if (code != 0) return (error.Length > 0 ? error : output).Trim();
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
        return null;
    }

    /// <summary>Volta ao boot normal e reinicia.</summary>
    public static async Task<string?> LeaveSafeModeAsync(bool reboot)
    {
        var (code, output, error) = await ProcessRunner.RunAsync("bcdedit.exe", "/deletevalue {current} safeboot");
        if (code != 0) return (error.Length > 0 ? error : output).Trim();
        if (reboot) Restart();
        return null;
    }

    public static void Restart() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });

    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private sealed class PnpDto
    {
        public string InstanceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Class { get; set; } = "";
    }
}
