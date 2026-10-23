using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

public sealed record BenchResult(DateTime Date, string Cpu, int Threads, double Single, double Multi)
{
    public string DateText => Date.ToString("dd/MM/yyyy HH:mm");
    public string SingleText => Single > 0 ? $"{Single:0}" : "—";
    public string MultiText => Multi > 0 ? $"{Multi:0}" : "—";
}

/// <summary>
/// Benchmark de CPU no estilo Cinebench: renderiza um fractal (Mandelbrot com 4 amostras por pixel)
/// em blocos, com N threads, por um tempo fixo. A pontuação é o total de iterações calculadas por segundo.
/// </summary>
public sealed class BenchmarkRun
{
    public const int Width = 640;
    public const int Height = 360;
    private const int Tile = 32;
    private const int TilesX = Width / Tile;
    private const int TilesPerFrame = TilesX * (Height / Tile + (Height % Tile > 0 ? 1 : 0));
    private const int MaxIter = 1200;
    private const int FramesPerLoop = 110;
    private const double CenterX = -0.743643887037151;
    private const double CenterY = 0.131825904205330;

    private long _iterations;
    private long _next = -1;

    public int[] Pixels { get; } = new int[Width * Height];
    public long Iterations => Interlocked.Read(ref _iterations);

    /// <summary>Pontos = milhões de iterações por segundo.</summary>
    public static double ToScore(double iterationsPerSecond) => iterationsPerSecond / 1_000_000;

    public Task<double> RunAsync(int threads, TimeSpan duration, CancellationToken ct) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() => Worker(sw, duration, ct))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        }).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        return Iterations / sw.Elapsed.TotalSeconds;
    }, ct);

    private void Worker(Stopwatch sw, TimeSpan duration, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && sw.Elapsed < duration)
        {
            var idx = Interlocked.Increment(ref _next);
            var frame = (int)(idx / TilesPerFrame % FramesPerLoop);
            var tile = (int)(idx % TilesPerFrame);
            Interlocked.Add(ref _iterations, RenderTile(frame, tile));
        }
    }

    private long RenderTile(int frame, int tile)
    {
        var scale = 3.2 * Math.Pow(0.93, frame) / Width;
        int x0 = tile % TilesX * Tile, y0 = tile / TilesX * Tile;
        long total = 0;
        for (var y = y0; y < Math.Min(y0 + Tile, Height); y++)
        {
            for (var x = x0; x < x0 + Tile; x++)
            {
                double sum = 0;
                var inside = 0;
                for (var s = 0; s < 4; s++)
                {
                    var px = CenterX + (x - Width / 2 + (s & 1) * 0.5) * scale;
                    var py = CenterY + (y - Height / 2 + (s >> 1) * 0.5) * scale;
                    double zx = 0, zy = 0, zx2 = 0, zy2 = 0;
                    var n = 0;
                    while (n < MaxIter && zx2 + zy2 <= 4)
                    {
                        zy = 2 * zx * zy + py;
                        zx = zx2 - zy2 + px;
                        zx2 = zx * zx;
                        zy2 = zy * zy;
                        n++;
                    }
                    total += n;
                    if (n == MaxIter) inside++;
                    else sum += n;
                }
                Pixels[y * Width + x] = inside == 4 ? unchecked((int)0xFF000000) : Color(sum / (4 - inside), frame);
            }
        }
        return total;
    }

    /// <summary>Tons de cinza (tema preto e branco do SoNexus), em BGRA.</summary>
    private static int Color(double n, int frame)
    {
        var v = Math.Log(n + 1) * 1.6 + frame * 0.03;
        var g = Math.Clamp((int)(120 + 125 * Math.Sin(v)), 0, 255);
        return unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g;
    }
}

public static class BenchmarkHistory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "benchmarks.json");

    public static List<BenchResult> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<BenchResult>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    public static void Add(BenchResult result)
    {
        var list = Load();
        list.Insert(0, result);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(50)));
    }
}
