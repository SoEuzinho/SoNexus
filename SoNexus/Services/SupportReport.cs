using System.Text;

namespace SoNexus.Services;

internal static class SupportReport
{
    public static string Build(DiagReport report, int days)
    {
        // Use only explicitly chosen fields. Event messages and device identifiers can contain private data.
        var text = new StringBuilder();
        text.AppendLine("Relatório de suporte do SoNexus");
        text.AppendLine($"SoNexus {typeof(App).Assembly.GetName().Version} · Windows {Environment.OSVersion.Version}");
        text.AppendLine($"Período analisado: últimos {days} dias");
        text.AppendLine();
        text.AppendLine($"Telas azuis: {report.Bluescreens}");
        text.AppendLine($"Desligamentos inesperados: {report.Shutdowns}");
        text.AppendLine($"Erros de hardware (WHEA): {report.Whea}");
        text.AppendLine($"Falhas de vídeo: {report.GpuResets}");
        text.AppendLine($"Erros de disco: {report.DiskErrors}");
        text.AppendLine($"Falhas de aplicativos: {report.AppCrashes.Sum(item => item.Count)}");
        text.AppendLine($"Dispositivos com problema: {report.Devices.Count}");
        foreach (var group in report.Devices.GroupBy(device => device.Code).OrderBy(group => group.Key))
            text.AppendLine($"  Código {group.Key}: {group.Count()}");

        var versions = DriverService.GetGpuDrivers().Select(driver => driver.Version)
            .Where(version => Version.TryParse(version, out _)).Distinct().Order().ToList();
        text.AppendLine();
        text.AppendLine(versions.Count == 0 ? "Versões dos drivers gráficos: indisponíveis"
            : $"Versões dos drivers gráficos: {string.Join(", ", versions)}");
        return text.ToString();
    }
}
