using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record InstalledProgram(
    string Name, string Version, string Publisher, string InstallDate, string Size,
    string? HiddenReason, string UninstallString)
{
    public bool Hidden => HiddenReason != null;
    public string Visibility => HiddenReason is null ? "Visível" : $"Oculto · {HiddenReason}";
}

public sealed class GhostDevice
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string InstanceId { get; set; } = "";

    public string Display => Name.Length > 0 ? Name : InstanceId;
}

/// <summary>Coisas instaladas que o Windows não mostra nas telas normais.</summary>
public static class HiddenItemsService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Todos os programas do registro, inclusive os que "Aplicativos instalados" esconde.</summary>
    public static List<InstalledProgram> GetPrograms()
    {
        var sources = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in sources)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall == null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || name.Trim().Length == 0) continue;

                var uninstallString = k.GetValue("UninstallString") as string ?? "";
                var releaseType = k.GetValue("ReleaseType") as string ?? "";
                string? reason = null;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) reason = "componente do sistema";
                else if (k.GetValue("ParentKeyName") != null || releaseType.Contains("Update") || releaseType.Contains("Hotfix")) reason = "atualização";
                else if (uninstallString.Length == 0) reason = "sem desinstalador";

                var size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? Format.Bytes((ulong)kb * 1024) : "";
                list.Add(new InstalledProgram(
                    name.Trim(),
                    k.GetValue("DisplayVersion") as string ?? "",
                    (k.GetValue("Publisher") as string ?? "").Trim(),
                    FormatDate(k.GetValue("InstallDate") as string),
                    size,
                    reason,
                    uninstallString));
            }
        }
        return list
            .DistinctBy(p => (p.Name, p.Version))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Abre o desinstalador do próprio programa.</summary>
    public static void Uninstall(InstalledProgram p)
    {
        var (exe, args) = CommandLine.Split(Environment.ExpandEnvironmentVariables(p.UninstallString));
        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            args = args.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
    }

    /// <summary>Dispositivos que já foram conectados mas não estão mais presentes (drivers antigos).</summary>
    public static Task<List<GhostDevice>> GetGhostDevicesAsync() =>
        ProcessRunner.PowerShellJsonAsync<GhostDevice>("""
            Get-PnpDevice | Where-Object { -not $_.Present } |
              Select-Object @{n='Name';e={"$($_.FriendlyName)"}}, @{n='Class';e={"$($_.Class)"}}, @{n='InstanceId';e={"$($_.InstanceId)"}}
            """);

    public static async Task<string?> RemoveDeviceAsync(GhostDevice d)
    {
        var (code, output, error) = await ProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{d.InstanceId}\"");
        if (code == 0) return null;
        var msg = (error.Length > 0 ? error : output).Trim();
        return msg.Length > 0 ? msg.Split('\n').Last().Trim() : $"pnputil retornou o código {code}.";
    }

    private static string FormatDate(string? yyyymmdd) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy")
            : "";
}
