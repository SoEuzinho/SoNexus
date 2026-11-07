using System.IO;

namespace SoNexus.Services;

/// <summary>Termos de Uso e Política de Privacidade: texto, versão atual e registro do aceite.</summary>
public static class Terms
{
    /// <summary>Aumente quando o texto mudar de forma relevante: todos terão de aceitar de novo.</summary>
    public const int CurrentVersion = 2;

    public static bool Accepted => AppSettings.Current.TermsVersion >= CurrentVersion;

    public static string Text
    {
        get
        {
            using var stream = typeof(Terms).Assembly.GetManifestResourceStream("SoNexus.Termos.txt");
            return stream == null ? "Termos não encontrados." : new StreamReader(stream).ReadToEnd();
        }
    }

    /// <summary>Guarda no próprio PC qual versão foi aceita e quando (nada é enviado a ninguém).</summary>
    public static void Accept()
    {
        var s = AppSettings.Current;
        s.TermsVersion = CurrentVersion;
        s.TermsAcceptedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        s.Save();
        AppLog.Write($"Termos de Uso e Privacidade v{CurrentVersion} aceitos em {s.TermsAcceptedAt}.");
    }
}
