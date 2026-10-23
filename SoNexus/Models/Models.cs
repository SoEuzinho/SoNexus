using System.Windows.Media;

namespace SoNexus.Models;

/// <summary>Um card do painel (ex.: "CPU · Temperatura").</summary>
public sealed class Metric : ObservableObject
{
    // Preto e branco: normal = cinza, atenção = branco, perigo = vermelho (única cor).
    public static readonly Brush Accent = Frozen("#FFFFFF");
    public static readonly Brush Ok = Frozen("#8C8C8C");
    public static readonly Brush Warn = Frozen("#FFFFFF");
    public static readonly Brush Danger = Frozen("#FF453A");

    private string _value = "—";
    private string _detail = "";
    private double _percent;
    private Brush _brush = Accent;

    public Metric(string group, string title)
    {
        Group = group;
        Title = title;
    }

    public string Group { get; }
    public string Title { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double Percent { get => _percent; set => Set(ref _percent, Math.Clamp(value, 0, 100)); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }

    public void SetTemperature(float? celsius, string detail = "")
    {
        Value = celsius is { } c ? $"{c:0} °C" : "—";
        Percent = celsius ?? 0;
        Brush = celsius switch { null => Accent, < 70 => Ok, < 85 => Warn, _ => Danger };
        Detail = detail;
    }

    public void SetLoad(float? percent, string detail = "")
    {
        Value = percent is { } p ? $"{p:0} %" : "—";
        Percent = percent ?? 0;
        Brush = Accent;
        Detail = detail;
    }

    public void SetText(string value, double percent, string detail = "")
    {
        Value = value;
        Percent = percent;
        Brush = Accent;
        Detail = detail;
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>Uma linha da tabela de sensores.</summary>
public sealed class SensorRow : ObservableObject
{
    private string _value = "—";
    private string _min = "—";
    private string _max = "—";

    public SensorRow(string hardware, string type, string name)
    {
        Hardware = hardware;
        Type = type;
        Name = name;
    }

    public string Hardware { get; }
    public string Type { get; }
    public string Name { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Min { get => _min; set => Set(ref _min, value); }
    public string Max { get => _max; set => Set(ref _max, value); }
}

public sealed record InfoItem(string Key, string Value);

public sealed record InfoSection(string Title, string Glyph, IReadOnlyList<InfoItem> Items);

public sealed record ProcessMemory(string Name, int Pid, long WorkingSet)
{
    public string Display => Format.Bytes((ulong)WorkingSet);
}

public static class Format
{
    public static string Bytes(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }
}
