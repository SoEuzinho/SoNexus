using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SoNexus.Services;

/// <summary>
/// Confere a assinatura digital de um programa antes de o SoNexus executá-lo como administrador:
/// a assinatura precisa ser válida para o Windows (WinVerifyTrust) e o certificado tem de ser da empresa esperada.
/// Sem isso, um arquivo trocado na pasta de downloads ganharia acesso de administrador.
/// </summary>
public static class Authenticode
{
    /// <summary>null = ok. Senão, o motivo em português.</summary>
    public static string? Verify(string file, params string[] allowedSigners)
    {
        if (!File.Exists(file)) return "arquivo não encontrado";
        if (!IsTrusted(file)) return "a assinatura digital do arquivo é inválida ou não existe";
        string subject;
        try
        {
#pragma warning disable SYSLIB0057 // API nova não lê assinatura de .exe; esta lê.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            subject = cert.GetNameInfo(X509NameType.SimpleName, false) + " | " + cert.Subject;
        }
        catch
        {
            return "não foi possível ler quem assinou o arquivo";
        }
        return allowedSigners.Any(s => subject.Contains(s, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"assinado por \"{subject.Split('|')[0].Trim()}\", que não é o fabricante esperado";
    }

    private static bool IsTrusted(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = file,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE (sem internet também funciona)
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                dwProvFlags = 0x80,       // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
