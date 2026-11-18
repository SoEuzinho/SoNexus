using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

internal static class ComponentInventory
{
    internal sealed record Component(string File, string Package, string Version, string Description);

    internal static List<Component> Read(string directory)
    {
        var packages = ReadPackages(directory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        var result = new List<Component>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", options))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var package = packages.GetValueOrDefault(relative) ?? packages.GetValueOrDefault(Path.GetFileName(file)) ?? "—";
            string? description = null;
            string? version = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                description = info.FileDescription;
                version = info.FileVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Ainda mostra o arquivo quando o metadado opcional não está disponível.
            }

            result.Add(new Component(relative, package, string.IsNullOrWhiteSpace(version) ? "—" : version,
                string.IsNullOrWhiteSpace(description) ? "Descrição indisponível no arquivo." : description));
        }
        return result.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadPackages(string directory)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "SoNexus.deps.json")));
            var root = manifest.RootElement;
            var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString();
            if (targetName == null || !root.GetProperty("targets").TryGetProperty(targetName, out var target))
                return packages;
            foreach (var package in target.EnumerateObject())
            {
                foreach (var section in new[] { "runtime", "native", "resources", "runtimeTargets" })
                {
                    if (!package.Value.TryGetProperty(section, out var assets)) continue;
                    foreach (var asset in assets.EnumerateObject())
                    {
                        if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        packages.TryAdd(asset.Name, package.Name);
                        if (section != "resources") packages.TryAdd(Path.GetFileName(asset.Name), package.Name);
                        if (section == "resources" && asset.Value.TryGetProperty("locale", out var locale))
                            packages.TryAdd($"{locale.GetString()}/{Path.GetFileName(asset.Name)}", package.Name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            AppLog.Write($"Inventário de componentes: {ex.Message}");
        }
        return packages;
    }
}
