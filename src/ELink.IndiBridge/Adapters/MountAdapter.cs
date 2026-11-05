using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Indi.Client;
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
        s.Parked = IsParked(park);
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
        var r = await Send(() => Client.SetTextsAsync(Device, "TIME_UTC", new[]
        {
            ("UTC", utc.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
            ("OFFSET", offset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
        }));
        if (r.Ok.Value) _timeGiven = true;
        return r;
    }

    private async Task<CommandResult> MoveAsync(SkyTarget target, string mode)
    {
        var c = Coordinates;
        if (c is null) return CommandResult.Fail($"{Device} has no equatorial coordinates property");
        if (Need("ON_COORD_SET", mode) is { } missing) return missing;
        if (mode == "TRACK") { if (await ReadyAsync() is { } notReady) return notReady; }
        else if (IsParked(P("TELESCOPE_PARK"))) return CommandResult.Fail("the mount is parked: unpark it first");
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
        var sent = await Send(async () =>
        {
            await SetSwitch("ON_COORD_SET", mode);
            await Client.SetNumbersAsync(Device, c.Value.Property, new[] { ("RA", ra), ("DEC", dec) });
        });
        if (sent.Ok.Value && mode == "TRACK") _gotoSent = true;
        return sent;
    }

    // ---- getting the mount ready to slew ---------------------------------------------------------------------------

    private bool _timeGiven, _gotoSent, _slewSeen;

    /// <summary>A park that is set (and not still being reached). Some drivers (OnStep) leave the property Idle rather than Ok.</summary>
    private static bool IsParked(IndiProperty? park) => park is not null && park.Switch("PARK") && park.State != IndiState.Busy;

    /// <summary>What a mount needs before it slews, in the order it needs it. OnStep in particular is fussy about it: it works
    /// out where its parked (or home) position is in the sky from its clock and site, so those come first; it will not slew
    /// while parked, so it is unparked, and only once it says it is; and it may come up with tracking off. A mount that is
    /// already unparked and tracking costs nothing here. Null when ready, else why not.</summary>
    private async Task<CommandResult?> ReadyAsync()
    {
        var log = (Action<string>)(m => SessionLog.Info(Node, Id, m));
        if (P("TELESCOPE_PARK") is { State: IndiState.Busy })
            await Until(() => P("TELESCOPE_PARK") is not { State: IndiState.Busy }, TimeSpan.FromMinutes(3));
        // the site service gives every mount the time and place as it appears; on the first slew after connecting, wait for that
        if (!_timeGiven && P("TIME_UTC") is not null)
        {
            await Until(() => _timeGiven, TimeSpan.FromSeconds(15));
            if (!_timeGiven) SessionLog.Warn(Node, Id, "the mount was not given the time and site (is 'push to mounts' off?): it slews by its own clock");
            _timeGiven = true;   // waited once: not again on every slew
        }
        if (IsParked(P("TELESCOPE_PARK")))
        {
            log("unparking the mount before slewing");
            var r = await Send(() => SetSwitch("TELESCOPE_PARK", "UNPARK"));
            if (!r.Ok.Value) return CommandResult.Fail("could not unpark the mount: " + r.Error.Text);
            await Until(() => P("TELESCOPE_PARK") is not { } p || p.State == IndiState.Alert || !IsParked(p) && p.State != IndiState.Busy, UnparkTimeout);
            var park = P("TELESCOPE_PARK");
            if (park is { State: IndiState.Alert }) return CommandResult.Fail("the mount refused to unpark (see the INDI log; OnStep needs its time and site, and a park position)");
            if (IsParked(park) || park is { State: IndiState.Busy }) return CommandResult.Fail($"the mount did not unpark within {UnparkTimeout.TotalSeconds:0} s");
            // the driver flips its switch at once; the controller itself takes a moment before it takes a goto (OnStep does)
            await Task.Delay(UnparkSettle);
            if (IsParked(P("TELESCOPE_PARK"))) return CommandResult.Fail("the mount went back to parked after unparking (see the INDI log)");
            log("mount unparked");
        }
        await TrackOnAsync();
        return null;
    }

    public TimeSpan UnparkTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan UnparkSettle { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Tracking on, where the mount can say (a mount that cannot is left as it is): a slew that ends with the mount
    /// standing still would trail every frame, and nothing downstream could tell.</summary>
    private async Task TrackOnAsync()
    {
        if (P("TELESCOPE_TRACK_STATE") is not { } track || !track.Has("TRACK_ON") || track.Switch("TRACK_ON")) return;
        if (IsParked(P("TELESCOPE_PARK"))) return;
        await Send(() => SetSwitch("TELESCOPE_TRACK_STATE", "TRACK_ON"));
        await Until(() => P("TELESCOPE_TRACK_STATE")?.Switch("TRACK_ON") != false, TimeSpan.FromSeconds(10));
        if (P("TELESCOPE_TRACK_STATE")?.Switch("TRACK_ON") == false) SessionLog.Warn(Node, Id, "the mount did not start tracking when asked");
    }

    private static async Task Until(Func<bool> done, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < end) await Task.Delay(200);
    }

    protected override async Task OnPropertyChangedAsync(IndiChange change)
    {
        if (change is not PropertyUpdated { Property: var p }) return;
        if (p.Name == "CONNECTION" && !Connected) { _timeGiven = false; _gotoSent = _slewSeen = false; return; }
        if (p.Name != Coordinates?.Property || !_gotoSent) return;
        if (p.State == IndiState.Busy) { _slewSeen = true; return; }
        if (!_slewSeen || p.State == IndiState.Alert) return;
        // our slew has ended: it should be tracking now (most mounts do so by themselves after a TRACK goto; OnStep may not)
        _gotoSent = _slewSeen = false;
        _ = Task.Run(TrackOnAsync);   // off the update loop: its wait needs the updates to keep coming
        await Task.CompletedTask;
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
