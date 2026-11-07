using System.Runtime.InteropServices;

namespace SoNexus.Services;

/// <summary>Listas de memória do Windows (as mesmas do RAMMap), em bytes.</summary>
public sealed record MemoryLists(ulong Standby, ulong Modified, ulong Free, ulong Zeroed)
{
    /// <summary>"Em espera" de prioridade baixa (0–4): o que dá para soltar sem atrapalhar programas abertos.</summary>
    public ulong StandbyLow { get; init; }
}

/// <summary>
/// Faz o que o RAMMap e o ISLC fazem: lê as listas de memória e solta a memória "em espera"
/// (cache de arquivos que o Windows guarda) ou o que os programas parados estão segurando.
/// Usa as chamadas internas do Windows (NtQuery/NtSetSystemInformation); precisa de administrador.
/// </summary>
public static class MemoryCleaner
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2, MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;

    /// <summary>Lê as listas. null sem permissão (sem administrador).</summary>
    public static MemoryLists? Read()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return null;
        // SYSTEM_MEMORY_LIST_INFORMATION: 5 contadores + 8 por prioridade + 8 reaproveitados + 1 = 22 × 8 bytes (x64).
        var buffer = new byte[22 * 8];
        if (NtQuerySystemInformation(SystemMemoryListInformation, buffer, buffer.Length, out _) != 0) return null;
        ulong At(int i) => BitConverter.ToUInt64(buffer, i * 8) * (ulong)Environment.SystemPageSize;
        ulong standby = 0, low = 0;
        for (var p = 0; p < 8; p++)
        {
            standby += At(5 + p);
            if (p <= 4) low += At(5 + p);
        }
        return new MemoryLists(standby, At(2), At(1), At(0)) { StandbyLow = low };
    }

    /// <summary>Solta a memória em espera. lowPriorityOnly = só a de prioridade baixa (mais suave).</summary>
    public static bool PurgeStandby(bool lowPriorityOnly = false) =>
        Command(lowPriorityOnly ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList);

    /// <summary>Tira da RAM o que programas parados não estão usando (vai para o arquivo de paginação se precisar).</summary>
    public static bool EmptyWorkingSets() => EnablePrivilege("SeIncreaseQuotaPrivilege") && Command(MemoryEmptyWorkingSets);

    private static bool Command(int command)
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return false;
        var handle = GCHandle.Alloc(command, GCHandleType.Pinned);
        try { return NtSetSystemInformation(SystemMemoryListInformation, handle.AddrOfPinnedObject(), sizeof(int)) == 0; }
        finally { handle.Free(); }
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
}
