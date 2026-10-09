using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class LiftTests
{
    private const int Opening = 9 * 60; // Starter Valley

    // ---------------------------------------------------------------- terrain edits

    [Fact]
    public void Pads_AreFlat_AndTheirEmbankmentsStayWithinTheGrade()
    {
        var sim = LiftWorld(out _);
        var grid = sim.Terrain;
        foreach (var edit in sim.State.TerrainEdits)
        {
            var pad = edit.Pad;
            var (x0, z0, x1, z1) = TerrainEditor.Bounds(pad, grid.SizeMeters, 0);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    long d = pad.DistanceOutside(x * 100L, z * 100L);
                    int diff = Math.Abs(grid.HeightAtSample(x, z) - pad.TargetHeightCm);
                    if (d == 0) Assert.Equal(0, diff);
                    else if (d <= 2_000) Assert.True(diff <= d * pad.EmbankmentPermille / 1000 + 1, $"embankment too steep at ({x}, {z})");
                }
        }
    }

    [Fact]
    public void TerrainEdits_AreDerived_NeverMutateTheGeneratedGrid_AndSurviveSaves()
    {
        var sim = LiftWorld(out _);
        var generated = TerrainGenerator.Generate(sim.State.Terrain, sim.State.Seed);

        Assert.Equal(generated.ComputeHash(), sim.BaseTerrain.ComputeHash());
        Assert.NotEqual(generated.ComputeHash(), sim.Terrain.ComputeHash());
        Assert.Equal(sim.Terrain.ComputeHash(), TerrainEditor.Apply(generated, sim.State.TerrainEdits).ComputeHash());

        var loaded = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(sim.State)));
        Assert.Equal(sim.Terrain.ComputeHash(), loaded.Terrain.ComputeHash());
    }

    [Fact]
    public void Pads_ClearTreesUnderThem()
    {
        var sim = LiftWorld(out _);
        var plateau = sim.State.TerrainEdits[1].Pad;
        var scatter = TerrainScatter.CollectAll(sim.Terrain);
        Assert.DoesNotContain(scatter, s => plateau.Contains(s.XCm, s.ZCm));
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void LiftPlanner_RejectsBadLifts()
    {
        var sim = LiftWorld(out _);
        string Plan(string type, PointCm a, PointCm b, string? op = null, int tier = 0) =>
            StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, type, a, b, operatorId: op, initialTier: tier).Issues
                .FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Code ?? "ok";

        var low = new PointCm(30_000, 95_000);
        var high = new PointCm(60_000, 70_000);
        Assert.Equal("unknownType", Plan("teleporter", low, high));
        Assert.Equal("unknownOperator", Plan("gondola_8", low, high, op: "nobody"));
        Assert.Equal("badTier", Plan("gondola_8", low, high, op: "alpine_lifts", tier: 7));
        Assert.Equal("tooShort", Plan("gondola_8", low, new PointCm(30_000, 85_000)));
        Assert.Equal("notUphill", Plan("gondola_8", high, low));
        // Through the existing valley station.
        Assert.Contains(StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, "gondola_8", new PointCm(20_500, 85_000), high).Issues,
            i => i.Code == "overlapsStructure");
    }

    [Fact]
    public void ParkingPlanner_NeedsAValleyStationInReach()
    {
        var sim = LiftWorld(out _);
        var far = StructurePlanner.PlanParking(sim.Terrain, sim.Network, sim.State, new PointCm(80_000, 90_000), new PointCm(80_000, 80_000), 50);
        Assert.Contains(far.Issues, i => i.Code == "noStation");
        var tooSmall = StructurePlanner.PlanParking(sim.Terrain, sim.Network, sim.State, new PointCm(15_000, 89_000), new PointCm(20_000, 85_000), 2);
        Assert.Contains(tooSmall.Issues, i => i.Code == "badSpaces");
    }

    [Fact]
    public void Pads_CannotBeBuiltOverWays()
    {
        var sim = LiftWorld(out _);
        var route = sim.State.Ways.Single(w => w.Name == "Old Hiking Route");
        var mid = sim.Network.Geometry(route.Id).PositionAt(sim.Network.Geometry(route.Id).LengthCm / 2);
        var plan = StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, "gondola_8", new PointCm(mid.X, mid.Z), new PointCm(60_500, 50_500));
        Assert.Contains(plan.Issues, i => i.Code == "overlapsWay");
    }

    // ---------------------------------------------------------------- network

    [Fact]
    public void StarterValley_HasLiftParkingAndHikingRoute_AllConnected()
    {
        var sim = LiftWorld(out var network);
        var state = sim.State;
        var lift = Assert.Single(state.Lifts);
        var lot = Assert.Single(state.ParkingLots);
        var route = Assert.Single(state.Ways);

        Assert.Equal("alpine_lifts", lift.OperatorId);
        Assert.Equal(250, lift.BikeCarrierPermille);
        Assert.Equal(lift.Id, lot.LiftId);
        Assert.Equal(WayOrigin.Scenario, route.Origin);
        Assert.Equal(lift.Valley.Id, route.StartHubId);
        Assert.Equal(lift.Mountain.Id, route.EndHubId);
        Assert.Equal(HubKind.Parking, network.BaseHub!.Kind);

        // Base → plateau: walk, then the lift; without the lift, walk and pedal up the hiking route.
        int plateau = network.HubNode(lift.Mountain.Id);
        var byLift = network.Route(network.BaseNode, plateau)!;
        Assert.Equal([LegKind.Walk, LegKind.Lift], byLift.Select(l => l.Kind));
        var byFoot = network.Route(network.BaseNode, plateau, _ => false)!;
        Assert.Equal([LegKind.Walk, LegKind.Way], byFoot.Select(l => l.Kind));
        Assert.Equal(route.Id, byFoot[1].WayId);
    }

    [Fact]
    public void Trails_SnapToThePlateau()
    {
        var sim = LiftWorld(out _, withTrails: true);
        var lift = sim.State.Lifts[0];
        var trails = sim.State.Ways.Where(w => w.Kind == WayKind.Trail).ToList();
        Assert.Equal(2, trails.Count);
        Assert.All(trails, t => Assert.Equal(lift.Mountain.Id, t.StartHubId));
        Assert.All(trails, t => Assert.Equal(lift.Valley.Id, t.EndHubId));
    }

    [Fact]
    public void ScenarioRoute_AndUsedStations_CannotBeDeleted()
    {
        var sim = LiftWorld(out _);
        var route = sim.State.Ways[0];
        Assert.Contains("scenario", Reject(sim, new DeleteWayCommand(route.Id)));
        Assert.Contains("attached", Reject(sim, new DeleteLiftCommand(sim.State.Lifts[0].Id)));
    }

    // ---------------------------------------------------------------- bike carriers and queues

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(333)]
    [InlineData(500)]
    [InlineData(1000)]
    public void BikeCarriers_AreSpreadEvenly_AtTheConfiguredShare(int permille)
    {
        int count = Enumerable.Range(0, 1000).Count(k => LiftMath.IsBikeCarrier(k, permille));
        Assert.Equal(permille, count);
        if (permille == 250)
            Assert.Equal([3, 7, 11, 15], Enumerable.Range(0, 16).Where(k => LiftMath.IsBikeCarrier(k, 250)));
    }

    [Fact]
    public void ThroughputPerHour_MatchesTheTier_AndTheQueueIsFifo()
    {
        var sim = LiftWorld(out _, withTrails: true, startTick: Opening);
        var lift = sim.State.Lifts[0];
        var type = LiftNetwork.FindType(sim.State, lift.TypeId)!;
        Assert.Equal(225, LiftMath.BikeRidersPerHour(type, lift.BikeCarrierPermille)); // tier 1: every 4th cabin, 3 bikes each

        // 400 riders already in line: more than an hour's worth.
        var queued = Enumerable.Range(0, 400).Select(_ => QueueRider(sim, lift)).ToList();
        var boarded = new List<RiderBoarded>();
        sim.Events.Subscribe<RiderBoarded>(boarded.Add);
        long before = lift.Stats.Riders;

        sim.RunTicks(60);
        sim.Events.Dispatch();

        Assert.Equal(225, lift.Stats.Riders - before);
        Assert.Equal(queued.Take(225).Select(g => g.Id), boarded.Take(225).Select(b => b.GuestId));
    }

    [Fact]
    public void LongQueues_ShowUp_AndRidersInLineLoseMood()
    {
        var sim = LiftWorld(out _, withTrails: true);
        sim.RunTicks(Opening + 4 * 60);
        var lift = sim.State.Lifts[0];
        Assert.True(lift.Stats.MaxQueue >= 20, $"max queue {lift.Stats.MaxQueue}");
        Assert.True(lift.Stats.Riders > 0);
        Assert.True(lift.Stats.SumWaitMinutes > 0);
        Assert.All(lift.Queue, id => Assert.Equal(RiderActivity.Queuing, sim.State.Guests.Single(g => g.Id == id).Activity));
    }

    [Fact]
    public void UnhappyRiders_LeaveTheQueue_AndAreRemovedFromIt()
    {
        var sim = LiftWorld(out _, withTrails: true, startTick: Opening);
        var lift = sim.State.Lifts[0];
        var others = Enumerable.Range(0, 30).Select(_ => QueueRider(sim, lift)).ToList();
        var grumpy = QueueRider(sim, lift);
        grumpy.Happiness = 10;

        sim.Step();

        Assert.DoesNotContain(sim.State.Guests, g => g.Id == grumpy.Id);
        Assert.DoesNotContain(grumpy.Id, lift.Queue);
        Assert.Contains(others[^1].Id, lift.Queue);
    }

    [Fact]
    public void WithoutBikeAccess_RidersPedalUpTheHikingRoute()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Commands.Enqueue(new SetLiftBikeAccessCommand(1, 0), 0);
        var seen = new HashSet<RiderActivity>();
        for (int i = 0; i < Opening + 3 * 60; i++)
        {
            sim.Step();
            foreach (var g in sim.State.Guests) seen.Add(g.Activity);
        }

        var lift = sim.State.Lifts[0];
        Assert.Equal(0, lift.BikeCarrierPermille);
        Assert.Equal(0, lift.Stats.Riders);
        Assert.Contains(RiderActivity.Climbing, seen);
        Assert.DoesNotContain(RiderActivity.Queuing, seen);
        Assert.True(sim.State.Ways.Where(w => w.Kind == WayKind.Trail).Sum(w => w.Stats.Runs) > 0);
    }

    [Fact]
    public void LongQueues_SendFitRidersUpTheHikingRoute()
    {
        var sim = LiftWorld(out var network, withTrails: true);
        sim.RunTicks(Opening + 5 * 60);
        var trail = sim.State.Ways.First(w => w.Kind == WayKind.Trail);
        var guest = new Guest { Energy = 1000, Skill = 500 };
        var lift = sim.State.Lifts[0];
        int start = network.StartNode(trail);

        lift.Queue.Clear();
        Assert.Contains(RiderSystem.BestRoute(sim.State, network, guest, network.BaseNode, start)!, l => l.Kind == LegKind.Lift);
        lift.Queue.AddRange(Enumerable.Range(100_000, 300));
        Assert.DoesNotContain(RiderSystem.BestRoute(sim.State, network, guest, network.BaseNode, start)!, l => l.Kind == LegKind.Lift);
        lift.Queue.Clear();
    }

    // ---------------------------------------------------------------- bike access tiers and fees

    [Fact]
    public void BookedTier_StartsAtTheNextOpening_AndEachDayPaysItsActiveTier()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        sim.Commands.Enqueue(new SetLiftBikeAccessCommand(1, 3), 10 * 60);
        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        var lift = () => sim.State.Lifts[0];

        sim.RunTicks(12 * 60);
        Assert.Equal(250, lift().BikeCarrierPermille);
        Assert.Equal(3, lift().BikeAccess!.PendingTierIndex);

        sim.RunTicks(GameTime.MinutesPerDay - 12 * 60 + Opening + 1);
        Assert.Equal(1000, lift().BikeCarrierPermille);
        Assert.Equal(3, lift().BikeAccess!.TierIndex);
        Assert.Null(lift().BikeAccess!.PendingTierIndex);

        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Equal([30_000L, 140_000L], days.Select(d => d.LiftFeesCents));
        Assert.Equal(170_000, sim.State.Finance.TotalLiftFeesCents);
    }

    [Fact]
    public void BikeAccess_CannotBeBooked_ForUnknownTiersOrParkOwnedLifts()
    {
        var sim = LiftWorld(out _);
        Assert.Contains("tier", Reject(sim, new SetLiftBikeAccessCommand(1, 4)));
        Assert.Contains("lift", Reject(sim, new SetLiftBikeAccessCommand(999, 1)));
    }

    // ---------------------------------------------------------------- parking

    [Fact]
    public void FullParkingLots_TurnGuestsAway()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.Commands = scenario.Commands
            .Select(c => c.Command is BuildParkingLotCommand p ? c with { Command = p with { Spaces = 10 } } : c)
            .ToList();
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.RunTicks(Opening + 3 * 60);

        Assert.True(sim.State.Stats.TotalTurnedAwayParkingFull > 0);
        Assert.True(sim.State.Guests.Count <= 20);
    }

    // ---------------------------------------------------------------- determinism and saves

    [Fact]
    public void LiftWorld_IsDeterministic_AndSeedsDiverge()
    {
        var a = TestWorlds.RunLift(7, 2 * GameTime.MinutesPerDay + 300, TestWorlds.LiftScript());
        var b = TestWorlds.RunLift(7, 2 * GameTime.MinutesPerDay + 300, TestWorlds.LiftScript());
        var c = TestWorlds.RunLift(8, 2 * GameTime.MinutesPerDay + 300, TestWorlds.LiftScript());

        Assert.Equal(StateHash.Compute(a.State), StateHash.Compute(b.State));
        Assert.NotEqual(StateHash.Compute(a.State), StateHash.Compute(c.State));
        Assert.Equal(500, a.State.Lifts[0].BikeCarrierPermille);
        Assert.True(a.State.Lifts[0].Stats.Riders > 0);
    }

    [Fact]
    public void SaveLoad_WithBusyQueues_ContinuesIdentically()
    {
        var uninterrupted = TestWorlds.RunLift(3, Opening + 6 * 60, TestWorlds.LiftScript());
        var first = TestWorlds.RunLift(3, Opening + 3 * 60, TestWorlds.LiftScript());
        Assert.NotEmpty(first.State.Lifts[0].Queue);
        Assert.Contains(first.State.Guests, g => g.Activity == RiderActivity.OnLift || g.Activity == RiderActivity.Queuing);

        var resumed = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(first.State)));
        resumed.RunTicks(3 * 60);

        Assert.Equal(StateHash.Compute(uninterrupted.State), StateHash.Compute(resumed.State));
    }

    [Fact]
    public void Version2Saves_WithoutLiftFields_StillLoad()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.RunTicks(Opening + 60);
        string json = SaveGame.Serialize(sim.State);

        // Strip everything Phase 3 added, as an older build would have written it.
        var root = JsonNode.Parse(json)!.AsObject();
        var world = root["world"]!.AsObject();
        foreach (string key in new[] { "terrainEdits", "terrainRevision", "liftTypes", "operators", "liftRules", "lifts", "parkingLots" })
            Assert.True(world.Remove(key), key);
        world["finance"]!.AsObject().Remove("totalLiftFeesCents");
        world["finance"]!.AsObject().Remove("liftFeesTodayCents");
        world["stats"]!.AsObject().Remove("totalTurnedAwayParkingFull");
        foreach (var way in world["ways"]!.AsArray())
            foreach (string key in new[] { "startHubId", "endHubId", "origin" }) way!.AsObject().Remove(key);
        foreach (var guest in world["guests"]!.AsArray())
        {
            guest!.AsObject().Remove("locationHubId");
            guest.AsObject().Remove("queueSinceTick");
            foreach (var leg in guest["route"]!.AsArray()) leg!.AsObject().Remove("kind");
        }

        var loaded = SaveGame.Deserialize(root.ToJsonString());
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }

    [Fact]
    public void Scenario_RejectsBadBikeAccessTiers()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.Operators[0].BikeAccessTiers[2].BikeCarrierPermille = 100;
        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(scenario));
        Assert.Contains("strictly increasing", ex.Message);
    }

    [Fact]
    public void Scenario_RejectsUnknownLiftFields()
    {
        string json = """{ "id": "x", "liftTypes": [ { "id": "g", "wings": 2 } ] }""";
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ScenarioLoader.Parse(json));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Starter Valley after its tick-0 commands (optionally with the demo trails), stepped to <paramref name="startTick"/>.</summary>
    private static Simulation LiftWorld(out WayNetwork network, bool withTrails = false, long startTick = 1)
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        if (withTrails)
            foreach (var c in TestWorlds.DemoLiftNetwork())
                sim.Commands.Enqueue(c.Command, 0);
        sim.RunTicks(startTick);
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        sim.Events.Clear();
        network = sim.Network;
        return sim;
    }

    /// <summary>A rider standing in the lift queue, on a lap to the first trail.</summary>
    private static Guest QueueRider(Simulation sim, Lift lift)
    {
        var network = sim.Network;
        var trail = network.Trails.First();
        var liftLeg = network.Route(network.HubNode(lift.Valley.Id), network.HubNode(lift.Mountain.Id))!.Single();
        var guest = new Guest
        {
            Id = sim.State.AllocateEntityId(),
            Skill = 500,
            Energy = 1000,
            Happiness = 700,
            ArrivedTick = sim.State.Tick,
            PlannedStayMinutes = 600,
            Activity = RiderActivity.Queuing,
            QueueSinceTick = sim.State.Tick,
            TrailId = trail.Id,
            Route = [liftLeg, new RouteLeg(trail.Id, 0, network.Geometry(trail.Id).LengthCm)],
        };
        sim.State.Guests.Add(guest);
        lift.Queue.Add(guest.Id);
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
