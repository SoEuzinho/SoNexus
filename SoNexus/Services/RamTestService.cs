using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoNexus.Services;

public sealed record RamError(int Pass, string Pattern, long Offset, ulong Expected, ulong Actual)
{
    public string OffsetText => $"0x{Offset:X10}";
    public string ExpectedText => $"0x{Expected:X16}";
    public string ActualText => $"0x{Actual:X16}";
    public string BitsText => $"{System.Numerics.BitOperations.PopCount(Expected ^ Actual)} bit(s): 0x{Expected ^ Actual:X16}";
}

public sealed record RamTestProgress(int Pass, int PatternIndex, int PatternCount, string Pattern, string Phase,
    double Percent, long Errors, double GBps);

/// <summary>
/// Teste de RAM no estilo MemTest86, rodando dentro do Windows: reserva parte da memória livre
/// e grava/confere vários padrões de bits em todos os núcleos.
/// </summary>
public sealed unsafe class RamTester : IDisposable
{
    private const long ChunkBytes = 128L << 20; // 128 MB
    private const int ChunkWords = (int)(ChunkBytes / sizeof(ulong));
    private const int MaxStoredErrors = 500;

    private readonly List<IntPtr> _chunks = new();
    private readonly List<RamError> _errors = new();
    private long _errorCount;

    public long AllocatedBytes => _chunks.Count * ChunkBytes;
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public IReadOnlyList<RamError> Errors
    {
        get { lock (_errors) return _errors.ToList(); }
    }

    private static readonly RamPattern[] Patterns =
    {
        new ConstantPattern("Zeros", 0UL),
        new ConstantPattern("Uns", ulong.MaxValue),
        new ConstantPattern("Xadrez 0xAA55", 0xAA55AA55AA55AA55UL),
        new ConstantPattern("Xadrez 0x55AA", 0x55AA55AA55AA55AAUL),
        new AddressPattern(),
        new WalkingPattern(false),
        new WalkingPattern(true),
        new RandomPattern(false),
        new RandomPattern(true),
    };

    /// <summary>Reserva até "bytes". Para antes se o Windows recusar mais memória.</summary>
    public long Allocate(long bytes)
    {
        var count = Math.Max(1, bytes / ChunkBytes);
        for (var i = 0; i < count; i++)
        {
            try { _chunks.Add((IntPtr)NativeMemory.Alloc((nuint)ChunkBytes)); }
            catch (OutOfMemoryException) { break; }
        }
        return AllocatedBytes;
    }

    /// <summary>passes = 0 roda até cancelar.</summary>
    public void Run(int passes, IProgress<RamTestProgress> progress, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        for (var pass = 1; passes == 0 || pass <= passes; pass++)
        {
            for (var p = 0; p < Patterns.Length; p++)
            {
                var pattern = Patterns[p];
                foreach (var verify in new[] { false, true })
                {
                    var done = 0;
                    var sw = Stopwatch.StartNew();
                    Parallel.For(0, _chunks.Count, options, (i, state) =>
                    {
                        if (ct.IsCancellationRequested) { state.Stop(); return; }
                        var ptr = (ulong*)_chunks[i];
                        var seed = (ulong)(pass * 7919 + i * 104729 + 1);
                        if (!verify)
                        {
                            pattern.Write(ptr, ChunkWords, seed);
                        }
                        else
                        {
                            var chunkBase = (long)i * ChunkBytes;
                            pattern.Verify(ptr, ChunkWords, seed, (index, expected, actual) =>
                                Report(pass, pattern.Name, chunkBase + index * sizeof(ulong), expected, actual));
                        }
                        var n = Interlocked.Increment(ref done);
                        var gbps = n * ChunkBytes / sw.Elapsed.TotalSeconds / (1L << 30);
                        var total = Patterns.Length * 2;
                        var percent = (p * 2 + (verify ? 1 : 0) + (double)n / _chunks.Count) / total * 100;
                        progress.Report(new RamTestProgress(pass, p + 1, Patterns.Length, pattern.Name,
                            verify ? "conferindo" : "gravando", percent, ErrorCount, gbps));
                    });
                    if (ct.IsCancellationRequested) return;
                }
            }
        }
    }

    private void Report(int pass, string pattern, long offset, ulong expected, ulong actual)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_errors)
        {
            if (_errors.Count < MaxStoredErrors) _errors.Add(new RamError(pass, pattern, offset, expected, actual));
        }
    }

    public void Dispose()
    {
        foreach (var c in _chunks) NativeMemory.Free((void*)c);
        _chunks.Clear();
    }

    // ---------- Padrões ----------

    private delegate void ErrorSink(long index, ulong expected, ulong actual);

    private abstract class RamPattern(string name)
    {
        public string Name { get; } = name;
        public abstract void Write(ulong* p, int n, ulong seed);
        public abstract void Verify(ulong* p, int n, ulong seed, ErrorSink error);
    }

    private sealed class ConstantPattern(string name, ulong value) : RamPattern(name)
    {
        public override void Write(ulong* p, int n, ulong seed) => new Span<ulong>(p, n).Fill(value);

        public override void Verify(ulong* p, int n, ulong seed, ErrorSink error)
        {
            var span = new Span<ulong>(p, n);
            var start = 0;
            while (start < n)
            {
                var i = span[start..].IndexOfAnyExcept(value); // vetorizado (rápido)
                if (i < 0) break;
                i += start;
                error(i, value, span[i]);
                start = i + 1;
            }
        }
    }

    /// <summary>Cada posição guarda o próprio endereço: pega erros de endereçamento.</summary>
    private sealed class AddressPattern() : RamPattern("Endereço próprio")
    {
        public override void Write(ulong* p, int n, ulong seed)
        {
            for (var i = 0; i < n; i++) p[i] = (ulong)(p + i);
        }

        public override void Verify(ulong* p, int n, ulong seed, ErrorSink error)
        {
            for (var i = 0; i < n; i++)
                if (p[i] != (ulong)(p + i)) error(i, (ulong)(p + i), p[i]);
        }
    }

    /// <summary>Um bit ligado (ou desligado) andando pelas 64 posições.</summary>
    private sealed class WalkingPattern(bool inverted) : RamPattern(inverted ? "Zeros caminhantes" : "Uns caminhantes")
    {
        private ulong Value(int i) => inverted ? ~(1UL << (i & 63)) : 1UL << (i & 63);

        public override void Write(ulong* p, int n, ulong seed)
        {
            for (var i = 0; i < n; i++) p[i] = Value(i);
        }

        public override void Verify(ulong* p, int n, ulong seed, ErrorSink error)
        {
            for (var i = 0; i < n; i++)
                if (p[i] != Value(i)) error(i, Value(i), p[i]);
        }
    }

    /// <summary>Números pseudoaleatórios (xorshift) regenerados na conferência.</summary>
    private sealed class RandomPattern(bool inverted) : RamPattern(inverted ? "Aleatório invertido" : "Aleatório")
    {
        public override void Write(ulong* p, int n, ulong seed)
        {
            var x = Start(seed);
            for (var i = 0; i < n; i++)
            {
                x = Next(x);
                p[i] = inverted ? ~x : x;
            }
        }

        public override void Verify(ulong* p, int n, ulong seed, ErrorSink error)
        {
            var x = Start(seed);
            for (var i = 0; i < n; i++)
            {
                x = Next(x);
                var expected = inverted ? ~x : x;
                if (p[i] != expected) error(i, expected, p[i]);
            }
        }

        private static ulong Start(ulong seed) => seed * 0x9E3779B97F4A7C15UL | 1;

        private static ulong Next(ulong x)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return x;
        }
    }
}
