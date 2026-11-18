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
        await RunBusyAsync(async () =>
        {
            await RefreshStatesAsync();
            if (!_appsLoaded) await LoadAppsAsync();
        });
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
        TweakStatus.Text = $"{applied} ajustes aplicados · {_tweaks.Count(t => t.HasBackup)} com backup" +
                           $" · {recommendedLeft} recomendados disponíveis para aplicar";
    }

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var t in _tweaks) t.IsSelected = t.IsAvailable && !t.IsApplied && t.Tweak.Recommended;
        TweaksTab.IsSelected = true;
        Log($"{_tweaks.Count(t => t.IsSelected)} ajuste(s) recomendado(s) selecionado(s). Revise a lista e clique em Aplicar selecionados.");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) =>
        await RunTweaksAsync(apply: true);

    private async void Revert_Click(object sender, RoutedEventArgs e) =>
        await RunTweaksAsync(apply: false);

    private async Task RunTweaksAsync(bool apply)
    {
        if (_busy || !RequireAdmin()) return;
        var marked = _tweaks.Where(t => t.IsSelected).ToList();
        var eligible = marked.Where(t => apply ? t.IsAvailable && !t.IsApplied : t.HasBackup).ToList();
        if (eligible.Count == 0)
        {
            Log(apply ? "Selecione um ajuste disponível ainda não aplicado." : "Selecione um ajuste que tenha backup do original.");
            return;
        }
        var verb = apply ? "Aplicar" : "Reverter";
        var list = Summarize(eligible.Select(t => t.Title));
        var skipped = marked.Count - eligible.Count;
        var checkpoint = apply && RestoreBeforeApply.IsChecked == true;
        var note = apply
            ? checkpoint ? "\n\nUm ponto de restauração será criado antes de aplicar." : "\n\nA operação seguirá sem um novo ponto de restauração."
            : "\n\nO SoNexus restaurará apenas as ações com backup do original.";
        var skipNote = skipped > 0 ? $"\n{skipped} selecionado(s) fora desta operação." : "";
        if (!Confirm($"{verb} {eligible.Count} ajuste(s)?\n\n{list}{skipNote}{note}")) return;

        await RunBusyAsync(async () =>
        {
            if (checkpoint && !await CreateRestorePointAsync())
            {
                Log("Aplicação cancelada. Você pode verificar a Proteção do Sistema ou desmarcar a criação do ponto e tentar de novo.");
                return;
            }

            var confirmed = 0;
            var affectedRestart = Restart.Nenhum;
            foreach (var t in eligible)
            {
                try
                {
                    await Task.Run(apply ? t.Tweak.Apply : t.Tweak.RevertOriginal);
                    var state = await Task.Run(t.ReadState);
                    t.SetState(state);
                    if (apply ? state.Applied : !state.HasBackup)
                    {
                        confirmed++;
                        t.IsSelected = false;
                        Log($"✔ {(apply ? "Aplicado e conferido" : "Originais restaurados")}: {t.Title}");
                    }
                    else
                    {
                        Log($"⚠ {t.Title}: não foi possível confirmar o resultado. Confira o estado e o backup antes de tentar de novo.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"✖ {t.Title}: {Friendly(ex)}");
                    AppLog.Write($"Debloat '{t.Tweak.Id}' falhou: {ex}");
                }
                if (t.Tweak.Restart > affectedRestart) affectedRestart = t.Tweak.Restart;
            }

            Log($"{verb}: {confirmed} confirmado(s), {eligible.Count - confirmed} com falha ou sem confirmação, {skipped} ignorado(s).");
            await RefreshStatesAsync();
            if (affectedRestart == Restart.Windows) Log("Uma mudança pode exigir reiniciar o PC.");
            else if (affectedRestart == Restart.Explorer) Log("Para ver mudanças na interface, use Reiniciar Explorer.");
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        await RunBusyAsync(async () => { await CreateRestorePointAsync(); });
    }

    private async Task<bool> CreateRestorePointAsync()
    {
        Log("Criando ponto de restauração...");
        var error = await DebloatService.CreateRestorePointAsync();
        Log(error is null ? "✔ Ponto de restauração criado." : $"✖ {error}");
        return error is null;
    }

    private async void Explorer_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        await Task.Run(DebloatService.RestartExplorer);
        Log("Explorer reiniciado.");
    });

    // ---------- Exportar / importar ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new SaveFileDialog { FileName = "sonexus-debloat.json", Filter = "Escolhas do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        // Exporta o que está aplicado agora + o que estiver marcado.
        var profile = new DebloatProfile(
            _tweaks.Where(t => t.IsApplied || t.IsSelected).Select(t => t.Tweak.Id).ToList(),
            _apps.Where(a => a.IsSelected).Select(a => a.Name).ToList());
        try
        {
            DebloatService.Export(dialog.FileName, profile);
            Log($"✔ Exportado: {profile.Tweaks.Count} ajuste(s) e {profile.Apps.Count} app(s) para {dialog.FileName}");
        }
        catch (Exception ex) { Log($"✖ Não foi possível exportar: {Friendly(ex)}"); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Filter = "Escolhas do SoNexus (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var profile = DebloatService.Import(dialog.FileName);
            foreach (var t in _tweaks) t.IsSelected = t.IsAvailable && !t.IsApplied && profile.Tweaks.Contains(t.Tweak.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var a in _apps) a.IsSelected = profile.Apps.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
            Log($"✔ Importado: {_tweaks.Count(t => t.IsSelected)} ajuste(s) e {_apps.Count(a => a.IsSelected)} app(s) marcados. Confira e clique em Aplicar / Remover.");
        }
        catch (Exception ex)
        {
            Log($"✖ Arquivo inválido: {ex.Message}");
        }
    }

    // ---------- Apps ----------

    private async void ReloadApps_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(LoadAppsAsync);

    private async Task LoadAppsAsync()
    {
        AppsStatus.Text = "Procurando apps pré-instalados...";
        try
        {
            var apps = await DebloatService.GetBloatAppsAsync();
            var selected = _apps.Where(a => a.IsSelected).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _apps.Clear();
            foreach (var a in apps) _apps.Add(new AppItem(a) { IsSelected = selected.Contains(a.Name) });
            var defaults = apps.Count(a => a.Default);
            AppsStatus.Text = apps.Count == 0
                ? "Nenhum app da lista de remoção foi encontrado."
                : $"{apps.Count} apps encontrados · {defaults} na seleção sugerida. A remoção exige confirmação nesta aba.";
            _appsLoaded = true;
        }
        catch (Exception ex)
        {
            AppsStatus.Text = $"Erro ao listar apps: {ex.Message}";
        }
    }

    private void SelectDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var a in _apps) a.IsSelected = a.App.Default;
    }

    private async void RemoveApps_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireAdmin()) return;
        var selected = _apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log("Nenhum app selecionado.");
            return;
        }
        var risky = selected.Where(a => a.App.Recommendation == "unsafe").Select(a => a.Friendly).ToList();
        var names = Summarize(selected.Select(a => a.Friendly));
        var warning = risky.Count > 0 ? $"\n\nATENÇÃO: {string.Join(", ", risky)} são usados por outras partes do Windows ou por jogos." : "";
        if (!Confirm($"Remover {selected.Count} app(s) para todos os usuários?\n\n{names}{warning}\n\nCada app precisará do próprio instalador para voltar.")) return;
        await RemoveAsync(selected);
    }

    private Task RemoveAsync(List<AppItem> apps) => RunBusyAsync(async () =>
    {
        var confirmed = 0;
        foreach (var a in apps)
        {
            Log($"Removendo {a.Friendly}...");
            try
            {
                if (await DebloatService.RemoveAsync(a.App))
                {
                    confirmed++;
                    a.IsSelected = false;
                    Log($"✔ {a.Friendly} removido.");
                }
                else Log($"✖ Não foi possível confirmar a remoção de {a.Friendly}.");
            }
            catch (Exception ex) { Log($"✖ {a.Friendly}: {Friendly(ex)}"); }
        }
        Log($"Remoção: {confirmed} confirmado(s), {apps.Count - confirmed} com falha ou sem confirmação.");
        await LoadAppsAsync();
    });

    // ---------- Auxiliares ----------

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        TopBar.IsEnabled = TabPages.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex) { Log($"✖ {Friendly(ex)}"); }
        finally
        {
            TopBar.IsEnabled = TabPages.IsEnabled = true;
            _busy = false;
        }
    }

    private bool RequireAdmin()
    {
        if (SensorDriver.IsAdmin) return true;
        Log("Abra o SoNexus como administrador para alterar o Windows.");
        return false;
    }

    private static string Summarize(IEnumerable<string> names)
    {
        var list = names.ToList();
        var first = string.Join("\n", list.Take(15).Select(name => $"• {name}"));
        return list.Count <= 15 ? first : $"{first}\n• e mais {list.Count - 15} item(ns) da seleção";
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException =>
            "o Windows protege essa configuração (sem permissão mesmo como administrador). " + (SensorDriver.IsAdmin ? "" : "Abra o SoNexus como administrador."),
        _ => ex.Message,
    };

    private static bool Confirm(string text) =>
        MessageBox.Show(text, "SoNexus", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Log(string line)
    {
        AppLog.Write($"[Debloat] {line}");
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        LogBox.ScrollToEnd();
    }
}
