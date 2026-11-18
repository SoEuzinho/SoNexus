using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoNexus.Services;

internal sealed record SmartSample(DateTimeOffset Time, int? Wear, ulong? MediaErrors, long? Reallocated, long? Pending, long? Uncorrectable)
{
    [JsonIgnore] public string DateText => Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    [JsonIgnore] public string WearText => Wear is { } used ? $"{used}%" : "—";

    public bool HasNewErrorsSince(SmartSample previous) =>
        MediaErrors > previous.MediaErrors || Reallocated > previous.Reallocated ||
        Pending > previous.Pending || Uncorrectable > previous.Uncorrectable;
}

internal static class SmartHistory
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoNexus", "smart-history.json");
    public static readonly TimeSpan SampleInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int MaxDisks = 32;
    private const int MaxSamples = 120;

    public static Dictionary<DiskHealth, List<SmartSample>> Record(IReadOnlyList<DiskHealth> disks)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var history = Read();
            var previousCount = history.Sum(disk => disk.Value.Count);
            foreach (var disk in history.Values)
                disk.RemoveAll(sample => now - sample.Time > Retention);

            var observed = new Dictionary<DiskHealth, List<SmartSample>>();
            var identifiable = disks.Where(disk => !string.IsNullOrWhiteSpace(disk.Serial))
                .GroupBy(Key).Where(group => group.Count() == 1);
            var changed = history.Sum(disk => disk.Value.Count) != previousCount;
            foreach (var group in identifiable)
            {
                var disk = group.Single();
                if (!history.TryGetValue(group.Key, out var samples)) samples = new();
                if (disk.SmartAvailable && (disk.LifeUsedPercent != null || disk.MediaErrors != null || disk.Reallocated != null ||
                                           disk.Pending != null || disk.Uncorrectable != null) &&
                    (samples.Count == 0 || now - samples[0].Time >= SampleInterval))
                {
                    samples.Insert(0, new(now, disk.LifeUsedPercent, disk.MediaErrors, disk.Reallocated, disk.Pending, disk.Uncorrectable));
                    if (samples.Count > MaxSamples) samples.RemoveRange(MaxSamples, samples.Count - MaxSamples);
                    history[group.Key] = samples;
                    changed = true;
                }
                observed[disk] = samples;
            }

            if (changed)
            {
                var retained = history.Where(disk => disk.Value.Count > 0)
                    .OrderByDescending(disk => disk.Value[0].Time).Take(MaxDisks)
                    .ToDictionary(disk => disk.Key, disk => disk.Value);
                Write(retained);
            }
            return observed;
        }
    }

    private static string Key(DiskHealth disk) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{disk.Model.Trim().ToUpperInvariant()}\n{disk.Serial.Trim().ToUpperInvariant()}\n{disk.Size}")));

    private static Dictionary<string, List<SmartSample>> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<SmartSample>>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            AppLog.Write($"Histórico SMART inválido: {ex.Message}");
            return new();
        }
    }

    private static void Write(Dictionary<string, List<SmartSample>> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(history));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
