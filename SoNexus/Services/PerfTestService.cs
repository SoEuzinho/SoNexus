using System.IO;
using System.Management;
using System.Text.Json;

namespace SoNexus.Services;

/// <summary>Um número medido: média de N rodadas e quanto elas variaram entre si (%).</summary>
public sealed record Measured(double Value, double VariationPct, int Runs)
{
    public static Measured From(IReadOnlyList<double> runs)
    {
        var avg = runs.Average();
        var spread = runs.Count > 1 && avg > 0 ? (runs.Max() - runs.Min()) / avg * 100 : 0;
        return new Measured(avg, spread, runs.Count);
    }
}

/// <summary>Resultado do "Teste completo". Campos null = parte não testada (ou o PC não tem).</summary>
public sealed class PerfRun
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string GpuDriver { get; set; } = "";
    public string RamConfig { get; set; } = "";
    public Measured? CpuSingle { get; set; }
    public Measured? CpuMulti { get; set; }
    public Measured? GpuGflops { get; set; }
    public Measured? VramGBs { get; set; }
    public Measured? RamReadGBs { get; set; }
    public Measured? RamLatencyNs { get; set; }
    public double? DiskReadMBs { get; set; }
    public double? DiskWriteMBs { get; set; }
    public double? DiskIops { get; set; }
    public float? MaxCpuTemp { get; set; }
    public float? MaxGpuTemp { get; set; }
    public string StopReason { get; set; } = "";

    public string Title => $"{Date:dd/MM/yyyy HH:mm}" + (Label.Length > 0 ? $" · {Label}" : "");

    public string Summary => string.Join(" · ", new[]
    {
        CpuMulti is { } c ? $"CPU {c.Value:N0} pts" : null,
        GpuGflops is { } g ? $"GPU {g.Value:N0} GFLOPS" : null,
        RamReadGBs is { } r ? $"RAM {r.Value:0.0} GB/s" : null,
        DiskReadMBs is { } d ? $"disco {d:N0} MB/s" : null,
        StopReason.Length > 0 ? StopReason : null,
    }.Where(x => x != null));
}

/// <summary>Uma linha da tabela: o item, o valor de agora, o de comparação e se mudou de verdade.</summary>
public sealed record PerfRow(string Item, string Unit, double? Now, double? Before, double Variation, bool LowerIsBetter)
{
    public string NowText => Now is { } v ? Fmt(v) : "—";
    public string BeforeText => Before is { } v ? Fmt(v) : "—";

    /// <summary>Diferença em %, já com o sinal "melhor = positivo".</summary>
    public double? Gain => Now is { } n && Before is { } b && b > 0 ? (LowerIsBetter ? b / n - 1 : n / b - 1) * 100 : null;

    /// <summary>Só conta como mudança se passar da variação normal entre rodadas (mínimo 3%).</summary>
    public string Verdict => Gain switch
    {
        null => "",
        var g when Math.Abs(g.Value) < Math.Max(3, Variation) => "igual (dentro da variação)",
        > 0 => $"▲ {Gain:0}% melhor",
        _ => $"▼ {-Gain:0}% pior",
    };

    private string Fmt(double v) => (v >= 100 ? v.ToString("N0") : v.ToString("0.0")) + " " + Unit;
}

/// <summary>
/// Teste completo: aquece, mede cada peça 3 vezes e guarda o resultado para comparar antes/depois
/// (ex.: antes e depois de ligar o EXPO, trocar a pasta térmica ou atualizar o driver).
/// Para sozinho se o processador passar de 95 °C ou a placa de 90 °C.
/// </summary>
public sealed class PerfTest
{
    public const int Rounds = 3;
    private const float CpuLimit = 95, GpuLimit = 90;

    /// <summary>Etapa atual, para a tela.</summary>
    public string Step { get; private set; } = "";
    /// <summary>0 a 1.</summary>
    public double Progress { get; private set; }
    public float? MaxCpuTemp { get; private set; }
    public float? MaxGpuTemp { get; private set; }

    private string? _hot;

    public async Task<PerfRun> RunAsync(bool disk, string label, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        var run = new PerfRun
        {
            Label = label.Trim(),
            AppVersion = typeof(PerfTest).Assembly.GetName().Version?.ToString(3) ?? "",
            Cpu = CpuName(),
            GpuDriver = GpuDriver(),
        };
        try
        {
            var layout = MemoryInfoService.Layout();
            run.RamConfig = $"{layout.Modules.Count} pente(s) · {layout.Speed} MT/s";
        }
        catch { }

        // Vigia de temperatura durante todo o teste.
        using var watch = new Timer(_ =>
        {
            var r = HardwareService.Instance.Readings;
            var cpu = HardwareService.CpuNow(r).Temp;
            var gpu = HardwareService.GpuNow(r).Temp;
            if (cpu > (MaxCpuTemp ?? 0)) MaxCpuTemp = cpu;
            if (gpu > (MaxGpuTemp ?? 0)) MaxGpuTemp = gpu;
            if (cpu >= CpuLimit) _hot = $"o processador chegou a {cpu:0} °C";
            else if (gpu >= GpuLimit) _hot = $"a placa de vídeo chegou a {gpu:0} °C";
            if (_hot != null) cts.Cancel();
        }, null, 0, 1000);

        var steps = 2 + Rounds * 2 + Rounds + 1 + Rounds + (disk ? 1 : 0);
        var done = 0;
        void Next(string step) { Step = step; Progress = (double)done++ / steps; }

        try
        {
            var threads = Environment.ProcessorCount;
            Next("Aquecendo o processador");
            await new BenchmarkRun().RunAsync(threads, TimeSpan.FromSeconds(5), ct);
            ct.ThrowIfCancellationRequested();

            var single = new List<double>();
            for (var i = 1; i <= Rounds; i++)
            {
                Next($"Processador, 1 núcleo ({i}/{Rounds})");
                single.Add(BenchmarkRun.ToScore(await new BenchmarkRun().RunAsync(1, TimeSpan.FromSeconds(5), ct)));
                ct.ThrowIfCancellationRequested();
            }
            run.CpuSingle = Measured.From(single);

            var multi = new List<double>();
            for (var i = 1; i <= Rounds; i++)
            {
                Next($"Processador, todos os {threads} threads ({i}/{Rounds})");
                multi.Add(BenchmarkRun.ToScore(await new BenchmarkRun().RunAsync(threads, TimeSpan.FromSeconds(5), ct)));
                ct.ThrowIfCancellationRequested();
            }
            run.CpuMulti = Measured.From(multi);

            Next("Placa de vídeo");
            try
            {
                using var gpu = await Task.Run(GpuTester.Create, ct);
                run.Gpu = gpu.AdapterName;
                await For(TimeSpan.FromSeconds(4), ct, c => gpu.RunCompute(c)); // aquece
                var gflops = new List<double>();
                for (var i = 1; i <= Rounds; i++)
                {
                    Next($"Placa de vídeo, cálculo ({i}/{Rounds})");
                    await For(TimeSpan.FromSeconds(5), ct, c => gpu.RunCompute(c));
                    if (gpu.Rate > 0) gflops.Add(gpu.Rate);
                }
                if (gflops.Count > 0) run.GpuGflops = Measured.From(gflops);

                Next("Memória da placa (VRAM)");
                using var vram = await Task.Run(GpuTester.Create, ct);
                await For(TimeSpan.FromSeconds(8), ct, c => vram.RunVram(c));
                if (vram.Rate > 0) run.VramGBs = new Measured(vram.Rate, 0, 1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                run.Gpu = $"não testada: {ex.Message}";
            }
            ct.ThrowIfCancellationRequested();

            var read = new List<double>();
            var latency = new List<double>();
            for (var i = 1; i <= Rounds; i++)
            {
                Next($"Memória RAM ({i}/{Rounds})");
                var m = await Task.Run(() => MemoryInfoService.Benchmark(_ => { }), ct);
                read.Add(m.ReadGBs);
                latency.Add(m.RamNs);
                ct.ThrowIfCancellationRequested();
            }
            run.RamReadGBs = Measured.From(read);
            run.RamLatencyNs = Measured.From(latency);

            if (disk)
            {
                Next("Disco do sistema");
                var folder = Path.GetTempPath();
                if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace > 3L << 30)
                {
                    var d = await Task.Run(() => DiskBenchService.Run(folder, _ => { }, ct, 512L << 20), ct);
                    run.DiskReadMBs = d.SeqReadMBs;
                    run.DiskWriteMBs = d.SeqWriteMBs;
                    run.DiskIops = d.RandIops;
                }
            }
            Progress = 1;
            Step = "Pronto";
        }
        catch (OperationCanceledException)
        {
            run.StopReason = _hot != null ? $"Parado por segurança: {_hot}." : "Cancelado.";
        }
        run.MaxCpuTemp = MaxCpuTemp;
        run.MaxGpuTemp = MaxGpuTemp;
        return run;
    }

    /// <summary>Roda um teste "até cancelar" por um tempo fixo.</summary>
    private static async Task For(TimeSpan duration, CancellationToken outer, Action<CancellationToken> work)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(duration);
        await Task.Run(() => work(cts.Token));
        outer.ThrowIfCancellationRequested();
    }

    private static string CpuName()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
    }

    /// <summary>Versão do driver de vídeo (para saber se uma mudança veio de atualização de driver).</summary>
    private static string GpuDriver()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                var name = o["Name"]?.ToString() ?? "";
                if (SystemInfoService.IsVirtualAdapter(name, o["PNPDeviceID"]?.ToString())) continue;
                return $"{name} {o["DriverVersion"]}".Trim();
            }
        }
        catch { }
        return "";
    }

    /// <summary>Linhas da comparação entre duas execuções.</summary>
    public static List<PerfRow> Compare(PerfRun now, PerfRun? before) => new()
    {
        Row("Processador · 1 núcleo", "pts", now.CpuSingle, before?.CpuSingle, false),
        Row("Processador · todos os núcleos", "pts", now.CpuMulti, before?.CpuMulti, false),
        Row("Placa de vídeo · cálculo", "GFLOPS", now.GpuGflops, before?.GpuGflops, false),
        Row("Memória da placa (VRAM)", "GB/s", now.VramGBs, before?.VramGBs, false),
        Row("Memória RAM · leitura", "GB/s", now.RamReadGBs, before?.RamReadGBs, false),
        Row("Memória RAM · latência", "ns", now.RamLatencyNs, before?.RamLatencyNs, true),
        new("Disco · leitura", "MB/s", now.DiskReadMBs, before?.DiskReadMBs, 5, false),
        new("Disco · gravação", "MB/s", now.DiskWriteMBs, before?.DiskWriteMBs, 5, false),
        new("Disco · aleatório 4K", "IOPS", now.DiskIops, before?.DiskIops, 8, false),
    };

    private static PerfRow Row(string item, string unit, Measured? now, Measured? before, bool lower) =>
        new(item, unit, now?.Value, before?.Value, Math.Max(now?.VariationPct ?? 0, before?.VariationPct ?? 0), lower);
}

public static class PerfHistory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "perf-runs.json");

    public static List<PerfRun> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<PerfRun>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public static void Add(PerfRun run)
    {
        var list = Load();
        list.Insert(0, run);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(40).ToList()));
        }
        catch { }
    }

    public static void Remove(DateTime date)
    {
        var list = Load();
        list.RemoveAll(r => r.Date == date);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list)); } catch { }
    }
}
