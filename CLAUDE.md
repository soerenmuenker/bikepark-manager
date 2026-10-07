# Bikepark Manager

RollerCoaster-Tycoon-style bikepark management game. Godot 4.7 .NET (C#) client on top of a pure,
deterministic C# simulation library. Full rationale: [docs/design/architecture.md](docs/design/architecture.md).

## Layout

- `src/Bikepark.Sim/` – simulation library (WorldState, Simulation tick loop, SimRandom, commands, events, save/load, scenarios, KPIs)
  - `Terrain/` – `TerrainGenerator` (integer-only heightmap + layers), `TerrainGrid` (queries), `TerrainScatter` (trees/rocks),
    `TerrainEdit`/`TerrainEditor` (stored flattened pads applied on top of the generated grid)
  - `Trails/` – `Way` (player-built access paths and trails), `WayGeometry` (integer spline, segments, rating),
    `WayPlanner` (validation, shared by command and preview), `WayNetwork` (derived graph, routing, corridors),
    `TrailFeature` (feature catalog types + placed features), `FeaturePlanner` (feature placement validation)
  - `Lifts/` – `LiftType`/`LiftOperator`/`LiftRules` (content), `Lift`/`ParkingLot` (state), `LiftMath` (bike carriers,
    throughput), `StructurePlanner` (validation of lifts, parking, pads), `LiftNetwork` (hubs + links for the network)
  - `Systems/RiderSystem.cs` – riders choose a trail and the cheaper way up (walk + lift queue, or pedal the paths), ride down, score fun (segments + features)
  - `Systems/LiftSystem.cs` – booked bike access tiers at opening, carrier dispatch, boarding bike cabins from the FIFO queue
  - `Crew/` – `CrewRules`/`ToolType` (content), `CrewMember`/`Job` (state), `WorkCosts` (all work/wood/speed numbers),
    `Forest` (which scatter trees are gone or claimed), `ClearingPlanner` (felling areas), `Jobs` (queue/cancel/complete)
  - `Systems/JobSystem.cs` – the crew at work: assigns workers to jobs in priority order, fells trees (wood), builds planned ways and features;
    after the shift only overtime that finishes a job
  - `Weather/` – `WeatherRules`/`WeatherState`/`DayWeather` (daily seeded forecast, ground wetness), `Systems/WeatherSystem.cs`
  - `Trails/TrailCondition.cs` – `WearRules` and feature condition (wear per rider pass, more when wet; worn
    features slower/less fun); `Systems/TrailCareSystem.cs` raises the repair warning and closes worn-out trails
  - `Systems/ParkSchedule.cs` – the daily timetable (open, last rides, lift warm-up, crew shift/overtime, day phase,
    quiet nights and the next wake-up); arrivals follow `ParkRules.ArrivalProfile`, guests take one planned lunch break
- `src/Bikepark.SimRunner/` – headless console runner: KPIs as JSON, `terrain` subcommand renders top-down PNG maps
- `tests/Bikepark.Sim.Tests/` – xUnit tests (determinism, commands, persistence, RNG, terrain)
- `game/` – Godot project (`Bikepark.csproj`, `scripts/SimHost.cs` drives the sim, `scripts/ui/` HUD (bottom bar + menus, drawn icons, theme in code),
  `scripts/terrain/` chunked terrain view, `scripts/camera/RtsCamera.cs`, `scripts/ways/` way view + build tool + feature tool/meshes,
  `scripts/riders/RiderView.cs`, `scripts/lifts/` lift/parking view + debug structure tool, `scripts/crew/` crew figures +
  felling tool, `scripts/world/` time-of-day + weather light (`DayLight`) and rain (`RainView`), `shaders/`)
- `data/` – JSON content (`scenarios/`, `lift_types.json`, `trail_features.json`, `tools.json`, `scripts/` command
  scripts such as `demo_lift_network.json`, `demo_features.json`, `demo_crew.json`)
- `Bikepark.sln` – root solution; Godot uses it via `project/solution_directory="../"`

## Commands

`dotnet` lives at `/usr/local/share/dotnet` and may not be on PATH: `export PATH=/usr/local/share/dotnet:$PATH`.

```bash
dotnet build Bikepark.sln
dotnet test Bikepark.sln
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 30 [--seed N] [--commands file.json]... [--daily] [--hourly] [--auto-repair] [--save out.json]
# --hourly: the last day per hour (phase, guests, on trails, queuing, eating, runs, crew working, lift minutes, rain, wetness, closed trails)
# Wear: 14 days with and without a stand-in player that repairs on every warning (--auto-repair): per-trail status, feature conditions, closed minutes, repairs
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 10 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json --daily [--auto-repair]
# Terrain: top-down PNG maps + stats (use this to check terrain changes, no Godot needed)
# (applies the scenario's own commands too: pads, lift line, parking and the hiking route are drawn)
dotnet run --project src/Bikepark.SimRunner -- terrain --scenario data/scenarios/starter_valley.json --out out/map.png --mode all [--seed N] [--scatter] [--commands data/scripts/demo_lift_network.json]
# Riders on the demo trails: per-trail runs, per-lift riders/queue/wait/tier/fee, why guests left or were turned away
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 3 --commands data/scripts/demo_lift_network.json
# ... with the demo trail features (planned, built by the crew: per-trail feature list, crew block with completed jobs, wood, wages)
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 10 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json
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
    floor) is derived in `WayGeometry`/`WayNetwork`. All placement validation lives in `FeaturePlanner`.
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
    (`RepairFeatureCommand`, from the warning pop-up); only a finished repair reopens a worn-out trail.

## Game controls (debug build)

HUD: bottom bar with clock/speed, category menus (B build · V trails · C crew · R riders · G lifts · M finances · O map; Esc
closes) and headline stats (click to open their menu) · Space pause, 1–4 speed · WASD/arrows/screen edge/middle-drag
pan · zoom: wheel, trackpad pinch / two-finger scroll, +/- keys (by character, any layout) or the bar's zoom buttons · Q/E or right-drag orbit · F1 cycles terrain overlay (natural / slope / surface) ·
P draw gravel access path, T draw trail (click or drag points, Backspace undo, Enter plans it for the crew, Esc cancel;
preview colored by gradient, tool panel shows trees to fell and crew-hours; ends snap onto plateaus) · L place lift (valley, then top) · K place parking lot (centre, then direction) ·
[ / ] book lower/higher bike access tier (from next opening; also in the Lifts menu) · trail features: pick one in
Build, point at a trail, click to plan it, Delete removes the one under the cursor (also ✕ in the Trails menu) · Trails menu per trail: worst feature, Repair… (pop-up), Close/Open ·
warning pop-up when a feature is below 20 %: pick workers and repair · Build →
Fell trees: click the centre, move to size, click to mark · while a build tool is active the menu folds into a chip above the bar (✕ or Esc stops the tool and brings the menu back) · Crew menu: hire/dismiss, tools, buy wood, job queue (↑ first,
✕ cancel) · System menu: Instant build (debug) · F follow next rider ·
1x = 1 game minute per 8 seconds (speeds 1x/4x/16x/60x). Gradients are shown on the game's -10..+10 scale
(`Trails/Gradient.cs`, 1 point = 9°). Debug args after `--`: `--demo`, `--speed=N`, `--report`, `--advance=<ticks>`,
`--demo-planned` (demo trails as crew jobs), `--demo-features` (after `--demo`), `--demo-crew`, `--instant`, `--panel=<menu>`, `--tool=<trail|path|fell|lift|parking|featureId>`, `--look=<x>,<z>,<distance>` (camera focus, meters),
`--screenshot=<file.png>` (windowed run; saves after ~4 s and quits — use it to check UI changes), `--script=<file>`
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
