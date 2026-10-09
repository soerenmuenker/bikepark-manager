# Bikepark Manager – Architecture

Status: living document. Last updated 2026-10-07.

Bikepark Manager is a park-management game in the spirit of RollerCoaster Tycoon. The core decision
is a **hard split between a deterministic simulation and the presentation layer**:

```
                ┌──────────────────────────── Godot client (game/) ───────────────────────────┐
                │  input / UI ──► ICommand ──┐                    ┌──► views, HUD, audio, FX   │
                │                            │                    │    (read WorldState,      │
                │  SimHost (real time → ticks, speed, save/load)  │     react to events)      │
                └────────────────────────────┼────────────────────┼───────────────────────────┘
                                             ▼                    │
   data/*.json ──► ScenarioLoader ──► ┌──────────── Bikepark.Sim (src/) ───────────────┐
                                      │ CommandQueue ─► Simulation.Step() ─► EventBus  │
                                      │                     │                          │
                                      │                 WorldState ◄─► SaveGame (JSON)  │
                                      └────────────────────────────────────────────────┘
                                             ▲
                         Bikepark.SimRunner (headless, KPIs) · Bikepark.Sim.Tests
```

## 1. Projects and dependencies

| Project | Kind | May depend on |
|---|---|---|
| `src/Bikepark.Sim` | class library, net10.0 | BCL only (System.Text.Json etc.). **Never Godot.** |
| `src/Bikepark.SimRunner` | console app | Bikepark.Sim |
| `tests/Bikepark.Sim.Tests` | xUnit | Bikepark.Sim |
| `game/Bikepark.csproj` | Godot 4.7 .NET project | Bikepark.Sim, GodotSharp |

Dependencies only point **towards** `Bikepark.Sim`. The sim has no knowledge of who drives it.
Everything that matters for gameplay can be run, tested and balanced without launching Godot.

## 2. Time and the tick loop

- **1 tick = 1 game minute.** `GameTime.MinutesPerDay = 1440`. Tick 0 is midnight of day 0.
- `Simulation.Step()` simulates exactly one tick:
  1. take all commands due at this tick from the `CommandQueue` and, in `(Tick, Sequence)` order,
     validate → apply → publish `CommandApplied` / `CommandRejected`;
  2. run all systems in a **fixed, explicit order** (`Weather → ParkHours → GuestArrival → Rider → Lift → Guest → TrailCare → Job → Finance`);
  3. increment `WorldState.Tick`.
- The sim has no notion of real time or frame rate. `SimHost` (Godot) converts real time into
  a number of ticks with a fixed-step accumulator, a speed multiplier and a per-frame cap.
  Pause = run zero ticks.
- Systems (`ISimSystem`) are stateless; all state they touch lives in `WorldState`.

## 3. State

- `WorldState` is the **single source of truth** and is a plain serializable object graph:
  clock, RNG state, park, rules, finance, stats, guests, id counter, pending commands.
- If it is not in `WorldState`, it does not survive a save and must not influence the simulation.
- Money is `long` cents. Ratios (happiness, chances) are integer permille (0..1000).
  No floating point in authoritative state or sim logic.
- Collections that are iterated during simulation are ordered (`List<T>`); no logic may depend
  on `Dictionary`/`HashSet` enumeration order.
- Entity ids come from `WorldState.AllocateEntityId()` — never `Guid.NewGuid()`.

## 4. Commands (view → sim)

- The only way anything outside the sim changes the world is an `ICommand`
  (`Validate(state)` returns a rejection reason or null; `Apply(ctx)` mutates).
- Commands are enqueued with a target tick (default: the next tick) and applied at the **start**
  of that tick. Same-tick commands apply in enqueue order. Past ticks are clamped to the next tick.
- Pending commands are stored in `WorldState.PendingCommands`, so they are saved and hashed.
- Commands are immutable records and polymorphic-JSON serializable. Each concrete command must be
  registered on `ICommand` with `[JsonDerivedType(typeof(X), "camelCaseName")]`. Discriminators are
  part of the save format: never rename or reuse one.
- A command stream + seed fully reproduces a game. This enables replays, scripted scenarios,
  bug repro files, and (later) lockstep multiplayer.

## 5. Events (sim → view)

- Systems and commands publish `ISimEvent` records through `SimContext.Publish`. Events are buffered
  in the `EventBus` and delivered when the host calls `Dispatch()` (Godot: once per frame;
  SimRunner: once per day).
- Events are **presentation-only**: notifications, sounds, particles, logs, UI refresh hints.
  They are not saved, and nothing in the sim reads them.
- Handlers must not mutate `WorldState`. To react to an event with gameplay, enqueue a command.
- The view may read `WorldState` at any time between steps (e.g. for rendering), but never writes it.

## 6. Determinism

Same scenario + same seed + same command stream ⇒ byte-identical `WorldState` (and `StateHash`).

Rules:
- Randomness only via `SimRandom` (PCG32, state in `WorldState.Rng`). No `System.Random`,
  no `Random.Shared`, no `Guid.NewGuid()`.
- No wall-clock time (`DateTime.Now`, `Stopwatch`, `Environment.TickCount`) in the sim.
- No floating-point in sim logic; use integers, permille or fixed-point.
- No parallelism inside a step unless results are merged in a fixed order.
- No dependence on hash-based collection iteration order, string hash codes, or culture.
- Systems run in a fixed order; new systems are appended deliberately.
- Tests enforce this: identical runs hash equal, save/load mid-run continues identically,
  chunked stepping equals continuous stepping, and `SimRandom` matches the PCG reference vectors.

## 7. Persistence

- `SaveGame.Serialize/Deserialize` writes `{ "version": N, "world": WorldState }` as camelCase JSON
  via `SimJson` options. Unknown fields are rejected so typos in content fail loudly.
- `SaveGame.CurrentVersion` is bumped on incompatible changes, with a migration step in `Load`.
- `StateHash.Compute(state)` = SHA-256 of the compact canonical JSON. Used by tests and the runner;
  later useful for desync detection.

## 8. Content (`/data`)

- Authored content is JSON under `/data` (scenarios now; trail pieces, guest types, prices later).
- It is loaded by the sim (`ScenarioLoader`) and validated on load; content is copied into
  `WorldState` (e.g. `ParkRules`) so saves are self-contained and do not change if content files change.
- In the Godot editor, content is read from `<repo>/data`; exported builds ship `data/` next to the
  executable (export pipeline TBD).

## 9. Tooling

- `Bikepark.SimRunner` runs scenarios headless and prints KPIs as JSON — for balancing,
  regression checks (compare `stateHash`), and CI.
- Godot is only needed to work on presentation.

## 10. Terrain

- `TerrainSettings` (in the scenario and in `WorldState.Terrain`) fully determine the terrain; a null seed is
  resolved to the scenario seed when the world is created. The `TerrainGrid` is **derived data**: generated on
  first access to `Simulation.Terrain`, cached by settings, never saved.
- Grid: 1 m spacing, (Size+1)² samples, +X east, +Z south. Layers per sample: height (int cm), slope (grade ‰),
  tree density, rock, roots (0..255) and water depth (cm; reserved, currently always empty).
- Generation (integer-only, see §6): main peak (radial falloff) + radiating meandering ridges + fractal value
  noise, rescaled to exactly the configured relief; then rock (steep / summit / outcrop noise), forest patches
  (coverage by histogram quantile) below a jittered tree line, roots under trees on slopes.
- Queries take cm coordinates and clamp to the map: `HeightAt`/`SlopeAt` interpolate bilinearly,
  `SurfaceAt` uses the nearest sample with priority Water > Rock > Roots > Forest > Grass.
- `TerrainScatter` places trees and rocks with a jittered global grid and hashing, so results are independent of
  how the view splits the map into chunks (tested).
- Tooling: `SimRunner terrain` renders relief / slope / surface maps as PNG (pure C#). This is the primary way to
  inspect generator changes; the Godot view uses the same overlay palettes.
- Godot view: 64 m chunks, each with a full-detail and a 4 m mesh switched by visibility range, skirts against
  LOD cracks, one MultiMesh per chunk for trees and rocks, `terrain.gdshader` for layer blending and overlays.

## 11. Ways and riders (Phase 2)

- **Gradients** use a game scale from -10 to +10, linear in angle (1 point = 9°; 0 flat, ±10 vertical), stored in
  tenths (`Gradient`, integer lookup table). Limits are deliberately generous (fun over realism): only extremes
  are errors, steep stretches are merged warnings, and steepness is paid for in gameplay (slow climbs, extra energy).
- **Ways** are what the player builds: gravel **access paths** (two-way, error beyond ±4.0, warning beyond 2.0;
  graded: their height is the terrain smoothed over ±25 m, ends on the ground) and downhill **trails** (one-way,
  error below -8.0 or above +3.0, warnings beyond -5.0 / +1.5; follow the ground). Splines are resampled every
  1 m of horizontal distance so grading and interpolation see evenly spaced samples. The first path's low end is the park **base** where guests arrive; a trail needs
  a path first and both of its ends must snap (12 m) onto existing ways. Validation guarantees every point of the
  network is reachable from the base and can get back to it.
- `WorldState.Ways` stores only player input (oriented, snapped control points and joins `(wayId, distanceCm)`).
  `WayGeometry` (integer Catmull-Rom, ~1 m samples, 10 m segments with grade, turn, surface, difficulty; rating
  from overall steepness, steep sections and features) and `WayNetwork` (junction graph, deterministic Dijkstra, spatial index for
  snapping and tree clearing) are derived and rebuilt when `WaysRevision` changes.
- `WayPlanner` is the single validator; the build command and the live preview both call it.
- **Riders** (guests): skill, style, energy, mood. Each lap: pick a trail (skill vs difficulty, style, variety,
  seeded RNG), route up the paths to its start, ride it, score fun per segment, update mood and trail stats.
  Each tick moves riders 60 s along their legs at an integer speed from skill, grade, roughness, turns and
  difficulty. Climbing costs energy; tired riders go home. Riders only leave between laps (or at closing).
- Time scale in the client: 1x = one game minute per 8 real seconds; the view interpolates rider progress between
  ticks (`SimHost.BeforeStep` + `InterpolationAlpha`).
- Building is instant and free in Phase 2; trees and rocks in a way's corridor disappear (derived, not saved).
  Phase 3 adds lifts and queues next to climbing (§12), Phase 4.1 trail features (§13), Phase 4.2 crews, cutting and
  build time.

## 12. Lifts, stations, parking and queues (Phase 3)

Design note: [lifts.md](lifts.md).

- **Terrain edits** are stored as `WorldState.TerrainEdits` (flattened oriented pads: target height, embankment
  gradient). `Simulation.Terrain` = `TerrainEditor.Apply(generated grid, edits)`, cached by `TerrainRevision`. The
  generated grid stays cached in `TerrainCache` and is never mutated. The embankment blends to the natural ground
  at its gradient, then steepens until it meets the ground. Pads clear trees, rocks and roots. Every structure
  change bumps `TerrainRevision`, so the network is rebuilt on `(WaysRevision, TerrainRevision)`.
- **Structures**: `Lift` (two stations on pads, type, optional operator, `BikeCarrierPermille`, FIFO `Queue`,
  dispatch clock) and `ParkingLot`. Content: `data/lift_types.json` (via `liftTypesFile` in the scenario) and the
  scenario's `operators` with bike access tiers, both copied into the world. `StructurePlanner` is the single
  validator (lifts, parking, pads), used by the build commands and the in-game preview.
- **Network**: stations and parking lots are hubs (graph nodes; way ends snap onto their pad edge). Lift links
  (one-way up) and walk links (two-way) join them. `RouteLeg.Kind` is Way / Lift / Walk. `Route(from, to,
  liftUsable)` can exclude lifts. The base is the parking hub, else the valley station, else the first access path.
- **Systems**: `… → RiderSystem → LiftSystem → GuestSystem → FinanceSystem`. RiderSystem picks the cheaper of the
  lift route (+ expected wait) and the pedalling route, walks, queues and moves riders on the lift. LiftSystem
  applies booked tiers at opening, dispatches carriers on an integer ms clock and boards the bike carriers
  (`LiftMath.IsBikeCarrier`). GuestSystem charges queue mood and restores energy while queuing or riding.
  FinanceSystem pays each operator the active tier's daily fee.
- **Trail traffic**: nobody overtakes on trails (only on access paths). Each minute `RiderSystem` first decides
  (laps, lunch), then moves riders: those already on a trail first, front to back per trail, so each follower is
  capped at `trailRules.riderGapCm` behind where the rider ahead is now (`TrailTraffic`, derived per minute). Time
  lost behind a slower rider costs `heldUpMoodPerMinute` and is counted in `WayStats.HeldUpSeconds`. Every trail leg
  starts with a wait at its entrance (`Guest.EntryWaitMs`, `entryWaitSeconds`): the rider re-checks the trail is
  open, gives way to riders on the trail within the gap and to faster (higher-skill) riders waiting there (for up
  to a minute), then drops in; `RunStarted` is published then.
- **Commands**: `buildLift`, `buildParkingLot`, `setLiftBikeAccess` (from the next opening), `deleteLift`,
  `deleteParkingLot` (rejected while ways attach). `buildWay` gained `origin` (`scenario` ways can't be deleted).
- **Saves**: all additions are new properties with defaults. Version-2 saves without them still load (tested), so
  the version stays 2.

## 13. Trail features (Phase 4.1)

Design note: [trail_features.md](trail_features.md).

- **Content**: `data/trail_features.json` (via `trailFeaturesFile` in the scenario) → `WorldState.TrailFeatureTypes`:
  material (dirt/wood), kind (drives the mesh), length, difficulty, gradient window and minimum turn of the covered
  segments, flow/technical affinity, fun weight. Placement margins, gap and count limit are in `TrailRules`.
- **State**: `Way.Features` stores only `{id, typeId, distanceCm}` per feature, sorted by distance; trails are never
  reshaped, so distances stay valid. Deleting a trail deletes its features.
- **Derived**: `WayNetwork.Build` resolves features (`FeaturesOn(wayId)`) and passes them to `WayGeometry.Build`;
  a covered segment's `Difficulty` = max(terrain, feature) and `FeatureDifficulty` keeps the feature's part.
  The trail's `DifficultyScore` = max(overall steepness: median gradient, steep sections: 90th-percentile terrain
  difficulty, hardest feature); `RatingCause` says which decided it. Steepness ramps (gradient score, tenths): sections
  green ≤ -1.5, blue ≤ -3.5, red ≤ -4.8, else black; overall (median) green ≤ -0.7, blue ≤ -1.5, red ≤ -2.4, else
  black. Roughness adds up to 100 and tight turns up to 100 to a segment. Feature tiers: berms, rollers, tables fit
  blue; kickers, wall-rides, small drops make a trail red; doubles (later rock gardens, long jumps) black.
- **Validation**: `FeaturePlanner` only (command and in-game ghost). **Commands**: `placeTrailFeature`,
  `removeTrailFeature` (bump `WaysRevision`; routes stay valid, riders are not reset). **Events**:
  `TrailFeaturePlaced`, `TrailFeatureRemoved`.
- **Riders**: each feature crossed on a run adds `funWeight` fun samples (`RiderSystem.FeatureFun`: skill match +
  style affinity, halved if scary). Trail choice already follows the raised `DifficultyScore`.
- **Saves**: new properties with defaults; version 2 saves without them load (empty catalog), so the version stays 2.

## 14. Crew, jobs and wood (Phase 4.2)

Design note: [crew_and_jobs.md](crew_and_jobs.md).

- **Content**: `data/tools.json` (via `toolsFile`) → `WorldState.ToolTypes` (work type, price, speed bonus);
  `crewRules` (wage, work hours, max crew and workers per job, work minutes per meter, steep extra, felling minutes and
  wood per tree, wood price, clearing radius) and `startingWood` in the scenario; features gain `workMinutes`, `wood`.
- **State**: `Crew` (generic workers: id, name, current `JobId`), `Jobs` (priority order), `WoodStock`,
  `OwnedToolIds`, `FelledTrees` (positions of scatter trees cut outside built corridors), `CrewStats`; `Way.Built`
  and `TrailFeature.Built` (default `true`, so older saves load fully built).
- **Jobs**: `BuildWay`, `BuildFeature`, `FellTrees`. A job first fells its `Trees` (fixed list, in order; each tree
  adds wood at once), then does `WorkMinutes` of `MainWorkType` work. Progress is permille crew-minutes; speed is
  `1000 + best owned tool bonus` for the work type needed now. `Crew/Jobs.cs` queues, cancels and completes jobs;
  `Crew/WorkCosts.cs` is the only place that computes work and wood (commands, previews, reports).
- **Trees**: derived from `TerrainScatter` and identified by position. `Crew/Forest.cs` decides which are gone
  (built corridors, `FelledTrees`) or claimed (listed in a job) — the sim's wood and the view's trees can't disagree.
  A planned way's corridor trees are listed in its job and felled before digging; when the way is finished its
  corridor covers them and they are dropped from `FelledTrees`.
- **`JobSystem`** (between `GuestSystem` and `FinanceSystem`, no RNG): during work hours free workers (hiring order)
  take the first workable job with room; a feature waits for its trail and for its wood (taken when work starts).
  A finished job sets `Built` and bumps `WaysRevision`. `FinanceSystem` charges wages at the end of the day.
- **Network**: planned ways keep geometry and nodes (snapping, features, drawing) but get no edges, aren't in
  `Trails` and don't clear their corridor; planned features don't change segment difficulty and give no fun.
- **Commands**: `buildWay`/`placeTrailFeature` plan (unless `instant`, debug; scenario ways are always built);
  `hireCrew`, `dismissCrew`, `buyTool`, `buyWood`, `fellTrees` (validated by `ClearingPlanner`), `prioritizeJob`,
  `cancelJob`; deleting a planned way or feature cancels its job and refunds wood. **Events**: `CrewHired`,
  `CrewDismissed`, `ToolBought`, `WoodBought`, `JobQueued`, `JobCompleted` (with a title), `JobCancelled`, `TreeFelled`.

## 15. Daily rhythm (Phase 5.1)

Details: [daily_rhythm.md](daily_rhythm.md).

- **`ParkSchedule` is the only timetable**: opening hours, last rides, lift warm-up, crew shift and overtime, the day
  phase, `IsQuiet` and `NextWakeTick` are pure functions of `WorldState` + tick. Systems and views ask it; nobody
  compares minutes against the rules directly.
- **Skipping never jumps `Tick`.** The host's night skip (and the manual skips) only run more steps per frame, so every
  minute is simulated: tier switches at opening, the books at 23:59, lunch plans and determinism all hold. Whether to
  skip is a host setting, not state.
- **Every new daily-rhythm rule defaults to "off"** (`LastRideMinutes`, `LiftWarmupMinutes`, `ArrivalProfile`,
  lunch window, `OvertimeMinutes`); scenarios opt in. Older saves need no migration.
- **Guests on a lap are never removed mid-lap before `close + lastRideMinutes`**; guests eating are never removed
  before closing. Lunch is planned at arrival (`Guest.LunchMinute`) and taken at the next break between laps.
- **Overtime only finishes work**: no new assignments after the shift; a worker stays only if the job fits in the
  overtime left.
- Day light is view-only (`game/scripts/world/DayLight.cs`), derived from the minute of the day.

## 16. Wear and weather (Phase 5.2)

Details: [wear_and_weather.md](wear_and_weather.md).

- **Weather is state, rolled once a day** by `WeatherSystem` (first system): `Today`, `Tomorrow` (the forecast),
  ground wetness. No odds in the scenario ⇒ always sunny and no RNG draws, so older scripts keep their hashes.
- **Only trail features wear.** Condition is `TrailFeature.Condition` (millionths, perfect by default), never in the
  derived `WaySegment` and never via `WaysRevision`: wear changes every minute, the geometry doesn't. Trails themselves
  do not wear.
- **Closures are filters, not graph changes**: `Way.IsRideable` (`Built && !Closed && !WornOut && !Repairing` (`Repairing` = crew assigned to one of its features)) is
  checked when a rider picks a trail, by routing (closed trails are never used as connectors) and again at every trail entrance
  (then they pick another); the network keeps the live `Way`/`TrailFeature` objects. Riders already on the trail finish;
  feature work only progresses once none is left on it (`Jobs.IsWaitingForRiders`).
- **`TrailCareSystem`** (after `GuestSystem`, before `JobSystem`, no RNG) raises the "needs repair" warning, closes a
  trail whose feature is at 0 and closes it while the crew is assigned to building or repairing a feature (`JobSystem` also closes it the
  moment it assigns workers, via `Jobs.UpdateWorkClosure`). It never queues work: repairs are manual
  (`RepairFeatureCommand`); only a finished repair reopens a worn-out trail.
- Save version 3 migrates away the per-segment condition and the maintain switch.
- All wear and work numbers come from `WearRules` / `CrewRules` (`TrailCondition`, `WorkCosts.RepairMinutes`: `crewRules.repairWorkPermille` of the build work).

## Open questions / next steps

- Further terrain edits (cut/fill by the player) as saved changes, like the pads.
- Water features (ponds, streams) using the reserved water layer, if gameplay needs them.
- Guest pathfinding and needs model; keep allocation-free and integer-based.
- Export pipeline for `/data`; possibly embed content as resources.
- Command-stream recording in `SimHost` for replays and bug reports.
