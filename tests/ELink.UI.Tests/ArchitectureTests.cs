using System.Reflection;
using ELink.UI.ViewModels;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>The MVVM rule of this project: the UI and the backend are linked by EVent only. The UI may know the
/// contracts and the EVent helpers, never a backend assembly (and vice versa).</summary>
public class ArchitectureTests
{
    private static readonly string[] BackendAssemblies = { "ELink.Indi", "ELink.IndiBridge", "ELink.Compose", "ELink.Automation", "ELink.Atlas", "ELink.Stellarium", "ELink.Bridge" };

    [Fact]
    public void UiAssemblyReferencesNoBackend()
    {
        var refs = typeof(MainViewModel).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
        foreach (var backend in BackendAssemblies) Assert.DoesNotContain(backend, refs);
        Assert.Contains("ELink.Contracts", refs);
    }

    [Fact]
    public void BackendAssembliesReferenceNoUi()
    {
        foreach (var t in new[] { typeof(ELink.IndiBridge.IndiServerLink), typeof(ELink.Compose.SmartScope), typeof(ELink.Automation.SequencerService), typeof(ELink.Atlas.AtlasService), typeof(ELink.Stellarium.StellariumService), typeof(ELink.Automation.CenteringService) })
        {
            var refs = t.Assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
            Assert.DoesNotContain("ELink.UI", refs);
            Assert.DoesNotContain("Avalonia.Base", refs);
        }
    }

    [Fact]
    public void ContractsAndCoreAreFreeOfBothSides()
    {
        foreach (var asm in new[] { typeof(ELink.Contracts.RawBytes).Assembly, typeof(ELink.Core.ElinkNode).Assembly })
        {
            var refs = asm.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
            foreach (var name in BackendAssemblies.Append("ELink.UI").Append("Avalonia.Base")) Assert.DoesNotContain(name, refs);
        }
    }
}
