using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SoNexus.Models;
using SoNexus.Services;

namespace SoNexus.Views;

public partial class DebloatView : UserControl
{
    private readonly ObservableCollection<TweakItem> _tweaks = new(DebloatService.Tweaks.Select(t => new TweakItem(t)));
    private readonly ObservableCollection<AppItem> _apps = new();
    private bool _appsLoaded;
    private bool _busy;

    public DebloatView()
    {
        InitializeComponent();
        ((CollectionViewSource)Resources["TweakGroups"]).Source = _tweaks;
        AppList.ItemsSource = _apps;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatesAsync();
        if (!_appsLoaded) await LoadAppsAsync();
    }

    // ---------- Ajustes ----------

    /// <summary>Ler o estado de ~60 ajustes (alguns consultam o PowerShell) fora da janela, para não travar.</summary>
    private async Task RefreshStatesAsync()
    {
        TweakStatus.Text = "Verificando o que já está aplicado...";
        var states = await Task.Run(() => _tweaks.Select(t => t.ReadState()).ToList());
        for (var i = 0; i < _tweaks.Count; i++) _tweaks[i].SetState(states[i]);
        var applied = _tweaks.Count(t => t.IsApplied);
        var recommendedLeft = _tweaks.Count(t => t.Tweak.Recommended && t.IsAvailable && !t.IsApplied);
        TweakStatus.Text = $"{applied} de {_tweaks.Count} ajustes aplicados" +
                           (recommendedLeft > 0 ? $" · faltam {recommendedLeft} recomendados" : " · todos os recomendados aplicados");
    }

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _tweaks) t.IsSelected = t.IsAvailable && !t.IsApplied && t.Tweak.Recommended;
        Log($"{_tweaks.Count(t => t.IsSelected)} ajuste(s) recomendado(s) selecionado(s).");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) =>
        await RunTweaksAsync(_tweaks.Where(t => t.IsSelected && t.IsAvailable).ToList(), apply: true, confirm: true);

    private async void Revert_Click(object sender, RoutedEventArgs e) =>
        await RunTweaksAsync(_tweaks.Where(t => t.IsSelected && t.IsAvailable).ToList(), apply: false, confirm: true);

    private async Task RunTweaksAsync(List<TweakItem> selected, bool apply, bool confirm)
    {
        if (selected.Count == 0)
        {
            Log("Nenhum ajuste selecionado.");
            return;
        }
        var verb = apply ? "Aplicar" : "Reverter";
        if (confirm && !Confirm($"{verb} {selected.Count} ajuste(s)?\n\nRecomendado: crie um ponto de restauração antes.")) return;

        SetBusy(true);
        var failed = 0;
        foreach (var t in selected)
        {
            try
            {
                await Task.Run(apply ? t.Tweak.Apply : t.Tweak.Revert);
                // Confere no sistema se a mudança realmente ficou.
                var (_, nowApplied) = await Task.Run(t.ReadState);
                if (nowApplied == apply)
                {
                    Log($"✔ {(apply ? "Aplicado" : "Revertido")} e conferido: {t.Title}");
                }
                else
                {
                    failed++;
                    Log($"⚠ {t.Title}: o Windows não manteve a mudança (pode estar bloqueado por política da empresa/escola ou não existir nesta versão do Windows).");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log($"✖ {t.Title}: {Friendly(ex)}");
                AppLog.Write($"Debloat '{t.Tweak.Id}' falhou: {ex}");
            }
            t.IsSelected = false;
        }
        if (failed > 0) Log($"{failed} ajuste(s) com problema. Detalhes técnicos em {AppLog.Path_}");
        await RefreshStatesAsync();

        if (selected.Any(t => t.Tweak.Restart == Restart.Windows))
            Log("Alguns ajustes só valem depois de reiniciar o PC.");
        else if (selected.Any(t => t.Tweak.Restart == Restart.Explorer))
            Log("Clique em \"Reiniciar Explorer\" para ver as mudanças na interface.");
        SetBusy(false);
    }

    /// <summary>Faz tudo de uma vez: ponto de restauração, ajustes recomendados e remoção dos apps padrão.</summary>
    private async void Default_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_appsLoaded) await LoadAppsAsync();
        var tweaks = _tweaks.Where(t => t.Tweak.Recommended && t.IsAvailable && !t.IsApplied).ToList();
        var apps = _apps.Where(a => a.App.Default).ToList();
        if (tweaks.Count == 0 && apps.Count == 0)
        {
            Log("O padrão recomendado já está todo aplicado.");
            return;
        }
        var list = string.Join("\n", tweaks.Select(t => "• " + t.Title).Take(14));
        if (tweaks.Count > 14) list += $"\n• ... e mais {tweaks.Count - 14}";
        if (!Confirm($"Aplicar o padrão recomendado?\n\n1) Criar ponto de restauração\n2) {tweaks.Count} ajuste(s):\n{list}\n3) Remover {apps.Count} app(s) pré-instalado(s) desnecessário(s)\n\nTudo pode ser revertido.")) return;

        SetBusy(true);
        Log("Criando ponto de restauração...");
        var error = await DebloatService.CreateRestorePointAsync();
        Log(error is null ? "✔ Ponto de restauração criado." : $"• Sem ponto de restauração: {error}");
        SetBusy(false);

        await RunTweaksAsync(tweaks, apply: true, confirm: false);
        if (apps.Count > 0) await RemoveAsync(apps);
        Log("Padrão recomendado concluído. Reinicie o Explorer (ou o PC) para ver tudo.");
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        Log("Criando ponto de restauração (pode levar até 1 minuto)...");
        var error = await DebloatService.CreateRestorePointAsync();
        Log(error is null ? "✔ Ponto de restauração \"SoNexus\" criado." : $"✖ {error}");
        SetBusy(false);
    }

    private async void Explorer_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(DebloatService.RestartExplorer);
        Log("Explorer reiniciado.");
    }

    // ---------- Exportar / importar ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "sonexus-debloat.json", Filter = "Escolhas do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        // Exporta o que está aplicado agora + o que estiver marcado.
        var profile = new DebloatProfile(
            _tweaks.Where(t => t.IsApplied || t.IsSelected).Select(t => t.Tweak.Id).ToList(),
            _apps.Where(a => a.IsSelected).Select(a => a.Name).ToList());
        DebloatService.Export(dialog.FileName, profile);
        Log($"✔ Exportado: {profile.Tweaks.Count} ajuste(s) e {profile.Apps.Count} app(s) para {dialog.FileName}");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Escolhas do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var profile = DebloatService.Import(dialog.FileName);
            foreach (var t in _tweaks) t.IsSelected = t.IsAvailable && !t.IsApplied && profile.Tweaks.Contains(t.Tweak.Id);
            foreach (var a in _apps) a.IsSelected = profile.Apps.Contains(a.Name);
            Log($"✔ Importado: {_tweaks.Count(t => t.IsSelected)} ajuste(s) e {_apps.Count(a => a.IsSelected)} app(s) marcados. Confira e clique em Aplicar / Remover.");
        }
        catch (Exception ex)
        {
            Log($"✖ Arquivo inválido: {ex.Message}");
        }
    }

    // ---------- Apps ----------

    private async void ReloadApps_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async Task LoadAppsAsync()
    {
        AppBar.IsEnabled = false;
        AppsStatus.Text = "Procurando apps pré-instalados...";
        try
        {
            var apps = await DebloatService.GetBloatAppsAsync();
            _apps.Clear();
            foreach (var a in apps) _apps.Add(new AppItem(a));
            var defaults = apps.Count(a => a.Default);
            AppsStatus.Text = apps.Count == 0
                ? "Nenhum app desnecessário encontrado."
                : $"{apps.Count} apps encontrados · {defaults} saem no padrão recomendado. \"Não recomendado\" = outras partes do Windows dependem dele.";
            _appsLoaded = true;
        }
        catch (Exception ex)
        {
            AppsStatus.Text = $"Erro ao listar apps: {ex.Message}";
        }
        AppBar.IsEnabled = true;
    }

    private void SelectDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _apps) a.IsSelected = a.App.Default;
    }

    private async void RemoveApps_Click(object sender, RoutedEventArgs e)
    {
        var selected = _apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log("Nenhum app selecionado.");
            return;
        }
        var risky = selected.Where(a => a.App.Recommendation == "unsafe").Select(a => a.Friendly).ToList();
        var names = string.Join("\n", selected.Select(a => "• " + a.Friendly).Take(20));
        var warning = risky.Count > 0 ? $"\n\nATENÇÃO: {string.Join(", ", risky)} são usados por outras partes do Windows ou por jogos." : "";
        if (!Confirm($"Remover estes apps para todos os usuários?\n\n{names}{warning}\n\nDá para reinstalar depois pela Microsoft Store.")) return;
        await RemoveAsync(selected);
    }

    private async Task RemoveAsync(List<AppItem> apps)
    {
        SetBusy(true);
        foreach (var a in apps)
        {
            Log($"Removendo {a.Friendly}...");
            var ok = await DebloatService.RemoveAsync(a.App);
            Log(ok ? $"✔ {a.Friendly} removido." : $"✖ Não foi possível remover {a.Friendly}.");
        }
        SetBusy(false);
        await LoadAppsAsync();
    }

    // ---------- Auxiliares ----------

    private void SetBusy(bool busy)
    {
        _busy = busy;
        TopBar.IsEnabled = TweakBar.IsEnabled = AppBar.IsEnabled = !busy;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException =>
            "o Windows protege essa configuração (sem permissão mesmo como administrador). " + (SensorDriver.IsAdmin ? "" : "Abra o SoNexus como administrador."),
        _ => ex.Message,
    };

    private static bool Confirm(string text) =>
        MessageBox.Show(text, "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Log(string line)
    {
        AppLog.Write($"[Debloat] {line}");
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        LogBox.ScrollToEnd();
    }
}
