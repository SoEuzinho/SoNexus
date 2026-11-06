using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using SoNexus.Models;

namespace SoNexus.Services;

public enum HealthLevel { Ok, Info, Warn, Critical }

/// <summary>Uma conclusão sobre uma peça, em linguagem simples, com o dado que a justifica.</summary>
public sealed record HealthTip(string Part, HealthLevel Level, string Title, string Detail)
{
    public string LevelText => Level switch
    {
        HealthLevel.Critical => "Urgente",
        HealthLevel.Warn => "Atenção",
        HealthLevel.Info => "Dica",
        _ => "OK",
    };

    /// <summary>Ícone (Segoe MDL2): nunca só cor para indicar o estado.</summary>
    public string Glyph => Level switch
    {
        HealthLevel.Critical => "", // erro
        HealthLevel.Warn => "",     // aviso
        HealthLevel.Info => "",     // informação
        _ => "",                    // ok
    };

    public bool IsProblem => Level is HealthLevel.Warn or HealthLevel.Critical;
}

/// <summary>
/// Confere cada peça e diz o que está bom, o que dá para melhorar e o que é urgente.
/// Só afirma o que consegue medir/ler no próprio PC: sem tabela de pontuação.
/// </summary>
public static class HardwareHealth
{
    public const string Cpu = "Processador";
    public const string Gpu = "Placa de vídeo";
    public const string Ram = "Memória RAM";
    public const string Storage = "Armazenamento";
    public const string Board = "Placa-mãe";
    public const string Power = "Energia e fonte";

    public static List<HealthTip> Check()
    {
        var list = new List<HealthTip>();
        Run(list, Ram, CheckRam);
        Run(list, Storage, CheckStorage);
        Run(list, Board, CheckBoard);
        Run(list, Gpu, CheckGpu);
        Run(list, Cpu, CheckCpu);
        Run(list, Power, CheckPower);
        return list.OrderByDescending(t => t.Level).ToList();
    }

    public static List<HealthTip> For(string part, List<HealthTip>? all = null) =>
        (all ?? Check()).Where(t => t.Part == part).ToList();

    private static void Run(List<HealthTip> list, string part, Action<List<HealthTip>> check)
    {
        var before = list.Count;
        try { check(list); }
        catch (Exception ex) { AppLog.Write($"Saúde ({part}): {ex}"); }
        if (list.Count == before) list.Add(new(part, HealthLevel.Ok, "Nada para corrigir", "Não encontramos problemas nesta peça."));
    }

    // ---------- Memória ----------

    private static void CheckRam(List<HealthTip> list)
    {
        var layout = MemoryInfoService.Layout();
        var mods = layout.Modules;
        if (mods.Count == 0) return;

        var total = (ulong)mods.Sum(m => (long)m.Capacity);
        if (mods.Count == 1)
        {
            var free = layout.Slots > 1 ? $" Você tem {layout.Slots - 1} slot(s) livre(s)." : "";
            list.Add(new(Ram, HealthLevel.Warn, "Memória em canal único (single channel)",
                $"Só 1 pente instalado: a RAM entrega metade da banda que poderia. Em jogos isso costuma tirar FPS, principalmente o 1% low (engasgos). " +
                $"Coloque um 2º pente igual ({Format.Bytes(mods[0].Capacity)}, mesma velocidade) no slot indicado no manual da placa-mãe (normalmente A2 + B2).{free}"));
        }
        else if (mods.Count == 3)
        {
            list.Add(new(Ram, HealthLevel.Info, "Número ímpar de pentes",
                "Com 3 pentes, parte da memória roda em canal único. 2 ou 4 pentes iguais dão o melhor desempenho."));
        }

        // Velocidade: o "nominal" do pente aparece no código (ex.: JHE5600U..., KF556C40...). Se rodar abaixo, o EXPO/XMP está desligado.
        var running = layout.Speed;
        var nominal = mods.Select(m => Math.Max(m.RatedMts, SpeedFromPart(m.Part))).Where(v => v > 0).DefaultIfEmpty(0u).Min();
        if (running > 0 && nominal > running + 100)
        {
            var profile = Hardware.IsAmd() ? "EXPO (ou DOCP)" : "XMP";
            list.Add(new(Ram, HealthLevel.Warn, $"RAM abaixo da velocidade: {running} de {nominal} MT/s",
                $"Os pentes aguentam {nominal} MT/s, mas estão rodando a {running} MT/s (o padrão de fábrica). " +
                $"Ative o perfil {profile} na BIOS da placa-mãe para usar a velocidade certa. É seguro e reversível; se o PC não ligar, a BIOS volta ao padrão sozinha ou com reset (CMOS)."));
        }

        if (total < 16UL << 30)
            list.Add(new(Ram, HealthLevel.Warn, "Pouca memória",
                $"{Format.Bytes(total)} de RAM. Jogos e navegadores atuais pedem 16 GB; com menos, aparecem travadinhas e o Windows usa o disco como memória."));
        else if (total < 32UL << 30)
            list.Add(new(Ram, HealthLevel.Info, $"{Format.Bytes(total)} de RAM",
                "Suficiente para jogar. Se você deixa jogo, navegador, Discord e programas de edição abertos juntos, 32 GB evita usar o disco como memória."));

        var usage = MemoryService.GetStatus();
        if (usage.Total > 0 && usage.Load >= 90)
            list.Add(new(Ram, HealthLevel.Warn, $"RAM quase cheia agora ({usage.Load}%)",
                "Feche programas que você não está usando. Veja quem gasta mais na aba Memória RAM → Info."));
    }

    /// <summary>Velocidade escrita no código do pente (ex.: "JHE5600U4616JG" → 5600). 0 se não achar.</summary>
    public static uint SpeedFromPart(string part)
    {
        foreach (Match m in Regex.Matches(part ?? "", @"(?<!\d)(2133|2400|2666|2933|3000|3200|3600|4000|4266|4400|4800|5200|5600|6000|6200|6400|6600|6800|7200|7600|8000|8400)(?!\d)"))
            return uint.Parse(m.Value);
        // Kingston FURY: KF + geração + velocidade/100 ("KF556C40" = DDR5-5600, "KF432C16" = DDR4-3200)
        var k = Regex.Match(part ?? "", @"KF[45](\d{2})C", RegexOptions.IgnoreCase);
        return k.Success ? uint.Parse(k.Groups[1].Value) * 100 : 0;
    }

    // ---------- Armazenamento ----------

    private static void CheckStorage(List<HealthTip> list)
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        using (var s = new ManagementObjectSearcher("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            foreach (ManagementObject v in s.Get())
            {
                using (v)
                {
                    var id = v["DeviceID"]?.ToString() ?? "";
                    var size = Convert.ToUInt64(v["Size"] ?? 0);
                    var free = Convert.ToUInt64(v["FreeSpace"] ?? 0);
                    if (size == 0) continue;
                    var pct = 100.0 * (size - free) / size;
                    var isSystem = id.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);
                    if (pct >= 95)
                        list.Add(new(Storage, HealthLevel.Critical, $"Unidade {id} quase sem espaço ({pct:0}% usado)",
                            $"Só {Format.Bytes(free)} livres. {(isSystem ? "Com o disco do Windows cheio, atualizações falham e o PC fica lento." : "Jogos podem falhar ao atualizar.")} Libere espaço."));
                    else if (pct >= 85 && isSystem)
                        list.Add(new(Storage, HealthLevel.Warn, $"Disco do Windows ({id}) com {pct:0}% usado",
                            $"Restam {Format.Bytes(free)}. Deixe pelo menos 15% livre no disco do sistema: SSD cheio fica mais lento e o Windows precisa de espaço para atualizar e para a memória virtual. " +
                            "Mova jogos para outra unidade ou use Limpeza de Disco."));
                }
            }
        }

        try
        {
            using var d = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, HealthStatus, MediaType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in d.Get())
            {
                using (o)
                {
                    var name = o["FriendlyName"]?.ToString() ?? "Disco";
                    var health = Convert.ToInt32(o["HealthStatus"] ?? 0);
                    if (health == 1)
                        list.Add(new(Storage, HealthLevel.Warn, $"{name}: o Windows marcou atenção",
                            "Faça backup dos arquivos importantes e rode o Teste de disco desta seção."));
                    else if (health == 2)
                        list.Add(new(Storage, HealthLevel.Critical, $"{name}: disco com problemas",
                            "O próprio disco está avisando falha. Faça backup agora e planeje a troca."));
                }
            }
        }
        catch { }
    }

    // ---------- Placa-mãe ----------

    private static void CheckBoard(List<HealthTip> list)
    {
        using var s = new ManagementObjectSearcher("SELECT ReleaseDate, SMBIOSBIOSVersion FROM Win32_BIOS");
        foreach (ManagementObject b in s.Get())
        {
            using (b)
            {
                if (b["ReleaseDate"]?.ToString() is not { Length: > 0 } raw) continue;
                var date = ManagementDateTimeConverter.ToDateTime(raw);
                var months = (DateTime.Today - date).TotalDays / 30.4;
                if (months >= 12)
                    list.Add(new(Board, HealthLevel.Info, $"BIOS de {date:MM/yyyy} ({months:0} meses)",
                        "Fabricantes lançam BIOS novas com correções de estabilidade, memória e segurança. Veja no site da sua placa-mãe se há versão mais nova e leia as notas antes de atualizar (não desligue o PC durante a atualização)."));
            }
        }
    }

    // ---------- Placa de vídeo ----------

    private static void CheckGpu(List<HealthTip> list)
    {
        var virtuals = new List<string>();
        using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
        {
            foreach (ManagementObject g in s.Get())
            {
                using (g)
                {
                    var name = g["Name"]?.ToString() ?? "";
                    if (SystemInfoService.IsVirtualAdapter(name, g["PNPDeviceID"]?.ToString())) virtuals.Add(name);
                }
            }
        }
        if (virtuals.Count > 0)
            list.Add(new(Gpu, HealthLevel.Info, "Adaptador virtual ignorado",
                $"{string.Join(", ", virtuals.Distinct())} é criado por um programa (VR, streaming ou acesso remoto), não é uma placa física. O SoNexus não conta ele como sua placa de vídeo."));

        var (temp, _, _, _) = HardwareService.GpuNow(HardwareService.Instance.Readings);
        if (temp is > 85)
            list.Add(new(Gpu, HealthLevel.Warn, $"Placa de vídeo quente: {temp:0} °C",
                "Acima de 85 °C a placa reduz o clock para se proteger. Limpe poeira, confira as ventoinhas do gabinete e a curva de ventoinha."));

        var (w, h, hz, maxHz) = GamesService.PrimaryDisplay();
        if (maxHz > hz && hz > 0)
            list.Add(new(Gpu, HealthLevel.Warn, $"Monitor em {hz} Hz, mas aceita {maxHz} Hz",
                $"Resolução {w}x{h}. Ajuste em Configurações > Sistema > Tela > Tela avançada > Taxa de atualização."));
    }

    // ---------- Processador ----------

    private static void CheckCpu(List<HealthTip> list)
    {
        var (temp, _, _) = HardwareService.CpuNow(HardwareService.Instance.Readings);
        if (temp is > 95)
            list.Add(new(Cpu, HealthLevel.Critical, $"Processador muito quente: {temp:0} °C",
                "Confira se o cooler está bem encaixado, se a ventoinha gira e se a pasta térmica não secou."));
        else if (temp is > 85)
            list.Add(new(Cpu, HealthLevel.Info, $"Processador a {temp:0} °C",
                "Ryzen 7000/9000 e Intel recentes sobem até ~95 °C de propósito sob carga. Se isso acontece parado (sem jogo ou programa pesado), revise o cooler."));
        else if (temp == null && !SensorDriver.IsAdmin)
            list.Add(new(Cpu, HealthLevel.Info, "Temperatura do processador indisponível",
                "Abra o SoNexus como administrador (e instale o driver PawnIO pela tela Início) para ler temperatura, consumo e tensões."));
    }

    // ---------- Energia ----------

    private static void CheckPower(List<HealthTip> list)
    {
        var r = HardwareService.Instance.Readings;
        var cpu = HardwareService.Find(r, HardwareService.IsCpu, SensorType.Power, true, "CPU Package", "Package");
        var gpu = HardwareService.Find(r, HardwareService.GpuFilter(r), SensorType.Power, true, "GPU Package", "GPU Power", "GPU Total");
        var peak = (cpu?.Max ?? cpu?.Value ?? 0) + (gpu?.Max ?? gpu?.Value ?? 0);
        if (peak <= 0) return;

        // Resto do PC (placa-mãe, RAM, discos, ventoinhas, RGB): ~75 W. Margem de 40% para picos e para a fonte trabalhar folgada.
        var system = peak + 75;
        var recommended = Math.Ceiling(system * 1.4 / 50) * 50;
        list.Add(new(Power, HealthLevel.Info, $"Fonte recomendada: {recommended:0} W ou mais",
            $"Maior consumo medido desde que o SoNexus abriu: CPU + placa de vídeo ≈ {peak:0} W (+ ~75 W do resto do PC). " +
            "O Windows não informa qual fonte está instalada; confira a etiqueta dela. Para um número mais fiel, rode o Stress test e volte aqui. " +
            "Prefira fontes 80 Plus de marca conhecida."));
    }
}

/// <summary>Atalhos pequenos sobre o processador (fabricante).</summary>
internal static class Hardware
{
    private static bool? _amd;

    public static bool IsAmd()
    {
        if (_amd is { } v) return v;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
                using (o) _amd = (o["Manufacturer"]?.ToString() ?? "").Contains("AMD", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return _amd ??= false;
    }
}
