using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class TrailGeometryAndPlannerTests
{
    // A 128 m plane falling 10 % towards +X (east).
    private static readonly TerrainGrid Plane = TestTerrain.Plane(100);
    private static readonly TrailRules Rules = new();

    private static PointCm P(int xM, int zM) => new(xM * 100, zM * 100);

    // ---------------------------------------------------------------- geometry

    [Fact]
    public void Spline_PassesThroughControlPoints_AndFollowsTheGround()
    {
        var points = new List<PointCm> { P(10, 20), P(40, 30), P(70, 20), P(100, 40) };
        var geometry = WayGeometry.Build(Plane, WayKind.Trail, points, 1000);

        for (int i = 0; i < geometry.SampleCount; i++)
            Assert.Equal(Plane.HeightAt(geometry.Xs[i], geometry.Zs[i]), geometry.Ys[i]);
        // Ends are exact; the line passes within half a sample spacing of every inner control point.
        Assert.Equal((points[0].X, points[0].Z), (geometry.Xs[0], geometry.Zs[0]));
        Assert.Equal((points[^1].X, points[^1].Z), (geometry.Xs[^1], geometry.Zs[^1]));
        foreach (var p in points)
            Assert.Contains(Enumerable.Range(0, geometry.SampleCount), i =>
                Math.Abs(geometry.Xs[i] - p.X) <= 50 && Math.Abs(geometry.Zs[i] - p.Z) <= 50);
        // Samples are evenly spaced (1 m horizontally), except possibly the last one.
        for (int i = 1; i < geometry.SampleCount - 1; i++)
        {
            long dx = geometry.Xs[i] - geometry.Xs[i - 1], dz = geometry.Zs[i] - geometry.Zs[i - 1];
            Assert.InRange(Math.Sqrt(dx * dx + dz * dz), 97, 103);
        }
    }

    [Fact]
    public void StraightLine_HasExpectedLengthGradeAndSegments()
    {
        var geometry = WayGeometry.Build(Plane, WayKind.Trail, [P(10, 50), P(90, 50)], 1000);

        // 80 m horizontal at 10 % grade: 80 * sqrt(1.01) = 80.40 m (samples rounded to whole cm).
        Assert.InRange(geometry.LengthCm, 8_030, 8_050);
        Assert.Equal(8, geometry.Segments.Count);
        Assert.All(geometry.Segments, s => Assert.InRange(s.GradePermille, -101, -99));
        Assert.All(geometry.Segments.Skip(1), s => Assert.Equal(0, s.TurnPermille));
        Assert.Equal(TrailRating.Green, geometry.Rating);
        Assert.Equal(geometry.LengthCm, geometry.Segments[^1].EndCm);
    }

    [Fact]
    public void PositionAt_InterpolatesAndClamps()
    {
        var geometry = WayGeometry.Build(Plane, WayKind.Trail, [P(10, 50), P(90, 50)], 1000);
        var start = geometry.PositionAt(-100);
        var end = geometry.PositionAt(long.MaxValue);
        var middle = geometry.PositionAt(geometry.LengthCm / 2);

        Assert.Equal((1000, 5000), (start.X, start.Z));
        Assert.Equal((9000, 5000), (end.X, end.Z));
        Assert.InRange(middle.X, 4_990, 5_010);
        Assert.True(middle.DirX > 0 && middle.DirZ == 0);
    }

    [Fact]
    public void AccessPath_IsGraded_ButKeepsEndsOnTheGround()
    {
        // Bumpy ground: ±1 m ridges every 4 m along X.
        var bumpy = TestTerrain.Grid((x, _) => 100_000 + (x % 4 < 2 ? 100 : -100), size: 64);
        var path = WayGeometry.Build(bumpy, WayKind.AccessPath, [P(5, 10), P(55, 10)], 1000);
        var trail = WayGeometry.Build(bumpy, WayKind.Trail, [P(5, 10), P(55, 10)], 1000);

        Assert.Equal(bumpy.HeightAt(500, 1000), path.StartHeightCm);
        Assert.Equal(bumpy.HeightAt(5500, 1000), path.EndHeightCm);
        int pathRange = path.Ys[15..^15].ToArray().Max() - path.Ys[15..^15].ToArray().Min();
        int trailRange = trail.Ys.ToArray().Max() - trail.Ys.ToArray().Min();
        Assert.True(pathRange < trailRange / 4, $"graded range {pathRange} cm vs ground {trailRange} cm");
    }

    // ---------------------------------------------------------------- planner

    [Fact]
    public void Path_ThatIsTooSteep_IsRejected_AlongTheContourItIsFine()
    {
        var steep = TestTerrain.Plane(1000); // 45° = gradient 5.0, beyond the 4.0 limit
        var down = WayPlanner.Plan(steep, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 50), P(90, 50)]);
        var across = WayPlanner.Plan(steep, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(50, 10), P(50, 90)]);

        Assert.Contains(down.Issues, i => i.Code == "pathTooSteep");
        Assert.True(across.IsValid, across.FirstError);
    }

    [Fact]
    public void SteepButAllowedPath_BuildsWithOneMergedWarning()
    {
        var steep = TestTerrain.Plane(500); // 26.6° = gradient 2.9: steep (> 2.0) but within the 4.0 limit
        var plan = WayPlanner.Plan(steep, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 50), P(90, 50)]);

        Assert.True(plan.IsValid, plan.FirstError);
        var warning = Assert.Single(plan.Issues, i => i.Code == "pathSteep");
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal(0, warning.AtCm);
        Assert.Equal(plan.LengthCm, warning.ToCm);
        Assert.Contains("+2.9", warning.Message);
    }

    [Fact]
    public void Path_DrawnDownhill_IsReversedToStartLow()
    {
        var plan = WayPlanner.Plan(Plane, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 50), P(90, 50)]);

        Assert.True(plan.IsValid, plan.FirstError);
        Assert.True(plan.Reversed);
        Assert.Equal(P(90, 50), plan.Points[0]);
        Assert.True(plan.Geometry!.StartHeightCm < plan.Geometry.EndHeightCm);
    }

    [Fact]
    public void Trail_NeedsAnAccessPathFirst()
    {
        var plan = WayPlanner.Plan(Plane, WayNetwork.Empty, Rules, WayKind.Trail, [P(10, 20), P(90, 20)]);
        Assert.Equal("needsAccessPath", Assert.Single(plan.Issues).Code);
    }

    [Fact]
    public void Trail_SnapsBothEndsOntoThePath_AndIsReversedToStartHigh()
    {
        var network = NetworkWithPath();
        var plan = WayPlanner.Plan(Plane, network, Rules, WayKind.Trail, [P(88, 25), P(50, 40), P(12, 25)]);

        Assert.True(plan.IsValid, plan.FirstError);
        Assert.True(plan.Reversed);
        Assert.NotNull(plan.StartJoin);
        Assert.NotNull(plan.EndJoin);
        Assert.Equal(20 * 100, plan.Points[0].Z); // moved onto the path at z = 20 m
        Assert.Equal(20 * 100, plan.Points[^1].Z);
        Assert.True(plan.DropCm > 0);
    }

    [Fact]
    public void Trail_WithAnUnconnectedEnd_IsRejected()
    {
        var plan = WayPlanner.Plan(Plane, NetworkWithPath(), Rules, WayKind.Trail, [P(12, 25), P(60, 80)]);
        Assert.Contains(plan.Issues, i => i.Code == "endNotConnected");
    }

    [Fact]
    public void Trail_ThatClimbs_IsRejected_GentleClimbsOnlyWarn()
    {
        // On a 70 % plane (gradient 3.9) a trail that turns back uphill climbs beyond the +3.0 limit.
        var steep = TestTerrain.Plane(700);
        var tooMuch = WayPlanner.Plan(steep, NetworkWith(trail: false, steep), Rules, WayKind.Trail, [P(15, 25), P(70, 30), P(40, 32), P(85, 25)]);
        Assert.Contains(tooMuch.Issues, i => i.Code == "trailUphill" && i.Severity == IssueSeverity.Error);

        // On a 40 % plane (gradient 2.4) the same climb is only a warning.
        var gentle = TestTerrain.Plane(400);
        var ok = WayPlanner.Plan(gentle, NetworkWith(trail: false, gentle), Rules, WayKind.Trail, [P(15, 25), P(70, 30), P(40, 32), P(85, 25)]);
        Assert.True(ok.IsValid, ok.FirstError);
        Assert.Contains(ok.Issues, i => i.Code == "trailClimb" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Planner_RejectsBadInput()
    {
        Assert.Equal("tooFewPoints", Code(WayPlanner.Plan(Plane, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 10)])));
        Assert.Equal("outsideMap", Code(WayPlanner.Plan(Plane, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 10), new PointCm(-5, 10)])));
        Assert.Contains(WayPlanner.Plan(Plane, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(50, 10), P(50, 20)]).Issues,
            i => i.Code == "tooShort");

        static string Code(WayPlan plan) => plan.Issues[0].Code;
    }

    [Fact]
    public void Planner_CountsTreesInTheCorridor()
    {
        var forest = TestTerrain.Grid((x, _) => 100_000 - x * 10, trees: (_, _) => 255, size: 128);
        var plan = WayPlanner.Plan(forest, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(10, 60), P(110, 60)]);

        var corridor = TerrainScatter.CollectAll(forest).Count(t =>
            t.Kind == ScatterKind.Tree && Math.Abs(t.ZCm - 6000) <= Rules.PathCorridorCm / 2 && t.XCm is >= 1000 and <= 11000);
        Assert.True(plan.TreesToClear > 0);
        Assert.InRange(plan.TreesToClear, corridor - 2, corridor + 2);
        Assert.Contains(plan.Issues, i => i.Code == "clearing" && i.Severity == IssueSeverity.Warning);
    }

    // ---------------------------------------------------------------- network

    [Fact]
    public void Routes_ClimbOnPaths_AndNeverGoUpATrail()
    {
        var network = NetworkWith(trail: true);
        var path = network.BaseWay!;
        var trail = network.Trails.Single();

        var toTrail = network.Route(network.BaseNode, network.StartNode(trail))!;
        var leg = Assert.Single(toTrail);
        Assert.Equal(path.Id, leg.WayId);
        Assert.True(leg.ToCm > leg.FromCm); // up the path

        var back = network.Route(network.EndNode(trail), network.StartNode(trail))!;
        Assert.DoesNotContain(back, l => l.WayId == trail.Id);
        Assert.Empty(network.Route(network.BaseNode, network.BaseNode)!);
    }

    [Fact]
    public void Corridor_ContainsPointsOnTheWay()
    {
        var network = NetworkWithPath();
        Assert.True(network.IsInCorridor(5000, 2000));
        Assert.True(network.IsInCorridor(5000, 2000 + Rules.PathCorridorCm / 2 - 20));
        Assert.False(network.IsInCorridor(5000, 2000 + Rules.PathCorridorCm));
    }

    private static WayNetwork NetworkWithPath() => NetworkWith(trail: false);

    private static WayNetwork NetworkWith(bool trail, TerrainGrid? terrain = null)
    {
        terrain ??= Plane;
        var path = WayPlanner.Plan(terrain, WayNetwork.Empty, Rules, WayKind.AccessPath, [P(5, 20), P(95, 20)]);
        Assert.True(path.IsValid, path.FirstError);
        var ways = new List<Way> { new() { Id = 1, Kind = WayKind.AccessPath, Points = path.Points } };
        var network = WayNetwork.Build(ways, terrain, Rules);
        if (!trail) return network;

        var t = WayPlanner.Plan(terrain, network, Rules, WayKind.Trail, [P(15, 25), P(50, 40), P(85, 25)]);
        Assert.True(t.IsValid, t.FirstError);
        ways.Add(new Way { Id = 2, Kind = WayKind.Trail, Points = t.Points, StartJoin = t.StartJoin, EndJoin = t.EndJoin });
        return WayNetwork.Build(ways, terrain, Rules);
    }
}

public class GradientTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(158, 10)]     // 9°   = 1.0
    [InlineData(-160, -10)]
    [InlineData(325, 20)]     // 18°  = 2.0
    [InlineData(1000, 50)]    // 45°  = 5.0
    [InlineData(-3078, -80)]  // 72°  = -8.0
    [InlineData(1_000_000, 100)] // vertical
    public void FromPermille_MapsAnglesLinearlyOntoMinus10To10(int permille, int tenths) =>
        Assert.Equal(tenths, Gradient.FromPermille(permille));

    [Fact]
    public void Conversions_RoundTrip_AndFormat()
    {
        for (int t = -99; t <= 99; t++)
            Assert.Equal(t, Gradient.FromPermille(Gradient.ToPermille(t)));
        Assert.Equal("-2.3", Gradient.Format(-23));
        Assert.Equal("+1.0", Gradient.Format(10));
        Assert.Equal("0.0", Gradient.Format(0));
    }
}

public class RiderTests
{
    [Fact]
    public void DemoNetwork_IsBuilt_AndRidersDoLaps()
    {
        var sim = DemoWorld(out _);
        var started = new List<RunStarted>();
        var finished = new List<RunFinished>();
        sim.Events.Subscribe<RunStarted>(started.Add);
        sim.Events.Subscribe<RunFinished>(finished.Add);

        sim.RunDays(1);
        sim.Events.Dispatch();

        Assert.Equal(3, sim.State.Ways.Count);
        Assert.True(finished.Count > 100, $"only {finished.Count} runs in a day");
        Assert.All(finished, f => Assert.Contains(sim.State.Ways, w => w.Id == f.TrailId && w.Kind == WayKind.Trail));
        Assert.All(sim.State.Ways.Where(w => w.Kind == WayKind.Trail), w => Assert.True(w.Stats.Runs > 0, $"{w.Name} never ridden"));
        // Every finished run was started first, by the same rider on the same trail.
        foreach (var f in finished)
            Assert.Contains(started, s => s.GuestId == f.GuestId && s.TrailId == f.TrailId && s.Tick <= f.Tick);
    }

    [Fact]
    public void ALap_IsClimbOnThePath_ThenTheTrail_ThenIdleAtTheTrailEnd()
    {
        var sim = DemoWorld(out var network);
        var guest = AddRider(sim, skill: 600);
        var seen = new List<RiderActivity>();
        int energyAtStart = guest.Energy;

        for (int i = 0; i < 120 && guest.RunsCompleted == 0; i++)
        {
            sim.Step();
            if (seen.Count == 0 || seen[^1] != guest.Activity) seen.Add(guest.Activity);
        }

        Assert.Equal(1, guest.RunsCompleted);
        Assert.Equal([RiderActivity.Climbing, RiderActivity.Descending], seen.Take(2));
        Assert.Equal(RiderActivity.Idle, seen[^1]);
        Assert.True(guest.Energy < energyAtStart);
        Assert.Contains(sim.State.Ways, w => w.Id == guest.LastTrailId && w.Kind == WayKind.Trail);
    }

    [Fact]
    public void SkilledRiders_AreFaster_AndTooHardSegmentsSlowEveryoneDown()
    {
        var rules = new TrailRules();
        var segment = new WaySegment(0, 0, 1000, -250, 50, 40, 40, 100, TerrainSurface.Forest, 400);
        var hard = segment with { Difficulty = 900 };
        var novice = new Guest { Skill = 200 };
        var expert = new Guest { Skill = 900 };

        Assert.True(RiderSystem.Speed(expert, rules, WayKind.Trail, segment, -250) > RiderSystem.Speed(novice, rules, WayKind.Trail, segment, -250));
        Assert.True(RiderSystem.Speed(novice, rules, WayKind.Trail, hard, -250) < RiderSystem.Speed(novice, rules, WayKind.Trail, segment, -250));
        // Climbing is slower than descending.
        Assert.True(RiderSystem.Speed(expert, rules, WayKind.AccessPath, segment, 100) < RiderSystem.Speed(expert, rules, WayKind.Trail, segment, -250));
    }

    [Fact]
    public void TiredRiders_GoHome()
    {
        var sim = DemoWorld(out _);
        var guest = AddRider(sim, skill: 500);
        guest.Energy = sim.State.TrailRules.TiredEnergy - 1;
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<GuestLeft>(left.Add);

        sim.Step();
        sim.Events.Dispatch();

        Assert.Contains(left, l => l.GuestId == guest.Id && l.Reason == GuestLeaveReason.Tired);
    }

    [Fact]
    public void DeletingATrail_SendsItsRidersBackToTheBase()
    {
        var sim = DemoWorld(out var network);
        sim.RunTicks(30);
        var red = sim.State.Ways.Single(w => w.Name == "Red Rocket");
        var path = sim.State.Ways.Single(w => w.Kind == WayKind.AccessPath);

        Assert.Contains("attached", Reject(sim, new DeleteWayCommand(path.Id)));
        sim.Commands.Enqueue(new DeleteWayCommand(red.Id));
        sim.Step();

        Assert.DoesNotContain(sim.State.Ways, w => w.Id == red.Id);
        Assert.All(sim.State.Guests, g =>
        {
            Assert.NotEqual(red.Id, g.LocationWayId);
            Assert.DoesNotContain(g.Route, l => l.WayId == red.Id);
        });
    }

    [Fact]
    public void WithoutAnAccessPath_GuestsWander_AsBefore()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.RunTicks(9 * 60);
        Assert.NotEmpty(sim.State.Guests);
        Assert.All(sim.State.Guests, g => Assert.Equal(RiderActivity.Wandering, g.Activity));
    }

    [Fact]
    public void SaveLoad_WithRidersMidRun_ContinuesIdentically()
    {
        var uninterrupted = DemoWorld(out _);
        uninterrupted.RunTicks(400);
        var first = DemoWorld(out _);
        first.RunTicks(200);
        Assert.Contains(first.State.Guests, g => g.Activity == RiderActivity.Descending);

        var resumed = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(first.State)));
        resumed.RunTicks(200);

        Assert.Equal(StateHash.Compute(uninterrupted.State), StateHash.Compute(resumed.State));
    }

    /// <summary>A test world at 08:00 on day 0 with the demo network built.</summary>
    // ---------------------------------------------------------------- traffic on trails

    [Fact]
    public void OnBusyTrails_NobodyOvertakes_AndRidersGetHeldUp()
    {
        var sim = TestWorlds.RunLift(1337, 11 * 60, TestWorlds.DemoLiftNetwork());
        var network = sim.Network;
        Dictionary<int, (List<RouteLeg> Route, int Trail, long Position)> before = [];
        int pairsChecked = 0;
        for (int tick = 0; tick < 180; tick++)
        {
            sim.Step();
            var now = sim.State.Guests
                .Where(g => g.Activity == RiderActivity.Descending && g.EntryWaitMs < 0 && g.Route.Count > 0)
                .ToDictionary(g => g.Id, g => (g.Route, Trail: g.Route[g.LegIndex].WayId, Position: TrailPosition(g)));
            foreach (var (a, pa) in now)
                foreach (var (b, pb) in now)
                {
                    if (a == b || pa.Trail != pb.Trail) continue;
                    if (!before.TryGetValue(a, out var wa) || !before.TryGetValue(b, out var wb)) continue;
                    if (!ReferenceEquals(wa.Route, pa.Route) || !ReferenceEquals(wb.Route, pb.Route) || wa.Trail != pa.Trail || wb.Trail != pb.Trail) continue;
                    if (wa.Position > wb.Position)
                    {
                        Assert.True(pa.Position > pb.Position, $"rider {b} overtook rider {a} on trail {pa.Trail}");
                        pairsChecked++;
                    }
                }
            before = now;
        }
        Assert.True(pairsChecked > 100, $"only {pairsChecked} pairs of riders seen on a trail");
        Assert.True(sim.State.Ways.Sum(w => w.Stats.HeldUpSeconds) > 0);
        Assert.All(sim.State.Ways.Where(w => w.Kind == WayKind.AccessPath), w => Assert.Equal(0, w.Stats.HeldUpSeconds));

        static long TrailPosition(Guest g)
        {
            long before = g.Route.Take(g.LegIndex).Sum(l => l.LengthCm);
            var leg = g.Route[g.LegIndex];
            return leg.FromCm + (g.RouteProgressCm - before);
        }
    }

    [Fact]
    public void AFastRiderBehindASlowOne_StaysBehind_AndLosesMood()
    {
        var sim = DemoWorld(out var network);
        sim.RunTicks(2 * 60); // the park is open
        const int redRocket = 3;
        long length = network.Geometry(redRocket).LengthCm;
        var slow = OnTrail(AddRider(sim, skill: 0), 3_000);
        var fast = OnTrail(AddRider(sim, skill: 1000), 2_000);

        sim.RunTicks(3);
        Assert.Equal(RiderActivity.Descending, fast.Activity);
        Assert.True(fast.RouteProgressCm <= slow.RouteProgressCm - sim.State.TrailRules.RiderGapCm);
        Assert.True(fast.Happiness < 700 - 8, $"fast rider's mood {fast.Happiness}"); // ~5 per minute held up (± 1 noise)
        Assert.True(slow.Happiness >= 700 - 3, $"slow rider's mood {slow.Happiness}");
        Assert.True(network.FindWay(redRocket)!.Stats.HeldUpSeconds >= 120);

        Guest OnTrail(Guest g, long at)
        {
            g.Activity = RiderActivity.Descending;
            g.Route = [new RouteLeg(redRocket, 0, length)];
            g.TrailId = redRocket;
            g.RouteProgressCm = at;
            g.EntryWaitMs = -1;
            return g;
        }
    }

    [Fact]
    public void AtATrailEntrance_RidersWait_AndGiveWayToFasterOnes()
    {
        var sim = DemoWorld(out var network);
        const int redRocket = 3;
        var leg = new RouteLeg(redRocket, 0, network.Geometry(redRocket).LengthCm);
        Guest Rider(int skill, int waitedMs, long progress = 0)
        {
            var g = AddRider(sim, skill);
            g.Activity = RiderActivity.Descending;
            g.Route = [leg];
            g.EntryWaitMs = waitedMs;
            g.RouteProgressCm = progress;
            return g;
        }
        int gap = sim.State.TrailRules.RiderGapCm;

        var slow = Rider(300, 10_000);
        var fast = Rider(900, 0);
        var traffic = new TrailTraffic(network, [slow, fast]);
        Assert.True(traffic.MustGiveWay(slow, leg, gap));   // a faster rider is waiting too
        Assert.False(traffic.MustGiveWay(fast, leg, gap));
        slow.EntryWaitMs = 60_000;
        Assert.False(traffic.MustGiveWay(slow, leg, gap));  // waited long enough: no more giving way

        var onTrail = Rider(500, -1, progress: gap / 2);
        traffic = new TrailTraffic(network, [fast, onTrail]);
        Assert.True(traffic.MustGiveWay(fast, leg, gap));   // somebody just dropped in
        onTrail.RouteProgressCm = gap;
        Assert.False(traffic.MustGiveWay(fast, leg, gap));
        Assert.Equal(0, traffic.RoomAhead(new Guest { Id = 999_999, Route = [leg], Activity = RiderActivity.Descending }, redRocket, 0, gap));
    }

    [Fact]
    public void ARunStarts_AfterTheEntranceWait()
    {
        var sim = DemoWorld(out _);
        sim.RunTicks(2 * 60);
        sim.State.TrailRules.EntryWaitSeconds = 60; // a whole minute, so the wait is seen between two ticks
        var started = new List<RunStarted>();
        sim.Events.Clear();
        sim.Events.Subscribe<RunStarted>(started.Add);
        var guest = AddRider(sim, skill: 600);
        bool sawWaiting = false;
        for (int i = 0; i < 120 && guest.RunsCompleted == 0; i++)
        {
            sim.Step();
            sim.Events.Dispatch();
            if (guest.EntryWaitMs >= 0)
            {
                sawWaiting = true;
                Assert.Equal(guest.Route.Take(guest.LegIndex).Sum(l => l.LengthCm), guest.RouteProgressCm); // still at the entrance
            }
        }
        Assert.Equal(1, guest.RunsCompleted);
        Assert.True(sawWaiting);
        Assert.Contains(started, s => s.GuestId == guest.Id);
    }

    private static Simulation DemoWorld(out WayNetwork network)
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 8 * 60);
        sim.RunTicks(8 * 60 + 1);
        sim.Events.Clear();
        Assert.Equal(3, sim.State.Ways.Count);
        network = sim.Network;
        return sim;
    }

    private static Guest AddRider(Simulation sim, int skill)
    {
        var guest = new Guest
        {
            Id = sim.State.AllocateEntityId(),
            Skill = skill,
            Energy = 1000,
            Happiness = 700,
            ArrivedTick = sim.State.Tick,
            PlannedStayMinutes = 600,
            Activity = RiderActivity.Wandering,
        };
        sim.State.Guests.Add(guest);
        return guest;
    }

    private static string Reject(Simulation sim, ICommand command)
    {
        sim.Commands.Enqueue(command);
        sim.Step();
        var rejected = sim.Events.Pending.OfType<CommandRejected>().Single(r => ReferenceEquals(r.Command, command));
        sim.Events.Clear();
        return rejected.Reason;
    }
}
