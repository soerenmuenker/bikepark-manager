using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Moves riders around the way network. Riders are simulated, not physically simulated: each lap a rider picks a
/// trail, follows the cheapest route up the access paths to its start, rides it down, and scores the run.
/// Each tick covers 60 s of travel, segment by segment, at a speed from skill, grade, surface and difficulty.
/// Without an access path guests keep their pre-trail behaviour (<see cref="RiderActivity.Wandering"/>).
/// </summary>
internal sealed class RiderSystem : ISimSystem
{
    private const int TickMilliseconds = 60_000;
    private const int MinSpeedCmPerS = 60;
    private const int UphillGradeThreshold = 20; // ‰ along travel; above this a rider is climbing

    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Guests.Count == 0) return;

        if (state.Ways.Count == 0 || !ctx.Network.HasAccessPath)
        {
            foreach (var guest in state.Guests)
                if (guest.Activity != RiderActivity.Wandering) MakeWandering(guest);
            return;
        }

        var network = ctx.Network;
        foreach (var guest in state.Guests)
        {
            if (guest.Activity == RiderActivity.Wandering)
                PlaceAtBase(guest, network);

            if (guest.Activity == RiderActivity.Idle)
            {
                if (guest.Energy < state.TrailRules.TiredEnergy || !ParkSchedule.IsOpen(state, ctx.Tick)) continue;
                if (!StartLap(ctx, network, guest)) continue;
            }

            Advance(ctx, network, guest, TickMilliseconds);
        }
    }

    /// <summary>Riders whose lap or position uses the way go back to the base (or wander if there is no path left).</summary>
    internal static void ResetRidersUsing(SimContext ctx, int wayId)
    {
        var network = ctx.Network;
        foreach (var guest in ctx.State.Guests)
        {
            bool affected = guest.LocationWayId == wayId || guest.TrailId == wayId || guest.Route.Any(l => l.WayId == wayId);
            if (!affected) continue;
            if (network.HasAccessPath) PlaceAtBase(guest, network); else MakeWandering(guest);
        }
    }

    private static void PlaceAtBase(Guest guest, WayNetwork network)
    {
        ClearLap(guest);
        guest.Activity = RiderActivity.Idle;
        guest.LocationWayId = network.BaseWay!.Id;
        guest.LocationCm = 0;
    }

    private static void MakeWandering(Guest guest)
    {
        ClearLap(guest);
        guest.Activity = RiderActivity.Wandering;
        guest.LocationWayId = 0;
        guest.LocationCm = 0;
    }

    private static void ClearLap(Guest guest)
    {
        guest.Route = [];
        guest.RouteProgressCm = 0;
        guest.LegIndex = 0;
        guest.TrailId = 0;
        guest.RunFun = 0;
        guest.RunSegments = 0;
    }

    // ---------------------------------------------------------------- choosing a trail

    private static bool StartLap(SimContext ctx, WayNetwork network, Guest guest)
    {
        int from = network.NodeAt(guest.LocationWayId, guest.LocationCm);
        if (from < 0)
        {
            PlaceAtBase(guest, network);
            from = network.BaseNode;
        }

        var options = new List<(Way Trail, List<RouteLeg> Route, int Weight)>();
        foreach (var trail in network.Trails)
        {
            var route = network.Route(from, network.StartNode(trail));
            if (route is null) continue;
            options.Add((trail, route, Weight(guest, trail, network.Geometry(trail.Id))));
        }
        if (options.Count == 0) return false;

        int roll = ctx.Rng.NextInt(options.Sum(o => o.Weight));
        var pick = options[^1];
        foreach (var option in options)
        {
            if (roll < option.Weight) { pick = option; break; }
            roll -= option.Weight;
        }

        var legs = pick.Route;
        legs.Add(new RouteLeg(pick.Trail.Id, 0, network.Geometry(pick.Trail.Id).LengthCm));
        guest.Route = legs;
        guest.RouteProgressCm = 0;
        guest.TrailId = pick.Trail.Id;
        guest.RunFun = 0;
        guest.RunSegments = 0;
        EnterLeg(ctx, network, guest, 0);
        return true;
    }

    /// <summary>How much a rider wants this trail: difficulty vs skill, style, and a little variety.</summary>
    internal static int Weight(Guest guest, Way trail, WayGeometry geometry)
    {
        int difficulty = geometry.DifficultyScore;
        int target = guest.Skill * 85 / 100;
        int match = Math.Clamp(1000 - Math.Abs(difficulty - target) * 2, 0, 1000);
        if (difficulty > guest.Skill + 150) match /= 4; // too scary

        var segments = geometry.Segments;
        int rough = (int)segments.Average(s => Math.Max(s.Rock, s.Roots * 8 / 10));
        int turns = (int)segments.Average(s => s.TurnPermille);
        int steep = (int)segments.Average(s => Math.Max(0, -s.GradePermille));
        int style = guest.Style switch
        {
            RiderStyle.Flow => Math.Clamp(500 + turns * 2 - rough * 2, 0, 1000),
            RiderStyle.Technical => Math.Clamp(200 + rough * 3 + steep / 2, 0, 1000),
            _ => Math.Clamp(1000 - difficulty, 0, 1000),
        };

        int weight = match * 6 / 10 + style * 3 / 10 + 100;
        if (trail.Id == guest.LastTrailId) weight = weight * 6 / 10;
        return Math.Max(20, weight);
    }

    // ---------------------------------------------------------------- moving

    private static void Advance(SimContext ctx, WayNetwork network, Guest guest, int budgetMs)
    {
        var rules = ctx.State.TrailRules;
        while (budgetMs > 0 && guest.Activity is RiderActivity.Climbing or RiderActivity.Descending)
        {
            var (legIndex, offset) = LocateOnRoute(guest);
            var leg = guest.Route[legIndex];
            if (!network.TryGetGeometry(leg.WayId, out var geometry))
            {
                PlaceAtBase(guest, network);
                return;
            }

            int dir = leg.ToCm >= leg.FromCm ? 1 : -1;
            long position = leg.FromCm + dir * offset;
            if (position == leg.ToCm)
            {
                FinishLeg(ctx, network, guest, legIndex);
                continue;
            }

            var segment = geometry.Segments[geometry.SegmentIndexAt(dir > 0 ? position : position - 1)];
            long boundary = dir > 0 ? Math.Min(segment.EndCm, leg.ToCm) : Math.Max(segment.StartCm, leg.ToCm);
            long toBoundary = Math.Abs(boundary - position);
            int gradeAlong = segment.GradePermille * dir;
            bool isRun = legIndex == guest.Route.Count - 1;
            int speed = Speed(guest, rules, geometry.Kind, segment, gradeAlong);

            long reach = (long)speed * budgetMs / 1000;
            if (reach <= 0) break;
            long move = Math.Min(reach, toBoundary);
            budgetMs -= (int)Math.Min(budgetMs, Math.Max(1, (move * 1000 + speed - 1) / speed));
            guest.RouteProgressCm += move;

            // Energy: climbing costs per meter gained, descending a little per distance.
            if (gradeAlong > 0)
                guest.Energy -= (int)(move * gradeAlong / 1000 * rules.ClimbEnergyPerMeterGain / 100);
            else
                guest.Energy -= (int)(move * rules.DescentEnergyPer100Meters / 10_000);
            guest.Energy = Math.Max(0, guest.Energy);

            if (isRun && move == toBoundary)
            {
                guest.RunFun += SegmentFun(guest, rules, segment, gradeAlong, speed);
                guest.RunSegments++;
            }
        }
    }

    /// <summary>The leg the rider is on and how far into it.</summary>
    private static (int Leg, long Offset) LocateOnRoute(Guest guest)
    {
        int leg = Math.Clamp(guest.LegIndex, 0, guest.Route.Count - 1);
        long before = 0;
        for (int i = 0; i < leg; i++)
            before += guest.Route[i].LengthCm;
        return (leg, Math.Clamp(guest.RouteProgressCm - before, 0, guest.Route[leg].LengthCm));
    }

    private static void FinishLeg(SimContext ctx, WayNetwork network, Guest guest, int legIndex)
    {
        if (legIndex + 1 < guest.Route.Count)
            EnterLeg(ctx, network, guest, legIndex + 1);
        else
            FinishRun(ctx, network, guest);
    }

    private static void EnterLeg(SimContext ctx, WayNetwork network, Guest guest, int legIndex)
    {
        var leg = guest.Route[legIndex];
        guest.LegIndex = legIndex;
        bool onTrail = network.FindWay(leg.WayId)?.Kind == WayKind.Trail;
        guest.Activity = onTrail ? RiderActivity.Descending : RiderActivity.Climbing;
        if (legIndex == guest.Route.Count - 1)
        {
            guest.RunStartTick = ctx.Tick;
            ctx.Publish(new RunStarted(ctx.Tick, guest.Id, guest.TrailId));
        }
    }

    private static void FinishRun(SimContext ctx, WayNetwork network, Guest guest)
    {
        int trailId = guest.TrailId;
        var trail = network.FindWay(trailId);
        int minutes = (int)(ctx.Tick - guest.RunStartTick + 1);
        int fun = guest.RunSegments == 0 ? 0 : (int)(guest.RunFun / guest.RunSegments);

        guest.Happiness = Math.Clamp(guest.Happiness + Math.Clamp((fun - 450) / 4, -80, 100), 0, 1000);
        guest.RunsCompleted++;
        guest.LastTrailId = trailId;
        if (trail is not null)
        {
            trail.Stats.Runs++;
            trail.Stats.RunsToday++;
            trail.Stats.SumRunMinutes += minutes;
            trail.Stats.SumFun += fun;
            guest.LocationWayId = trailId;
            guest.LocationCm = network.Geometry(trailId).LengthCm;
        }
        ClearLap(guest);
        guest.Activity = RiderActivity.Idle;
        ctx.Publish(new RunFinished(ctx.Tick, guest.Id, trailId, minutes, fun));
    }

    // ---------------------------------------------------------------- tuning

    /// <summary>Riding speed in cm/s on a segment.</summary>
    internal static int Speed(Guest guest, TrailRules rules, WayKind kind, WaySegment segment, int gradeAlong)
    {
        int skill = guest.Skill;
        int climb = rules.ClimbSpeedMinCmPerS + (rules.ClimbSpeedMaxCmPerS - rules.ClimbSpeedMinCmPerS) * skill / 1000;
        int descent = rules.DescentSpeedMinCmPerS + (rules.DescentSpeedMaxCmPerS - rules.DescentSpeedMinCmPerS) * skill / 1000;
        long v;

        if (gradeAlong > UphillGradeThreshold)
        {
            v = climb * 1000L / (1000 + gradeAlong * 6);
        }
        else if (kind == WayKind.AccessPath)
        {
            v = gradeAlong >= 0 ? climb : descent * 7 / 10; // cruising down gravel
        }
        else
        {
            v = descent;
            v = v * (1000 + Math.Clamp(-gradeAlong, 0, 300)) / 1000;
            int rough = Math.Max(segment.Rock, segment.Roots * 8 / 10);
            v = v * (1000 - rough * 300 / 255) / 1000;
            int over = Math.Max(0, segment.Difficulty - skill);
            v = v * 1000 / (1000 + over * 3);
            v = v * (1000 - Math.Clamp(segment.TurnPermille - 80, 0, 450)) / 1000; // gentle bends are free
        }
        return (int)Math.Max(MinSpeedCmPerS, v);
    }

    /// <summary>How much a rider enjoyed a trail segment, 0..1000.</summary>
    internal static int SegmentFun(Guest guest, TrailRules rules, WaySegment segment, int gradeAlong, int speed)
    {
        if (gradeAlong > UphillGradeThreshold) return 150;

        int difficulty = segment.Difficulty;
        int target = guest.Skill * 85 / 100;
        int match = Math.Clamp(1000 - Math.Abs(difficulty - target) * 2, 0, 1000);
        int speedScore = Math.Clamp(speed * 1000 / rules.DescentSpeedMaxCmPerS, 0, 1000);
        int down = Math.Max(0, -gradeAlong);
        int rough = Math.Max(segment.Rock, segment.Roots * 8 / 10);
        int style = guest.Style switch
        {
            RiderStyle.Flow => Math.Clamp(400 + Math.Min(400, segment.TurnPermille * 2) + down / 2 - rough * 3, 0, 1000),
            RiderStyle.Technical => Math.Clamp(200 + rough * 3 + down, 0, 1000),
            _ => Math.Clamp(1000 - difficulty - Math.Max(0, down - 150), 0, 1000),
        };

        int fun = (match * 45 + speedScore * 25 + style * 30) / 100;
        if (difficulty > guest.Skill + 200) fun /= 2; // scared
        return fun;
    }
}
