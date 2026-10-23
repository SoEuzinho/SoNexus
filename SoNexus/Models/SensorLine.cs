using System.Collections.ObjectModel;
using SoNexus.Services;

namespace SoNexus.Models;

/// <summary>Uma linha "nome · origem · valor" que é atualizada no lugar (sem recriar a lista inteira).</summary>
public sealed class SensorLine : ObservableObject
{
    private string _value = "";

    public SensorLine(string id, string name, string source)
    {
        Id = id;
        Name = name;
        Source = source;
    }

    public string Id { get; }
    public string Name { get; }
    public string Source { get; }
    public string Value { get => _value; set => Set(ref _value, value); }

    /// <summary>Atualiza a coleção: mexe só nos valores e acrescenta sensores novos no fim.</summary>
    public static void Sync(ObservableCollection<SensorLine> lines, IEnumerable<SensorReading> readings,
        Func<SensorReading, string> source)
    {
        var byId = lines.ToDictionary(l => l.Id);
        foreach (var r in readings)
        {
            if (!byId.TryGetValue(r.Id, out var line))
            {
                line = new SensorLine(r.Id, r.Name, source(r));
                byId[r.Id] = line;
                lines.Add(line);
            }
            line.Value = SensorFormat.Value(r.Type, r.Value);
        }
    }
}
