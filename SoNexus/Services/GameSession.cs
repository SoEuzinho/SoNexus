using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

/// <summary>O que segurou o FPS num segundo de jogo (medido pelos sensores, não pela tabela).</summary>
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
        if (s.GpuLoad < 80) return FrameLimit.Other;
        return FrameLimit.Gpu; // 80-95%: placa praticamente cheia
    }

    public static int? CapOf(double fps, int refreshHz) =>
        CommonCaps.Append(refreshHz).Cast<int?>().FirstOrDefault(c => Math.Abs(fps - c!.Value) <= Math.Max(1.5, c.Value * 0.02));

    private static bool IsCapped(double fps, int refreshHz) => CapOf(fps, refreshHz) != null;

    public static string Name(FrameLimit l) => l switch
    {
        FrameLimit.Gpu => "Placa de vídeo",
        FrameLimit.Cpu => "Processador",
        FrameLimit.Cap => "Limitador / V-Sync",
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
            FrameLimit.Thermal => ("Esquentando demais", $"{(s.GpuTemp >= 87 ? $"Placa a {s.GpuTemp:0} °C" : $"Processador a {s.CpuTemp:0} °C")}: acima disso as peças baixam o clock sozinhas (thermal throttling) e o FPS cai. Limpe a poeira, melhore o fluxo de ar ou deixe as ventoinhas mais fortes."),
            FrameLimit.Vram => ("Memória de vídeo cheia", $"A VRAM está em {s.VramPct:0}%. Quando enche, a placa usa a RAM do PC e aparecem engasgos. Baixe a qualidade das texturas."),
            FrameLimit.Ram => ("RAM do PC quase cheia", $"{s.RamPct}% da memória em uso: o Windows começa a usar o disco e o jogo engasga. Feche navegador e programas em segundo plano."),
            FrameLimit.Unknown => ("Sem sensor de uso da placa", "Sem o uso da placa de vídeo não dá para dizer quem segura o FPS. Abra o SoNexus como administrador para ler todos os sensores."),
            FrameLimit.Gpu => ("Limite: placa de vídeo", $"A placa está trabalhando a {s.GpuLoad:0}%, o cenário normal em jogos pesados. Para mais FPS: baixe sombras, reflexos e efeitos, ou ative DLSS/FSR/XeSS."),
            FrameLimit.Cap => ($"FPS travado em {cap}", cap == refreshHz
                ? "É a taxa do monitor: V-Sync, G-Sync/FreeSync ou o limitador do jogo estão segurando. Isso é bom para fluidez."
                : "Há um limitador de FPS ativo (no jogo, no painel da NVIDIA/AMD ou no RivaTuner). Tire-o se quiser mais FPS."),
            FrameLimit.Cpu => ("Limite: processador", $"Um núcleo do processador está a {s.CoreMax:0}% e a placa está só a {s.GpuLoad:0}%: o processador não consegue preparar quadros mais rápido. Suba a resolução/gráficos (é \"de graça\"), feche programas em segundo plano."),
            _ => ("Nem placa nem processador no limite", $"Placa a {s.GpuLoad:0}% e núcleo mais ocupado a {s.CoreMax:0}%. O motor do jogo, o servidor online, a leitura do disco ou um limitador estão segurando o FPS."),
        };
    }
}

/// <summary>
/// Uma partida medida: soma, segundo a segundo, quem segurou o FPS. É isto que responde
/// "o meu PC tem gargalo?" com dado real, em vez da estimativa por tabela.
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
    public float? MaxCpuTemp { get; private set; }
    public float? MaxGpuTemp { get; private set; }
    public double MaxVramPct { get; private set; }
    public uint MaxRamPct { get; private set; }

    public GameSession(string game) => Game = game;

    public void Add(GameSecond s, FrameLimit limit)
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
    }

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
            FrameLimit.Gpu => ($"A placa de vídeo foi o limite em {pct:0}% do tempo",
                "É o normal e o saudável: o processador entrega quadros mais rápido do que a placa desenha, então nada está sobrando. " +
                "Mais FPS: DLSS/FSR/XeSS, sombras e reflexos mais baixos. Trocar o processador não mudaria nada neste jogo."),
            FrameLimit.Cpu => ($"O processador segurou o FPS em {pct:0}% do tempo",
                $"Um núcleo ficou perto de 100% enquanto a placa sobrava (média {AvgGpu:0}%). Você tem um gargalo de processador neste jogo. " +
                (ram != null ? $"Antes de trocar peça: {ram.Title.ToLowerInvariant()} — corrigir isso dá FPS de graça. " : "") +
                "Feche programas em segundo plano; aumentar a resolução ou os gráficos não vai baixar o FPS (a placa tem folga)."),
            FrameLimit.Cap => ($"O FPS ficou travado num limite em {pct:0}% do tempo",
                "V-Sync, G-Sync/FreeSync ou um limitador seguraram o FPS de propósito. Não é gargalo: o PC aguenta mais. Tire o limite se quiser mais FPS."),
            FrameLimit.Thermal => ($"Temperatura alta em {pct:0}% do tempo",
                $"Máximas: processador {MaxCpuTemp:0} °C, placa {MaxGpuTemp:0} °C. Quente assim as peças reduzem o clock. Limpe a poeira e melhore a ventilação antes de qualquer outra coisa."),
            FrameLimit.Vram => ($"A memória da placa encheu em {pct:0}% do tempo", "Baixe a qualidade das texturas: com a VRAM cheia o jogo engasga mesmo com FPS alto."),
            FrameLimit.Ram => ($"A RAM do PC ficou cheia em {pct:0}% do tempo",
                $"Pico de {MaxRamPct}% de uso. Feche o navegador e programas em segundo plano" + (ram != null ? $"; e veja: {ram.Title.ToLowerInvariant()}." : "; se acontece sempre, mais RAM resolve.")),
            FrameLimit.Other => ($"Nem placa nem processador no limite em {pct:0}% do tempo",
                $"Placa em média a {AvgGpu:0}% e núcleo mais ocupado a {AvgCore:0}%. O próprio jogo, o servidor online ou o disco seguram o FPS. Trocar peça não ajudaria."),
            _ => ("Sem sensor de uso da placa", "Abra o SoNexus como administrador para ler o uso da placa e saber quem segura o FPS."),
        };
    }
}

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

    public string When => Date.ToString("dd/MM HH:mm");
    public string Summary => $"{AvgFps:0} FPS médio" + (Low1 is { } l ? $" · 1% low {l:0}" : "") + $" · {TimeSpan.FromSeconds(Seconds):h\\:mm\\:ss}";
}

public static class SessionHistory
{
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
        var list = Load();
        list.Insert(0, new SavedSession
        {
            Game = s.Game, Date = s.Started, Seconds = s.Seconds, AvgFps = stats?.AvgFps ?? s.AvgFps, Low1 = stats?.Low1,
            Main = GameDiagnosis.Name(s.Main), MainPct = Math.Round(s.Share(s.Main) * 100), Title = s.Verdict().Title,
        });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(30).ToList()));
        }
        catch { }
    }
}
