using System.Text.RegularExpressions;

namespace SoNexus.Services;

/// <summary>
/// Desempenho relativo aproximado em jogos (1080p). Placas: GTX 1060 6GB = 100. Processadores: Ryzen 5 3600 = 100.
/// Valores médios de reviews públicas; servem para estimativas, não são medições do seu PC.
/// </summary>
public static class PerformanceTables
{
    public static readonly (string Key, int Score)[] Gpus =
    {
        // NVIDIA (antigas: aparecem muito nos requisitos de jogos mais velhos)
        ("GTX 460", 28), ("GTX 470", 33), ("GTX 480", 38), ("GTX 550 TI", 22), ("GTX 560 TI", 32), ("GTX 560", 29), ("GTX 570", 37), ("GTX 580", 42),
        ("GTX 650 TI", 33), ("GTX 650", 22), ("GTX 660 TI", 50), ("GTX 660", 45), ("GTX 670", 57), ("GTX 680", 62), ("GTX 690", 90),
        ("GTX 750", 33), ("GTX 760", 52), ("GTX 770", 65), ("GTX 780 TI", 85), ("GTX 780", 72), ("GTX TITAN X", 125), ("GTX TITAN", 80),
        ("GT 730", 12), ("GT 1030", 25), ("GTX 750 TI", 38), ("GTX 950", 50), ("GTX 960", 60), ("GTX 970", 85), ("GTX 980 TI", 125), ("GTX 980", 100),
        ("GTX 1050 TI", 60), ("GTX 1050", 50), ("GTX 1060 3", 90), ("GTX 1060", 100), ("GTX 1070 TI", 145), ("GTX 1070", 130),
        ("GTX 1080 TI", 205), ("GTX 1080", 160), ("GTX 1630", 45), ("GTX 1650 SUPER", 92), ("GTX 1650", 72),
        ("GTX 1660 SUPER", 118), ("GTX 1660 TI", 120), ("GTX 1660", 105),
        ("RTX 2060 SUPER", 160), ("RTX 2060", 140), ("RTX 2070 SUPER", 185), ("RTX 2070", 165), ("RTX 2080 SUPER", 210),
        ("RTX 2080 TI", 245), ("RTX 2080", 200), ("RTX 3050", 125), ("RTX 3060 TI", 215), ("RTX 3060", 170),
        ("RTX 3070 TI", 260), ("RTX 3070", 245), ("RTX 3080 TI", 330), ("RTX 3080", 300), ("RTX 3090 TI", 365), ("RTX 3090", 340),
        ("RTX 4060 TI", 240), ("RTX 4060", 200), ("RTX 4070 TI SUPER", 390), ("RTX 4070 TI", 365), ("RTX 4070 SUPER", 340),
        ("RTX 4070", 300), ("RTX 4080 SUPER", 450), ("RTX 4080", 440), ("RTX 4090", 560),
        ("RTX 5050", 190), ("RTX 5060 TI", 280), ("RTX 5060", 235), ("RTX 5070 TI", 450), ("RTX 5070", 360), ("RTX 5080", 500), ("RTX 5090", 680),
        // AMD (antigas)
        ("HD 5770", 22), ("HD 5850", 28), ("HD 5870", 32), ("HD 6850", 27), ("HD 6870", 30), ("HD 6950", 36), ("HD 6970", 40),
        ("HD 7770", 30), ("HD 7790", 38), ("HD 7850", 48), ("HD 7870", 55), ("HD 7950", 62), ("HD 7970", 70),
        ("R7 260X", 38), ("R7 370", 45), ("R9 270X", 55), ("R9 270", 50), ("R9 280X", 70), ("R9 280", 62), ("R9 285", 65),
        ("R9 290X", 88), ("R9 290", 82), ("R9 380X", 72), ("R9 380", 68), ("R9 390X", 95), ("R9 390", 90), ("R9 FURY X", 110), ("R9 FURY", 102),
        ("RX 460", 55), ("RX 470", 85), ("RX 480", 95), ("RX 550", 35), ("RX 560", 55), ("RX 570", 90), ("RX 580", 100), ("RX 590", 108),
        ("VEGA 56", 135), ("VEGA 64", 150), ("RADEON VII", 185), ("RX 5500 XT", 95), ("RX 5600 XT", 150), ("RX 5700 XT", 190), ("RX 5700", 170),
        ("RX 6400", 60), ("RX 6500 XT", 85), ("RX 6600 XT", 190), ("RX 6650 XT", 200), ("RX 6600", 165), ("RX 6700 XT", 245), ("RX 6750 XT", 260),
        ("RX 6700", 225), ("RX 6800 XT", 345), ("RX 6800", 300), ("RX 6900 XT", 365), ("RX 6950 XT", 385),
        ("RX 7600 XT", 210), ("RX 7600", 200), ("RX 7700 XT", 290), ("RX 7800 XT", 345), ("RX 7900 GRE", 375), ("RX 7900 XTX", 490), ("RX 7900 XT", 430),
        ("RX 9060 XT", 265), ("RX 9070 XT", 450), ("RX 9070", 400),
        // Intel
        ("ARC A380", 70), ("ARC A580", 150), ("ARC A750", 175), ("ARC A770", 190), ("ARC B570", 190), ("ARC B580", 215),
        // Vídeo integrado
        ("RADEON 890M", 95), ("RADEON 780M", 85), ("RADEON 760M", 70), ("RADEON 680M", 75), ("VEGA 11", 35), ("VEGA 8", 30),
        ("IRIS XE", 35), ("UHD GRAPHICS 770", 22), ("UHD GRAPHICS 630", 15), ("UHD GRAPHICS", 14), ("HD GRAPHICS", 10),
        ("RADEON(TM) GRAPHICS", 20), ("RADEON GRAPHICS", 20),
    };

    public static readonly (string Key, int Score)[] Cpus =
    {
        // Intel
        ("I5-750", 35), ("I5 750", 35), ("I5-2500K", 50), ("I5-2400", 46), ("I7-2600K", 55), ("I7-2600", 53), ("I5-3570K", 55), ("I5-3470", 52), ("I7-3770K", 62), ("I7-3770", 60),
        ("I5-4460", 58), ("I5-4590", 60), ("I5-4670K", 63), ("I7-4770K", 70), ("I7-4770", 68), ("I7-4790K", 75), ("I7-4790", 72),
        ("I3-2100", 32), ("I3-3220", 36), ("I3-4130", 40), ("I3-4160", 42), ("I3-6100", 50), ("I3-7100", 53), ("I3-9100", 68),
        ("I5-2300", 44), ("I5-2320", 45), ("I5-2500", 48), ("I5-3330", 50), ("I5-3550", 53), ("I5-4430", 55), ("I5-4570", 59), ("I5-4690K", 64), ("I5-4690", 62),
        ("I7-920", 35), ("I7-950", 38), ("I7-3820", 58), ("I7-4930K", 66), ("I7-5820K", 75), ("I7-6800K", 80),
        ("I5-6400", 62), ("I5-6500", 65), ("I5-6600K", 72), ("I5-6600", 70), ("I7-6700K", 82), ("I7-6700", 78),
        ("I5-7400", 68), ("I5-7500", 71), ("I5-7600K", 78), ("I5-7600", 75), ("I7-7700K", 88), ("I7-7700", 84),
        ("I3-8100", 62), ("I5-8400", 82), ("I5-8500", 84), ("I5-8600K", 88), ("I5-9500", 86), ("I5-10500", 95), ("I7-8700K", 96), ("I7-8700", 92), ("I5-9400F", 85), ("I5-9400", 85), ("I5-9600K", 92),
        ("I7-9700K", 100), ("I7-9700", 98), ("I9-9900K", 105), ("I3-10100", 78), ("I5-10400", 92), ("I5-10600K", 102), ("I7-10700K", 110), ("I7-10700", 108), ("I9-10900K", 115),
        ("I5-11400", 105), ("I5-11600K", 112), ("I7-11700K", 118), ("I9-11900K", 122), ("I3-12100", 105), ("I5-12400", 125), ("I5-12600K", 140),
        ("I7-12700K", 150), ("I7-12700", 145), ("I9-12900K", 155), ("I5-13400", 130), ("I5-13600K", 155), ("I7-13700K", 165), ("I9-13900K", 175),
        ("I5-14400", 135), ("I5-14600K", 160), ("I7-14700K", 170), ("I9-14900K", 178), ("ULTRA 5 245K", 150), ("ULTRA 7 265K", 160), ("ULTRA 9 285K", 170),
        // AMD
        ("FX-4300", 30), ("FX-6100", 33), ("FX-6300", 38), ("FX-8120", 40), ("FX-8320", 43), ("FX-8350", 45), ("FX-9590", 50),
        ("PHENOM II X4", 30), ("PHENOM II X6", 35), ("ATHLON II X4", 25), ("A10-7850K", 35), ("A8-7600", 30), ("RYZEN 3 1200", 50), ("RYZEN 3 1300X", 55), ("RYZEN 5 1400", 58), ("RYZEN 5 1500X", 62), ("RYZEN 5 1600X", 67), ("RYZEN 5 1600", 65),
        ("RYZEN 7 1700X", 70), ("RYZEN 7 1700", 68), ("RYZEN 3 2200G", 55), ("RYZEN 5 2400G", 62), ("RYZEN 5 2600X", 78), ("RYZEN 5 2600", 75), ("RYZEN 7 2700X", 82),
        ("RYZEN 3 3100", 80), ("RYZEN 3 3300X", 90), ("RYZEN 5 3500X", 90), ("RYZEN 5 3600X", 103), ("RYZEN 5 3600", 100), ("RYZEN 7 3700X", 105), ("RYZEN 7 3800X", 107),
        ("RYZEN 9 3900X", 108), ("RYZEN 9 3950X", 110), ("RYZEN 5 4500", 98), ("RYZEN 5 5500", 108), ("RYZEN 5 5600X", 128), ("RYZEN 5 5600G", 115), ("RYZEN 5 5600", 125),
        ("RYZEN 7 5700X3D", 150), ("RYZEN 7 5700X", 130), ("RYZEN 7 5700G", 120), ("RYZEN 7 5800X3D", 160), ("RYZEN 7 5800X", 133), ("RYZEN 9 5900X", 135), ("RYZEN 9 5950X", 137),
        ("RYZEN 5 7500F", 148), ("RYZEN 5 7600X", 155), ("RYZEN 5 7600", 150), ("RYZEN 7 7700X", 162), ("RYZEN 7 7700", 158), ("RYZEN 7 7800X3D", 195),
        ("RYZEN 9 7900X3D", 185), ("RYZEN 9 7900X", 165), ("RYZEN 9 7900", 160), ("RYZEN 9 7950X3D", 198), ("RYZEN 9 7950X", 168),
        ("RYZEN 5 9600X", 168), ("RYZEN 7 9700X", 172), ("RYZEN 7 9800X3D", 215), ("RYZEN 9 9900X", 175), ("RYZEN 9 9950X3D", 215), ("RYZEN 9 9950X", 178),
    };

    // Ordena da chave mais longa para a mais curta: "RTX 4070 TI SUPER" antes de "RTX 4070".
    private static readonly (string Key, int Score)[] GpusByLength = Gpus.OrderByDescending(g => g.Key.Length).ToArray();
    private static readonly (string Key, int Score)[] CpusByLength = Cpus.OrderByDescending(c => c.Key.Length).ToArray();

    private static string Normalize(string s) =>
        " " + s.ToUpperInvariant().Replace("™", "").Replace("(R)", "").Replace("®", "").Replace("GEFORCE", "").Replace("NVIDIA", "")
            .Replace("INTEL CORE", "").Replace("CORE ", "").Replace("AMD ", "").Replace("  ", " ") + " ";

    /// <summary>Todas as placas citadas no texto (sem contar a mesma palavra duas vezes).</summary>
    public static List<int> FindGpus(string text) => FindAll(Normalize(text), GpusByLength, false);

    public static List<int> FindCpus(string text) =>
        FindAll(Normalize(text).Replace("CORE I", "I").Replace(" I3 ", " I3-").Replace(" I7 ", " I7-").Replace(" I5 ", " I5-").Replace(" I9 ", " I9-"), CpusByLength, true);

    public static int? GpuScore(string name)
    {
        if (FindGpus(name).Cast<int?>().FirstOrDefault() is not { } score) return null;
        // A versão de notebook da mesma placa roda com menos energia: ~20% mais lenta.
        var laptop = Regex.IsMatch(name, @"LAPTOP|MOBILE|MAX-Q|\bMX\d", RegexOptions.IgnoreCase);
        return laptop ? (int)(score * 0.8) : score;
    }

    public static int? CpuScore(string name) => FindCpus(name).Cast<int?>().FirstOrDefault() ?? EstimateCpu(name);

    /// <summary>
    /// Processador fora da tabela (notebooks, modelos novos): estima pela família e geração do nome.
    /// Ex.: "i5-1135G7" = 11ª geração, notebook; "Ryzen 7 5800H" = série 5000, notebook.
    /// </summary>
    public static int? EstimateCpu(string name)
    {
        var n = name.ToUpperInvariant();
        double mobile(string suffix) => suffix switch
        {
            var s when s.StartsWith('U') || s.StartsWith('Y') => 0.72,
            var s when s.StartsWith('G') || s.StartsWith('P') => 0.8,
            var s when s.StartsWith('H') => 0.92,
            _ => 1.0,
        };

        var intel = Regex.Match(n, @"I([3579])[- ](\d{4,5})([A-Z]*\d?)");
        if (intel.Success)
        {
            var digits = intel.Groups[2].Value;
            // 5 dígitos (12400) e notebooks de 4 dígitos começando com 1 (1135G7, 1235U) = geração de 2 dígitos.
            var gen = digits.Length == 5 || digits[0] == '1' ? int.Parse(digits[..2]) : int.Parse(digits[..1]);
            var i5 = gen switch { <= 2 => 48, 3 => 52, 4 => 60, 5 => 62, 6 => 65, 7 => 70, 8 => 82, 9 => 85, 10 => 92, 11 => 105, 12 => 125, 13 => 130, _ => 135 };
            var tier = intel.Groups[1].Value switch { "3" => 0.8, "7" => 1.12, "9" => 1.2, _ => 1.0 };
            return (int)(i5 * tier * mobile(intel.Groups[3].Value));
        }
        var ultra = Regex.Match(n, @"ULTRA ([579]) (\d)\d\d([A-Z]*)");
        if (ultra.Success)
        {
            var desktop = ultra.Groups[2].Value == "2" && ultra.Groups[3].Value.StartsWith('K');
            var tier = ultra.Groups[1].Value switch { "5" => 0.92, "9" => 1.06, _ => 1.0 };
            return (int)((desktop ? 160 : 125) * tier);
        }
        var ryzen = Regex.Match(n, @"RYZEN (?:AI )?([3579]) (?:PRO )?(\d)\d{2,3}([A-Z0-9]*)");
        if (ryzen.Success)
        {
            var series = ryzen.Groups[2].Value[0] - '0';
            var r5 = series switch { 1 => 65, 2 => 75, 3 => 100, 4 => 95, 5 => 125, 6 => 130, 7 => 150, 8 => 150, _ => 168 };
            var tier = ryzen.Groups[1].Value switch { "3" => 0.8, "7" => 1.07, "9" => 1.1, _ => 1.0 };
            var x3d = ryzen.Groups[3].Value.Contains("X3D") ? 1.25 : 1.0;
            return (int)(r5 * tier * x3d * mobile(ryzen.Groups[3].Value));
        }
        return null;
    }

    // Sufixos de processador que não mudam o desempenho em jogos (F = sem vídeo integrado, K = desbloqueado...).
    private static readonly string[] CpuSuffixes = { "F", "KF", "K", "KS", "T", "E", "S" };

    private static List<int> FindAll(string text, (string Key, int Score)[] table, bool cpu)
    {
        var used = new bool[text.Length];
        var found = new List<(int Pos, int Score)>();
        foreach (var (key, score) in table)
        {
            var start = 0;
            while ((start = text.IndexOf(key, start, StringComparison.Ordinal)) >= 0)
            {
                var end = start + key.Length;
                // A chave precisa terminar numa fronteira ("RTX 4060" não pode casar dentro de "RTX 4060TI").
                var boundary = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (!boundary && cpu)
                {
                    // "I5-12400F" casa com "I5-12400".
                    var tail = new string(text[end..].TakeWhile(char.IsLetter).ToArray());
                    var after = end + tail.Length;
                    boundary = CpuSuffixes.Contains(tail) && (after >= text.Length || !char.IsLetterOrDigit(text[after]));
                }
                if (boundary && !used.Skip(start).Take(key.Length).Any(u => u))
                {
                    for (var i = start; i < end; i++) used[i] = true;
                    found.Add((start, score));
                }
                start = end;
            }
        }
        return found.OrderBy(f => f.Pos).Select(f => f.Score).ToList();
    }
}
