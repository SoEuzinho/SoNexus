using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace SoNexus.Services;

/// <summary>
/// Driver PawnIO: o LibreHardwareMonitor precisa dele para ler temperatura/consumo da CPU, tensões
/// e ventoinhas da placa-mãe. É assinado e não é bloqueado pelo Windows Defender
/// (ao contrário do antigo WinRing0). O instalador oficial vem embutido no SoNexus.
/// </summary>
public static class SensorDriver
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static bool IsInstalled
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey(Uninstall);
            using var w = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return k != null || w != null;
        }
    }

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Extrai e roda o instalador oficial. Retorna o código de saída (0 = ok, 3010 = precisa reiniciar).</summary>
    public static async Task<int> InstallAsync()
    {
        // Pasta protegida + assinatura conferida + arquivo travado contra escrita enquanto roda:
        // ninguém consegue trocar o instalador entre extrair e executar como administrador.
        var setup = Path.Combine(SecureStore.Folder("Setup"), "PawnIO_setup.exe");
        await using (var resource = typeof(SensorDriver).Assembly.GetManifestResourceStream("SoNexus.PawnIO_setup.exe")
                                    ?? throw new InvalidOperationException("Instalador do PawnIO não encontrado no SoNexus."))
        await using (var file = File.Create(setup))
        {
            await resource.CopyToAsync(file);
        }
        if (Authenticode.Verify(setup, "namazso") is { } problem)
            throw new InvalidOperationException($"O instalador do driver não passou na verificação de segurança: {problem}.");
        await using var guard = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var p = Process.Start(new ProcessStartInfo(setup, "-install") { UseShellExecute = true })!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Reabre o SoNexus para os sensores carregarem com o driver novo.</summary>
    public static void RestartApp()
    {
        // Solta a trava de cópia única antes, senão a cópia nova só "acordaria" esta e fecharia.
        ((App)System.Windows.Application.Current).ReleaseInstance();
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        App.ExitCompletely();
    }
}

/// <summary>Arquivo de registro em %LocalAppData%\SoNexus\sonexus.log, para descobrir o que falhou.</summary>
public static class AppLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "sonexus.log");
    private static readonly object Lock = new();

    public static string Path_ => FilePath;

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
