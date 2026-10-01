using System.Collections.Immutable;

namespace ELink.Indi.Protocol;

/// <summary>Anything the server sends.</summary>
public abstract record IndiMessage;

/// <summary>defXXXVector: a property was defined (or re-announced).</summary>
public sealed record IndiDefine(IndiProperty Property, string? Message) : IndiMessage;

/// <summary>An element value inside a setXXXVector; Blob data is set for BLOB updates.</summary>
public sealed record IndiElementUpdate(string Name, string Value, byte[]? Blob = null, string? BlobFormat = null);

/// <summary>setXXXVector: values and/or state of an existing property changed.</summary>
public sealed record IndiSet(
    IndiPropertyType Type,
    string Device,
    string Name,
    IndiState? State,
    double? Timeout,
    DateTime? Timestamp,
    string? Message,
    ImmutableArray<IndiElementUpdate> Updates) : IndiMessage;

/// <summary>delProperty: one property (Name set) or a whole device (Name null) went away.</summary>
public sealed record IndiDelete(string Device, string? Name, string? Message) : IndiMessage;

/// <summary>message: a free-text log line from a device (or the server when Device is null).</summary>
public sealed record IndiLog(string? Device, DateTime? Timestamp, string Text) : IndiMessage;

/// <summary>What we send to the server.</summary>
public abstract record IndiCommand;

public sealed record IndiGetProperties(string? Device = null, string? Name = null) : IndiCommand;
public sealed record IndiEnableBlob(string Device, string? Name, IndiBlobMode Mode) : IndiCommand;

/// <summary>One value of a newXXXVector.</summary>
public sealed record IndiNewElement(string Name, string Value, byte[]? Blob = null, string? BlobFormat = null);

/// <summary>newXXXVector: ask a device to change a property.</summary>
public sealed record IndiNew(IndiPropertyType Type, string Device, string Name, ImmutableArray<IndiNewElement> Elements) : IndiCommand;
