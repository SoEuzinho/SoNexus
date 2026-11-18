using System.IO;
using System.Text.Json;

namespace SoNexus.Services;

internal sealed record OverlayProfile(int Theme, int Corner, int Size, int Color, int Flow,
    int Opacity, bool Animate, string[] Items);

internal sealed class OverlayProfiles
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "overlay-profiles.json");

    private Dictionary<string, OverlayProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public OverlayProfiles()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, OverlayProfile>>(
                    File.ReadAllText(FilePath)) is { } saved)
            {
                foreach (var (path, profile) in saved)
                    if (profile?.Items != null) _profiles[path] = profile;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Não foi possível ler os perfis do painel: {ex.Message}");
        }
    }

    public OverlayProfile? ForExecutable(string? path) =>
        path == null ? null : _profiles.GetValueOrDefault(path);

    public void Set(string path, OverlayProfile? profile)
    {
        var updated = new Dictionary<string, OverlayProfile>(_profiles, StringComparer.OrdinalIgnoreCase);
        if (profile == null) updated.Remove(path);
        else updated[path] = profile;

        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
            File.Move(temporary, FilePath, overwrite: true);
            _profiles = updated;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
