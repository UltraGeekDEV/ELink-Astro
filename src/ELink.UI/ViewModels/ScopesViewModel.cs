using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>The smart scopes: one card each with what it is doing now, and the selected one in full. The scopes do the work
/// themselves; this only shows it and offers the manual actions.</summary>
public sealed partial class ScopesViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly CatalogViewModel _catalog;
    private readonly SemaphoreSlim _rebuild = new(1, 1);

    public ScopesViewModel(MeshSession mesh, CatalogViewModel catalog, AutofocusViewModel autofocus, CenteringViewModel centering)
    {
        _mesh = mesh; _catalog = catalog; Autofocus = autofocus; Centering = centering;
        catalog.CompositionChanged += () => UiThread.Post(() => _ = RebuildAsync());
    }

    public AutofocusViewModel Autofocus { get; }
    public CenteringViewModel Centering { get; }
    public ObservableCollection<ScopePanelViewModel> Cards { get; } = new();
    [ObservableProperty] private ScopePanelViewModel? _selected;
    public bool HasScopes => Cards.Count > 0;
    public bool HasNoScopes => Cards.Count == 0;
    /// <summary>Asked to switch the shell to the Rig view (set by the shell).</summary>
    public Action? OpenRig { get; set; }

    [RelayCommand] private void GoToRig() => OpenRig?.Invoke();

    /// <summary>Selects a scope by its id (e.g. from a chip in the status bar).</summary>
    public void Select(string scopeId) => Selected = Cards.FirstOrDefault(c => c.ScopeId == scopeId) ?? Selected;

    private async Task RebuildAsync()
    {
        await _rebuild.WaitAsync();
        try
        {
            var defs = _catalog.Composition.Scopes.ToList();
            // keep cards whose scope is unchanged; build the rest
            foreach (var card in Cards.Where(c => defs.All(d => d.Id.Text != c.ScopeId)).ToList()) { Cards.Remove(card); card.Dispose(); }
            foreach (var def in defs)
            {
                string guider = def.GuiderId.Text != "" ? def.GuiderId.Text : def.GuideShooterId.Text != "" ? def.Id.Text + "-guider" : "";
                var trains = def.Shooters.Select(x => x.Id.Text).Where(id => _catalog.Composition.Trains.Any(t => t.Id.Text == id)).ToList();
                var existing = Cards.FirstOrDefault(c => c.ScopeId == def.Id.Text);
                string name = def.DisplayName.Text != "" ? def.DisplayName.Text : def.Id.Text;
                // a changed definition (guiding added, trains changed) needs a new card: the old one follows the old ids
                if (existing is not null && existing.GuiderId == guider && existing.TrainIds.SequenceEqual(trains) && existing.DisplayName == name) continue;
                var panel = new ScopePanelViewModel(_mesh, def.Id.Text, name, guider, trains) { Accent = _catalog.ScopeColor(def.Id.Text) };
                await panel.StartAsync();
                if (existing is not null) { int at = Cards.IndexOf(existing); Cards[at] = panel; existing.Dispose(); if (Selected == existing) Selected = panel; }
                else Cards.Add(panel);
            }
            foreach (var card in Cards) card.Accent = _catalog.ScopeColor(card.ScopeId);   // (positions move when a scope is removed)
            Selected ??= Cards.FirstOrDefault();
            if (Selected is not null && !Cards.Contains(Selected)) Selected = Cards.FirstOrDefault();
            OnPropertyChanged(nameof(HasScopes)); OnPropertyChanged(nameof(HasNoScopes));
        }
        finally { _rebuild.Release(); }
    }

    public void Dispose() { foreach (var c in Cards) c.Dispose(); Cards.Clear(); }
}
