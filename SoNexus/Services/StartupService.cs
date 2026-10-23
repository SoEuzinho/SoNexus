using System.IO;
using System.Management;
using Microsoft.Win32;
using SoNexus.Models;

namespace SoNexus.Services;

public enum StartupKind { Registro, Pasta, Tarefa, Servico }

/// <summary>Um programa que inicia sozinho com o Windows.</summary>
public sealed class StartupEntry : ObservableObject
{
    private bool _enabled;

    public required StartupKind Kind { get; init; }
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    public string? ImagePath { get; init; }
    public string Publisher { get; init; } = "";
    public bool FileExists { get; init; }
    public bool CanToggle { get; init; } = true;

    public bool IsMicrosoft => Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public string Status => FileExists ? "" : "Arquivo não encontrado";
    public string KindName => Kind switch
    {
        StartupKind.Registro => "Registro",
        StartupKind.Pasta => "Pasta Inicializar",
        StartupKind.Tarefa => "Tarefa agendada",
        _ => "Serviço",
    };

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // Dados para ativar/desativar
    internal RegistryHive ApprovedHive { get; init; }
    internal string? ApprovedKey { get; init; }
    internal string? ApprovedValue { get; init; }
    internal string? TaskPath { get; init; }
    internal string? ServiceName { get; init; }

    public void ResyncEnabled() => Raise(nameof(Enabled));
}

public sealed class TaskInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string State { get; set; } = "";
    public string Exec { get; set; } = "";
    public string Args { get; set; } = "";
    public string Author { get; set; } = "";
    public bool Hidden { get; set; }

    public string Command => $"{Exec} {Args}".Trim();
    public string StateName => State switch
    {
        "Ready" => "Pronta",
        "Running" => "Executando",
        "Disabled" => "Desativada",
        _ => State,
    };
}

/// <summary>Lista tudo que inicia com o Windows, no estilo do Autoruns.</summary>
public static class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    // "approved" = subchave de StartupApproved usada pelo Gerenciador de Tarefas (null = não dá para desativar)
    private static readonly (RegistryHive Hive, RegistryView View, string Path, string? Approved, string Label)[] RunKeys =
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, RunPath, "Run", @"HKCU\...\Run"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, RunOncePath, null, @"HKCU\...\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, "Run", @"HKLM\...\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, RunPath, "Run32", @"HKLM\...\WOW6432Node\...\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, RunOncePath, null, @"HKLM\...\RunOnce"),
    };

    public static async Task<List<StartupEntry>> LoadAsync()
    {
        var list = new List<StartupEntry>();
        var tasks = LoadTasksAsync();
        await Task.Run(() =>
        {
            AddRunKeys(list);
            AddStartupFolders(list);
            AddServices(list);
        });
        foreach (var t in await tasks)
        {
            var image = CommandLine.ResolveImage(t.Exec);
            list.Add(new StartupEntry
            {
                Kind = StartupKind.Tarefa,
                Name = t.Name,
                Command = t.Command,
                Location = t.Path,
                ImagePath = image,
                FileExists = image != null && File.Exists(image),
                Publisher = CommandLine.Publisher(image) is { Length: > 0 } pub ? pub : t.Author,
                Enabled = t.State != "Disabled",
                TaskPath = t.Path,
            });
        }
        return list;
    }

    public static Task<List<TaskInfo>> LoadTasksAsync() =>
        ProcessRunner.PowerShellJsonAsync<TaskInfo>("""
            Get-ScheduledTask | ForEach-Object {
              $a = @($_.Actions | Where-Object { $_.Execute })[0]
              if ($a) { [pscustomobject]@{ Name="$($_.TaskName)"; Path="$($_.TaskPath)"; State="$($_.State)"; Exec="$($a.Execute)"; Args="$($a.Arguments)"; Author="$($_.Author)"; Hidden=[bool]$_.Settings.Hidden } }
            }
            """);

    public static async Task<string?> SetEnabledAsync(StartupEntry e, bool enabled)
    {
        try
        {
            switch (e.Kind)
            {
                case StartupKind.Registro or StartupKind.Pasta:
                    SetApproved(e.ApprovedHive, e.ApprovedKey!, e.ApprovedValue!, enabled);
                    return null;

                case StartupKind.Tarefa:
                    var cmd = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
                    var (code, _, err) = await ProcessRunner.PowerShellAsync(
                        $"{cmd} -TaskName {ProcessRunner.Quote(e.Name)} -TaskPath {ProcessRunner.Quote(e.TaskPath!)} -ErrorAction Stop | Out-Null");
                    return code == 0 ? null : err.Trim().Split('\n')[0];

                default:
                    var start = enabled ? "auto" : "disabled";
                    var sc = await Task.Run(() => ProcessRunner.Sc($"config \"{e.ServiceName}\" start= {start}"));
                    return sc == 0 ? null : $"sc.exe retornou o código {sc}.";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------- Registro ----------

    private static void AddRunKeys(List<StartupEntry> list)
    {
        foreach (var (hive, view, path, approved, label) in RunKeys)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            if (key == null) continue;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                var command = key.GetValue(name)?.ToString() ?? "";
                var image = CommandLine.ResolveImage(command);
                list.Add(new StartupEntry
                {
                    Kind = StartupKind.Registro,
                    Name = name,
                    Command = command,
                    Location = label,
                    ImagePath = image,
                    FileExists = image != null && File.Exists(image),
                    Publisher = CommandLine.Publisher(image),
                    CanToggle = approved != null,
                    Enabled = approved == null || IsApproved(hive, approved, name),
                    ApprovedHive = hive,
                    ApprovedKey = approved,
                    ApprovedValue = name,
                });
            }
        }
    }

    private static void AddStartupFolders(List<StartupEntry> list)
    {
        var folders = new[]
        {
            (Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "Inicializar (usuário)"),
            (Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "Inicializar (todos)"),
        };
        foreach (var (folder, hive, label) in folders)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var (target, args) = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : (file, "");
                list.Add(new StartupEntry
                {
                    Kind = StartupKind.Pasta,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = $"\"{target}\" {args}".Trim(),
                    Location = label,
                    ImagePath = target,
                    FileExists = File.Exists(target) || Directory.Exists(target),
                    Publisher = CommandLine.Publisher(target),
                    Enabled = IsApproved(hive, "StartupFolder", fileName),
                    ApprovedHive = hive,
                    ApprovedKey = "StartupFolder",
                    ApprovedValue = fileName,
                });
            }
        }
    }

    /// <summary>Mesma regra do Gerenciador de Tarefas: 1º byte par = ativo, ímpar = desativado.</summary>
    private static bool IsApproved(RegistryHive hive, string sub, string name)
    {
        using var k = Reg.Open(hive, $@"{ApprovedRoot}\{sub}");
        return k?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
    }

    private static void SetApproved(RegistryHive hive, string sub, string name, bool enabled)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        using var k = Reg.Create(hive, $@"{ApprovedRoot}\{sub}");
        k.SetValue(name, data, RegistryValueKind.Binary);
    }

    private static (string Target, string Args) ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return (lnk, "");
            dynamic shell = Activator.CreateInstance(type)!;
            var shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            string args = shortcut.Arguments;
            return (string.IsNullOrEmpty(target) ? lnk : target, args);
        }
        catch
        {
            return (lnk, "");
        }
    }

    // ---------- Serviços ----------

    private static void AddServices(List<StartupEntry> list)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service WHERE StartMode='Auto' OR StartMode='Disabled'");
            foreach (ManagementObject s in searcher.Get())
            {
                using (s)
                {
                    var command = s["PathName"]?.ToString() ?? "";
                    var image = CommandLine.ResolveImage(command);
                    var mode = s["StartMode"]?.ToString();
                    list.Add(new StartupEntry
                    {
                        Kind = StartupKind.Servico,
                        Name = s["DisplayName"]?.ToString() ?? s["Name"]?.ToString() ?? "",
                        Command = command,
                        Location = $@"Serviço: {s["Name"]}",
                        ImagePath = image,
                        FileExists = image != null && File.Exists(image),
                        Publisher = CommandLine.Publisher(image),
                        Enabled = mode == "Auto",
                        ServiceName = s["Name"]?.ToString(),
                    });
                }
            }
        }
        catch
        {
            // WMI indisponível: segue sem serviços
        }
    }
}
