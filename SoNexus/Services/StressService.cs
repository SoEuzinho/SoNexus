using System.Numerics;
using System.Runtime.InteropServices;

namespace SoNexus.Services;

public enum StressMode { Cpu, CpuMemoria, Ram, Gpu, Vram, Tudo }

/// <summary>
/// Stress test no estilo OCCT: todos os núcleos repetem um cálculo vetorial (AVX) pesado e
/// conferem o resultado com uma referência. Resultado diferente = erro de cálculo = PC instável.
/// </summary>
public sealed class StressTest
{
    private long _iterations;
    private long _errors;

    public long Iterations => Interlocked.Read(ref _iterations);
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Quantidade de RAM sendo testada no modo RAM.</summary>
    public ulong RamTested { get; private set; }

    /// <summary>
    /// RAM: ocupa ~70% da memória livre, cada thread grava um padrão pseudoaleatório e lê de volta.
    /// Um bit trocado = pente com defeito ou XMP/EXPO instável.
    /// </summary>
    public Task RunRamAsync(int threads, CancellationToken ct) => Task.Run(() =>
    {
        var free = MemoryService.GetStatus().Available;
        var perThread = (long)(free * 0.7 / (ulong)threads) / 8 * 8;
        perThread = Math.Clamp(perThread, 16L << 20, 2L << 30);
        RamTested = (ulong)(perThread * threads);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            ulong[] block;
            try { block = new ulong[perThread / 8]; }
            catch (OutOfMemoryException) { return; }
            var seed = (ulong)(t + 1) * 0x9E3779B97F4A7C15;
            while (!ct.IsCancellationRequested)
            {
                seed += 0xD1B54A32D192ED03;
                var x = seed;
                for (var i = 0; i < block.Length; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    block[i] = x;
                }
                x = seed;
                var wrong = 0;
                for (var i = 0; i < block.Length; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    if (block[i] != x) wrong++;
                }
                if (wrong > 0) Interlocked.Add(ref _errors, wrong);
                Interlocked.Increment(ref _iterations);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
    });

    public Task RunAsync(StressMode mode, int threads, CancellationToken ct) => Task.Run(() =>
    {
        // Modo CPU: dados cabem no cache L1 (calor máximo no núcleo).
        // Modo CPU + memória: 16 MB por thread, força controlador de memória e RAM.
        var (length, rounds) = mode == StressMode.Cpu ? (4096, 40_000) : (2 * 1024 * 1024, 30);
        var initial = new double[length];
        for (var i = 0; i < length; i++) initial[i] = 1.0 + i % 97 * 0.01;

        var reference = Kernel(initial.ToArray(), rounds);

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            var work = new double[length];
            while (!ct.IsCancellationRequested)
            {
                initial.CopyTo(work, 0);
                if (Kernel(work, rounds) != reference) Interlocked.Increment(ref _errors);
                Interlocked.Increment(ref _iterations);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal, // mantém a janela respondendo
        }).ToList();

        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
    });

    /// <summary>Cálculo determinístico: no mesmo PC estável, sempre dá exatamente o mesmo resultado.</summary>
    private static double Kernel(double[] data, int rounds)
    {
        var v = MemoryMarshal.Cast<double, Vector<double>>(data.AsSpan());
        var a = new Vector<double>(0.999999);
        var b = new Vector<double>(0.5);
        var c = new Vector<double>(1.0000003);
        var d = new Vector<double>(-0.25);
        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < v.Length; i++)
            {
                var x = v[i];
                x = x * a + b;
                x = x * c + d;
                x = x * a + b;
                x = x * c + d;
                x = x * a + b;
                x = x * c + d;
                v[i] = x;
            }
        }
        double sum = 0;
        foreach (var x in data) sum += x;
        return sum;
    }
}
