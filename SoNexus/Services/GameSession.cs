using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

/// <summary>Sinais heurísticos observados nas amostras de FPS e sensores do sistema.</summary>
public enum FrameLimit { Gpu, Cpu, Cap, Thermal, Vram, Ram, Other, Unknown }

/// <summary>Um segundo de jogo: o que os sensores e o contador de quadros diziam.</summary>
public sealed record GameSecond(double Fps, float? CpuLoad, float CoreMax, float? CpuTemp, float? GpuLoad, float? GpuTemp, double? VramPct, uint RamPct);

public static class GameDiagnosis
{
    private static readonly int[] CommonCaps = { 30, 60, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 360 };

    /// <summary>
    /// Quem está segurando o FPS agora. A placa a 95%+ é o limite; um núcleo a 90%+ com a placa folgada é o processador;
    /// FPS parado num número redondo com a placa folgada é um limitador. Sem sensor de uso da placa, não dá para afirmar.
    /// </summary>
    public static FrameLimit Classify(GameSecond s, int refreshHz)
    {
        if (s.GpuTemp >= 87 || s.CpuTemp >= 95) return FrameLimit.Thermal;
        if (s.VramPct >= 95) return FrameLimit.Vram;
        if (s.RamPct >= 90) return FrameLimit.Ram;
        if (s.GpuLoad == null) return FrameLimit.Unknown;
        if (s.GpuLoad >= 95) return FrameLimit.Gpu;
        if (IsCapped(s.Fps, refreshHz) && s.GpuLoad < 90) return FrameLimit.Cap;
        if (s.CoreMax >= 90 && s.GpuLoad < 90) return FrameLimit.Cpu;
        return FrameLimit.Other;
    }

    public static int? CapOf(double fps, int refreshHz) =>
        CommonCaps.Append(refreshHz).Cast<int?>().FirstOrDefault(c => Math.Abs(fps - c!.Value) <= Math.Max(1.5, c.Value * 0.02));

    private static bool IsCapped(double fps, int refreshHz) => CapOf(fps, refreshHz) != null;

    public static string Name(FrameLimit l) => l switch
    {
        FrameLimit.Gpu => "Placa de vídeo",
        FrameLimit.Cpu => "Processador",
        FrameLimit.Cap => "Possível limite de FPS",
        FrameLimit.Thermal => "Temperatura",
        FrameLimit.Vram => "VRAM cheia",
        FrameLimit.Ram => "RAM cheia",
        FrameLimit.Other => "Jogo / outro",
        _ => "Sem sensor",
    };

    /// <summary>Título e explicação para o momento atual (cartão "Diagnóstico ao vivo").</summary>
    public static (string Title, string Detail) Explain(FrameLimit l, GameSecond s, int refreshHz)
    {
        var cap = CapOf(s.Fps, refreshHz);
        return l switch
        {
            FrameLimit.Thermal => ("Temperatura alta", $"{(s.GpuTemp >= 87 ? $"Placa a {s.GpuTemp:0} °C" : $"Processador a {s.CpuTemp:0} °C")}. A temperatura merece atenção. O limite térmico depende do modelo e a leitura por si só não confirma redução de clock."),
            FrameLimit.Vram => ("Memória de vídeo cheia", $"A VRAM está em {s.VramPct:0}%. Quando enche, a placa usa a RAM do PC e aparecem engasgos. Baixe a qualidade das texturas."),
            FrameLimit.Ram => ("RAM do PC quase cheia", $"{s.RamPct}% da memória em uso: o Windows começa a usar o disco e o jogo engasga. Feche navegador e programas em segundo plano."),
            FrameLimit.Unknown => ("Sem sensor de uso da placa", "Sem o uso da placa de vídeo não dá para dizer quem segura o FPS. Abra o SoNexus como administrador para ler todos os sensores."),
            FrameLimit.Gpu => ("Sinal de limite na GPU", $"O sensor da placa registra {s.GpuLoad:0}% de uso. Reduza um ajuste gráfico e compare o FPS na mesma cena para confirmar."),
            FrameLimit.Cap => ($"FPS próximo de {cap}", "A amostra está perto de um limite comum. Confira o V-Sync e os limites de FPS do jogo e do driver."),
            FrameLimit.Cpu => ("Sinal de limite na CPU", $"O núcleo mais ocupado do sistema registra {s.CoreMax:0}% e o sensor da placa, {s.GpuLoad:0}%. Compare a mesma cena e verifique também os processos em segundo plano."),
            _ => ("Sem limite claro", $"O sensor da placa registra {s.GpuLoad:0}% de uso. As leituras desta amostra não isolam uma causa."),
        };
    }
}

/// <summary>
/// Resume a telemetria da partida e os sinais heurísticos observados.
/// </summary>
public sealed class GameSession
{
    /// <summary>Menos que isso é pouco para afirmar algo (menu, tela de carregamento).</summary>
    public const int MinSeconds = 30;

    public string Game { get; }
    public DateTime Started { get; } = DateTime.Now;
    public int Seconds { get; private set; }
    private readonly int[] _counts = new int[Enum.GetValues<FrameLimit>().Length];
    private double _fpsSum, _gpuSum, _coreSum;
    private int _gpuSamples;
    private readonly Queue<SessionPoint> _timeline = new();
    private SessionPoint? _bucket;
    private int? _lastStutters;
    public float? MaxCpuTemp { get; private set; }
    public float? MaxGpuTemp { get; private set; }
    public double MaxVramPct { get; private set; }
    public uint MaxRamPct { get; private set; }

    public GameSession(string game) => Game = game;

    public void Add(GameSecond s, FrameLimit limit, int? cumulativeStutters = null)
    {
        if (s.Fps < 5) return; // jogo pausado, carregando ou minimizado
        Seconds++;
        _counts[(int)limit]++;
        _fpsSum += s.Fps;
        _coreSum += s.CoreMax;
        if (s.GpuLoad is { } g) { _gpuSum += g; _gpuSamples++; }
        if (s.CpuTemp > (MaxCpuTemp ?? 0)) MaxCpuTemp = s.CpuTemp;
        if (s.GpuTemp > (MaxGpuTemp ?? 0)) MaxGpuTemp = s.GpuTemp;
        MaxVramPct = Math.Max(MaxVramPct, s.VramPct ?? 0);
        MaxRamPct = Math.Max(MaxRamPct, s.RamPct);

        var stutters = cumulativeStutters is { } count && _lastStutters is { } last ? Math.Max(0, count - last) : (int?)null;
        _lastStutters = cumulativeStutters;
        _bucket = _bucket is { } previous
            ? new(Seconds, Math.Min(previous.MinFps, s.Fps), Peak(previous.CpuTemp, s.CpuTemp),
                Peak(previous.GpuTemp, s.GpuTemp), AddCounts(previous.Stutters, stutters))
            : new(Seconds, s.Fps, s.CpuTemp, s.GpuTemp, stutters);
        if (Seconds % 5 == 0)
        {
            if (_timeline.Count == 720) _timeline.Dequeue();
            _timeline.Enqueue(_bucket);
            _bucket = null;
        }
    }

    private static float? Peak(float? first, float? second) =>
        first is { } a && second is { } b ? Math.Max(a, b) : first ?? second;

    private static int? AddCounts(int? first, int? second) =>
        first.HasValue || second.HasValue ? first.GetValueOrDefault() + second.GetValueOrDefault() : null;

    public SessionPoint[] Timeline() => _bucket is { } point ? _timeline.Append(point).TakeLast(720).ToArray() : _timeline.ToArray();

    public double Share(FrameLimit l) => Seconds == 0 ? 0 : (double)_counts[(int)l] / Seconds;
    public double AvgFps => Seconds == 0 ? 0 : _fpsSum / Seconds;
    public double? AvgGpu => _gpuSamples == 0 ? null : _gpuSum / _gpuSamples;
    public double AvgCore => Seconds == 0 ? 0 : _coreSum / Seconds;

    /// <summary>As partes que aparecem na barra (do maior para o menor, sem as zeradas).</summary>
    public List<(FrameLimit Limit, double Share)> Shares() =>
        Enum.GetValues<FrameLimit>().Select(l => (l, Share(l))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).ToList();

    public FrameLimit Main => Shares().Select(x => x.Limit).DefaultIfEmpty(FrameLimit.Unknown).First();

    /// <summary>Veredito da partida inteira, em português simples, com o que fazer.</summary>
    public (string Title, string Detail) Verdict(IReadOnlyList<HealthTip>? ramTips = null)
    {
        if (Seconds < MinSeconds)
            return ("Medindo...", $"Jogue mais {MinSeconds - Seconds} s para o SoNexus dizer o que segura o seu PC nesta partida.");
        var main = Main;
        var pct = Share(main) * 100;
        var ram = ramTips?.FirstOrDefault(t => t.Level >= HealthLevel.Warn);
        return main switch
        {
            FrameLimit.Gpu => ($"Sinal de limite na GPU em {pct:0}% das amostras",
                "A placa apresentou carga alta. Compare o FPS na mesma cena com um ajuste gráfico mais leve."),
            FrameLimit.Cpu => ($"Sinal de limite na CPU em {pct:0}% das amostras",
                $"Um núcleo do sistema ficou muito ocupado com folga na placa (média {AvgGpu:0}%). Compare a mesma cena com outros programas fechados." +
                (ram != null ? $" Confira também: {ram.Title}." : "")),
            FrameLimit.Cap => ($"FPS perto de limites comuns em {pct:0}% das amostras",
                "Confira se o jogo ou o driver tem um limite de FPS ativo e compare de novo."),
            FrameLimit.Thermal => ($"Temperatura alta em {pct:0}% do tempo",
                $"Temperaturas máximas dos sensores: CPU {MaxCpuTemp?.ToString("0") ?? "—"} °C, GPU {MaxGpuTemp?.ToString("0") ?? "—"} °C. Confira os limites do seu hardware."),
            FrameLimit.Vram => ($"A memória da placa encheu em {pct:0}% do tempo", "Baixe a qualidade das texturas: com a VRAM cheia o jogo engasga mesmo com FPS alto."),
            FrameLimit.Ram => ($"A RAM do PC ficou cheia em {pct:0}% do tempo",
                $"Pico de {MaxRamPct}% de uso. Feche o navegador e programas em segundo plano" + (ram != null ? $"; e veja: {ram.Title.ToLowerInvariant()}." : "; se acontece sempre, mais RAM resolve.")),
            FrameLimit.Other => ($"Sem limite claro em {pct:0}% das amostras",
                "A telemetria disponível não isolou uma causa para a taxa de quadros."),
            _ => ("Sem sensor de uso da placa", "Abra o SoNexus como administrador para ler o uso da placa e saber quem segura o FPS."),
        };
    }
}

public sealed record SessionPoint(int Second, double MinFps, float? CpuTemp, float? GpuTemp, int? Stutters);

/// <summary>Partida salva para comparar depois (aba "Seu PC e gargalo").</summary>
public sealed class SavedSession
{
    public string Game { get; set; } = "";
    public DateTime Date { get; set; }
    public int Seconds { get; set; }
    public double AvgFps { get; set; }
    public double? Low1 { get; set; }
    public string Main { get; set; } = "";
    public double MainPct { get; set; }
    public string Title { get; set; } = "";
    public SessionPoint[]? Timeline { get; set; }

    public string When => Date.ToString("dd/MM HH:mm");
    public string ComparisonLabel => $"{Game} · {When}";
    public string Summary => $"{AvgFps:0} FPS médio" + (Low1 is { } l ? $" · 1% low {l:0}" : "") + $" · {TimeSpan.FromSeconds(Seconds):h\\:mm\\:ss}";
}

public static class SessionHistory
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "sessions.json");

    public static List<SavedSession> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<SavedSession>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Guarda a partida (só as que deram para medir) e mantém as 30 mais novas.</summary>
    public static void Save(GameSession s, FpsStats? stats)
    {
        if (s.Seconds < GameSession.MinSeconds) return;
        lock (Sync)
        {
            var list = Load();
            list.Insert(0, new SavedSession
            {
                Game = s.Game, Date = s.Started, Seconds = s.Seconds, AvgFps = stats?.AvgFps ?? s.AvgFps, Low1 = stats?.Low1,
                Main = GameDiagnosis.Name(s.Main), MainPct = Math.Round(s.Share(s.Main) * 100), Title = s.Verdict().Title,
                Timeline = s.Timeline(),
            });
            var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(list.Take(30).ToList()));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"Não foi possível guardar o histórico de partidas: {ex.Message}");
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
