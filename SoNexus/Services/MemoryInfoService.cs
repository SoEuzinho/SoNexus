using System.Diagnostics;
using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Como a RAM está sendo usada agora (em bytes). Os campos "?" só existem como administrador.</summary>
public sealed record MemoryDetails(
    ulong Total, ulong Available, ulong? Standby, ulong? Modified, ulong? Free, ulong? BadPages,
    ulong SystemCache, ulong PagedPool, ulong NonPagedPool, ulong Committed, ulong CommitLimit, ulong CommitPeak,
    uint Processes, uint Threads, uint Handles, ulong PageFileUsed, ulong PageFileSize)
{
    public ulong InUse => Total - Available;
}

public sealed record MemoryModule(
    string Slot, ulong Capacity, string Maker, string Part, string Type, uint RatedMts, uint ConfiguredMts,
    double Voltage, int Ranks, int DataWidth, string Serial);

public sealed record MemoryLayout(List<MemoryModule> Modules, int Slots, ulong MaxCapacity)
{
    public int Channels => Math.Min(Modules.Count, 2);
    public uint Speed => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredMts > 0 ? m.ConfiguredMts : m.RatedMts);
    /// <summary>Banda teórica: MT/s × 8 bytes × canais.</summary>
    public double TheoreticalGBs => Speed * 8.0 * Math.Max(1, Channels) / 1000;
}

public sealed record CacheLatency(string Size, double Ns, string Level);

public sealed record MemoryBenchResult(List<CacheLatency> Latencies, double RamNs, double ReadGBs, double WriteGBs, double CopyGBs);

public static class MemoryInfoService
{
    public static MemoryDetails Details()
    {
        var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        GetPerformanceInfo(ref pi, pi.cb);
        var page = (ulong)pi.PageSize;

        ulong? standby = null, modified = null, free = null, bad = null;
        if (MemoryService.EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            var info = new SYSTEM_MEMORY_LIST_INFORMATION();
            if (NtQuerySystemInformation(80, ref info, Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(), out _) == 0)
            {
                ulong sb = 0;
                for (var i = 0; i < 8; i++) sb += (ulong)info.PageCountByPriority(i);
                standby = sb * page;
                modified = (ulong)info.ModifiedPageCount * page;
                free = ((ulong)info.FreePageCount + (ulong)info.ZeroPageCount) * page;
                bad = (ulong)info.BadPageCount;
            }
        }

        ulong pfUsed = 0, pfSize = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT CurrentUsage, AllocatedBaseSize FROM Win32_PageFileUsage");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    pfUsed += Convert.ToUInt64(o["CurrentUsage"]) << 20;
                    pfSize += Convert.ToUInt64(o["AllocatedBaseSize"]) << 20;
                }
            }
        }
        catch { }

        return new MemoryDetails(
            (ulong)pi.PhysicalTotal * page, (ulong)pi.PhysicalAvailable * page, standby, modified, free, bad,
            (ulong)pi.SystemCache * page, (ulong)pi.KernelPaged * page, (ulong)pi.KernelNonpaged * page,
            (ulong)pi.CommitTotal * page, (ulong)pi.CommitLimit * page, (ulong)pi.CommitPeak * page,
            pi.ProcessCount, pi.ThreadCount, pi.HandleCount, pfUsed, pfSize);
    }

    public static MemoryLayout Layout()
    {
        var modules = new List<MemoryModule>();
        int slots = 0;
        ulong max = 0;
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject m in s.Get())
                {
                    using (m)
                    {
                        string S(string p) => m[p]?.ToString()?.Trim() ?? "";
                        uint U(string p) { try { return Convert.ToUInt32(m[p] ?? 0); } catch { return 0; } }
                        var type = U("SMBIOSMemoryType") switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => "DDR" };
                        modules.Add(new MemoryModule(
                            S("DeviceLocator").Length > 0 ? S("DeviceLocator") : S("BankLabel"),
                            Convert.ToUInt64(m["Capacity"] ?? 0), S("Manufacturer"), S("PartNumber"), type,
                            U("Speed"), U("ConfiguredClockSpeed"), U("ConfiguredVoltage") / 1000.0,
                            (int)U("Attributes"), (int)U("DataWidth"), S("SerialNumber")));
                    }
                }
            }
            using (var a = new ManagementObjectSearcher("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray"))
            {
                foreach (ManagementObject o in a.Get())
                {
                    using (o)
                    {
                        slots += Convert.ToInt32(o["MemoryDevices"] ?? 0);
                        max += Convert.ToUInt64(o["MaxCapacityEx"] ?? 0) * 1024; // KB -> bytes
                    }
                }
            }
        }
        catch { }
        return new MemoryLayout(modules, slots, max);
    }

    // ---------- Teste de latência e velocidade ----------

    private static readonly (long Bytes, string Label)[] LatencySizes =
    {
        (16 << 10, "16 KB"), (32 << 10, "32 KB"), (128 << 10, "128 KB"), (512 << 10, "512 KB"),
        (1 << 20, "1 MB"), (4 << 20, "4 MB"), (16 << 20, "16 MB"), (32 << 20, "32 MB"),
        (64 << 20, "64 MB"), (128 << 20, "128 MB"), (256 << 20, "256 MB"),
    };

    /// <summary>
    /// Mede a latência (pulando de ponteiro em ponteiro em ordem aleatória, como o AIDA64)
    /// em tamanhos crescentes, o que mostra L1, L2, L3 e a RAM, e depois a velocidade de leitura/escrita/cópia.
    /// </summary>
    public static MemoryBenchResult Benchmark(Action<string> progress)
    {
        var thread = Thread.CurrentThread;
        var old = thread.Priority;
        thread.Priority = ThreadPriority.Highest;
        try
        {
            var l2 = CacheBytes(2);
            var l3 = CacheBytes(3);
            var latencies = new List<CacheLatency>();
            foreach (var (bytes, label) in LatencySizes)
            {
                progress($"Latência com {label}...");
                var ns = ChaseNs(bytes);
                // Acima de ~8 MB a CPU também erra o TLB (tabela de endereços), então o tempo sobe antes de sair da L3.
                var level = bytes <= 32 << 10 ? "L1" : bytes <= l2 ? "L2" : bytes <= 8 << 20 ? "L3" : bytes <= l3 ? "L3+TLB" : "RAM";
                latencies.Add(new CacheLatency(label, ns, level));
            }
            progress("Velocidade de leitura, escrita e cópia...");
            var (read, write, copy) = Bandwidth(256L << 20);
            return new MemoryBenchResult(latencies, latencies[^1].Ns, read, write, copy);
        }
        finally
        {
            thread.Priority = old;
        }
    }

    private static long CacheBytes(int level)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT L2CacheSize, L3CacheSize FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
                using (o) return Convert.ToInt64(o[level == 2 ? "L2CacheSize" : "L3CacheSize"] ?? 0) * 1024;
        }
        catch { }
        return level == 2 ? 1 << 20 : 32 << 20;
    }

    private static unsafe double ChaseNs(long bytes)
    {
        const int line = 64;
        var lines = (int)(bytes / line);
        var mem = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, 4096);
        try
        {
            // Algoritmo de Sattolo: uma permutação que forma um único ciclo passando por todas as linhas.
            var next = new int[lines];
            for (var i = 0; i < lines; i++) next[i] = i;
            var rnd = new Random(1234);
            for (var i = lines - 1; i > 0; i--)
            {
                var j = rnd.Next(i);
                (next[i], next[j]) = (next[j], next[i]);
            }
            for (var i = 0; i < lines; i++) *(byte**)(mem + (long)i * line) = mem + (long)next[i] * line;

            var p = mem;
            for (var i = 0; i < lines; i++) p = *(byte**)p; // aquece

            var steps = Math.Clamp(lines * 4, 2_000_000, 8_000_000);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < steps; i += 4)
            {
                p = *(byte**)p;
                p = *(byte**)p;
                p = *(byte**)p;
                p = *(byte**)p;
            }
            sw.Stop();
            GC.KeepAlive((IntPtr)p);
            return sw.Elapsed.TotalNanoseconds / steps;
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
    }

    private static unsafe (double Read, double Write, double Copy) Bandwidth(long bytes)
    {
        var threads = Environment.ProcessorCount;
        var mem = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, 4096);
        try
        {
            new Span<byte>(mem, (int)Math.Min(bytes, int.MaxValue)).Fill(1);
            var chunk = bytes / threads / 64 * 64;

            double Run(Action<long> work, int reps)
            {
                var sw = Stopwatch.StartNew();
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    for (var r = 0; r < reps; r++) work(t * chunk);
                });
                return sw.Elapsed.TotalSeconds;
            }

            const int reps = 6;
            var vectors = (int)(chunk / Vector<byte>.Count);
            var sink = new ulong[threads];

            var readTime = Run(offset =>
            {
                var span = new ReadOnlySpan<Vector<ulong>>(mem + offset, vectors);
                var acc = Vector<ulong>.Zero;
                foreach (var v in span) acc ^= v;
                sink[offset / chunk] ^= acc[0];
            }, reps);

            var writeTime = Run(offset => new Span<ulong>(mem + offset, (int)(chunk / 8)).Fill(0x5555555555555555UL), reps);

            var half = chunk / 2;
            var copyTime = Run(offset => Buffer.MemoryCopy(mem + offset, mem + offset + half, half, half), reps);

            double Gbs(double seconds, long total) => total * reps / seconds / 1e9;
            return (Gbs(readTime, chunk * threads), Gbs(writeTime, chunk * threads), Gbs(copyTime, half * threads));
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
        }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public nuint ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public fixed ulong PageCountByPriorityRaw[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public nuint ModifiedPageCountPageFile;

        public nuint PageCountByPriority(int i) => (nuint)PageCountByPriorityRaw[i];
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_MEMORY_LIST_INFORMATION info, int length, out int returned);
}
