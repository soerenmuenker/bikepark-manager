# Bikepark Manager

RollerCoaster-Tycoon-style bikepark management game. Godot 4.7 .NET (C#) client on top of a pure,
deterministic C# simulation library. Full rationale: [docs/design/architecture.md](docs/design/architecture.md).

## Layout

- `src/Bikepark.Sim/` – simulation library (WorldState, Simulation tick loop, SimRandom, commands, events, save/load, scenarios, KPIs)
  - `Terrain/` – `TerrainGenerator` (integer-only heightmap + layers), `TerrainGrid` (queries), `TerrainScatter` (trees/rocks),
    `TerrainEdit`/`TerrainEditor` (stored flattened pads applied on top of the generated grid)
  - `Trails/` – `Way` (player-built access paths and trails), `WayGeometry` (integer spline, segments, rating),
    `WayPlanner` (validation, shared by command and preview), `WayNetwork` (derived graph, routing, corridors,
    `IsConnected`/`IsRideable`), `WayEditing` (split, renaturalize, join onto loose ends, split at gravel paths),
    `TrailFeature` (feature catalog types + placed features), `FeaturePlanner` (feature placement validation)
  - `Land/` – `Parcel` (content: named land outlines with price and required level), `LandMath` (who owns which point,
    the only land check, used by every planner); `Commands/LandCommands.cs` (`BuyParcelCommand`)
  - `Lifts/` – `LiftType`/`LiftOperator`/`LiftRules` (content), `Lift`/`ParkingLot` (state), `LiftMath` (bike carriers,
    throughput), `StructurePlanner` (validation of lifts, parking, pads), `LiftNetwork` (hubs + links for the network)
  - `Systems/RiderSystem.cs` – riders choose a trail and the cheaper way up (walk + lift queue, or pedal the paths), ride down, score fun (segments + features);
    no overtaking on trails (`TrailTraffic`: gap to the rider ahead, held-up riders lose mood; a short wait at each
    trail entrance where faster riders go first)
  - `Systems/LiftSystem.cs` – booked bike access tiers at opening, carrier dispatch, boarding bike cabins from the FIFO queue
  - `Crew/` – `CrewRules`/`ToolType` (content), `CrewMember`/`Job` (state), `WorkCosts` (all work/wood/speed numbers),
    `Forest` (which scatter trees are gone or claimed), `ClearingPlanner` (felling areas), `Jobs` (queue/cancel/complete)
  - `Systems/JobSystem.cs` – the crew at work: assigns workers to jobs in priority order, fells trees (wood), builds planned ways and features;
    after the shift only overtime that finishes a job
  - `Weather/` – `WeatherRules`/`WeatherState`/`DayWeather` (daily seeded forecast, ground wetness), `Systems/WeatherSystem.cs`
  - `Trails/TrailCondition.cs` – `WearRules` and feature condition (wear per rider pass, more when wet; worn
    features slower/less fun); `Systems/TrailCareSystem.cs` raises the repair warning and closes worn-out trails
  - `Reputation/` – `ReputationRules` (content: skill groups and what they value, demand curves, influencers, XP and
    level thresholds), `ReputationState` (reviews window, posts, visitor history), `ReviewMath` (a visit → aspects →
    stars), `ReputationMath` (rating, demand), `ParkProgress` (derived XP and level); `Systems/ReputationSystem.cs`
    books influencers and announces level changes
  - `Safety/` – `CrashRules` (content: crash chances, severity, helicopter, insurance), `CrashMath` (pure chances in
    parts per billion, premium), `SafetyState` (counts, accident history); crash rolls live in `RiderSystem` (`Crash`),
    `Systems/SafetySystem.cs` books the daily insurance; `Trails/Crossings.cs` finds where ways cross (derived in
    `WayNetwork.Crossings`, warned about in `WayPlanner`)
  - `Systems/ParkSchedule.cs` – the daily timetable (open, last rides, lift warm-up, crew shift/overtime, day phase,
    quiet nights and the next wake-up); arrivals follow `ParkRules.ArrivalProfile`, guests take one planned lunch break
- `src/Bikepark.SimRunner/` – headless console runner: KPIs as JSON, `terrain` subcommand renders top-down PNG maps
- `tests/Bikepark.Sim.Tests/` – xUnit tests (determinism, commands, persistence, RNG, terrain)
- `game/` – Godot project (`Bikepark.csproj`, `scripts/SimHost.cs` drives the sim, `scripts/ui/` HUD (bottom bar + menus incl. `ReputationPanel`, drawn icons, theme in code),
  `scripts/terrain/` chunked terrain view, `scripts/camera/RtsCamera.cs`, `scripts/ways/` way view + build tool + feature tool/meshes,
  `scripts/riders/RiderView.cs`, `scripts/lifts/` lift/parking view + debug structure tool, `scripts/crew/` crew figures +
  felling tool, `scripts/world/` time-of-day + weather light (`DayLight`) and rain (`RainView`), `shaders/`)
- `data/` – JSON content (`scenarios/`: `demo_valley.json` = the sandbox "Demo" (no parcels: all land owned, gondola
  rented; tests and demo scripts use it) and `starter_valley.json` = the career (old ski hill with a derelict T-bar, parcels
  to buy, company gondola locked); `lift_types.json` (T-bar, chairlift, gondola with costs, levels, build days),
  `trail_features.json`, `tools.json`, `scripts/` command scripts such as `demo_lift_network.json`, `demo_features.json`,
  `demo_crew.json`, `career_opening.json`)
- `Bikepark.sln` – root solution; Godot uses it via `project/solution_directory="../"`

## Commands

`dotnet` lives at `/usr/local/share/dotnet` and may not be on PATH: `export PATH=/usr/local/share/dotnet:$PATH`.

```bash
dotnet build Bikepark.sln
dotnet test Bikepark.sln
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 30 [--seed N] [--commands file.json]... [--daily] [--hourly] [--auto-repair] [--save out.json]
# --hourly: the last day per hour (phase, guests, on trails, queuing, eating, runs, crew working, lift minutes, rain, wetness, closed trails)
# Wear: 14 days with and without a stand-in player that repairs on every warning (--auto-repair): per-trail status, feature conditions, closed minutes, repairs
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 10 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json --daily [--auto-repair]
# Terrain: top-down PNG maps + stats (use this to check terrain changes, no Godot needed)
# (applies the scenario's own commands too: pads, lift line, parking and the hiking route are drawn)
dotnet run --project src/Bikepark.SimRunner -- terrain --scenario data/scenarios/demo_valley.json --out out/map.png --mode all [--seed N] [--scatter] [--commands data/scripts/demo_lift_network.json]
# Reputation: the output's "reputation" block (stars overall/per group, demand parts, XP parts, level, influencer posts,
# a log of influencer visits and level changes); --daily adds reviews, rating, demand, XP and level per day
# Crashes: the output's "safety" block (crashes, serious, collisions, evacuations, average helicopter wait, insurance,
# crossings found, a crash log) and per trail crashes / serious / collisions / per 1000 runs (crashes per feature in
# featureConditions); --daily adds crashes, serious crashes and insurance per day
# Career: the first weeks (restore the T-bar, two trails on the ski hill, buy the foot forest at level 2); the "land"
# block lists parcels (owned / level / price), money spent on land and lifts, upkeep and a log; lifts show their status
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 20 --commands data/scripts/career_opening.json --daily
# Riders on the demo trails: per-trail runs, per-lift riders/queue/wait/tier/fee, why guests left or were turned away
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 3 --commands data/scripts/demo_lift_network.json
# ... with the demo trail features (planned, built by the crew: per-trail feature list, crew block with completed jobs, wood, wages)
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 10 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json
# Godot (from game/): compile, then run headless with the demo trails at 60x, printing KPIs (incl. queues) every game hour
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --build-solutions --quit
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --fixed-fps 60 --quit-after 2400 -- --demo --speed=4 --report
```

## Architecture rules (must follow)

1. **`Bikepark.Sim` never references Godot** (no `using Godot`, no GodotSharp package, no engine types).
   Dependencies only point towards the sim. Gameplay logic goes in the sim, not in `game/`.
2. **`WorldState` is the single source of truth.** Everything that affects simulation lives there and is
   JSON-serializable; anything not in it is lost on save and must not influence the sim.
3. **Fixed-step time:** 1 tick = 1 game minute (1440/day). `Simulation.Step()` = apply due commands →
   run systems in fixed order → `Tick++`. Real time → ticks happens only in `SimHost`.
4. **All external changes go through `ICommand`s** enqueued on `CommandQueue` and applied at tick start.
   The view never writes `WorldState`. New commands: immutable `record`, implement `Validate`/`Apply`,
   register `[JsonDerivedType(..., "camelCaseName")]` on `ICommand`. Never rename a discriminator.
5. **Events are one-way, sim → view**, buffered in `EventBus` until the host calls `Dispatch()`.
   Events are presentation-only and not saved; handlers must not mutate state (enqueue a command instead).
6. **Determinism** (same seed + commands ⇒ identical state hash):
   - randomness only via `ctx.Rng` (`SimRandom`, PCG32); never `System.Random`, `Random.Shared`, `Guid.NewGuid()`
   - no `DateTime.Now`/`Stopwatch`/wall-clock time in the sim
   - no floats in sim logic or state: money in `long` cents, ratios in integer permille
   - iterate only ordered collections (`List<T>`); never depend on `Dictionary`/`HashSet` order
   - systems are stateless and run in the order listed in `Simulation`; append new ones deliberately
   - entity ids via `WorldState.AllocateEntityId()`
7. **Saves:** `SaveGame` writes `{version, world}`. Changing the shape of `WorldState` incompatibly ⇒ bump
   `SaveGame.CurrentVersion` and add a migration.
8. **Content** lives in `/data` as JSON, is validated on load, and is copied into `WorldState` so saves are
   self-contained. Unknown JSON fields are errors.
9. **Derived data is never saved.** The terrain is regenerated from `WorldState.Terrain` (settings + seed) via
   `Simulation.Terrain` (lazy, cached in `TerrainCache`). Anything derived must be a pure function of `WorldState`;
   future terrain edits must be stored as changes in `WorldState`, not by mutating the grid.
10. **Terrain generation is integer-only** (Q16 fixed point in `FixedMath`/`IntNoise`, heights in cm, slopes as
    grade permille). Changing the generator changes every map: check the 2D maps before and after.
11. **Ways store only player input** (oriented, snapped control points + joins). Geometry, segments, ratings, the
    routing graph and corridors are derived in `WayNetwork` (rebuilt on `WaysRevision`). All way validation lives
    in `WayPlanner` so the build command and the in-game preview can never disagree. Riders are simulated, not
    physically simulated: they move along route legs by distance (cm) per tick; the view interpolates.
12. **Structures** (lifts, parking lots) store only input: pads go into `WorldState.TerrainEdits` (bump
    `TerrainRevision`), the terrain/network are re-derived. Stations and parking lots are network hubs; all
    structure validation lives in `StructurePlanner`. Bike access: `LiftMath.IsBikeCarrier` decides which carriers take
    bikes (sim and view); tier changes apply at the next opening; fees are charged by `FinanceSystem`.
13. **Trail features** store only `{typeId, distanceCm}` on `Way.Features`; their effect (segment difficulty, rating
    floor) is derived in `WayGeometry`/`WayNetwork`. Rating = hardest of overall steepness (median gradient), steep
    sections (90th-percentile terrain) and features (blue: berms/rollers/tables, red: kickers/wall-rides/small drops,
    black: doubles). All placement validation lives in `FeaturePlanner`.
14. **Crew jobs build things.** Player ways and features are planned (`Built = false`) and built by a `Job` (unless the
    debug `instant` flag is set; scenario ways are always built). Only built ways are routed/ridden and only built
    features count. All work, wood and speed numbers come from `WorkCosts`; which trees stand comes only from `Forest`
    (scatter trees are identified by position; felled ones are stored in `FelledTrees`).
15. **Daily rhythm:** `ParkSchedule` is the only timetable (systems and views ask it). Skips (night skip, skip to opening)
    only step more ticks per frame, never jump `Tick`. New daily-rhythm rules default to off. Riders on a lap finish it
    in the last rides; overtime only finishes jobs (no new assignments).
16. **Wear and weather:** weather is rolled once a day by `WeatherSystem` (no odds ⇒ always sunny, no RNG). Trail
    only trail features wear: condition lives on `TrailFeature.Condition` (never in `WaySegment`, never bumps
    `WaysRevision`). Closures are filters (`Way.IsRideable`: closed, worn out at 0 %, or under repair), not graph
    changes. `TrailCareSystem` warns (< 20 %) and closes, it never queues work: repairs are manual
    (`RepairFeatureCommand`, from the warning pop-up); only a finished repair reopens a worn-out trail. Feature work closes
    its trail when workers are assigned and only progresses once no rider is on it; riders re-check every trail entrance.
17. **Reputation** (`ReputationRules.Enabled`, off by default; Starter Valley turns it on): reviews are written only when a
    guest leaves (`GuestSystem` → `ReputationSystem.Review`) from per-visit accumulators on `Guest`; the rating is the
    average of the last `WindowSize` reviews per group (shown from `MinReviewsForRating` all-time reviews). Demand =
    visibility (all-time reviews) × rating factor × influencer post, applied in `GuestArrivalSystem`. An influencer is a
    real guest (`IsInfluencer`, booked at opening), their review becomes the post. XP and level are derived
    (`ParkProgress`, never saved; `Reputation.Level` only remembers the last announced level). `ReputationSystem` runs
    before `FinanceSystem` (it reads today's visitors before the books close).
18. **Crashes** (`CrashRules.Enabled`, off by default; Starter Valley turns it on): rolled only in `RiderSystem`, only
    on a rider's run leg and only while uninjured: per built feature passed, per finished 10 m segment (terrain) and per
    crossing passed while someone on the other way is within the window (a collision downs both). Chances are pure
    (`CrashMath`: difficulty vs skill, feature condition, wetness, fatigue). A minor crash: the rider rides down at
    `MinorSpeedCmPerS` and leaves (`GuestLeaveReason.Injured`). A serious one: `RiderActivity.Injured`, the rider stays
    where they fell and `TrailTraffic` tracks them, so nobody passes, until `RescueAtTick` (public-service helicopter,
    no staff), then `Evacuated` (even after closing). Crossings are derived geometry, never junctions. Crashes count on
    `WayStats`, `TrailFeature.Crashes` and `SafetyState`; reviews: a serious crash makes safety 0. Insurance =
    base + per accident of the last `InsuranceDays`, charged by `SafetySystem` (before `FinanceSystem`).
19a. **Entrance fee and lift pass:** `Park.EntryFeeCents` is paid on arrival (drives demand); `Park.LiftTicketCents` (0 = free
    lifts, the Demo) is a day pass bought once per visit when a guest first boards a lift (`RiderSystem.BuyLiftPass`,
    `Guest.HasLiftPass`, revenue in `Finance.TotalLiftTicketsCents`). `BestRoute` adds the unbought pass to the lift cost
    (`LiftRules.LiftTicketCostCmPerCent`) and rules lifts out for guests who can't pay it. `SetLiftTicketCommand`.
19. **Land and owned lifts:** parcels are content; `OwnedParcelIds` is state (no parcels = all land owned). Ownership is
    checked only through `LandMath`, inside the planners (`WayPlanner` per geometry sample via its `onOwnLand` predicate,
    `StructurePlanner.CheckPad` for station and parking pads, `ClearingPlanner` for the centre and the trees), so preview
    and command agree; scenario builds (`Origin = Scenario`) skip it. Buying needs the park level
    (`ParkProgress.CurrentLevel`, derived) and the price. Lifts the park builds itself are paid at once and run from the
    next opening after `BuildDays` (`Lift.ReadyTick`); a scenario lift can start `Derelict` and be restored for half
    the price (`RestoreLiftCommand`); `Lift.InService` gates dispatch, routing (bike carriers 0) and upkeep
    (`FinanceSystem`, own lifts only). Company lifts are rented: tiers above 0 need the land at both stations
    (`LiftWorks.StationsOwned`). Riders on unsheltered lifts (T-bar, chairlift) get wet in the rain.
20. **Trail editing** (`WayEditing`, instant and free): `SplitTrailCommand` (the upper part keeps the trail, the lower one
    is "<name> 2", joined to its end), `RenaturalizeTrailCommand` (the section is gone, trees grow back; the pieces get
    loose ends, remnants under `MinLengthMeters` go, joins onto the removed section become loose). Pieces get control
    points re-sampled from the derived geometry; features and joins move with them. When a way becomes built
    (`OnBuilt`, from `BuildWayCommand` and the BuildWay job): a trail that starts on a trail's loose end is absorbed into
    it, and one that ends on a trail's loose start absorbs that trail (one trail again); a gravel path ending in the middle
    of a trail splits it there (gravel routes divide trails). Connectivity is derived: `WayNetwork.IsConnected` (start
    reachable from the base, base reachable from the end); unconnected trails are closed via `WayNetwork.IsRideable`
    (riders check it, never `Way.IsRideable` alone). Gravel platforms (`Platform`, `HubKind.Platform`, 12 × 12 m pad,
    free) are hubs ways can start and end on.
    A T-bar (`NetworkLink.Towed`) pulls riders up a straight gravel track on the ground between its stations; the track
    is derived (never a `Way`, nobody walks it): ways crossing it are found in `WayNetwork.TowCrossingsOn` (warned
    about in `WayPlanner` and in `StructurePlanner.PlanLift`), and a trail rider passing such a crossing while a towed
    rider is within the crossing window rolls the usual collision. A towed rider hurt there stops the whole lift
    (`Lift.StoppedUntilTick`: no boarding, nobody on it moves, `LiftRunning` false): minor for `TowStopMinutes` (the
    rider gets off the track and goes home), serious until the helicopter (`RescueAtTick`); other crashes don't stop it. Boarded riders start with the head start of their
    carrier's departure within the minute (`LiftSystem.Dispatch`), so riders boarded together are spaced like carriers.

## Game controls (debug build)

Start screen (when run without world-setting debug args): start a new career (park name), continue or delete a saved
career, or play the Demo (sandbox, demo trails built, never saved). Careers are saved automatically, one file each
(`game/scripts/CareerStore.cs`: `user://careers/<id>.json` + `<id>.meta.json`, on macOS in
`~/Library/Application Support/Godot/app_userdata/Bikepark Manager/careers`), when the window closes / the game quits
(`SimHost._ExitTree`, WM close) and on System menu → Save & back to menu; there is no manual save. Saves are written
atomically (`SaveGame.SaveAtomic`); `WorldState.DayHistory` (last 60 day reports) keeps the charts across loads. HUD: bottom bar with clock/speed, category menus (B build · V trails · C crew · R riders · G lifts · M finances · U rating
(reputation: stars per skill group and what they value, demand, park level, FakeSocial posts) · N land (parcels: buy,
level and price; borders on the map: yours teal, for sale yellow, locked grey; names while the menu is open) · O map; Esc closes) and headline stats (click to open their menu) · Space pause, 1–4 speed · WASD/arrows/screen edge/middle-drag
pan · zoom: wheel, trackpad pinch / two-finger scroll, +/- keys (by character, any layout) or the bar's zoom buttons · Q/E or right-drag orbit · F1 cycles terrain overlay (natural / slope / surface) ·
P draw gravel access path, T draw trail (click or drag points, Backspace undo, Enter plans it for the crew, Esc cancel;
preview colored by gradient, tool panel shows trees to fell and crew-hours; ends snap onto plateaus) · lifts: pick T-bar / chairlift / gondola in Build (price, level, lock state), L place it (valley, then top), Enter orders it from the contractor (paid at once, running after its build days; debug Instant build: free, at once) · Lifts menu: restore a rusty lift, construction countdown, upkeep · K place parking lot (centre, then direction) · Build → Gravel platform (centre, then direction; link paths and trails) · Build → Renaturalize (click where a trail section starts and ends: it's removed, the pieces stay closed until connected; draw a trail from a loose end to join it again) · Build → Split trail (click a point) · a warning lists unconnected trails when you leave the Build menu, the Trails menu marks them ·
[ / ] book lower/higher bike access tier (from next opening; also in the Lifts menu) · trail features: pick one in
Build, point at a trail, click to plan it, Delete removes the one under the cursor (also ✕ in the Trails menu) · Trails menu per trail: worst feature, click the trail (or Features…) for the feature overview + repairs, Close/Open ·
warning pop-up when a feature is below 20 % (pauses the game; ✕/Later closes it and goes on at 1x): pick workers and repair · Build →
Fell trees: click the centre, move to size, click to mark · while a build tool is active the menu folds into a chip above the bar (✕ or Esc stops the tool and brings the menu back) · Crew menu: hire/dismiss, tools, buy wood, job queue (↑ first,
✕ cancel) · System menu: Instant build (debug) · F follow next rider, or click a rider to follow it (a card at the top left shows
their stats; ✕ or panning stops following; influencers wear pink; injured riders: red and lying (serious: they
block the trail until the rescue helicopter has flown them out) or orange (minor, riding down slowly); the Trails menu lists crashes per trail, the feature overview per feature) ·
1x = 1 game minute per 8 seconds (speeds 1x/4x/16x/60x). Gradients are shown on the game's -10..+10 scale
(`Trails/Gradient.cs`, 1 point = 9°). Debug args after `--`: `--new-career=<park name>` / `--career=<id>` (start or continue a career, saved on quit), `--list-careers`, `--scenario=<demo|starter|file>` (default the Demo; skips the start screen like `--demo`, `--script=`, `--advance=`), `--demo`, `--speed=N`, `--report`, `--advance=<ticks>`,
`--demo-planned` (demo trails as crew jobs), `--demo-features` (after `--demo`), `--demo-crew`, `--instant`, `--panel=<menu>`, `--tool=<trail|path|fell|lift|parking|platform|renaturalize|split|featureId>`, `--look=<x>,<z>,<distance>` (camera focus, meters),
`--features=<wayId>` (open the feature overview), `--follow` (follow the first rider, shows the rider card), `--screenshot=<file.png>` (windowed run; saves after ~4 s and quits — use it to check UI changes), `--script=<file>`
(queue a command script, before `--advance`), `--no-night-skip`
(turn off the automatic night skip, e.g. for screenshots at night with `--speed=0`).
Day: crew 07:30, gondola warm-up 08:30, open 09:00–18:00, lunch ~12–13:30, last rides until 18:45, then the night is
skipped automatically (Game menu → Skip nights).

## Conventions

- C# latest, nullable enabled, warnings are errors (`Directory.Build.props`), file-scoped namespaces.
- Every new sim feature needs tests; keep `DeterminismTests` green and extend `TestWorlds.Script()` when
  adding commands.
- `game/Bikepark.csproj` must pin `<TargetFramework>net10.0</TargetFramework>`. Godot may re-add `net8.0` when
  it touches the csproj; remove it if so.
