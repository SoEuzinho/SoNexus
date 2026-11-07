using System.IO;

namespace SoNexus.Services;

/// <summary>Um lugar que dá para limpar: só arquivos temporários e caches que os programas recriam sozinhos.</summary>
public sealed record CleanupTarget(string Name, string Detail, string[] Folders, TimeSpan MinAge)
{
    public long Bytes { get; set; }
    public int Files { get; set; }
}

/// <summary>
/// Limpeza de temporários e caches (pasta Temp do usuário e do Windows, cache de shaders de NVIDIA/AMD/DirectX).
/// Nunca toca em documentos, downloads ou Lixeira. Arquivo em uso é pulado.
/// </summary>
public static class CleanupService
{
    public static List<CleanupTarget> Targets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new()
        {
            new("Temporários do usuário", "Arquivos que programas deixaram para trás (mais de 1 dia).",
                new[] { Path.GetTempPath() }, TimeSpan.FromDays(1)),
            new("Temporários do Windows", "Restos de instalações e atualizações (mais de 1 dia).",
                new[] { Path.Combine(windows, "Temp") }, TimeSpan.FromDays(1)),
            new("Cache de shaders", "NVIDIA, AMD e DirectX recriam sozinhos. O 1º jogo depois pode engasgar uns minutos enquanto recria. Bom depois de atualizar o driver.",
                new[]
                {
                    Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"), Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"), Path.Combine(local, "AMD", "VkCache"),
                }, TimeSpan.Zero),
            new("Relatórios de erro do Windows", "Relatórios de travamentos já enviados ou antigos.",
                new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive") }, TimeSpan.Zero),
        };
    }

    /// <summary>Conta quanto dá para liberar (não apaga nada).</summary>
    public static void Measure(CleanupTarget t)
    {
        long bytes = 0;
        var files = 0;
        foreach (var f in Files(t)) { bytes += f.Length; files++; }
        t.Bytes = bytes;
        t.Files = files;
    }

    /// <summary>Apaga e devolve quanto liberou. Pastas vazias que sobrarem também saem (menos a raiz).</summary>
    public static long Clean(CleanupTarget t, CancellationToken ct)
    {
        long freed = 0;
        foreach (var f in Files(t))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var size = f.Length;
                f.Attributes = FileAttributes.Normal;
                f.Delete();
                freed += size;
            }
            catch { } // em uso ou sem permissão: fica
        }
        foreach (var root in t.Folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            catch { }
        }
        return freed;
    }

    private static IEnumerable<FileInfo> Files(CleanupTarget t)
    {
        var cutoff = DateTime.Now - t.MinAge;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var root in t.Folders.Where(Directory.Exists))
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", options); }
            catch { continue; }
            foreach (var f in files)
                if (f.LastWriteTime <= cutoff) yield return f;
        }
    }
}
