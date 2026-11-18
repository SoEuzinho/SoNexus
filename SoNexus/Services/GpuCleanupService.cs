using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed record CleanupOptions(bool IncludeAudio, bool BlockWindowsUpdate, bool RemoveServices, bool RemoveFolders, bool RemoveControlPanel,
    string? BackupDirectory = null);
public sealed record GpuCleanupResult(int Removed, int Failed, bool RestartRequired);

/// <summary>
/// Remove pacotes selecionados do DriverStore e, opcionalmente, componentes gráficos do fabricante.
/// </summary>
public static class GpuCleanupService
{
    private const string DriverSearching = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    private static readonly Regex PublishedInf = new(@"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Name(GpuVendor v) => v switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", _ => "Intel" };

    private static bool IsVendor(GpuVendor v, string text) => v switch
    {
        GpuVendor.Nvidia => text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Amd => text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
                         || text.Equals("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("ATI Technologies", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase),
        _ => text.Contains("Intel", StringComparison.OrdinalIgnoreCase),
    };

    public static List<GpuVendor> DetectInstalled(IEnumerable<GpuDriver> drivers) =>
        drivers
            .SelectMany(g => Enum.GetValues<GpuVendor>().Where(v => IsVendor(v, g.Name) || IsVendor(v, g.Vendor)))
            .Distinct()
            .ToList();

    public static async Task<List<DriverPackage>> GetPackagesAsync(GpuVendor vendor, bool includeAudio)
    {
        var (code, output, error) = await ProcessRunner.PowerShellAsync(
            """$ErrorActionPreference='Stop'; ConvertTo-Json -Compress -InputObject @(Get-WindowsDriver -Online | Select-Object @{n='Driver';e={"$($_.Driver)"}}, @{n='OriginalFileName';e={"$($_.OriginalFileName)"}}, @{n='ClassName';e={"$($_.ClassName)"}}, @{n='ProviderName';e={"$($_.ProviderName)"}}, @{n='Version';e={"$($_.Version)"}})""");
        if (code != 0)
            throw new InvalidOperationException(CommandFailure("DISM", code, output, error));
        var all = JsonSerializer.Deserialize<List<DriverPackage>>(output.Trim())
                  ?? throw new InvalidDataException("O DISM não devolveu uma lista de drivers.");
        return all
            .Where(p => IsEligiblePackage(vendor, p, includeAudio))
            .OrderBy(p => p.ClassName).ThenBy(p => p.Driver)
            .ToList();
    }

    internal static bool IsEligiblePackage(GpuVendor vendor, DriverPackage package, bool includeAudio)
    {
        if (!PublishedInf.IsMatch(package.Driver) || !IsVendor(vendor, package.ProviderName)) return false;
        if (package.ClassName.Equals("Display", StringComparison.OrdinalIgnoreCase)) return true;
        if (!includeAudio || !package.ClassName.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)) return false;
        return vendor switch
        {
            GpuVendor.Nvidia => package.InfName.StartsWith("nvhda", StringComparison.OrdinalIgnoreCase),
            GpuVendor.Amd => package.InfName.StartsWith("atihd", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
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

    public static async Task<GpuCleanupResult> CleanAsync(GpuVendor vendor, IReadOnlyList<DriverPackage> packages, CleanupOptions o, Action<string> log)
    {
        if (!SensorDriver.IsAdmin) throw new InvalidOperationException("Abra o SoNexus como administrador.");
        if (packages.Count == 0) throw new InvalidOperationException("Selecione pelo menos um pacote.");
        var current = await CheckSelectionAsync(vendor, packages, o.IncludeAudio);
        if (o.BackupDirectory is { } backupDirectory)
        {
            await ExportPackagesAsync(vendor, packages, backupDirectory, log);
            current = await CheckSelectionAsync(vendor, packages, o.IncludeAudio);
        }

        if (o.BlockWindowsUpdate)
        {
            SetAutoDriverInstall(false);
            log("✔ Busca automática de drivers desativada em DriverSearching.");
        }

        return await RemovePackagesAsync(vendor, packages, o, current.Count, log);
    }

    private static async Task<Dictionary<string, DriverPackage>> CheckSelectionAsync(
        GpuVendor vendor, IReadOnlyList<DriverPackage> packages, bool includeAudio)
    {
        var current = (await GetPackagesAsync(vendor, includeAudio)).ToDictionary(p => p.Driver, StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            if (!current.TryGetValue(p.Driver, out var actual) ||
                !string.Equals(actual.OriginalFileName, p.OriginalFileName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.ClassName, p.ClassName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.ProviderName, p.ProviderName, StringComparison.OrdinalIgnoreCase) ||
                actual.Version != p.Version)
                throw new InvalidOperationException($"O pacote {p.Driver} mudou. Atualize a lista e revise a seleção.");
        }
        return current;
    }

    private static async Task ExportPackagesAsync(
        GpuVendor vendor, IReadOnlyList<DriverPackage> packages, string directory, Action<string> log)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("A pasta escolhida para o backup não está disponível.");

        var folderName = $"SoNexus-{Name(vendor)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var backup = Directory.CreateDirectory(Path.Combine(directory, folderName));
        log($"Backup dos drivers: {backup.FullName}");
        foreach (var package in packages.DistinctBy(p => p.Driver, StringComparer.OrdinalIgnoreCase))
        {
            var destination = backup.CreateSubdirectory(Path.GetFileNameWithoutExtension(package.Driver));
            var (code, output, error) = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                $"/export-driver {package.Driver} \"{destination.FullName}\"");
            if (code != 0) throw new IOException(CommandFailure("PnPUtil", code, output, error));

            var exported = destination.EnumerateFiles("*.inf", SearchOption.AllDirectories)
                .Any(file => file.Length > 0 && file.Name.Equals(package.InfName, StringComparison.OrdinalIgnoreCase));
            if (!exported)
                throw new IOException($"O arquivo {package.InfName} não apareceu no backup. Remoção cancelada.");
            log($"✔ Backup verificado: {package.Driver} ({package.InfName})");
        }
    }

    public static async Task<string> RestorePackageAsync(string infPath)
    {
        if (!SensorDriver.IsAdmin) throw new InvalidOperationException("Abra o SoNexus como administrador.");
        if (!Path.IsPathFullyQualified(infPath) || !Path.GetExtension(infPath).Equals(".inf", StringComparison.OrdinalIgnoreCase)
            || infPath.Contains('"') || !File.Exists(infPath))
            throw new FileNotFoundException("Selecione um arquivo INF existente dentro do backup.");
        var (code, output, error) = await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"/add-driver \"{infPath}\" /install");
        if (code is not (0 or 3010)) throw new InvalidOperationException(CommandFailure("PnPUtil", code, output, error));
        return code == 3010
            ? "Pacote importado. O Windows solicitou reinício."
            : "Pacote importado. O Windows aplica o driver conforme a compatibilidade e a prioridade do dispositivo.";
    }

    private static async Task<GpuCleanupResult> RemovePackagesAsync(
        GpuVendor vendor, IReadOnlyList<DriverPackage> packages, CleanupOptions o, int availableCount, Action<string> log)
    {
        var removed = 0;
        var failed = 0;
        var restartRequired = false;
        foreach (var p in packages.DistinctBy(p => p.Driver, StringComparer.OrdinalIgnoreCase))
        {
            var (code, output, error) = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"/delete-driver {p.Driver} /uninstall");
            if (code is 0 or 3010)
            {
                removed++;
                restartRequired |= code == 3010;
                log($"✔ Pacote removido: {p.Driver} ({p.InfName}){(code == 3010 ? " · requer reinício" : "")}");
            }
            else
            {
                failed++;
                log($"✖ {p.Driver}: {CommandFailure("PnPUtil", code, output, error)}");
            }
        }

        var allVendorPackagesSelected = availableCount == removed;
        if (failed > 0 || !allVendorPackagesSelected)
        {
            if (o.RemoveServices || o.RemoveControlPanel || o.RemoveFolders)
                log("• Limpeza complementar ignorada porque ainda há pacotes compatíveis fora da remoção concluída.");
            return new(removed, failed, restartRequired);
        }

        if (o.RemoveServices)
        {
            foreach (var svc in Services(vendor))
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                if (key == null) continue;
                var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
                await ProcessRunner.RunAsync(sc, $"stop \"{svc}\"");
                var (code, output, error) = await ProcessRunner.RunAsync(sc, $"delete \"{svc}\"");
                if (code != 0) failed++;
                log(code == 0 ? $"✔ Serviço marcado para remoção: {svc}" : $"✖ Serviço {svc}: {CommandFailure("sc.exe", code, output, error)}");
            }
        }

        if (o.RemoveControlPanel)
        {
            foreach (var app in ControlPanelApps(vendor))
            {
                if (await DebloatService.RemoveAppAsync(app)) log($"✔ Painel ausente após verificação: {app}");
                else { failed++; log($"✖ Não foi possível remover o painel: {app}"); }
            }
        }

        if (o.RemoveFolders)
        {
            foreach (var dir in Folders(vendor).Where(Directory.Exists))
            {
                try
                {
                    await Task.Run(() => ClearCache(dir));
                    log($"✔ Cache limpo: {dir}");
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"✖ Não foi possível limpar todo o cache {dir}: {ex.Message}");
                }
            }
        }

        return new(removed, failed, restartRequired);
    }

    private static string CommandFailure(string command, int code, string output, string error)
    {
        var details = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        return details.Length == 0 ? $"{command} retornou {code}." : $"{command} retornou {code}. {details}";
    }

    private static IEnumerable<string> Services(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => new[] { "NVDisplay.ContainerLocalSystem" },
        GpuVendor.Amd => new[] { "AMD External Events Utility" },
        _ => new[] { "igfxCUIService2.0.0.0", "igccservice" },
    };

    private static IEnumerable<string> ControlPanelApps(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => new[] { "NVIDIACorp.NVIDIAControlPanel" },
        GpuVendor.Amd => new[] { "AdvancedMicroDevicesInc-2.AMDRadeonSoftware" },
        _ => new[] { "AppUp.IntelGraphicsExperience", "AppUp.IntelGraphicsControlPanel" },
    };

    private static IEnumerable<string> Folders(GpuVendor v)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local)) return Array.Empty<string>();
        return v switch
        {
            GpuVendor.Nvidia => new[]
            {
                Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
            },
            GpuVendor.Amd => new[]
            {
                Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "GLCache"), Path.Combine(local, "AMD", "VkCache"),
            },
            _ => new[] { Path.Combine(local, "Intel", "ShaderCache") },
        };
    }

    private static void ClearCache(string path)
    {
        var directory = new DirectoryInfo(path);
        for (DirectoryInfo? parent = directory; parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("O caminho do cache passa por um link ou ponto de montagem.");

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in directory.EnumerateFiles("*", options))
            file.Delete();
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
        var bcdedit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        var (code, output, error) = await ProcessRunner.RunAsync(bcdedit, "/set {current} safeboot minimal");
        if (code != 0) return CommandFailure("BCDEdit", code, output, error);
        var restartError = await RestartAsync();
        if (restartError == null) return null;
        var undo = await ProcessRunner.RunAsync(bcdedit, "/deletevalue {current} safeboot");
        return undo.ExitCode == 0 ? restartError
            : $"{restartError} Também falhou a restauração do modo de inicialização. {CommandFailure("BCDEdit", undo.ExitCode, undo.Output, undo.Error)}";
    }

    /// <summary>Volta ao boot normal e reinicia.</summary>
    public static async Task<string?> LeaveSafeModeAsync(bool reboot)
    {
        var (code, output, error) = await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), "/deletevalue {current} safeboot");
        if (code != 0) return CommandFailure("BCDEdit", code, output, error);
        return reboot ? await RestartAsync() : null;
    }

    public static async Task<string?> RestartAsync()
    {
        try
        {
            var (code, output, error) = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0");
            return code == 0 ? null : CommandFailure("shutdown.exe", code, output, error);
        }
        catch (Exception ex)
        {
            return $"Não foi possível iniciar o reinício: {ex.Message}";
        }
    }

    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

}
