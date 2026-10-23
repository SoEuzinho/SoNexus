using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SoNexus.Services;

/// <summary>Executa PowerShell e utilitários do Windows (sc.exe, pnputil) sem abrir janela.</summary>
public static class ProcessRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<(int ExitCode, string Output, string Error)> PowerShellAsync(string script)
    {
        var full = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        // Caminho completo: em alguns PCs o PATH foi alterado por outros programas.
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) exe = "powershell.exe";
        return await RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}").ConfigureAwait(false);
    }

    /// <summary>Roda um script que produz objetos e devolve-os desserializados como lista.</summary>
    public static async Task<List<T>> PowerShellJsonAsync<T>(string script)
    {
        var (_, output, _) = await PowerShellAsync($"ConvertTo-Json -Compress -Depth 4 -InputObject @({script})").ConfigureAwait(false);
        output = output.Trim();
        if (output.Length == 0) return new();
        return JsonSerializer.Deserialize<List<T>>(output, JsonOptions) ?? new();
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string exe, string args, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        // ConfigureAwait(false): várias partes do app esperam isto de forma síncrona (.GetResult());
        // sem ele, chamado da tela, a continuação esperaria a tela que está parada esperando = app congelado.
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().ConfigureAwait(false);
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static int Sc(string args) => RunAsync("sc.exe", args).GetAwaiter().GetResult().ExitCode;

    /// <summary>Aspas simples do PowerShell.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}

/// <summary>Separa "programa.exe argumentos" e localiza o arquivo executável real.</summary>
public static class CommandLine
{
    public static (string Exe, string Args) Split(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end > 0) return (c[1..end], c[(end + 1)..].Trim());
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (c[..(exe + 4)], c[(exe + 4)..].Trim());
        var space = c.IndexOf(' ');
        return space > 0 ? (c[..space], c[(space + 1)..].Trim()) : (c, "");
    }

    public static string? ResolveImage(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith(@"\??\")) c = c[4..];
        var (exe, args) = Split(c);

        // rundll32 "C:\x\algo.dll",Funcao  ->  o arquivo que importa é a DLL
        if (Path.GetFileName(exe).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
        {
            var dll = args.Split(',')[0].Trim().Trim('"');
            if (dll.Length > 0) exe = dll;
        }

        if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe[12..]);
        if (!Path.IsPathRooted(exe))
        {
            if (!Path.HasExtension(exe)) exe += ".exe";
            exe = Path.Combine(Environment.SystemDirectory, exe);
        }
        return exe;
    }

    public static string Publisher(string? path)
    {
        if (path is null || !File.Exists(path)) return "";
        try { return FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? ""; }
        catch { return ""; }
    }
}
