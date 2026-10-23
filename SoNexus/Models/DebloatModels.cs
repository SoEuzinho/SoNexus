using System.Windows.Media;
using SoNexus.Services;

namespace SoNexus.Models;

public sealed class TweakItem : ObservableObject
{
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C)));

    private bool _isSelected;
    private bool _isAvailable = true;
    private bool _isApplied;
    private string _statusText = "…";
    private Brush _statusBrush = Muted;

    public TweakItem(Tweak tweak) => Tweak = tweak;

    public Tweak Tweak { get; }
    public string Category => Tweak.Category;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Note => Tweak.Note;
    public string RiskText => Tweak.Risk == Risk.Seguro ? "SEGURO" : "MODERADO";
    public Brush RiskBrush => Tweak.Risk == Risk.Seguro ? Metric.Ok : Metric.Warn;
    public string RecommendedText => Tweak.Recommended ? "recomendado" : "";

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }
    public bool IsApplied { get => _isApplied; private set => Set(ref _isApplied, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    /// <summary>Lê o estado no sistema (pode demorar: rodar fora da janela).</summary>
    public (bool Available, bool Applied) ReadState()
    {
        try
        {
            var available = Tweak.IsAvailable;
            return (available, available && Tweak.IsApplied);
        }
        catch
        {
            return (false, false);
        }
    }

    public void SetState((bool Available, bool Applied) state)
    {
        IsAvailable = state.Available;
        IsApplied = state.Applied;
        StatusText = !IsAvailable ? "Indisponível" : IsApplied ? "Aplicado" : "Padrão do Windows";
        StatusBrush = IsApplied ? Metric.Ok : Muted;
        if (!IsAvailable) IsSelected = false;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

public sealed class AppItem : ObservableObject
{
    private bool _isSelected;

    public AppItem(BloatApp app) => App = app;

    public BloatApp App { get; }
    public string Friendly => App.Friendly;
    public string Name => App.Name;
    public string Version => App.Version;
    public string RecText => App.Recommendation switch
    {
        "safe" => App.Default ? "SEGURO · PADRÃO" : "SEGURO",
        "optional" => "OPCIONAL",
        _ => "NÃO RECOMENDADO",
    };
    public Brush RecBrush => App.Recommendation switch { "safe" => Metric.Ok, "optional" => Metric.Warn, _ => Metric.Danger };
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
