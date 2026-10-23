using System.Diagnostics;
using System.Runtime.InteropServices;
using SoNexus.Models;

namespace SoNexus.Services;

public sealed record MemoryStatus(ulong Total, ulong Available, uint Load)
{
    public ulong Used => Total - Available;
}

/// <summary>Leitura e liberação de memória RAM (estilo RAMMap / ISLC).</summary>
public static class MemoryService
{
    public static MemoryStatus GetStatus()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref s);
        return new MemoryStatus(s.ullTotalPhys, s.ullAvailPhys, s.dwMemoryLoad);
    }

    /// <summary>Esvazia o working set de todos os processos (as páginas vão para a lista de espera).</summary>
    public static (int Trimmed, int Skipped) TrimWorkingSets()
    {
        EnablePrivilege("SeDebugPrivilege");
        int ok = 0, skipped = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                if (h == IntPtr.Zero) { skipped++; continue; }
                if (EmptyWorkingSet(h)) ok++; else skipped++;
                CloseHandle(h);
            }
        }
        return (ok, skipped);
    }

    /// <summary>Limpa a Standby List (cache de arquivos em RAM), como o "Empty Standby List" do RAMMap.</summary>
    public static string? PurgeStandbyList()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            return "Sem permissão (execute o SoNexus como administrador).";
        int command = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        return status == 0 ? null : $"Falhou (NTSTATUS 0x{status:X8}).";
    }

    public static List<ProcessMemory> TopProcesses(int count)
    {
        var list = new List<ProcessMemory>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new ProcessMemory(p.ProcessName, p.Id, p.WorkingSet64)); } catch { }
            }
        }
        return list.OrderByDescending(p => p.WorkingSet).Take(count).ToList();
    }

    // ---------- Win32 ----------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
