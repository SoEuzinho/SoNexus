using System.Runtime.InteropServices;
using System.Text;

namespace SoNexus.Services;

public sealed record NvStatus(uint Temp, double PowerW, uint CoreMHz, uint MemMHz, uint? FanPercent);

public sealed record NvLimits(
    double PowerMinW, double PowerMaxW, double PowerDefaultW, double PowerCurrentW,
    int CoreMin, int CoreMax, int CoreCurrent, bool CoreSupported,
    int MemMin, int MemMax, int MemCurrent, bool MemSupported);

/// <summary>
/// Overclock de placas NVIDIA pela biblioteca oficial NVML (nvml.dll, instalada com o driver):
/// limite de energia e deslocamento de clock do núcleo e da memória.
/// </summary>
public static class Nvml
{
    private static bool _initialized;

    public sealed record Gpu(IntPtr Handle, string Name);

    public static List<Gpu> Init(out string? error)
    {
        error = null;
        try
        {
            if (!_initialized)
            {
                Check(nvmlInit_v2());
                _initialized = true;
            }
            Check(nvmlDeviceGetCount_v2(out var count));
            var list = new List<Gpu>();
            for (uint i = 0; i < count; i++)
            {
                Check(nvmlDeviceGetHandleByIndex_v2(i, out var h));
                var sb = new StringBuilder(96);
                nvmlDeviceGetName(h, sb, (uint)sb.Capacity);
                list.Add(new Gpu(h, sb.ToString()));
            }
            return list;
        }
        catch (DllNotFoundException)
        {
            error = "Nenhum driver NVIDIA encontrado (nvml.dll).";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return new();
    }

    public static NvStatus Status(Gpu g)
    {
        nvmlDeviceGetTemperature(g.Handle, 0, out var temp);
        nvmlDeviceGetPowerUsage(g.Handle, out var mw);
        nvmlDeviceGetClockInfo(g.Handle, 0, out var core);
        nvmlDeviceGetClockInfo(g.Handle, 2, out var mem);
        uint? fan = nvmlDeviceGetFanSpeed(g.Handle, out var f) == 0 ? f : null;
        return new NvStatus(temp, mw / 1000.0, core, mem, fan);
    }

    public static NvLimits Limits(Gpu g)
    {
        nvmlDeviceGetPowerManagementLimitConstraints(g.Handle, out var pMin, out var pMax);
        nvmlDeviceGetPowerManagementDefaultLimit(g.Handle, out var pDef);
        nvmlDeviceGetPowerManagementLimit(g.Handle, out var pCur);

        int coreMin = 0, coreMax = 0, coreCur = 0, memMin = 0, memMax = 0, memCur = 0;
        bool coreOk, memOk;
        try
        {
            coreOk = nvmlDeviceGetGpcClkVfOffset(g.Handle, out coreCur) == 0;
            if (nvmlDeviceGetGpcClkMinMaxVfOffset(g.Handle, out coreMin, out coreMax) != 0 || coreMax <= coreMin)
                (coreMin, coreMax) = (-300, 400); // driver não informou a faixa: usa uma conservadora
        }
        catch (EntryPointNotFoundException) { coreOk = false; }
        try
        {
            memOk = nvmlDeviceGetMemClkVfOffset(g.Handle, out memCur) == 0;
            if (nvmlDeviceGetMemClkMinMaxVfOffset(g.Handle, out memMin, out memMax) != 0 || memMax <= memMin)
                (memMin, memMax) = (-1000, 2000);
        }
        catch (EntryPointNotFoundException) { memOk = false; }

        return new NvLimits(pMin / 1000.0, pMax / 1000.0, pDef / 1000.0, pCur / 1000.0,
            coreMin, coreMax, coreCur, coreOk, memMin, memMax, memCur, memOk);
    }

    /// <summary>Aplica os valores. Retorna null se deu certo ou a mensagem de erro.</summary>
    public static string? Apply(Gpu g, double powerW, int? coreOffset, int? memOffset)
    {
        var errors = new List<string>();
        var r = nvmlDeviceSetPowerManagementLimit(g.Handle, (uint)Math.Round(powerW * 1000));
        if (r != 0) errors.Add($"Limite de energia: {Message(r)}");
        if (coreOffset is { } c && (r = nvmlDeviceSetGpcClkVfOffset(g.Handle, c)) != 0) errors.Add($"Clock do núcleo: {Message(r)}");
        if (memOffset is { } m && (r = nvmlDeviceSetMemClkVfOffset(g.Handle, m)) != 0) errors.Add($"Clock da memória: {Message(r)}");
        return errors.Count == 0 ? null : string.Join("\n", errors);
    }

    public static string? Reset(Gpu g)
    {
        var l = Limits(g);
        return Apply(g, l.PowerDefaultW, l.CoreSupported ? 0 : null, l.MemSupported ? 0 : null);
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException(Message(result));
    }

    private static string Message(int code) => code switch
    {
        3 => "não suportado por esta placa/driver",
        4 => "sem permissão (execute como administrador)",
        2 => "valor fora do limite permitido",
        _ => Marshal.PtrToStringAnsi(nvmlErrorString(code)) ?? $"erro {code}",
    };

    // ---------- nvml.dll ----------

    private const string Dll = "nvml.dll";

    [DllImport(Dll)] private static extern int nvmlInit_v2();
    [DllImport(Dll)] private static extern IntPtr nvmlErrorString(int result);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint min, out uint max);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limit);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);
    [DllImport(Dll)] private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limit);
    [DllImport(Dll)] private static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr device, out int offset);
    [DllImport(Dll)] private static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr device, int offset);
    [DllImport(Dll)] private static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr device, out int min, out int max);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemClkVfOffset(IntPtr device, out int offset);
    [DllImport(Dll)] private static extern int nvmlDeviceSetMemClkVfOffset(IntPtr device, int offset);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemClkMinMaxVfOffset(IntPtr device, out int min, out int max);
}

public sealed record PowerPlan(Guid Id, string Name, bool Active)
{
    public string Display => Active ? $"{Name}  (ativo)" : Name;
    public override string ToString() => Display;
}

/// <summary>Planos de energia do Windows via powercfg.</summary>
public static partial class PowerPlans
{
    private const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static async Task<List<PowerPlan>> ListAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var (_, output, _) = await ProcessRunner.RunAsync("powercfg.exe", "/list", oem);
        return PlanRegex().Matches(output)
            .Select(m => new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .ToList();
    }

    public static async Task SetActiveAsync(Guid id) => await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {id}");

    /// <summary>Cria o plano "Desempenho Máximo" (escondido por padrão no Windows).</summary>
    public static async Task AddUltimateAsync() => await ProcessRunner.RunAsync("powercfg.exe", $"-duplicatescheme {UltimatePerformance}");

    [System.Text.RegularExpressions.GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)\s*(\*)?")]
    private static partial System.Text.RegularExpressions.Regex PlanRegex();
}
