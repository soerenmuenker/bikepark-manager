using Bikepark.Sim.Core;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Moves riders around the network. Riders are simulated, not physically simulated: each lap a rider picks a
/// trail, takes the cheapest way to its start (walk from the parking lot, queue for and ride the lift, or pedal up
/// the access paths, whichever costs less given the current queue and the rider's energy), rides it down, and scores
/// the run. Each tick covers 60 s of travel, segment by segment, at a speed from skill, grade, surface and difficulty.
/// Riders in a lift queue wait for <see cref="LiftSystem"/> to board them. Without a base (no access path, station or
/// parking lot) guests keep their pre-trail behaviour (<see cref="RiderActivity.Wandering"/>).
/// </summary>
internal sealed class RiderSystem : ISimSystem
{
    private const int TickMilliseconds = 60_000;
    private const int MinSpeedCmPerS = 60;
    private const int UphillGradeThreshold = 20; // ‰ along travel; above this a rider is climbing

    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        // Crossing passages are kept for this minute and the one before (collisions across the minute's start).
        state.Safety.Passages.RemoveAll(p => p.TimeMs < (ctx.Tick - 1) * TickMilliseconds);
        if (state.Guests.Count == 0) return;

        var network = ctx.Network;
        if (!network.HasBase)
        {
            foreach (var guest in state.Guests)
                if (guest.Activity != RiderActivity.Wandering) MakeWandering(state, guest);
            return;
        }

        var movers = new List<Guest>();
        var injured = new List<Guest>();
        foreach (var guest in state.Guests)
        {
            if (guest.Activity == RiderActivity.Injured)
            {
                injured.Add(guest); // lies on the trail (and blocks it) until the helicopter comes
                continue;
            }
            if (guest.Activity == RiderActivity.Wandering)
                PlaceAtBase(state, guest, network);

            if (guest.Activity == RiderActivity.Eating)
            {
                if (ctx.Tick < guest.BusyUntilTick) continue;
                guest.Activity = RiderActivity.Idle;
                guest.FacilityId = 0;
            }

            if (guest.Activity == RiderActivity.Idle)
            {
                if (guest.Injury != InjurySeverity.None) continue; // going home
                if (!ParkSchedule.IsOpen(state, ctx.Tick)) continue;
                if (TryStartLunch(ctx, guest)) continue;
                if (guest.Energy < state.TrailRules.TiredEnergy) continue;
                if (!StartLap(ctx, network, guest)) continue;
            }

            if (IsMoving(guest)) movers.Add(guest);
        }

        // Riders already on a trail move first, front to back, so each one sees where the rider ahead is now; then the
        // others in list order (riders dropping into a trail this minute line up behind them).
        var traffic = new TrailTraffic(network, movers, injured);
        foreach (var guest in traffic.MovingOrder(movers))
            Advance(ctx, network, traffic, guest, TickMilliseconds);
    }

    /// <summary>
    /// At their first break between laps after their planned <see cref="Guest.LunchMinute"/>, guests have lunch.
    /// Scenarios with bars: at a bar at the guest's base with a free seat (its lunch price; guests who can't afford it,
    /// or who find no seat by the end of the lunch window, eat a packed lunch where they are: no money, little mood);
    /// while there is no such bar the guest rides on and tries again after the next lap. Scenarios without bars: lunch
    /// where they are at <see cref="ParkRules.LunchPriceCents"/> (guests who can't afford it bring their own).
    /// Lunch restores energy and mood.
    /// </summary>
    private static bool TryStartLunch(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        var rules = state.Rules;
        int minute = GameTime.MinuteOfDay(ctx.Tick);
        if (guest.HadLunch || guest.LunchMinute < 0 || minute < guest.LunchMinute)
            return false;

        long paid = 0;
        int facilityId = 0;
        int happiness = rules.LunchHappiness;
        if (!FacilityMath.BarsNeeded(state))
        {
            if (guest.CashCents >= rules.LunchPriceCents)
            {
                paid = rules.LunchPriceCents;
                guest.CashCents -= paid;
                state.Finance.Earn(paid);
                state.Finance.TotalFoodCents += paid;
                state.Finance.FoodTodayCents += paid;
            }
        }
        else if (FacilityMath.FreeBar(state, ctx.Network, guest) is { } bar && guest.CashCents >= bar.Type.LunchPriceCents)
        {
            paid = bar.Type.LunchPriceCents;
            FacilityMath.Sell(state, bar.Facility, guest, paid);
            bar.Facility.Customers++;
            state.Finance.TotalFoodCents += paid;
            state.Finance.FoodTodayCents += paid;
            facilityId = bar.Facility.Id;
        }
        else if (minute < rules.LunchEndMinute && state.Facilities.Any(f =>
                     FacilityMath.FindType(state, f.TypeId) is { Kind: FacilityKind.Bar } type && guest.CashCents >= type.LunchPriceCents))
        {
            return false; // ride on: maybe the next lap ends at a bar with a free seat
        }
        else
        {
            happiness = rules.PackedLunchHappiness;
        }
        guest.HadLunch = true;
        guest.Activity = RiderActivity.Eating;
        guest.FacilityId = facilityId;
        guest.BusyUntilTick = ctx.Tick + ctx.Rng.Range(rules.LunchMinMinutes, rules.LunchMaxMinutes + 1);
        guest.Energy = Math.Min(1000, guest.Energy + rules.LunchEnergy);
        guest.Happiness = Math.Min(1000, guest.Happiness + happiness);
        ctx.Publish(new GuestAteLunch(ctx.Tick, guest.Id, paid, facilityId));
        return true;
    }

    /// <summary>
    /// Riders whose lap or position uses one of the ids (ways, lifts, stations, parking lots) go back to the base
    /// (or wander if there is no base left). Call after the change, so the base reflects the new network.
    /// </summary>
    internal static void ResetRidersUsing(SimContext ctx, params int[] ids)
    {
        var network = ctx.Network;
        foreach (var guest in ctx.State.Guests)
        {
            bool affected = ids.Contains(guest.LocationWayId) || ids.Contains(guest.LocationHubId) || ids.Contains(guest.TrailId)
                            || guest.Route.Any(l => ids.Contains(l.WayId));
            if (!affected) continue;
            if (network.HasBase) PlaceAtBase(ctx.State, guest, network); else MakeWandering(ctx.State, guest);
        }
    }

    /// <summary>Sends a rider back to where guests arrive (riders in a queue leave it).</summary>
    internal static void PlaceAtBase(WorldState state, Guest guest, WayNetwork network)
    {
        ClearLap(state, guest);
        guest.Activity = RiderActivity.Idle;
        if (network.BaseHub is { } hub)
        {
            guest.LocationHubId = hub.Id;
            guest.LocationWayId = 0;
        }
        else
        {
            guest.LocationHubId = 0;
            guest.LocationWayId = network.BaseWay!.Id;
        }
        guest.LocationCm = 0;
    }

    private static void MakeWandering(WorldState state, Guest guest)
    {
        ClearLap(state, guest);
        guest.Activity = RiderActivity.Wandering;
        guest.LocationWayId = 0;
        guest.LocationHubId = 0;
        guest.LocationCm = 0;
    }

    private static void ClearLap(WorldState state, Guest guest)
    {
        if (guest.Activity == RiderActivity.Queuing)
            LiftSystem.LeaveQueue(state, guest);
        guest.Route = [];
        guest.RouteProgressCm = 0;
        guest.LegIndex = 0;
        guest.EntryWaitMs = -1;
        guest.TrailId = 0;
        guest.RunFun = 0;
        guest.RunSegments = 0;
    }

    // ---------------------------------------------------------------- choosing a trail

    private static bool StartLap(SimContext ctx, WayNetwork network, Guest guest)
    {
        var state = ctx.State;
        int from = guest.LocationHubId != 0 ? network.HubNode(guest.LocationHubId) : network.NodeAt(guest.LocationWayId, guest.LocationCm);
        if (from < 0)
        {
            PlaceAtBase(state, guest, network);
            from = network.BaseNode;
        }

        var options = new List<(Way Trail, List<RouteLeg> Route, int Weight)>();
        foreach (var trail in network.Trails)
        {
            if (!network.IsRideable(trail)) continue; // closed, worn out or not connected
            var route = BestRoute(state, network, guest, from, network.StartNode(trail));
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
        guest.LocationHubId = 0;
        EnterLeg(ctx, network, guest, 0);
        return true;
    }

    /// <summary>
    /// The cheaper of the best route using lifts (plus the expected queue time) and the best route on foot / pedalling.
    /// Climbing costs more for tired riders. Lifts without bike carriers are never used, nor trails that are closed.
    /// </summary>
    internal static List<RouteLeg>? BestRoute(WorldState state, WayNetwork network, Guest guest, int from, int to)
    {
        // Without the day pass a guest needs the cash for it; if the pass is too expensive for them lifts are out.
        long ticket = guest.HasLiftPass ? 0 : state.Park.LiftTicketCents;
        bool canPay = ticket <= guest.CashCents;
        bool Usable(int liftId) => canPay && state.Lifts.FirstOrDefault(l => l.Id == liftId) is { BikeCarrierPermille: > 0 };
        bool Open(int wayId) => network.FindWay(wayId) is not { } way || network.IsRideable(way);
        var withLifts = network.Route(from, to, Usable, out long liftCost, Open);
        if (withLifts is null || withLifts.All(l => l.Kind != LegKind.Lift)) return withLifts;

        foreach (var leg in withLifts)
        {
            if (leg.Kind != LegKind.Lift) continue;
            var lift = state.Lifts.First(l => l.Id == leg.WayId);
            var type = LiftNetwork.FindType(state, lift.TypeId)!;
            int wait = Math.Max(0, LiftMath.ExpectedWaitMinutes(type, lift.BikeCarrierPermille, lift.Queue.Count));
            liftCost += (long)wait * state.LiftRules.LiftWaitCostCmPerMinute;
        }
        liftCost += ticket * state.LiftRules.LiftTicketCostCmPerCent;

        var climbing = network.Route(from, to, _ => false, out long climbCost, Open);
        if (climbing is null) return withLifts;
        climbCost = climbCost * (2000 - Math.Clamp(guest.Energy, 0, 1000)) / 1000;
        return climbCost < liftCost ? climbing : withLifts;
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

    private static bool IsMoving(Guest guest) =>
        guest.Activity is RiderActivity.Climbing or RiderActivity.Descending or RiderActivity.Walking or RiderActivity.OnLift;

    private static void Advance(SimContext ctx, WayNetwork network, TrailTraffic traffic, Guest guest, int budgetMs)
    {
        var rules = ctx.State.TrailRules;
        int heldUpMs = 0;
        while (budgetMs > 0 && IsMoving(guest))
        {
            var (legIndex, offset) = LocateOnRoute(guest);
            var leg = guest.Route[legIndex];
            if (guest.EntryWaitMs >= 0)
            {
                // At a trail entrance: re-check it is open, wait a moment, give way, then drop in.
                traffic.Track(guest);
                if (network.FindWay(leg.WayId) is { } closed && !network.IsRideable(closed))
                {
                    ChooseAgainAt(ctx, network, guest, closed, leg.FromCm);
                    continue;
                }
                int wait = rules.EntryWaitSeconds * 1000 - guest.EntryWaitMs;
                if (wait > 0)
                {
                    wait = Math.Min(wait, budgetMs);
                    guest.EntryWaitMs += wait;
                    budgetMs -= wait;
                    continue;
                }
                if (traffic.MustGiveWay(guest, leg, rules.RiderGapCm))
                {
                    guest.EntryWaitMs += budgetMs;
                    break;
                }
                guest.EntryWaitMs = -1;
                if (legIndex == guest.Route.Count - 1)
                {
                    guest.RunStartTick = ctx.Tick;
                    ctx.Publish(new RunStarted(ctx.Tick, guest.Id, guest.TrailId));
                }
                continue;
            }
            if (offset == leg.LengthCm)
            {
                FinishLeg(ctx, network, guest, legIndex);
                if (guest.Activity == RiderActivity.Queuing)
                    guest.QueuedAtMs = TickMilliseconds - budgetMs; // reached the station now: later carriers only
                continue;
            }

            if (leg.Kind != LegKind.Way)
            {
                // A lift stopped after a crash on its track: everybody on it waits where they are.
                if (leg.Kind == LegKind.Lift && ctx.State.Lifts.FirstOrDefault(l => l.Id == leg.WayId) is { } stopped && stopped.IsStopped(ctx.Tick))
                    break;
                int linkSpeed = LinkSpeed(ctx.State, leg);
                if (linkSpeed <= 0)
                {
                    PlaceAtBase(ctx.State, guest, network);
                    return;
                }
                long linkMove = Math.Min((long)linkSpeed * budgetMs / 1000, leg.LengthCm - offset);
                if (linkMove <= 0) break;
                int linkStartedMs = TickMilliseconds - budgetMs;
                budgetMs -= (int)Math.Min(budgetMs, Math.Max(1, (linkMove * 1000 + linkSpeed - 1) / linkSpeed));
                guest.RouteProgressCm += linkMove;
                // Pulled up a T-bar across a way: a crossing passage like any other.
                if (leg.Kind == LegKind.Lift && guest.Injury == InjurySeverity.None && ctx.State.CrashRules.Enabled)
                {
                    long from = leg.FromCm + offset;
                    foreach (var (liftCm, wayId, wayCm) in network.TowCrossingsOnLift(leg.WayId))
                    {
                        if (liftCm <= from) continue;
                        if (liftCm > from + linkMove) break;
                        int atMs = linkStartedMs + (int)((liftCm - from) * 1000 / linkSpeed);
                        if (PassCrossing(ctx, network, traffic, guest, leg.WayId, liftCm, wayId, wayCm, atMs, linkSpeed, from + linkMove - liftCm, 0))
                            return;
                    }
                }
                continue;
            }

            if (!network.TryGetGeometry(leg.WayId, out var geometry))
            {
                PlaceAtBase(ctx.State, guest, network);
                return;
            }

            int dir = leg.ToCm >= leg.FromCm ? 1 : -1;
            long position = leg.FromCm + dir * offset;
            var segment = geometry.Segments[geometry.SegmentIndexAt(dir > 0 ? position : position - 1)];
            long boundary = dir > 0 ? Math.Min(segment.EndCm, leg.ToCm) : Math.Max(segment.StartCm, leg.ToCm);
            long toBoundary = Math.Abs(boundary - position);
            int gradeAlong = segment.GradePermille * dir;
            bool isRun = legIndex == guest.Route.Count - 1;
            int speed = Speed(guest, rules, geometry.Kind, segment, gradeAlong);
            // Worn features are slower and less fun (the trail between them doesn't wear).
            int condition = 1000;
            if (geometry.Kind == WayKind.Trail)
                foreach (var placed in network.FeaturesOn(leg.WayId))
                    if (placed.Feature.Built && placed.Covers(position))
                        condition = TrailCondition.Permille(placed.Feature);
            if (condition < 1000)
                speed = Math.Max(MinSpeedCmPerS, speed * (1000 - TrailCondition.SpeedLossPermille(ctx.State.WearRules, condition)) / 1000);
            if (guest.Injury == InjurySeverity.Minor)
                speed = Math.Min(speed, ctx.State.CrashRules.MinorSpeedCmPerS); // hurt: rolling down carefully

            long reach = (long)speed * budgetMs / 1000;
            if (reach <= 0) break;
            long move = Math.Min(reach, toBoundary);
            // No overtaking on trails: stay a gap behind the rider ahead (who has already moved this minute).
            bool heldUp = false;
            if (geometry.Kind == WayKind.Trail && traffic.RoomAhead(guest, leg.WayId, position, rules.RiderGapCm) is var room && room < move)
            {
                move = Math.Max(0, room);
                heldUp = true;
            }
            int startedMs = TickMilliseconds - budgetMs; // when in this minute the move starts
            if (move > 0 || !heldUp)
                budgetMs -= (int)Math.Min(budgetMs, Math.Max(1, (move * 1000 + speed - 1) / speed));
            guest.RouteProgressCm += move;

            // Energy: climbing costs per meter gained, more on steep grades (a 100 % grade doubles it);
            // descending costs a little per distance.
            if (gradeAlong > 0)
                guest.Energy -= (int)(move * gradeAlong / 1000 * rules.ClimbEnergyPerMeterGain * (1000 + gradeAlong) / 100_000);
            else
                guest.Energy -= (int)(move * rules.DescentEnergyPer100Meters / 10_000);
            guest.Energy = Math.Max(0, guest.Energy);

            var crashRules = ctx.State.CrashRules;
            bool rolls = isRun && crashRules.Enabled && guest.Injury == InjurySeverity.None && geometry.Kind == WayKind.Trail;
            int wetness = ctx.State.Weather.WetnessPermille;
            if (isRun && guest.Injury == InjurySeverity.None)
            {
                // Features whose start the rider passed in this move.
                foreach (var feature in network.FeaturesOn(leg.WayId))
                {
                    if (feature.StartCm <= position) continue;
                    if (feature.StartCm > position + move) break;
                    if (!feature.Feature.Built) continue; // planned: nothing there to ride yet
                    int featureFun = FeatureFun(guest, feature.Type);
                    featureFun = Math.Max(0, featureFun - TrailCondition.FunLoss(ctx.State.WearRules, TrailCondition.Permille(feature.Feature)));
                    if (Reputation.ReviewMath.IsJump(feature.Type.Kind))
                    {
                        guest.JumpFunSum += featureFun;
                        guest.JumpCount++;
                    }
                    guest.RunFun += (long)featureFun * feature.Type.FunWeight;
                    guest.RunSegments += feature.Type.FunWeight;
                    TrailCondition.Wear(feature.Feature, TrailCondition.PassWear(ctx.State, feature.Type));
                    if (rolls && CrashMath.Roll(ctx.Rng, CrashMath.FeatureChancePpb(crashRules, ctx.State.WearRules, guest, feature.Type,
                            TrailCondition.Permille(feature.Feature), wetness)))
                    {
                        guest.RouteProgressCm -= position + move - feature.StartCm; // down where the feature starts
                        Crash(ctx, network, traffic, guest, leg.WayId, feature.StartCm, CrashCause.Feature, feature, feature.Type.Difficulty);
                        return;
                    }
                }
                if (move == toBoundary)
                {
                    guest.RunFun += SegmentFun(guest, rules, segment, gradeAlong, speed);
                    guest.RunSegments++;
                    if (rolls && CrashMath.Roll(ctx.Rng, CrashMath.TerrainChancePpb(crashRules, guest, segment, wetness)))
                    {
                        Crash(ctx, network, traffic, guest, leg.WayId, position + move, CrashCause.Terrain, null, segment.TerrainDifficulty);
                        return;
                    }
                }
            }

            // Crossings passed in this move (of another way or a T-bar track), in riding order: riders meeting there collide.
            if (move > 0 && guest.Injury == InjurySeverity.None && crashRules.Enabled)
                foreach (var (cm, otherId, otherCm) in CrossingsPassed(network, leg.WayId, position, dir * move))
                {
                    long into = Math.Abs(cm - position);
                    int atMs = startedMs + (int)(into * 1000 / speed);
                    if (PassCrossing(ctx, network, traffic, guest, leg.WayId, cm, otherId, otherCm, atMs, speed, move - into, segment.Difficulty))
                        return;
                }

            if (heldUp)
            {
                // Stuck behind a slower rider for the rest of the minute.
                heldUpMs += budgetMs;
                guest.HeldUpSeconds += budgetMs / 1000;
                if (network.FindWay(leg.WayId) is { } held) held.Stats.HeldUpSeconds += budgetMs / 1000;
                budgetMs = 0;
            }
        }

        if (heldUpMs > 0)
            guest.Happiness = Math.Max(0, guest.Happiness - (int)((long)rules.HeldUpMoodPerMinute * heldUpMs / TickMilliseconds));
    }

    /// <summary>
    /// Crossings of a way (with other ways and T-bar tracks) strictly after <paramref name="position"/> and up to
    /// <paramref name="delta"/> further along it (negative: riding it backwards), in the order they are reached:
    /// (distance along the way, the other way or lift, distance along that).
    /// </summary>
    private static List<(long Cm, int OtherId, long OtherCm)> CrossingsPassed(WayNetwork network, int wayId, long position, long delta)
    {
        var passed = new List<(long Cm, int OtherId, long OtherCm)>();
        long lo = Math.Min(position, position + delta), hi = Math.Max(position, position + delta);
        bool Passed(long cm) => delta > 0 ? cm > lo && cm <= hi : cm >= lo && cm < hi;
        foreach (var c in network.CrossingsOn(wayId))
            if (Passed(c.Cm)) passed.Add(c);
        foreach (var c in network.TowCrossingsOn(wayId))
            if (Passed(c.Cm)) passed.Add(c);
        if (passed.Count > 1)
            passed.Sort((a, b) => Math.Abs(a.Cm - position).CompareTo(Math.Abs(b.Cm - position)) is var c && c != 0 ? c : a.OtherId.CompareTo(b.OtherId));
        return passed;
    }

    /// <summary>
    /// A rider passes a crossing point (on <paramref name="ownId"/>, a way or a towed lift, at <paramref name="ownCm"/>)
    /// <paramref name="atMs"/> into this minute: a rider of the other side who passed it at about the same moment
    /// (<see cref="CrashMath.Collide"/>, this minute or the one before) collides with them. Both crash where the crossing
    /// is: the mover goes back <paramref name="overshootCm"/>, the other rider back to the crossing too if they are still
    /// on that way or lift (otherwise only the mover crashes). True if the mover crashed.
    /// </summary>
    private static bool PassCrossing(SimContext ctx, WayNetwork network, TrailTraffic traffic, Guest guest, int ownId, long ownCm,
        int otherId, long otherCm, int atMs, int speed, long overshootCm, int difficulty)
    {
        var state = ctx.State;
        var passages = state.Safety.Passages;
        var mine = new CrossingPassage(ctx.Tick * TickMilliseconds + atMs, guest.Id, ownId, ownCm, speed);
        CrossingPassage? hit = null;
        foreach (var p in passages)
            if (p.WayId == otherId && p.Cm == otherCm && p.GuestId != guest.Id && CrashMath.Collide(state.CrashRules, mine, p))
            {
                hit = p;
                break;
            }
        passages.Add(mine);
        if (hit is null) return false;

        // The trail (or path) the collision happened on, for stats and messages; a towed rider's is the crossing way.
        bool ownIsLift = state.Lifts.Any(l => l.Id == ownId);
        int wayId = ownIsLift ? otherId : ownId;
        long wayCm = ownIsLift ? otherCm : ownCm;
        guest.RouteProgressCm -= Math.Max(0, overshootCm);
        Crash(ctx, network, traffic, guest, wayId, wayCm, CrashCause.Collision, null, difficulty);

        var other = state.Guests.FirstOrDefault(g => g.Id == hit.GuestId);
        if (other is null || other.Injury != InjurySeverity.None || other.Route.Count == 0) return true;
        var (legIndex, offset) = LocateOnRoute(other);
        var leg = other.Route[legIndex];
        if (other.Activity == RiderActivity.OnLift && leg.Kind == LegKind.Lift && leg.WayId == otherId)
        {
            other.RouteProgressCm -= Math.Max(0, leg.FromCm + offset - otherCm); // pulled on past it: back to where they were hit
            Crash(ctx, network, traffic, other, wayId, wayCm, CrashCause.Collision, null, difficulty);
        }
        else if (PositionOnWay(other, out int way, out long at) && way == otherId)
        {
            // Back to the crossing, but still ahead of whoever rode up behind them since (nobody overtakes on a trail).
            long dir = Math.Sign(at - otherCm), back = otherCm;
            foreach (var behind in state.Guests)
                if (behind != other && PositionOnWay(behind, out int w, out long p) && w == otherId && (p - back) * dir >= 0 && (at - p) * dir > 0)
                    back = p + dir;
            other.RouteProgressCm -= Math.Abs(at - back);
            Crash(ctx, network, traffic, other, otherId, back, CrashCause.Collision, null, difficulty);
        }
        // else: they have already gone on from there
        return true;
    }

    /// <summary>
    /// A crash: mood drops, the crash is counted (trail, feature, park) and announced. A minor crash leaves the rider
    /// riding down slowly (then they go home); a serious one leaves them lying on the trail, blocking it, until the rescue
    /// helicopter has flown them out. Riders close behind see it happen.
    /// </summary>
    internal static void Crash(SimContext ctx, WayNetwork network, TrailTraffic traffic, Guest guest, int wayId, long cm, CrashCause cause,
        PlacedFeature? feature, int difficulty)
    {
        var state = ctx.State;
        var rules = state.CrashRules;
        bool jump = feature is { } f && ReviewMath.IsJump(f.Type.Kind);
        bool serious = ctx.Rng.ChancePermille(CrashMath.SeriousPermille(rules, cause, jump, difficulty, guest.Skill));
        guest.Injury = serious ? InjurySeverity.Serious : InjurySeverity.Minor;
        guest.CrashTick = ctx.Tick;
        guest.CrashWayId = wayId;
        guest.CrashCause = cause;
        guest.CrashFeatureId = feature?.Feature.Id ?? 0;
        guest.Happiness = Math.Max(0, guest.Happiness - (serious ? rules.SeriousMoodLoss : rules.MinorMoodLoss));

        if (network.FindWay(wayId) is { } way)
        {
            way.Stats.Crashes++;
            way.Stats.CrashesToday++;
            if (serious) way.Stats.SeriousCrashes++;
            if (cause == CrashCause.Collision) way.Stats.Collisions++;
        }
        if (feature is { } placed) placed.Feature.Crashes++;
        var safety = state.Safety;
        safety.TotalCrashes++;
        if (cause == CrashCause.Collision) safety.TotalCollisions++;
        if (serious)
        {
            safety.TotalSerious++;
            safety.SeriousToday++;
        }
        else
            safety.MinorToday++;
        // Hurt while being pulled up a T-bar: the lift stops until the crash is resolved.
        var towedOn = guest.Activity == RiderActivity.OnLift && guest.Route.Count > guest.LegIndex
                      && guest.Route[guest.LegIndex] is { Kind: LegKind.Lift } liftLeg
            ? state.Lifts.FirstOrDefault(l => l.Id == liftLeg.WayId)
            : null;
        traffic.Witness(guest, wayId, cm);
        ctx.Publish(new RiderCrashed(ctx.Tick, guest.Id, wayId, cm, guest.Injury, cause, guest.CrashFeatureId, towedOn?.Id ?? 0));


        if (serious)
        {
            guest.Activity = RiderActivity.Injured;
            guest.RescueAtTick = ctx.Tick + ctx.Rng.Range(rules.HelicopterMinMinutes, rules.HelicopterMaxMinutes + 1);
            traffic.Track(guest);
            ctx.Publish(new HelicopterCalled(ctx.Tick, guest.Id, wayId, guest.RescueAtTick));
        }
        if (towedOn is null)
        {
            CloseAfterCrash(ctx, network, wayId, serious ? guest.RescueAtTick : ctx.Tick + rules.MinorCloseMinutes);
            return;
        }
        // Serious: until the helicopter has flown them out. Minor: they let go, get off the track and go home.
        long until = serious ? guest.RescueAtTick : ctx.Tick + rules.TowStopMinutes;
        if (!serious) PlaceAtBase(state, guest, network);
        if (until > towedOn.StoppedUntilTick)
        {
            towedOn.StoppedUntilTick = until;
            ctx.Publish(new LiftStopped(ctx.Tick, towedOn.Id, until, guest.Id));
        }
    }

    /// <summary>A crash closes its trail to new riders until <paramref name="until"/> (riders on it finish their run).</summary>
    private static void CloseAfterCrash(SimContext ctx, WayNetwork network, int wayId, long until)
    {
        if (network.FindWay(wayId) is not { Kind: WayKind.Trail } trail || until <= ctx.Tick || until <= trail.CrashClosedUntilTick) return;
        bool wasRideable = trail.IsRideable;
        trail.CrashClosedUntilTick = until;
        if (wasRideable) ctx.Publish(new TrailClosed(ctx.Tick, trail.Id, TrailClosedReason.Crash));
    }

    /// <summary>Where a rider riding a way (dropped in, on a trail or path) is on it.</summary>
    internal static bool PositionOnWay(Guest guest, out int wayId, out long position)
    {
        wayId = 0;
        position = 0;
        if (guest.Route.Count == 0 || guest.EntryWaitMs >= 0
            || guest.Activity is not (RiderActivity.Descending or RiderActivity.Climbing or RiderActivity.Injured))
            return false;
        var (legIndex, offset) = LocateOnRoute(guest);
        var leg = guest.Route[legIndex];
        if (leg.Kind != LegKind.Way) return false;
        wayId = leg.WayId;
        position = leg.ToCm >= leg.FromCm ? leg.FromCm + offset : leg.FromCm - offset;
        return true;
    }

    /// <summary>Speed along a lift or walk leg in cm/s (0 if the lift no longer exists).</summary>
    private static int LinkSpeed(WorldState state, RouteLeg leg)
    {
        if (leg.Kind == LegKind.Walk) return state.LiftRules.WalkSpeedCmPerS;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == leg.WayId);
        return lift is null ? 0 : LiftNetwork.FindType(state, lift.TypeId)?.SpeedCmPerS ?? 0;
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
        var leg = guest.Route[legIndex];
        if (leg.Kind == LegKind.Lift)
            ctx.Publish(new RiderUnloaded(ctx.Tick, guest.Id, leg.WayId));
        if (legIndex + 1 < guest.Route.Count)
        {
            // Every trail entrance is checked again: riders whose next leg is a trail that closed since they planned
            // the lap (their run or a trail on the way to it) don't enter it; they choose again from where they stand.
            var next = guest.Route[legIndex + 1];
            if (next.Kind == LegKind.Way && network.FindWay(next.WayId) is { Kind: WayKind.Trail } closed && !network.IsRideable(closed))
            {
                ChooseAgainAt(ctx, network, guest, closed, next.FromCm);
                return;
            }
            EnterLeg(ctx, network, guest, legIndex + 1);
        }
        else
            FinishRun(ctx, network, guest);
    }

    /// <summary>A rider at the entrance of a closed trail picks another lap from there (back to the base if there is none).</summary>
    private static void ChooseAgainAt(SimContext ctx, WayNetwork network, Guest guest, Way closed, long entranceCm)
    {
        guest.LocationHubId = 0;
        guest.LocationWayId = closed.Id;
        guest.LocationCm = entranceCm;
        ClearLap(ctx.State, guest);
        if (!StartLap(ctx, network, guest))
            PlaceAtBase(ctx.State, guest, network);
    }

    private static void EnterLeg(SimContext ctx, WayNetwork network, Guest guest, int legIndex)
    {
        var leg = guest.Route[legIndex];
        guest.LegIndex = legIndex;
        switch (leg.Kind)
        {
            case LegKind.Walk:
                guest.Activity = RiderActivity.Walking;
                break;
            case LegKind.Lift:
                var lift = ctx.State.Lifts.FirstOrDefault(l => l.Id == leg.WayId);
                if (lift is null)
                {
                    PlaceAtBase(ctx.State, guest, network);
                    return;
                }
                if (!guest.HasLiftPass && !BuyLiftPass(ctx, guest))
                {
                    // Can't afford the day pass any more: back to the base, where lifts are out of the route choice.
                    PlaceAtBase(ctx.State, guest, network);
                    return;
                }
                LiftSystem.JoinQueue(ctx, lift, guest);
                break;
            default:
                bool onTrail = network.FindWay(leg.WayId)?.Kind == WayKind.Trail;
                guest.Activity = onTrail ? RiderActivity.Descending : RiderActivity.Climbing;
                // Every trail starts with a short wait at its entrance (see Advance); the run starts on dropping in.
                guest.EntryWaitMs = onTrail ? 0 : -1;
                break;
        }
    }

    /// <summary>Buys the day pass on the first lift of a visit. False if the guest can't pay for it.</summary>
    private static bool BuyLiftPass(SimContext ctx, Guest guest)
    {
        long ticket = ctx.State.Park.LiftTicketCents;
        if (guest.CashCents < ticket) return false;
        guest.CashCents -= ticket;
        guest.PaidEntryCents += ticket;
        guest.HasLiftPass = true;
        if (ticket > 0)
        {
            ctx.State.Finance.Earn(ticket);
            ctx.State.Finance.TotalLiftTicketsCents += ticket;
            ctx.State.Finance.LiftTicketsTodayCents += ticket;
        }
        return true;
    }

    private static void FinishRun(SimContext ctx, WayNetwork network, Guest guest)
    {
        int trailId = guest.TrailId;
        var trail = network.FindWay(trailId);
        int minutes = (int)(ctx.Tick - guest.RunStartTick + 1);
        int fun = guest.RunSegments == 0 ? 0 : (int)(guest.RunFun / guest.RunSegments);

        guest.Happiness = Math.Clamp(guest.Happiness + Math.Clamp((fun - 450) / 4, -80, 100), 0, 1000);
        guest.RunsCompleted++;
        guest.VisitFunSum += fun;
        guest.LastTrailId = trailId;
        if (trail is not null)
        {
            int difficulty = network.Geometry(trailId).DifficultyScore;
            guest.HardestDifficulty = Math.Max(guest.HardestDifficulty, difficulty);
            if (difficulty > guest.Skill + ctx.State.ReputationRules.ScaredMargin)
                guest.ScaredRuns++;
            if (!guest.TrailsRidden.Contains(trailId))
                guest.TrailsRidden.Add(trailId);
            trail.Stats.Runs++;
            trail.Stats.RunsToday++;
            trail.Stats.SumRunMinutes += minutes;
            trail.Stats.SumFun += fun;
            guest.LocationWayId = trailId;
            guest.LocationHubId = 0;
            guest.LocationCm = network.Geometry(trailId).LengthCm;
        }
        ClearLap(ctx.State, guest);
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

    /// <summary>How much a rider enjoyed riding a trail feature, 0..1000: skill match plus the type's appeal for the style.</summary>
    internal static int FeatureFun(Guest guest, TrailFeatureType type)
    {
        int target = guest.Skill * 85 / 100;
        int match = Math.Clamp(1000 - Math.Abs(type.Difficulty - target) * 2, 0, 1000);
        int style = guest.Style switch
        {
            RiderStyle.Flow => type.FlowAffinity,
            RiderStyle.Technical => type.TechAffinity,
            _ => Math.Clamp(1000 - type.Difficulty, 0, 1000),
        };
        int fun = (match * 50 + style * 50) / 100;
        if (type.Difficulty > guest.Skill + 200) fun /= 2; // scared
        return fun;
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

/// <summary>
/// Who is on which trail during one minute of <see cref="RiderSystem"/> (derived, rebuilt every minute). Riders on a
/// trail keep <see cref="TrailRules.RiderGapCm"/> to the rider ahead, so nobody overtakes on trails; at an entrance a
/// rider gives way to riders close by on the trail and to faster riders waiting there too.
/// </summary>
internal sealed class TrailTraffic
{
    /// <summary>A rider who has waited this long at an entrance doesn't give way to faster riders any more.</summary>
    private const int MaxGiveWayMs = 60_000;

    private readonly WayNetwork _network;
    private readonly Dictionary<int, List<Guest>> _byTrail = [];

    public TrailTraffic(WayNetwork network, List<Guest> movers, List<Guest>? injured = null)
    {
        _network = network;
        foreach (var guest in movers)
            Track(guest);
        foreach (var guest in injured ?? [])
            Track(guest); // lying on the trail: nobody gets past
    }

    /// <summary>Riders on the trail close behind a crash (within a few gaps) saw it happen.</summary>
    public void Witness(Guest crashed, int trailId, long position)
    {
        if (!_byTrail.TryGetValue(trailId, out var list)) return;
        foreach (var other in list)
            if (other != crashed && OnTrail(other, out long at) && Leg(other).WayId == trailId && at <= position && position - at <= WitnessRangeCm)
                other.CrashesSeen++;
    }

    private const long WitnessRangeCm = 5_000;

    /// <summary>
    /// Riders on a trail (dropped in) first, by trail and front to back; then riders waiting at a trail entrance, fastest
    /// first (so a faster rider drops in first and the slower ones can follow in the same minute); then everybody else in
    /// list order, riders on a lift last (they reach the top after the trail riders of this minute have moved).
    /// </summary>
    public IEnumerable<Guest> MovingOrder(List<Guest> movers) =>
        movers.Select((g, i) => (Guest: g, Index: i, Trail: OnTrail(g, out long position) ? Leg(g).WayId : 0, Position: position))
            .OrderBy(x => x.Trail != 0 ? 0 : x.Guest.EntryWaitMs >= 0 ? 1 : x.Guest.Activity == RiderActivity.OnLift ? 3 : 2)
            .ThenBy(x => x.Trail)
            .ThenByDescending(x => x.Position)
            .ThenByDescending(x => x.Trail == 0 && x.Guest.EntryWaitMs >= 0 ? x.Guest.Skill : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Guest)
            .ToList();

    /// <summary>Registers a rider whose current leg is a trail (call when they get there).</summary>
    public void Track(Guest guest)
    {
        if (guest.Route.Count == 0 || Leg(guest) is not { Kind: LegKind.Way } leg || _network.FindWay(leg.WayId)?.Kind != WayKind.Trail)
            return;
        if (!_byTrail.TryGetValue(leg.WayId, out var list))
            _byTrail[leg.WayId] = list = [];
        if (!list.Contains(guest))
            list.Add(guest);
    }

    /// <summary>How far the rider may move from <paramref name="position"/> on the trail before closing up on the rider ahead.</summary>
    public long RoomAhead(Guest guest, int trailId, long position, int gapCm)
    {
        if (!_byTrail.TryGetValue(trailId, out var list)) return long.MaxValue;
        long room = long.MaxValue;
        foreach (var other in list)
        {
            if (other == guest || !OnTrail(other, out long at) || Leg(other).WayId != trailId) continue;
            bool ahead = at > position || at == position && other.Id < guest.Id;
            if (ahead) room = Math.Min(room, at - gapCm - position);
        }
        return room;
    }

    /// <summary>
    /// True if a rider at the entrance of <paramref name="leg"/> has to wait: a rider on the trail is within the gap of
    /// the entrance (just ahead, or coming down from behind at a junction), or a faster rider is waiting there too.
    /// </summary>
    public bool MustGiveWay(Guest guest, RouteLeg leg, int gapCm)
    {
        if (!_byTrail.TryGetValue(leg.WayId, out var list)) return false;
        foreach (var other in list)
        {
            if (other == guest || other.Route.Count == 0 || Leg(other) is not { Kind: LegKind.Way } otherLeg || otherLeg.WayId != leg.WayId) continue;
            if (OnTrail(other, out long at))
            {
                if (Math.Abs(at - leg.FromCm) < gapCm) return true;
            }
            else if (other.EntryWaitMs >= 0 && otherLeg.FromCm == leg.FromCm && other.Skill > guest.Skill
                     && guest.EntryWaitMs < MaxGiveWayMs && other.Activity == RiderActivity.Descending)
                return true;
        }
        return false;
    }

    private static RouteLeg Leg(Guest guest) => guest.Route[Math.Clamp(guest.LegIndex, 0, guest.Route.Count - 1)];

    /// <summary>True if the rider has dropped into the trail of their current leg; <paramref name="position"/> is where on it.</summary>
    private bool OnTrail(Guest guest, out long position)
    {
        position = 0;
        if (guest.EntryWaitMs >= 0 || guest.Activity is not (RiderActivity.Descending or RiderActivity.Injured) || guest.Route.Count == 0) return false;
        var leg = Leg(guest);
        if (leg.Kind != LegKind.Way || _network.FindWay(leg.WayId)?.Kind != WayKind.Trail) return false;
        long before = 0;
        for (int i = 0; i < guest.LegIndex; i++)
            before += guest.Route[i].LengthCm;
        position = leg.FromCm + Math.Clamp(guest.RouteProgressCm - before, 0, leg.LengthCm);
        return true;
    }
}
