using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ELink.UI.ViewModels;

/// <summary>One page of a view that has several (Rig, Advanced): a title in the side list and the view model shown for it.</summary>
public sealed partial class Section : ObservableObject
{
    public Section(string title, string description, object content) { Title = title; Description = description; Content = content; }
    public string Title { get; }
    public string Description { get; }
    public object Content { get; }
    [ObservableProperty] private bool _active;
}

/// <summary>A view with a short list of sections down the left and the chosen one filling the rest.</summary>
public abstract partial class SectionHostViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<Section> Sections { get; } = new();
    [ObservableProperty] private Section? _selected;
    public abstract string Heading { get; }

    protected void Add(string title, string description, object content) => Sections.Add(new Section(title, description, content));

    /// <summary>Shows a section by its title (anything not found leaves the current one).</summary>
    public void Show(string? title)
    {
        var s = title is null ? null : Sections.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        Selected = s ?? Selected ?? Sections.FirstOrDefault();
    }

    [RelayCommand] private void Select(Section? section) { if (section is not null) Selected = section; }

    partial void OnSelectedChanged(Section? value) { foreach (var s in Sections) s.Active = s == value; }

    public virtual void Dispose() { }
}

/// <summary>Everything about the rig: how it is put together, where it is, what is plugged in, the drivers, the calibration library.</summary>
public sealed class RigViewModel : SectionHostViewModel
{
    public RigViewModel(ComposerViewModel setup, SiteViewModel site, EquipmentViewModel equipment, ProfilesViewModel drivers, CalibrationViewModel calibration)
    {
        Add("Set up", "Mount, telescopes, cameras and your scopes", setup);
        Add("Equipment", "What is plugged in, and whether it is connected", equipment);
        Add("Site", "Where you are, your horizon, and the night", site);
        Add("Drivers", "Which INDI drivers make up the rig", drivers);
        Add("Calibration", "Darks, biases and flats", calibration);
        Show("Set up");
    }
    public override string Heading => "Rig";
}

/// <summary>The rarely needed: raw INDI properties, where frames are saved, and the older stand-alone tools.</summary>
public sealed class AdvancedViewModel : SectionHostViewModel
{
    public AdvancedViewModel(IndiBrowserViewModel indi, StorageViewModel storage, LiveStackViewModel liveStack, HelpViewModel help)
    {
        Add("Help", "What the words mean, and the keys", help);
        Add("INDI properties", "Every property of every INDI device, editable", indi);
        Add("Saving frames", "Where frames are saved, and from which scope", storage);
        Add("Manual live stack", "Stack frames from chosen cameras into a field of your choice", liveStack);
        Show("Help");
    }
    public override string Heading => "Advanced";
}
