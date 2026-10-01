# ELink

An event-driven astronomy control stack (think Ekos / N.I.N.A.) built on **EVent**, a distributed event/dRPC mesh.
C# / .NET 8, Avalonia UI. Every device and every logic block is an EVent endpoint; UI and backend know each other
only as "something that runs EVent". Smart scopes (point-to + shoot-at) compose from any equipment and from each other.

First component: an **INDI <-> EVent translation layer**. See [TODO.md](TODO.md) for the roadmap.

## Building
EVent is not on nuget.org. Put `EVent.<ver>.nupkg` (and `EVent.Scripting`) into `nuget/` (a local feed, see `nuget.config`), then `dotnet build`.
