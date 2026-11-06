using System.Windows;
using System.Windows.Controls;

namespace SoNexus.Views;

/// <summary>
/// Uma página do menu lateral (ex.: "Jogos") com abas no topo. Cada aba só é criada
/// na primeira vez que é aberta, para o app abrir rápido e gastar menos memória.
/// </summary>
public sealed class SectionView : UserControl
{
    private readonly (string Title, Func<UserControl> Create)[] _pages;
    private readonly UserControl?[] _cache;
    private readonly ContentControl _host = new();

    public SectionView(params (string Title, Func<UserControl> Create)[] pages) : this(false, pages) { }

    /// <param name="secondary">Abas de segundo nível (dentro de uma peça do Hardware): pílulas menores.</param>
    public SectionView(bool secondary, params (string Title, Func<UserControl> Create)[] pages)
    {
        _pages = pages;
        _cache = new UserControl?[pages.Length];

        // WrapPanel: em janelas estreitas as abas descem para a linha de baixo em vez de sumir.
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, secondary ? 14 : 22) };
        var style = (Style)Application.Current.FindResource(secondary ? "SubNavPill" : "SubNav");
        for (var i = 0; i < pages.Length; i++)
        {
            var index = i;
            var tab = new RadioButton { Content = pages[i].Title, Style = style };
            tab.Checked += (_, _) => Show(index);
            bar.Children.Add(tab);
            _tabs.Add(tab);
        }

        var dock = new DockPanel();
        if (pages.Length > 1)
        {
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
        }
        dock.Children.Add(_host);
        Content = dock;

        ((RadioButton)bar.Children[0]).IsChecked = true;

        // Uma página compartilhada pode ter sido levada por outra seção: ao voltar, mostra de novo.
        IsVisibleChanged += (_, _) => { if (IsVisible && _host.Content == null && _current >= 0) Show(_current); };
    }

    private int _current = -1;

    private readonly List<RadioButton> _tabs = new();

    private void Show(int index)
    {
        _current = index;
        var page = _cache[index] ??= _pages[index].Create();
        // Página compartilhada (ex.: Stress test em Processador e em Placa de vídeo): solta da aba anterior antes de mostrar aqui.
        if (page.Parent is ContentControl old && !ReferenceEquals(old, _host)) old.Content = null;
        _host.Content = page;
    }

    /// <summary>A página de uma aba, criando se ainda não existe (sem trocar a aba aberta).</summary>
    public UserControl? Page(string title)
    {
        var i = Array.FindIndex(_pages, p => p.Title == title);
        return i < 0 ? null : _cache[i] ??= _pages[i].Create();
    }

    /// <summary>Abre uma aba pelo nome (usado pelos atalhos da bandeja).</summary>
    public void Select(string title)
    {
        var tab = _tabs.FirstOrDefault(t => (string)t.Content == title);
        if (tab != null) tab.IsChecked = true;
    }
}

/// <summary>
/// Telas que aparecem em mais de um lugar (ex.: Stress test em Processador e em Placa de vídeo).
/// Uma instância só: o teste em andamento continua o mesmo, e não dá para rodar dois ao mesmo tempo.
/// </summary>
public static class SharedPages
{
    private static readonly Dictionary<Type, UserControl> Pages = new();

    public static T Get<T>() where T : UserControl, new()
    {
        if (!Pages.TryGetValue(typeof(T), out var page)) Pages[typeof(T)] = page = new T();
        return (T)page;
    }
}
