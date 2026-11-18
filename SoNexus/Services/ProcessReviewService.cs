using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoNexus.Services;

internal static class ProcessReviewService
{
    private static readonly HashSet<string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "dwm.exe", "explorer.exe", "lsass.exe", "services.exe", "smss.exe", "svchost.exe", "wininit.exe", "winlogon.exe",
    };

    internal sealed record Snapshot(HashSet<string> Names, List<NetAlert> Alerts, int Examined, int Unavailable);
    internal sealed record StartupSnapshot(List<NetAlert> Alerts, int Examined, bool TasksAvailable);
    private sealed record StartupCandidate(string Name, string Location, string Path);

    internal static Snapshot InspectProcesses(IReadOnlyList<TcpConnection> connections)
    {
        var connected = connections.Where(c => c.Pid > 0 && c.State == "Conectada").Select(c => c.Pid).ToHashSet();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<NetAlert>();
        var examined = 0;
        var unavailable = 0;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = profile.Length == 0 ? "" : Path.Combine(profile, "Downloads");

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                string? path;
                try
                {
                    name = process.ProcessName;
                    names.Add(name);
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    unavailable++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    unavailable++;
                    continue;
                }
                examined++;

                var signal = ClassifyLocation(path, windows, temp, downloads);
                if (signal == null) continue;
                var trust = TrustedLocally(path);
                var hasConnection = connected.Contains(process.Id);
                var severity = !trust && (signal == "Nome de componente do Windows fora da pasta do sistema" || hasConnection) ? "Média" : "Baixa";
                alerts.Add(new NetAlert(severity, signal,
                    $"{name} (PID {process.Id}) · {path}\n{TrustText(trust)}" +
                    (hasConnection ? " Possui uma conexão TCP ativa." : ""), name));
            }
        }
        return new(names, alerts, examined, unavailable);
    }

    internal static async Task<StartupSnapshot> InspectStartupAsync()
    {
        var entries = await Task.Run(ReadRegistryStartup).ConfigureAwait(false);
        var tasksAvailable = true;
        try
        {
            foreach (var task in await StartupService.LoadTasksAsync(failOnError: true).ConfigureAwait(false))
                if (task.State != "Disabled" && CommandLine.ResolveImage(task.Exec) is { } path)
                    entries.Add(new StartupCandidate(task.Name, "Tarefa agendada", path));
        }
        catch (Exception ex)
        {
            tasksAvailable = false;
            AppLog.Write($"Segurança: consulta de tarefas: {ex.Message}");
        }
        var alerts = await Task.Run(() => InspectStartup(entries)).ConfigureAwait(false);
        return new(alerts, entries.Count, tasksAvailable);
    }

    private static List<StartupCandidate> ReadRegistryStartup()
    {
        var entries = new List<StartupCandidate>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var name in new[] { "Run", "RunOnce" })
            {
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\{name}");
                if (key == null) continue;
                foreach (var value in key.GetValueNames())
                    if (key.GetValue(value) is string command && CommandLine.ResolveImage(command) is { } path)
                        entries.Add(new StartupCandidate(value, $"{hive} · {view} · {name}", path));
            }
        }
        return entries;
    }

    private static List<NetAlert> InspectStartup(IEnumerable<StartupCandidate> entries)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = profile.Length == 0 ? "" : Path.Combine(profile, "Downloads");
        var alerts = new List<NetAlert>();
        foreach (var entry in entries)
        {
            var path = entry.Path;
            var signal = ClassifyLocation(path, windows, temp, downloads);
            if (signal == null) continue;
            var trust = TrustedLocally(path);
            alerts.Add(new NetAlert(trust ? "Baixa" : "Média", $"Inicialização · {entry.Name}",
                $"{entry.Location} · {path}\n{signal}. {TrustText(trust)}", entry.Name));
        }
        return alerts;
    }

    internal static string? ClassifyLocation(string path, string windows, string temp, string downloads)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        if (WindowsNames.Contains(Path.GetFileName(path)) && !IsUnder(path, windows))
            return "Nome de componente do Windows fora da pasta do sistema";
        if (IsUnder(path, temp)) return "Executável em pasta temporária";
        if (IsUnder(path, downloads)) return "Executável em Downloads";
        return null;
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length > 0 && path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool TrustedLocally(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var file = new FileInfo(Path.GetFullPath(path));
            if (new DriveInfo(file.Directory!.Root.FullName).DriveType != DriveType.Fixed) return false;
            var parents = new Stack<DirectoryInfo>();
            for (var folder = file.Directory; folder != null; folder = folder.Parent) parents.Push(folder);
            foreach (var folder in parents)
                if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                Authenticode.IsTrusted(file.FullName, cachedOnly: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string TrustText(bool trusted) =>
        trusted ? "Assinatura aceita pela verificação local." : "Assinatura não confirmada pela verificação local.";
}
