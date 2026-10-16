using Bikepark.Game.Crew;
using Bikepark.Game.Ways;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Land;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>What is under the cursor: a short label for the hover tip and what a click opens.</summary>
internal sealed record WorldPick(string Text, Action Open);

/// <summary>
/// Click on (almost) anything in the world to open its menu, scrolled to it and highlighted: a rider (follow + rider card),
/// a worker (Crew menu, their job), a lift station, line or parking lot (Lifts menu: stats, lift pass), a trail or one of
/// its features (the feature overview), a planned way (Crew menu, its job), a gravel path or platform (a short note),
/// land for sale (Land menu, the parcel). While hovering, the cursor turns into a hand and a tip names the thing. Only
/// when no build tool is active and the mouse isn't over the HUD. Reads the sim only.
/// </summary>
internal sealed partial class WorldPicker : Node
{
    private readonly HudContext _ctx;
    private readonly CrewView? _crew;
    private readonly Func<bool> _enabled;
    private readonly Action<Hud.Menu, string?> _show;
    private readonly Action<string> _note;
    private readonly PanelContainer _tip;
    private readonly Label _tipText;

    /// <summary>Where the last click opened something: no tip until the mouse moves (a menu may have opened under it).</summary>
    private Vector2? _clickedAt;

    public WorldPicker(HudContext ctx, CrewView? crew, Func<bool> enabled, Action<Hud.Menu, string?> show, Action<string> note, Control tipParent)
    {
        _ctx = ctx;
        _crew = crew;
        _enabled = enabled;
        _show = show;
        _note = note;
        Name = "WorldPicker";
        _tip = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = UiTheme.Box(new Color(UiTheme.Panel, 0.92f), 6, 8, 4, new Color(UiTheme.Accent, 0.5f), 1);
        style.ShadowSize = 0;
        _tip.AddThemeStyleboxOverride("panel", style);
        _tipText = UiTheme.Label("", 12);
        _tipText.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tip.AddChild(_tipText);
        tipParent.AddChild(_tip);
    }

    public WorldPicker() : this(null!, null, () => false, (_, _) => { }, _ => { }, new Control()) { }

    public override void _Process(double delta)
    {
        var viewport = GetViewport();
        var mouse = viewport.GetMousePosition();
        if (_clickedAt is { } at && at.DistanceTo(mouse) > 2f) _clickedAt = null;
        var pick = Active() && _clickedAt is null && viewport.GuiGetHoveredControl() is null ? Pick(mouse) : null;
        Input.SetDefaultCursorShape(pick is null ? Input.CursorShape.Arrow : Input.CursorShape.PointingHand);
        _tip.Visible = pick is not null;
        if (pick is null) return;
        _tipText.Text = pick.Text;
        var size = _tip.GetCombinedMinimumSize();
        _tip.Size = size;
        var view = viewport.GetVisibleRect().Size;
        _tip.Position = new Vector2(Math.Min(mouse.X + 16, view.X - size.X - 4), Math.Max(4, mouse.Y - size.Y - 10));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click || !Active()) return;
        if (Pick(click.Position) is not { } pick) return;
        pick.Open();
        _tip.Visible = false;
        _clickedAt = click.Position;
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Opens what is at a screen position, as a click there would (debug <c>--pick</c>).</summary>
    public void Click(Vector2 screen)
    {
        if (Pick(screen) is not { } pick) return;
        pick.Open();
        _clickedAt = screen;
    }

    private bool Active() => _ctx is not null && _enabled() && !_ctx.Host.ShowStartScreen;

    /// <summary>The thing under a screen position, most specific first.</summary>
    public WorldPick? Pick(Vector2 screen)
    {
        var sim = _ctx.Sim;
        var state = sim.State;
        var network = sim.Network;
        var camera = _ctx.Camera.Camera;

        // People first (they are small and move): riders, then workers.
        if (_ctx.Riders.Pick(screen) is { } guestId)
            return new WorldPick("Rider · click to follow", () => _ctx.Riders.Follow(guestId));
        if (_crew?.Pick(camera, screen) is { } memberId && state.Crew.FirstOrDefault(m => m.Id == memberId) is { } member)
        {
            var job = member.JobId == 0 ? null : Jobs.Find(state, member.JobId);
            return new WorldPick(job is null ? $"{member.Name} · crew, waiting for work" : $"{member.Name} · {Jobs.Title(state, job)}",
                () => _show(Hud.Menu.Crew, job?.Id.ToString()));
        }

        if (!_ctx.Terrain.TryRaycast(camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen), 4000f, out var hit)) return null;
        int x = (int)MathF.Round(hit.X * 100), z = (int)MathF.Round(hit.Z * 100);
        // Pick radius grows with the distance to the camera (a few pixels on screen).
        int radius = (int)(Math.Clamp(camera.GlobalPosition.DistanceTo(hit) * 0.012f, 2.5f, 12f) * 100);

        // Structures: standing on a station, parking lot or platform.
        if (network.HubAt(x, z, radius / 3) is { } hub)
        {
            int owner = StructureRemoval.OwnerOfHub(state, hub.Id);
            if (state.Lifts.FirstOrDefault(l => l.Id == owner) is { } lift)
                return LiftPick(lift, hub.Kind == HubKind.ValleyStation ? "valley station" : "top station");
            if (state.ParkingLots.FirstOrDefault(p => p.Id == owner) is { } lot && state.Lifts.FirstOrDefault(l => l.Id == lot.LiftId) is { } served)
                return new WorldPick($"{lot.Name} · {lot.Spaces} spaces · click for {served.Name}", () => _show(Hud.Menu.Lifts, served.Id.ToString()));
            if (state.Platforms.FirstOrDefault(p => p.Id == owner) is { } platform)
                return new WorldPick("Gravel platform",
                    () => _note($"{platform.Name}: a gravel platform where paths and trails start and end. Build → Renaturalize removes it."));
        }

        // Bars and shops: standing on their pad.
        if (Bikepark.Sim.Facilities.FacilityMath.AtPoint(state, x, z, radius / 3) is { } facility)
        {
            var type = Bikepark.Sim.Facilities.FacilityMath.FindType(state, facility.TypeId);
            string busy = type?.Kind == Bikepark.Sim.Facilities.FacilityKind.Bar
                ? $" · {Bikepark.Sim.Facilities.FacilityMath.SeatsTaken(state, facility)}/{Bikepark.Sim.Facilities.FacilityMath.Seats(type, facility)} seats taken" : "";
            return new WorldPick($"{facility.Name}{busy} · {UiTheme.Money(facility.RevenueTodayCents)} today · click for finances",
                () => _show(Hud.Menu.Finance, facility.Id.ToString()));
        }

        // Ways: the closest one (a trail's feature counts as the trail), unless a lift line is closer.
        var way = network.Nearest(x, z, radius) is { } n ? network.FindWay(n.WayId) is { } w ? (Way: w, n.DistanceCm, Off: Distance(x, z, n.Point)) : default : default;
        var line = NearestLiftLine(x, z, Math.Max(radius, 400));
        if (line is { } l && (way.Way is null || l.Off < way.Off))
            return LiftPick(l.Lift, "lift line");
        if (way.Way is { } found)
            return WayPick(found, way.DistanceCm);

        // Land for sale.
        if (state.Parcels.Count > 0 && !LandMath.IsOwned(state, x, z) && LandMath.ParcelAt(state, x, z) is { } parcel)
            return new WorldPick($"{parcel.Name} · not your land · click for the Land menu",
                () => _show(Hud.Menu.Land, parcel.Id));
        return null;
    }

    private WorldPick LiftPick(Lift lift, string part)
    {
        int liftId = lift.Id;
        string status = !lift.InService ? lift.Derelict && lift.ReadyTick == 0 ? " · rusty" : " · under construction" : $" · {lift.Queue.Count} queuing";
        return new WorldPick($"{lift.Name} ({part}){status} · click for lift stats", () => _show(Hud.Menu.Lifts, liftId.ToString()));
    }

    private WorldPick WayPick(Way way, long atCm)
    {
        var state = _ctx.Sim.State;
        var network = _ctx.Sim.Network;
        int wayId = way.Id;
        if (!way.Built)
        {
            var job = Jobs.ForWay(state, wayId);
            return new WorldPick($"{(way.Kind == WayKind.Trail ? way.Name : "Gravel path")} · planned · click for the crew's job",
                () => _show(Hud.Menu.Crew, job?.Id.ToString()));
        }
        var g = network.Geometry(wayId);
        if (way.Kind == WayKind.AccessPath)
            return new WorldPick($"{(way.Name.Length > 0 ? way.Name : "Gravel path")} · {g.LengthCm / 100} m",
                () => _note($"{(way.Name.Length > 0 ? way.Name : "A gravel path")}: {g.LengthCm / 100} m, {Math.Abs(g.EndHeightCm - g.StartHeightCm) / 100} m up. " +
                            "Riders pedal up and walk on it; gravel paths have no menu."));

        string what = network.FeaturesOn(wayId).FirstOrDefault(f => f.StartCm - 300 <= atCm && atCm <= f.EndCm + 300) is { Type: not null } f
            ? $"{f.Type.Name} on {way.Name} · {(f.Feature.Built ? $"{TrailCondition.Permille(f.Feature) / 10} %" : "planned")}"
            : $"{way.Name} · {WayMeshes.RatingText(g)}{(network.IsRideable(way) ? "" : " · closed")}";
        return new WorldPick($"{what} · click for features and repairs", () => _ctx.OpenRepair(wayId));
    }

    /// <summary>The lift whose line (valley to top station) passes closest, within the radius.</summary>
    private (Lift Lift, long Off)? NearestLiftLine(int x, int z, int radiusCm)
    {
        var state = _ctx.Sim.State;
        (Lift, long)? best = null;
        long bestOff = radiusCm;
        foreach (var lift in state.Lifts)
        {
            if (LiftNetwork.Pad(state, lift.Valley.TerrainEditId) is not { } a || LiftNetwork.Pad(state, lift.Mountain.TerrainEditId) is not { } b) continue;
            long off = SegmentDistance(x, z, a.CenterX, a.CenterZ, b.CenterX, b.CenterZ);
            if (off > bestOff) continue;
            best = (lift, off);
            bestOff = off;
        }
        return best;
    }

    private static long Distance(int x, int z, PointCm p)
    {
        long dx = p.X - x, dz = p.Z - z;
        return (long)Math.Sqrt(dx * dx + dz * dz);
    }

    private static long SegmentDistance(long px, long pz, long ax, long az, long bx, long bz)
    {
        double vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * vx + (pz - az) * vz) / len2, 0, 1);
        double dx = px - (ax + t * vx), dz = pz - (az + t * vz);
        return (long)Math.Sqrt(dx * dx + dz * dz);
    }
}
