using System.IO;
using System.Text;
using SoNexus.Models;

namespace SoNexus.Services;

/// <summary>Resultado do teste geral de um disco: veredito simples e o porquê.</summary>
public sealed class DiskCheckResult : ObservableObject
{
    private string _status = "Na fila";
    private string _verdict = "";
    private string _summary = "";

    public required DiskHealth Disk { get; init; }
    public string Title => Disk.Model;
    public string Subtitle => $"{Disk.Type}{(Disk.Rpm is { } r ? $" {r} rpm" : "")} · {Format.Bytes(Disk.Size)}" +
                              (Disk.Volumes.Count > 0 ? " · " + string.Join(" ", Disk.Volumes.Select(v => v.Split(' ')[0])) : " · sem partição");

    /// <summary>"Bom", "Atenção" ou "Ruim" (vazio enquanto testa).</summary>
    public string Verdict { get => _verdict; set { Set(ref _verdict, value); Raise(nameof(VerdictText)); } }
    public string VerdictText => Verdict switch
    {
        "Ruim" => "Ruim — faça backup e troque o disco",
        "Atenção" => "Atenção — funciona, mas fique de olho",
        "Bom" => "Bom — pode confiar",
        _ => "",
    };
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public List<string> Problems { get; } = new();
    public List<string> Good { get; } = new();

    // Números para o relatório.
    public int Unreadable { get; set; }
    public int SlowBlocks { get; set; }
    public int Sampled { get; set; }
    public DiskBenchResult? Speed { get; set; }
    public string? SpeedSkipped { get; set; }
}

/// <summary>
/// Teste geral de todos os discos do PC (SSD, NVMe, HD, USB): saúde pelo SMART, leitura de setores por amostragem
/// (só leitura) e velocidade real. No fim, um veredito simples por disco.
/// </summary>
public static class DiskCheckService
{
    /// <summary>Velocidade mínima esperada de leitura sequencial (MB/s) por tipo; abaixo disso, algo segura o disco.</summary>
    private static (double Read, double Iops) Expected(DiskHealth d) =>
        d.IsUsb ? (0, 0)                     // depende da porta/cabo: não dá para cobrar
        : d.IsNvme ? (700, 5000)
        : d.IsHdd ? (60, 40)
        : (250, 3000);                       // SSD SATA

    public static async Task<List<DiskCheckResult>> PrepareAsync() =>
        (await Task.Run(StorageService.Disks)).Select(d => new DiskCheckResult { Disk = d }).ToList();

    /// <summary>Testa um disco. <paramref name="speed"/>: grava e lê 1 GB temporário (apagado no fim).</summary>
    public static void Check(DiskCheckResult r, bool speed, Action<string> status, CancellationToken ct)
    {
        var d = r.Disk;

        // 1) Saúde (SMART + Windows): o veredito do Armazenamento.
        status("Lendo a saúde (SMART)...");
        foreach (var issue in d.Issues) r.Problems.Add(issue);
        var health = d.Verdict; // Crítico, Atenção, Saudável, Sem SMART
        if (d.SmartAvailable)
        {
            if (d.LifeUsedPercent is { } u) r.Good.Add($"Vida útil restante: {Math.Max(0, 100 - u)}%.");
            if (d.Temperature is { } t) r.Good.Add($"Temperatura: {t} °C.");
            if (d.PowerOnHours is { } h) r.Good.Add($"Ligado por {h:N0} h ({h / 24.0 / 365:0.0} anos).");
        }

        // 2) Setores: 3000 pontos espalhados pelo disco inteiro (só leitura).
        var unreadable = 0;
        if (!ct.IsCancellationRequested)
        {
            try
            {
                var bad = StorageService.Scan(d.Index, d.Size, full: false, d.IsHdd,
                    new Progress<ScanProgress>(p => status($"Lendo setores: {p.Percent:0}% · {p.MBps:0} MB/s")), ct);
                unreadable = bad.Count(b => b.Kind == "Ilegível");
                r.Unreadable = unreadable;
                r.SlowBlocks = bad.Count - unreadable;
                r.Sampled = 3000;
                if (unreadable > 0) r.Problems.Add($"{unreadable} setor(es) ILEGÍVEL(IS) na amostra: o disco está perdendo dados.");
                else r.Good.Add("Nenhum setor ilegível na amostra (3.000 pontos do disco inteiro).");
                // Lentidão pontual é normal com o PC em uso; muitos trechos lentos em HD indicam desgaste mecânico.
                if (r.SlowBlocks > (d.IsHdd ? 60 : 30)) r.Problems.Add($"{r.SlowBlocks} trecho(s) lento(s) de leitura: sinal de desgaste (ou o disco estava muito ocupado durante o teste).");
            }
            catch (Exception ex)
            {
                r.Problems.Add($"Não deu para ler os setores: {ex.Message}");
            }
        }

        // 3) Velocidade real numa partição do disco.
        var slowSpeed = false;
        if (speed && !ct.IsCancellationRequested)
        {
            var folder = TestFolder(d, out var skip);
            if (folder == null) r.SpeedSkipped = skip;
            else
            {
                try
                {
                    status("Medindo a velocidade (1 GB temporário)...");
                    Directory.CreateDirectory(folder);
                    var s = DiskBenchService.Run(folder, status, ct);
                    r.Speed = s;
                    var (minRead, minIops) = Expected(d);
                    if (minRead > 0 && s.SeqReadMBs < minRead)
                    {
                        slowSpeed = true;
                        r.Problems.Add($"Leitura de {s.SeqReadMBs:0} MB/s, abaixo do esperado para {d.Type} (mínimo ~{minRead:0} MB/s)." +
                                       (d.IsNvme ? " Confira se está no slot M.2 certo (PCIe da CPU) e se não esquenta." :
                                        d.IsHdd ? " HD lento assim costuma estar desgastado ou muito fragmentado." : " Confira o cabo SATA e se a porta é SATA 3 (6 Gb/s)."));
                    }
                    else if (minIops > 0 && s.RandIops < minIops)
                    {
                        slowSpeed = true;
                        r.Problems.Add($"Acesso aleatório lento ({s.RandIops:N0} IOPS, esperado ~{minIops:N0}+): programas e jogos abrem mais devagar.");
                    }
                    else r.Good.Add($"Velocidade: leitura {s.SeqReadMBs:0} MB/s, gravação {s.SeqWriteMBs:0} MB/s, {s.RandIops:N0} IOPS.");
                    if (d.IsUsb && s.SeqReadMBs < 45) r.Problems.Add("Muito lento para USB 3: o disco pode estar numa porta USB 2.0 (preta). Use uma porta azul/USB-C.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { r.SpeedSkipped = $"não deu para medir: {ex.Message}"; }
                finally
                {
                    if (!IsSystemTemp(folder)) try { Directory.Delete(folder, true); } catch { }
                }
            }
        }

        // Veredito final.
        r.Verdict = health == "Crítico" || unreadable > 0 ? "Ruim"
            : health == "Atenção" || slowSpeed || r.SlowBlocks > (d.IsHdd ? 60 : 30) ? "Atenção"
            : "Bom";
        r.Summary = r.Verdict switch
        {
            "Ruim" => "O disco mostra sinais de falha. Copie seus arquivos importantes para outro lugar o quanto antes.",
            "Atenção" => "Funciona, mas tem pontos para acompanhar. Rode o teste de novo em algumas semanas.",
            _ => d.SmartAvailable ? "Saúde, setores e velocidade estão normais." : "Setores e velocidade normais. (Este disco não informa dados SMART, comum em USB.)",
        };
        r.Status = ct.IsCancellationRequested ? "Interrompido" : "Concluído";
    }

    /// <summary>Pasta para o arquivo de teste: no disco do Windows usa a pasta temporária; nos outros, uma pasta na raiz.</summary>
    private static string? TestFolder(DiskHealth d, out string skip)
    {
        skip = "";
        var letters = d.Volumes.Select(v => v.Split(':')[0]).Where(l => l.Length == 1).ToList();
        if (letters.Count == 0) { skip = "sem partição com letra (não dá para gravar o arquivo de teste)"; return null; }
        var sys = Path.GetPathRoot(Environment.SystemDirectory)!.Substring(0, 1);
        foreach (var l in letters.OrderByDescending(l => l == sys))
        {
            var drive = new DriveInfo(l);
            if (!drive.IsReady) continue;
            if (drive.AvailableFreeSpace < 3L << 30) { skip = $"pouco espaço livre em {l}: (precisa de 3 GB)"; continue; }
            return l == sys ? Path.GetTempPath() : Path.Combine(drive.Name, "SoNexus-teste.tmp");
        }
        if (skip.Length == 0) skip = "partição indisponível";
        return null;
    }

    private static bool IsSystemTemp(string folder) =>
        Path.GetFullPath(folder).TrimEnd('\\').Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Relatório em texto (para copiar e mandar num fórum/suporte).</summary>
    public static string Report(IEnumerable<DiskCheckResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SoNexus · teste geral de discos · {DateTime.Now:dd/MM/yyyy HH:mm}");
        foreach (var r in results)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.Title} ({r.Subtitle})");
            sb.AppendLine($"  Resultado: {r.VerdictText}");
            foreach (var p in r.Problems) sb.AppendLine($"  ! {p}");
            foreach (var g in r.Good) sb.AppendLine($"  ✔ {g}");
            if (r.SpeedSkipped != null) sb.AppendLine($"  Velocidade não medida: {r.SpeedSkipped}");
        }
        return sb.ToString();
    }
}
