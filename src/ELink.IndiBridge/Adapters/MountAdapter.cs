using ELink.Contracts.Equipment;
using ELink.Core.Astro;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

/// <summary>INDI telescope (mount) driver to the ELink Mount contract.</summary>
public sealed class MountAdapter(AdapterContext ctx) : IndiDeviceAdapter<MountState>(ctx)
{
    public override string Kind => DeviceKinds.Mount;

    // INDI telescopes publish JNow (EQUATORIAL_EOD_COORD) or J2000 (EQUATORIAL_COORD); use what the driver has.
    private (string Property, string Epoch)? Coordinates =>
        P("EQUATORIAL_EOD_COORD") is not null ? ("EQUATORIAL_EOD_COORD", "JNow")
        : P("EQUATORIAL_COORD") is not null ? ("EQUATORIAL_COORD", "J2000") : null;

    protected override MountState BuildState()
    {
        var s = new MountState { Connected = Connected };
        if (!Connected) { s.Phase = "Disconnected"; return s; }
        var c = Coordinates;
        var coords = c is null ? null : P(c.Value.Property);
        if (coords is not null)
        {
            s.RaHours = coords.Number("RA"); s.DecDegrees = coords.Number("DEC"); s.Epoch = c!.Value.Epoch;
        }
        // the mount's own target: set by whoever commanded the slew (ELink, a hand controller, Ekos)
        if (P("TARGET_EOD_COORD") is { } target && (c?.Epoch ?? "JNow") == "JNow" && !(target.Number("RA") == 0 && target.Number("DEC") == 0))
        { s.TargetRaHours = target.Number("RA"); s.TargetDecDegrees = target.Number("DEC"); }
        var track = P("TELESCOPE_TRACK_STATE");
        s.Tracking = track?.Switch("TRACK_ON") ?? false;
        var park = P("TELESCOPE_PARK");
        s.Parked = park?.Switch("PARK") == true && park.State == IndiState.Ok;
        s.PierSide = P("TELESCOPE_PIER_SIDE") is { } pier ? (pier.Switch("PIER_EAST") ? "East" : pier.Switch("PIER_WEST") ? "West" : "Unknown") : "Unknown";

        s.Phase =
            coords?.State == IndiState.Alert || park?.State == IndiState.Alert ? "Error"
            : park?.State == IndiState.Busy ? "Parking"
            : s.Parked ? "Parked"
            : coords?.State == IndiState.Busy ? "Slewing"
            : s.Tracking ? "Tracking" : "Idle";
        s.Message = s.Phase == "Error" ? "the driver reported an alert; see the INDI log" : "";
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<SkyTarget, CommandResult>("Goto", t => MoveAsync(t, "TRACK"), "slew to a sky position and start tracking it");
        await RegisterCommandAsync<SkyTarget, CommandResult>("Sync", t => MoveAsync(t, "SYNC"), "tell the mount that it currently points at this position");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("Abort", _ => AbortAsync(), "stop any slew immediately");
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("Park", ParkAsync, "park (true) or unpark (false) the mount");
        await RegisterCommandAsync<BinaryConvertibleBool, CommandResult>("SetTracking", TrackAsync, "start or stop tracking");
        await RegisterCommandAsync<ELink.Contracts.Site.GeoLocation, CommandResult>(ELink.Contracts.Site.SiteIds.MountSetLocation, SetLocationAsync, "tell the mount where it stands (GEOGRAPHIC_COORD)");
        await RegisterCommandAsync<BinaryConvertibleString, CommandResult>(ELink.Contracts.Site.SiteIds.MountSetTime, SetTimeAsync, "set the mount's clock: ISO 8601 UTC, empty = now (TIME_UTC)");
    }

    private async Task<CommandResult> SetLocationAsync(ELink.Contracts.Site.GeoLocation g)
    {
        if (Need("GEOGRAPHIC_COORD") is { } missing) return missing;
        double lat = g.LatitudeDegrees.Value, lon = g.LongitudeDegrees.Value;
        if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90) return CommandResult.Fail("invalid location");
        lon = ((lon % 360) + 360) % 360;   // INDI: 0..360 east
        return await Send(() => Client.SetNumbersAsync(Device, "GEOGRAPHIC_COORD", new[] { ("LAT", lat), ("LONG", lon), ("ELEV", g.ElevationMeters.Value) }));
    }

    private async Task<CommandResult> SetTimeAsync(BinaryConvertibleString iso)
    {
        if (Need("TIME_UTC") is { } missing) return missing;
        var utc = DateTime.UtcNow;
        if (iso.Text.Trim() != "" && !DateTime.TryParse(iso.Text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out utc))
            return CommandResult.Fail("time must be ISO 8601");
        double offset = TimeZoneInfo.Local.GetUtcOffset(utc).TotalHours;
        return await Send(() => Client.SetTextsAsync(Device, "TIME_UTC", new[]
        {
            ("UTC", utc.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
            ("OFFSET", offset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
        }));
    }

    private async Task<CommandResult> MoveAsync(SkyTarget target, string mode)
    {
        var c = Coordinates;
        if (c is null) return CommandResult.Fail($"{Device} has no equatorial coordinates property");
        if (Need("ON_COORD_SET", mode) is { } missing) return missing;
        if (P("TELESCOPE_PARK") is { } park && park.Switch("PARK") && park.State == IndiState.Ok)
            return CommandResult.Fail("the mount is parked: unpark it first");
        double ra = target.RaHours.Value, dec = target.DecDegrees.Value;
        if (double.IsNaN(ra) || double.IsNaN(dec) || dec < -90 || dec > 90) return CommandResult.Fail("invalid coordinates");
        string from = target.Epoch.Text, to = c.Value.Epoch;
        if (from != "JNow" && from != "J2000") return CommandResult.Fail($"unknown epoch {from}");
        if (from != to)
        {
            var now = DateTime.UtcNow;
            (ra, dec) = to == "JNow" ? Precession.J2000ToDate(ra, dec, now) : Precession.DateToJ2000(ra, dec, now);
        }
        ra = Precession.NormalizeHours(ra);
        return await Send(async () =>
        {
            await SetSwitch("ON_COORD_SET", mode);
            await Client.SetNumbersAsync(Device, c.Value.Property, new[] { ("RA", ra), ("DEC", dec) });
        });
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("TELESCOPE_ABORT_MOTION", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("TELESCOPE_ABORT_MOTION", "ABORT"));
    }

    private async Task<CommandResult> ParkAsync(BinaryConvertibleBool park)
    {
        string element = park.Value ? "PARK" : "UNPARK";
        if (Need("TELESCOPE_PARK", element) is { } missing) return missing;
        return await Send(() => SetSwitch("TELESCOPE_PARK", element));
    }

    private async Task<CommandResult> TrackAsync(BinaryConvertibleBool on)
    {
        string element = on.Value ? "TRACK_ON" : "TRACK_OFF";
        if (Need("TELESCOPE_TRACK_STATE", element) is { } missing) return missing;
        return await Send(() => SetSwitch("TELESCOPE_TRACK_STATE", element));
    }
}
