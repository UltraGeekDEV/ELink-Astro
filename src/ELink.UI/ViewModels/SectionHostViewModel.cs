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
        // in the order a first set-up goes
        Add("Drivers", "1 · Start the INDI drivers of your equipment (skip it if they are already running)", drivers);
        Add("Equipment", "2 · What is plugged in, and whether it is connected", equipment);
        Add("Set up", "3 · Telescopes (the optics and cameras) and scopes (a mount with its telescopes)", setup);
        Add("Site", "4 · Where you are and your horizon (optional: it lets ELink plan around the night)", site);
        Add("Calibration", "Darks, biases and flats (once per camera, whenever you like)", calibration);
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
        Add("INDI properties", "For experts: every property of every INDI device, editable. Changing them can upset the equipment", indi);
        Add("Saving frames", "Where frames are saved, and for which cameras and scopes (images started from Sky are saved by default)", storage);
        Add("Custom stack", "For experts: stack frames from chosen cameras into a field of your choice (images made from Sky stack themselves)", liveStack);
        Show("Help");
    }
    public override string Heading => "Advanced";
}
