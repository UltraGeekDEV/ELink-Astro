using System.Collections.Immutable;

namespace ELink.Indi.Protocol;

public enum IndiPropertyType { Text, Number, Switch, Light, Blob }
public enum IndiState { Idle, Ok, Busy, Alert }
public enum IndiPerm { ReadOnly, WriteOnly, ReadWrite }
public enum IndiSwitchRule { OneOfMany, AtMostOne, AnyOfMany }
public enum IndiBlobMode { Never, Also, Only }

/// <summary>One member of a property vector. Immutable; values are kept as the protocol strings
/// (use the typed accessors) so nothing is lost in translation.</summary>
public sealed record IndiElement(
    string Name,
    string Label,
    string Value,
    string? Format = null,
    double Min = 0,
    double Max = 0,
    double Step = 0,
    byte[]? Blob = null,
    string? BlobFormat = null)
{
    public double AsNumber() => IndiNumber.Parse(Value);
    public bool AsSwitch() => Value.Equals("On", StringComparison.OrdinalIgnoreCase);
    public IndiState AsLight() => IndiEnums.ParseState(Value);
}

/// <summary>A property vector (a "property" in INDI terms): immutable snapshot, replaced on every update.</summary>
public sealed record IndiProperty(
    string Device,
    string Name,
    string Label,
    string Group,
    IndiPropertyType Type,
    IndiState State,
    IndiPerm Perm,
    IndiSwitchRule Rule,
    double Timeout,
    DateTime? Timestamp,
    ImmutableArray<IndiElement> Elements)
{
    public IndiElement? this[string element] => Elements.FirstOrDefault(e => e.Name == element);
    public bool Has(string element) => this[element] is not null;
    public double Number(string element) => this[element]?.AsNumber() ?? double.NaN;
    public bool Switch(string element) => this[element]?.AsSwitch() ?? false;
    public string Text(string element) => this[element]?.Value ?? "";
    public bool Writable => Perm != IndiPerm.ReadOnly;

    /// <summary>The name of the (first) switch that is On, e.g. for one-of-many rules.</summary>
    public string? OnSwitch => Type == IndiPropertyType.Switch ? Elements.FirstOrDefault(e => e.AsSwitch())?.Name : null;

    public string Key => Device + "/" + Name;
}

public static class IndiEnums
{
    public static IndiState ParseState(string? s) => s switch
    {
        "Ok" => IndiState.Ok,
        "Busy" => IndiState.Busy,
        "Alert" => IndiState.Alert,
        _ => IndiState.Idle,
    };

    public static string ToWire(this IndiState s) => s switch
    {
        IndiState.Ok => "Ok", IndiState.Busy => "Busy", IndiState.Alert => "Alert", _ => "Idle",
    };

    public static IndiPerm ParsePerm(string? s) => s switch
    {
        "ro" => IndiPerm.ReadOnly, "wo" => IndiPerm.WriteOnly, _ => IndiPerm.ReadWrite,
    };

    public static IndiSwitchRule ParseRule(string? s) => s switch
    {
        "AtMostOne" => IndiSwitchRule.AtMostOne, "AnyOfMany" => IndiSwitchRule.AnyOfMany, _ => IndiSwitchRule.OneOfMany,
    };

    public static string ToWire(this IndiBlobMode m) => m switch
    {
        IndiBlobMode.Also => "Also", IndiBlobMode.Only => "Only", _ => "Never",
    };
}
