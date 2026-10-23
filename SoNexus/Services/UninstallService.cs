using System.Diagnostics;
using System.IO;

namespace SoNexus.Services;

/// <summary>Desinstalar o SoNexus de dentro do próprio app, deixando o PC como estava (se a pessoa quiser).</summary>
public static class UninstallService
{
    /// <summary>Desinstalador criado pelo instalador (não existe na versão portátil).</summary>
    public static string? Uninstaller
    {
        get
        {
            var file = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
            return File.Exists(file) ? file : null;
        }
    }

    /// <summary>Ajustes do Windows que o SoNexus aplicou e sabe desfazer (têm backup do valor original).</summary>
    public static List<Tweak> AppliedTweaks() =>
        DebloatService.Tweaks.Where(t => t.IsAvailable && t.AppliedBySoNexus).ToList();

    /// <summary>Desfaz os ajustes, um por um. Retorna os que não deu para desfazer.</summary>
    public static List<string> RevertTweaks(Action<string> progress)
    {
        var failed = new List<string>();
        foreach (var t in AppliedTweaks())
        {
            progress($"Desfazendo: {t.Title}");
            try { t.Revert(); }
            catch (Exception ex)
            {
                failed.Add(t.Title);
                AppLog.Write($"Desinstalar: não desfez '{t.Id}': {ex.Message}");
            }
        }
        return failed;
    }

    /// <summary>Apaga configurações, registros e backups (versão portátil; na instalada o desinstalador faz isso).</summary>
    public static void DeleteData()
    {
        AppSettings.DisableSave();
        foreach (var folder in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoNexus"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus"),
                 })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) { Debug.WriteLine(ex); } // o log em uso pode ficar; não é problema
        }
    }

    /// <summary>Abre o desinstalador oficial já sabendo se apaga os dados, e fecha o SoNexus.</summary>
    public static void RunUninstaller(bool deleteData)
    {
        var file = Uninstaller ?? throw new InvalidOperationException("Desinstalador não encontrado.");
        // Iniciar com o Windows e ventoinhas: o desinstalador remove a tarefa; ao fechar, as ventoinhas voltam para a placa.
        Process.Start(new ProcessStartInfo(file, deleteData ? "/DELETEDATA" : "/KEEPDATA") { UseShellExecute = true });
        App.ExitCompletely();
    }
}
