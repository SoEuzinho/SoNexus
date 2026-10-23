using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoNexus.Services;

/// <summary>
/// Pasta em %ProgramData%\SoNexus onde só administradores e o Windows podem gravar.
/// Tudo o que o SoNexus executa ou aplica como administrador (instaladores, backups do registro) fica aqui:
/// em %LocalAppData% qualquer programa do usuário poderia trocar o arquivo e ganhar acesso de administrador.
/// </summary>
public static class SecureStore
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoNexus");
    private static bool _secured;

    public static string Folder(string name)
    {
        EnsureRoot();
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string File_(string name)
    {
        EnsureRoot();
        return Path.Combine(Root, name);
    }

    private static void EnsureRoot()
    {
        if (_secured) return;
        var dir = Directory.CreateDirectory(Root);
        try
        {
            // Sem herança: administradores e SYSTEM controlam; usuários comuns só leem.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            dir.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Sem administrador não dá para proteger; quem precisa disso (instalar, aplicar ajustes) já exige admin.
            AppLog.Write($"Pasta protegida: {ex.Message}");
        }
        _secured = true;
    }

    /// <summary>Traz backups antigos de %LocalAppData% (versões anteriores) para a pasta protegida, uma vez.</summary>
    public static void MigrateOldBackups()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus");
            var oldJson = Path.Combine(old, "tweak-backup.json");
            var newJson = File_("tweak-backup.json");
            if (File.Exists(oldJson) && !File.Exists(newJson)) File.Move(oldJson, newJson);
            var oldRegs = Path.Combine(old, "registry-backup");
            if (Directory.Exists(oldRegs))
            {
                var dest = Folder("registry-backup");
                foreach (var f in Directory.GetFiles(oldRegs, "*.reg"))
                {
                    var target = Path.Combine(dest, Path.GetFileName(f));
                    if (!File.Exists(target)) File.Move(f, target);
                }
            }
            foreach (var leftover in new[] { Path.Combine(old, "PawnIO_setup.exe") })
                if (File.Exists(leftover)) File.Delete(leftover);
            var oldDrivers = Path.Combine(old, "Drivers");
            if (Directory.Exists(oldDrivers)) Directory.Delete(oldDrivers, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Migração de backups: {ex.Message}");
        }
    }
}
