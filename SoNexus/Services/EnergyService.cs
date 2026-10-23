using System.Management;
using LibreHardwareMonitor.Hardware;

namespace SoNexus.Services;

public sealed record VoltageRow(string Name, string Source, string Value, string Min, string Max, string Status, bool Bad);

public sealed record PowerBudget(double CpuMaxW, double GpuMaxW, double RestW, int RecommendedPsuW);

/// <summary>Estimativas de consumo do PC inteiro, fonte recomendada e verificação das tensões.</summary>
public static class EnergyService
{
    /// <summary>
    /// O que os sensores não medem (placa-mãe, RAM, discos, ventoinhas, USB), estimado pelo hardware instalado.
    /// O monitor não entra na conta.
    /// </summary>
    public static double EstimateRestW()
    {
        double watts = 30; // placa-mãe, chipset, USB
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                watts += s.Get().Count * 4;
            using (var d = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject o in d.Get())
                {
                    using (o)
                    {
                        var media = Convert.ToInt32(o["MediaType"] ?? 0);
                        var bus = Convert.ToInt32(o["BusType"] ?? 0);
                        watts += media == 3 ? 7 : bus == 17 ? 4 : 3; // HDD, NVMe, SSD SATA
                    }
                }
            }
        }
        catch { }
        var fans = HardwareService.Instance.Readings.Count(r => r.Type == SensorType.Fan && r.Value > 0);
        return watts + fans * 2;
    }

    public static PowerBudget Budget(double restW)
    {
        var r = HardwareService.Instance.Readings;
        var cpuSeen = HardwareService.Find(r, HardwareService.IsCpu, SensorType.Power, true, "CPU Package", "Package")?.Max ?? 0;
        var cores = Environment.ProcessorCount / 2;
        double cpuTypical = cores switch { <= 6 => 120, <= 8 => 160, <= 12 => 230, _ => 253 };
        var cpuMax = Math.Max(cpuSeen, cpuTypical);

        double gpuMax = 0;
        var gpus = Nvml.Init(out _);
        if (gpus.FirstOrDefault() is { } g) gpuMax = Nvml.Limits(g).PowerMaxW;
        if (gpuMax <= 0) gpuMax = Math.Max(HardwareService.GpuNow(r).Power ?? 0, 150);

        // Soma dos picos + folga de 20% (picos rápidos da GPU e envelhecimento da fonte), arredondado para 50 W.
        var need = (cpuMax + gpuMax + restW + 30) * 1.2;
        var psu = (int)(Math.Ceiling(need / 50) * 50);
        return new PowerBudget(cpuMax, gpuMax, restW, psu);
    }

    private static readonly Dictionary<string, (string Min, string Max)> Range = new();

    /// <summary>Tensões com a faixa aceitável das linhas da fonte (padrão ATX ±5%) e da bateria da BIOS.</summary>
    public static List<VoltageRow> Voltages(IReadOnlyList<SensorReading> readings)
    {
        var rows = new List<VoltageRow>();
        foreach (var v in readings.Where(x => x.Type == SensorType.Voltage && x.Value > 0 && !x.Name.Contains('#')))
        {
            var value = v.Value!.Value;
            var (status, bad) = Check(v.Name, value);
            rows.Add(new VoltageRow(v.Name, v.Hardware, $"{value:0.000} V",
                v.Min is { } mn ? $"{mn:0.000}" : "—", v.Max is { } mx ? $"{mx:0.000}" : "—", status, bad));
        }
        return rows.OrderByDescending(r => r.Bad).ThenBy(r => r.Source).ToList();
    }

    private static (string Status, bool Bad) Check(string name, float v)
    {
        var n = name.ToUpperInvariant().Replace(" ", "");
        (double Lo, double Hi, string Label)? spec =
            n.Contains("12V") ? (11.4, 12.6, "Linha de 12 V da fonte") :
            n.Contains("5V") && !n.Contains("3") ? (4.75, 5.25, "Linha de 5 V da fonte") :
            n.Contains("3.3V") || n.Contains("3VCC") || n.Contains("+3.3") ? (3.135, 3.465, "Linha de 3,3 V") :
            n.Contains("3VSB") || n.Contains("AVSB") ? (3.135, 3.465, "Standby 3,3 V") :
            n.Contains("VBAT") || n.Contains("BATTERY") || n.Contains("CMOS") ? (2.8, 3.4, "Bateria da BIOS") :
            null;
        if (spec is not { } s) return ("", false);
        if (v >= s.Lo && v <= s.Hi) return ($"OK ({s.Lo:0.00}–{s.Hi:0.00} V)", false);
        return n.Contains("BAT") || n.Contains("CMOS")
            ? ("Bateria da BIOS fraca: troque a pilha CR2032", true)
            : ($"Fora da faixa ({s.Lo:0.00}–{s.Hi:0.00} V). Pode ser erro de leitura da placa; se o PC desliga sozinho, desconfie da fonte.", true);
    }
}
