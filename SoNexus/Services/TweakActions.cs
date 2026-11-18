using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SoNexus.Services;

public enum Risk { Seguro, Moderado }

public enum Restart { Nenhum, Explorer, Windows }

/// <summary>Uma mudança reversível no sistema (valor de registro, serviço, recurso do Windows...).</summary>
public abstract class TweakAction
{
    public virtual bool IsAvailable => true;
    /// <summary>O SoNexus guardou o valor original (foi ele quem aplicou): dá para desfazer com segurança.</summary>
    public virtual bool HasBackup => false;
    public abstract bool IsApplied();
    public abstract void Apply();
    public abstract void Revert();
}

public sealed class Tweak
{
    private static readonly object OperationGate = new();
    /// <summary>Identificador estável (usado para exportar/importar as escolhas).</summary>
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public Risk Risk { get; init; } = Risk.Seguro;
    public Restart Restart { get; init; } = Restart.Nenhum;
    /// <summary>Faz parte do "padrão recomendado".</summary>
    public bool Recommended { get; init; }
    public required TweakAction[] Actions { get; init; }

    public bool IsAvailable => Actions.Any(a => a.IsAvailable);
    public bool IsApplied => IsAvailable && Actions.Where(a => a.IsAvailable).All(a => a.IsApplied());

    /// <summary>Aplicado pelo SoNexus (tem backup do original). Ajustes que a pessoa fez por conta própria ficam de fora.</summary>
    public bool AppliedBySoNexus => Actions.Any(a => a.HasBackup);

    public string Note => Actions.Any(action => action is OptionalFeature)
        ? "reinicie o PC · dependências podem permanecer após reverter"
        : Restart switch
        {
            Restart.Explorer => "reinicie o Explorer",
            Restart.Windows => "requer reiniciar o PC",
            _ => "",
        };

    public void Apply()
    {
        lock (OperationGate)
            foreach (var a in Actions.Where(a => a.IsAvailable && !a.IsApplied())) a.Apply();
    }

    public void Revert()
    {
        lock (OperationGate)
            foreach (var a in Actions.Where(a => a.IsAvailable)) a.Revert();
    }

    public void RevertOriginal()
    {
        lock (OperationGate)
        {
            var actions = Actions.Where(a => a.HasBackup).Reverse().ToList();
            if (actions.Count == 0) throw new InvalidOperationException("Nenhuma ação tem backup do valor original.");
            var failures = new List<Exception>();
            foreach (var action in actions)
            {
                try { action.Revert(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException("Falhou a restauração de uma ou mais ações.", failures);
        }
    }
}

internal static class Reg
{
    private static string? _desktopUserSid;
    private static bool _sidChecked;

    /// <summary>
    /// Conta que está usando a área de trabalho (dona do explorer.exe). Quando um usuário comum abre o SoNexus
    /// com a senha de OUTRA conta de administrador, o HKCU do processo é o do administrador; aí os ajustes
    /// "do usuário" iriam para a conta errada. Retorna null quando é a mesma conta (caso normal).
    /// </summary>
    private static string? DesktopUserSid()
    {
        if (_sidChecked) return _desktopUserSid;
        _sidChecked = true;
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            using var s = new System.Management.ManagementObjectSearcher("SELECT Handle FROM Win32_Process WHERE Name='explorer.exe'");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                using (o)
                {
                    var r = o.InvokeMethod("GetOwnerSid", null, null);
                    var sid = r?["Sid"]?.ToString();
                    if (string.IsNullOrEmpty(sid)) continue;
                    using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                    using var hive = users.OpenSubKey(sid);
                    if (sid != me && hive != null) _desktopUserSid = sid;
                    break;
                }
            }
        }
        catch { }
        return _desktopUserSid;
    }

    private static (RegistryHive Hive, string Path) Map(RegistryHive hive, string path) =>
        hive == RegistryHive.CurrentUser && DesktopUserSid() is { } sid ? (RegistryHive.Users, $@"{sid}\{path}") : (hive, path);

    public static RegistryKey? Open(RegistryHive hive, string path, bool writable = false)
    {
        (hive, path) = Map(hive, path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(path, writable);
    }

    public static RegistryKey Create(RegistryHive hive, string path)
    {
        (hive, path) = Map(hive, path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.CreateSubKey(path, true);
    }

    public static void DeleteTree(RegistryHive hive, string path)
    {
        (hive, path) = Map(hive, path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public static void DeleteEmptyKey(RegistryHive hive, string path)
    {
        (hive, path) = Map(hive, path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using (var key = root.OpenSubKey(path))
        {
            if (key == null || key.SubKeyCount != 0 || key.ValueCount != 0) return;
        }
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    /// <summary>Caminho completo para o reg.exe (HKCU vira HKU\SID quando for outra conta).</summary>
    public static string FullName(RegistryHive hive, string path)
    {
        (hive, path) = Map(hive, path);
        return $@"{HiveName(hive)}\{path}";
    }

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => hive.ToString(),
    };
}

/// <summary>
/// Guarda o valor que existia antes de cada ajuste, para "Reverter" voltar exatamente ao estado original do PC.
/// Fica em %LocalAppData%\SoNexus\tweak-backup.json.
/// </summary>
internal static class TweakBackup
{
    private sealed class Entry
    {
        public bool Existed { get; set; }
        public string Kind { get; set; } = "";
        public string Data { get; set; } = "";
    }

    // Pasta protegida (só administradores gravam): estes valores voltam para o registro do sistema ao "Reverter".
    private static string FilePath => SecureStore.File_("tweak-backup.json");
    private static readonly object Sync = new();
    private static Dictionary<string, Entry>? _entries;

    public static string KeyFolder => SecureStore.Folder("registry-backup");

    private static Dictionary<string, Entry> Entries()
    {
        return _entries ??= File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("O arquivo de backup dos ajustes está inválido.")
            : new();
    }

    private static void Commit(Dictionary<string, Entry> entries)
    {
        var path = FilePath;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, overwrite: true);
            _entries = entries;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Registra o valor atual, só na primeira vez (assim aplicar duas vezes não perde o original).</summary>
    public static void Remember(string id, object? current, RegistryValueKind kind)
    {
        lock (Sync)
        {
            if (Entries().ContainsKey(id)) return;
            var next = new Dictionary<string, Entry>(Entries());
            next[id] = current switch
            {
                null => new Entry { Existed = false },
                byte[] b => new Entry { Existed = true, Kind = "Binary", Data = Convert.ToBase64String(b) },
                int i => new Entry { Existed = true, Kind = "DWord", Data = i.ToString() },
                long l => new Entry { Existed = true, Kind = "QWord", Data = l.ToString() },
                string[] items => new Entry { Existed = true, Kind = "MultiString", Data = JsonSerializer.Serialize(items) },
                _ => new Entry { Existed = true, Kind = kind == RegistryValueKind.ExpandString ? "ExpandString" : "String", Data = current.ToString() ?? "" },
            };
            Commit(next);
        }
    }

    public static bool Contains(string id)
    {
        lock (Sync) return Entries().ContainsKey(id);
    }

    /// <summary>Só descarta o backup depois que a restauração termina sem erro.</summary>
    public static bool TryRestore(string id, Action<bool, object?, RegistryValueKind> restore)
    {
        lock (Sync)
        {
            if (!Entries().TryGetValue(id, out var e)) return false;
            (object? value, RegistryValueKind kind) = e.Kind switch
            {
                "Binary" => ((object?)Convert.FromBase64String(e.Data), RegistryValueKind.Binary),
                "DWord" => (int.Parse(e.Data), RegistryValueKind.DWord),
                "QWord" => (long.Parse(e.Data), RegistryValueKind.QWord),
                "MultiString" => (JsonSerializer.Deserialize<string[]>(e.Data), RegistryValueKind.MultiString),
                "ExpandString" => (e.Data, RegistryValueKind.ExpandString),
                _ => (e.Data, RegistryValueKind.String),
            };
            restore(e.Existed, value, kind);
            var next = new Dictionary<string, Entry>(Entries());
            next.Remove(id);
            Commit(next);
            return true;
        }
    }
}

/// <summary>
/// Define um valor do registro: int = DWORD, string = texto, byte[] = binário.
/// Ao reverter, volta o valor original salvo; sem backup, usa <paramref name="defaultValue"/> (null = apagar o valor).
/// </summary>
public sealed class RegValue(RegistryHive hive, string path, string name, object value, object? defaultValue, bool needsKey = false) : TweakAction
{
    private string Id => $@"{Reg.HiveName(hive)}\{path}\{name}";

    public override bool HasBackup => TweakBackup.Contains(Id);

    /// <summary>Chamado depois de gravar, para a mudança valer na hora (ex.: avisar o Windows).</summary>
    public Action? AfterChange { get; init; }

    public override bool IsAvailable
    {
        get
        {
            try
            {
                using var k = Reg.Open(hive, path);
                return !needsKey || k != null;
            }
            catch (System.Security.SecurityException)
            {
                return false; // chave protegida (precisa de administrador)
            }
        }
    }

    public override bool IsApplied()
    {
        using var k = Reg.Open(hive, path);
        var current = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            int i => current is int c && c == i,
            string s => current is string c && c == s,
            byte[] b => current is byte[] c && c.SequenceEqual(b),
            _ => false,
        };
    }

    public override void Apply()
    {
        using (var k = Reg.Open(hive, path))
        {
            var current = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = current != null ? k!.GetValueKind(name) : RegistryValueKind.Unknown;
            TweakBackup.Remember(Id, current, kind);
        }
        Write(value, KindOf(value));
    }

    public override void Revert()
    {
        if (TweakBackup.TryRestore(Id, (existed, original, kind) =>
            {
                if (existed && original != null) Write(original, kind);
                else Delete();
            })) return;
        if (defaultValue is null) Delete();
        else Write(defaultValue, KindOf(defaultValue));
    }

    private static RegistryValueKind KindOf(object v) => v switch
    {
        int => RegistryValueKind.DWord,
        byte[] => RegistryValueKind.Binary,
        _ => RegistryValueKind.String,
    };

    private void Write(object v, RegistryValueKind kind)
    {
        using (var k = Reg.Create(hive, path)) k.SetValue(name, v, kind);
        AfterChange?.Invoke();
    }

    private void Delete()
    {
        using (var k = Reg.Open(hive, path, writable: true)) k?.DeleteValue(name, throwOnMissingValue: false);
        AfterChange?.Invoke();
    }
}

/// <summary>Cria uma chave (aplicado) ou apaga a árvore dela (padrão).</summary>
public sealed class RegKey(RegistryHive hive, string path, string deletePath) : TweakAction
{
    private string Id => $"Key|{Reg.FullName(hive, path)}";
    public override bool HasBackup => TweakBackup.Contains(Id);

    public override bool IsApplied()
    {
        using var k = Reg.Open(hive, path);
        return k != null;
    }

    public override void Apply()
    {
        using (var parent = Reg.Open(hive, deletePath))
            TweakBackup.Remember(Id, parent != null ? 1 : 0, RegistryValueKind.DWord);
        using var k = Reg.Create(hive, path);
        k.SetValue("", "");
    }

    public override void Revert()
    {
        if (TweakBackup.TryRestore(Id, (_, original, _) =>
            {
                using (var key = Reg.Open(hive, path, writable: true))
                    if (key?.GetValue("") is string text && text.Length == 0)
                        key.DeleteValue("", throwOnMissingValue: false);
                Reg.DeleteEmptyKey(hive, path);
                if (original is 0) Reg.DeleteEmptyKey(hive, deletePath);
            })) return;
        Reg.DeleteTree(hive, deletePath);
    }
}

/// <summary>
/// Remove chaves do registro (ex.: itens do menu de contexto). Antes, exporta cada uma para um .reg,
/// e "Reverter" importa de volta.
/// </summary>
public sealed class RegKeyRemove(string id, params (RegistryHive Hive, string Path)[] keys) : TweakAction
{
    private string BackupFile(int i) => Path.Combine(TweakBackup.KeyFolder, $"{id}-{i}.reg");

    public override bool HasBackup => keys.Select((_, i) => BackupFile(i)).Any(File.Exists);

    private bool Exists((RegistryHive Hive, string Path) key)
    {
        using var k = Reg.Open(key.Hive, key.Path);
        return k != null;
    }

    public override bool IsAvailable => keys.Any(Exists) || keys.Select((_, i) => BackupFile(i)).Any(File.Exists);

    public override bool IsApplied() => !keys.Any(Exists);

    public override void Apply()
    {
        Directory.CreateDirectory(TweakBackup.KeyFolder);
        for (var i = 0; i < keys.Length; i++)
        {
            var (hive, path) = keys[i];
            if (!Exists(keys[i])) continue;
            var backup = BackupFile(i);
            if (!File.Exists(backup))
            {
                var temporary = $"{backup}.{Guid.NewGuid():N}.tmp";
                try
                {
                    var (code, _, err) = ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "reg.exe"),
                        $"export \"{Reg.FullName(hive, path)}\" \"{temporary}\" /y").GetAwaiter().GetResult();
                    if (code != 0 || !File.Exists(temporary))
                        throw new InvalidOperationException($"não foi possível fazer backup de {path}: {err.Trim()}");
                    File.Move(temporary, backup);
                }
                finally
                {
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            Reg.DeleteTree(hive, path);
        }
    }

    public override void Revert()
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (!File.Exists(BackupFile(i))) continue;
            var backup = BackupFile(i);
            var (code, _, err) = ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "reg.exe"),
                $"import \"{backup}\"").GetAwaiter().GetResult();
            if (code != 0 || !Exists(keys[i]))
                throw new InvalidOperationException($"não foi possível restaurar {keys[i].Path}: {err.Trim()} (código {code}).");
            File.Delete(backup);
        }
    }
}

/// <summary>Desativa um serviço. defaultStart: auto, demand ou delayed-auto.</summary>
public sealed class ServiceDisable(string service, string defaultStart) : TweakAction
{
    private sealed record Snapshot(string Mode, bool? WasRunning);
    private string KeyPath => $@"SYSTEM\CurrentControlSet\Services\{service}";
    private string BackupId => $"Service|{service}";
    public override bool HasBackup => TweakBackup.Contains(BackupId);

    public override bool IsAvailable
    {
        get
        {
            using var k = Reg.Open(RegistryHive.LocalMachine, KeyPath);
            return k != null;
        }
    }

    public override bool IsApplied()
    {
        using var k = Reg.Open(RegistryHive.LocalMachine, KeyPath);
        return k?.GetValue("Start") is int start && start == 4;
    }

    public override void Apply()
    {
        using (var key = Reg.Open(RegistryHive.LocalMachine, KeyPath))
        {
            if (key?.GetValue("Start") is not int start)
                throw new InvalidOperationException($"Não foi possível ler a inicialização original de {service}.");
            var delayed = key.GetValue("DelayedAutoStart") is 1;
            var original = start switch
            {
                0 => "boot", 1 => "system", 2 => delayed ? "delayed-auto" : "auto",
                3 => "demand", 4 => "disabled", _ => null,
            };
            if (original == null) throw new InvalidOperationException($"Tipo de inicialização desconhecido para {service}.");
            TweakBackup.Remember(BackupId, JsonSerializer.Serialize(new Snapshot(original, Running())), RegistryValueKind.String);
        }
        Check(ProcessRunner.Sc($"config {service} start= disabled"));
        var stop = ProcessRunner.Sc($"stop {service}");
        if (stop != 1062) Check(stop); // serviço já parado
    }

    public override void Revert()
    {
        if (TweakBackup.TryRestore(BackupId, (_, original, _) =>
            {
                if (original is not string saved || JsonSerializer.Deserialize<Snapshot>(saved) is not { } snapshot ||
                    snapshot.Mode is not ("boot" or "system" or "auto" or "delayed-auto" or "demand" or "disabled"))
                    throw new InvalidDataException($"Backup inválido para {service}.");
                Check(ProcessRunner.Sc($"config {service} start= {snapshot.Mode}"));
                if (snapshot.WasRunning == true)
                {
                    var start = ProcessRunner.Sc($"start {service}");
                    if (start != 1056) Check(start); // serviço já em execução
                }
            })) return;
        Check(ProcessRunner.Sc($"config {service} start= {defaultStart}"));
    }

    private bool? Running()
    {
        try
        {
            using var query = new System.Management.ManagementObjectSearcher($"SELECT Started FROM Win32_Service WHERE Name='{service}'");
            foreach (System.Management.ManagementObject result in query.Get())
            {
                using (result) return result["Started"] as bool?;
            }
        }
        catch (Exception ex) { AppLog.Write($"Estado do serviço {service}: {ex.Message}"); }
        return null;
    }

    private void Check(int code)
    {
        if (code != 0) throw new InvalidOperationException($"sc.exe falhou no serviço {service} (código {code}).");
    }
}

/// <summary>Liga um recurso opcional do Windows (Sandbox, WSL...). "Reverter" desliga.</summary>
public sealed class OptionalFeature(params string[] features) : TweakAction
{
    private static readonly Dictionary<string, (string State, DateTimeOffset CheckedAt)> States = new();
    private static string BackupId(string feature) => $"Feature|{feature}";
    public override bool HasBackup => features.Any(feature => TweakBackup.Contains(BackupId(feature)));

    private string State(string feature)
    {
        lock (States)
        {
            if (States.TryGetValue(feature, out var saved) && DateTimeOffset.UtcNow - saved.CheckedAt < TimeSpan.FromSeconds(10))
                return saved.State;
            var (code, output, _) = ProcessRunner.PowerShellAsync(
                $"(Get-WindowsOptionalFeature -Online -FeatureName {ProcessRunner.Quote(feature)} -ErrorAction Stop).State").GetAwaiter().GetResult();
            var state = code == 0 ? output.Trim() : "";
            States[feature] = (state, DateTimeOffset.UtcNow);
            return state;
        }
    }

    public override bool IsAvailable => features.All(f => State(f).Length > 0);

    // "EnablePending" = ativado, só falta reiniciar o PC.
    public override bool IsApplied() => features.All(f => State(f) is "Enabled" or "EnablePending");

    public override void Apply()
    {
        var toEnable = new List<string>();
        foreach (var feature in features)
        {
            var original = State(feature);
            if (original is "Enabled" or "EnablePending") continue;
            if (original is not ("Disabled" or "DisabledWithPayloadRemoved"))
                throw new InvalidOperationException($"Estado de {feature}: {original}. Verifique se há reinício pendente.");
            TweakBackup.Remember(BackupId(feature), original, RegistryValueKind.String);
            toEnable.Add(feature);
        }
        foreach (var feature in toEnable)
            Run(feature, "Enable-WindowsOptionalFeature", "-All");
    }

    public override void Revert()
    {
        if (!HasBackup)
        {
            foreach (var feature in features.Reverse()) Run(feature, "Disable-WindowsOptionalFeature", "");
            return;
        }
        foreach (var feature in features.Reverse())
            TweakBackup.TryRestore(BackupId(feature), (_, original, _) =>
            {
                if (original is not ("Disabled" or "DisabledWithPayloadRemoved"))
                    throw new InvalidDataException($"Backup inválido para {feature}.");
                if (!Equals(State(feature), original))
                    Run(feature, "Disable-WindowsOptionalFeature", original is "DisabledWithPayloadRemoved" ? "-Remove" : "");
            });
    }

    private static void Run(string feature, string cmd, string extra)
    {
        var (code, _, err) = ProcessRunner.PowerShellAsync(
            $"{cmd} -Online -FeatureName {ProcessRunner.Quote(feature)} {extra} -NoRestart -ErrorAction Stop | Out-Null")
            .GetAwaiter().GetResult();
        lock (States) States.Clear(); // -All pode ativar dependências consultadas antes
        if (code != 0) throw new InvalidOperationException($"{feature}: {err.Trim()} (código {code}).");
    }
}
