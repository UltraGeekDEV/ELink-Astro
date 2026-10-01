using System.Globalization;
using ELink.Contracts.Indi;
using ELink.Indi.Protocol;

namespace ELink.IndiBridge;

/// <summary>Conversions between INDI model objects and the EVent contract types.</summary>
public static class IndiMap
{
    public static IndiPropertyInfo ToInfo(IndiProperty p)
    {
        var info = new IndiPropertyInfo
        {
            Device = p.Device, Property = p.Name, Label = p.Label, Group = p.Group,
            Type = p.Type.ToString(), State = p.State.ToWire(),
            Permission = p.Perm switch { IndiPerm.ReadOnly => "ro", IndiPerm.WriteOnly => "wo", _ => "rw" },
            Rule = p.Type == IndiPropertyType.Switch ? p.Rule.ToString() : "",
            Timeout = p.Timeout,
        };
        foreach (var e in p.Elements)
        {
            info.Elements.Add(new IndiElementInfo
            {
                Id = e.Name, Label = e.Label,
                Value = p.Type == IndiPropertyType.Blob ? (e.Blob?.Length ?? 0).ToString(CultureInfo.InvariantCulture) : e.Value,
                Format = e.Format ?? "", Min = e.Min, Max = e.Max, Step = e.Step,
            });
        }
        return info;
    }

    public static IndiPropertyInfo Tombstone(string device, string property) =>
        new() { Device = device, Property = property };

    public static IndiPropertyType ParseType(string text) =>
        Enum.TryParse<IndiPropertyType>(text, out var t) ? t : IndiPropertyType.Text;
}
