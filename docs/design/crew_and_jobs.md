# Crew and resources (Phase 4.2)

Status: implemented 2026-10-07. Rules that must not break are in `architecture.md` §14. Builds on
[trail_features.md](trail_features.md) (Phase 4.1).

Plan row: *"Jobs, crew, tree cutting, wood stock, tools; placing the phase 4.1 features on a trail now consumes wood
and crew-time to complete. Done when: building a line of features costs wood and takes days, tracked via crew jobs."*

Decisions:
- **Trails and access paths are crew jobs too**, not only features.
- **Wood comes from felling and can be bought.** Felling is a marked area of forest, plus the trees in a planned way's
  corridor. Buying costs a high price, so nobody gets stuck.
- **Workers are generic**: one wage, one speed. Tools speed up one kind of work for the whole crew.

## What the player sees

- **Drawing a trail or path, or placing a feature, plans it.**
  - The tool panel shows the cost first, for example "Crew: 22 trees to fell first (+44 wood) · ≈ 26 crew-h" or
    "Crew: ≈ 10 crew-h · 30 wood (have 20)".
  - Planned ways are dashed light-blue blueprints, labelled "planned · felling 12/30 trees" or "planned · 40 % built".
    The part already dug turns solid from the start.
  - Planned features are light-blue ghosts that fill in with dirt or wood colour as the crew builds them.
  - Riders ignore planned ways and features.
- **Build → Fell trees.**
  - Click a centre and move the mouse to size the circle, 5–40 m radius.
  - Orange markers show the trees that would be cut. The panel shows the tree count, the wood gained and the crew time.
  - Click to mark the area. Trees disappear one by one as they are felled.
- **Crew menu (C).** Its tiles show workers, wages per day, wood, open jobs and the work ahead. It also has:
  - **Workers:** Hire / Dismiss buttons.
  - **Tools:** a button per tool showing the speed-up and the price, or "owned".
  - **Wood:** Buy 10 / Buy 50 and "Fell trees…".
  - **Jobs:** the queue in priority order. Each job shows its state ("Felling 12/30 trees", "Digging 41 % · ≈ 5 crew-h
    left · 3 workers", "Waiting for wood: needs 25, 5 in stock", "Waiting for the trail to be built"), a progress bar,
    ↑ to move it to the top and ✕ to cancel it.
- **Bottom bar:** Crew ("3 · 19" = workers · jobs) and Wood chips; the wood chip turns amber while a job is waiting
  for wood.
- **Toasts:** "Planned Flow Country: the crew will build it", "Built: Berm on Flow Country at 40 m",
  "Flow Country is built and open".
- **In the world:** workers are orange-vest figures with hard hats at the tree they fell, at the dug end of a way or
  on the feature they build. Idle workers wait at the base during their shift; in overtime only those still on a job
  are out.
- **Finance menu:** crew wages and tools & wood totals.
- **System menu:** an "Instant build (debug)" toggle, which works like `--instant`.

## Done-when KPI

SimRunner command (Starter Valley, seed 1337, 10 days):

```bash
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 10 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json [--commands data/scripts/demo_crew.json]
```

The trails are built at once (`"instant": true`). The 19 demo features are planned and built by the crew.
Work numbers cut on 2026-10-07 (features about 40 %, paths and trees about half), re-measured the same day with the Phase 5.1 daily rhythm (crew 07:30–17:30 plus up to 90 min overtime to finish a job,
see [daily_rhythm.md](daily_rhythm.md)).

| Run | Crew | Felled / bought / used wood | Last feature built | Open after 10 days | Wages | Red Rocket |
|---|---|---|---|---|---|---|
| no features | 2 workers | – | – | – | €3,600 | Red (569) |
| features, 2 workers, no tools | 2 workers | 0 / 0 / 15 | day 3, 18:14 (14 of 19) | 5 wood features waiting for wood | €3,600 | Red (650) |
| + `demo_crew.json` | 3 workers, shovel set, chainsaw, 30 m felling area | 160 / 0 / 155 | day 3, 11:16 (19 of 19) | – | €5,400 + €2,400 tools | Black (700) |

With `demo_crew.json`, the line takes three days (two features are finished in overtime after closing: day 2 18:34,
day 1 18:03):

| Day | Built |
|---|---|
| 1 | Flow Country's nine dirt features and the kicker |
| 2 | Red Rocket's doubles and berm, the felling (80 trees, 160 wood), the first wall-ride and drop |
| 3 | The other wall-rides and drops |

The ratings and fun end where Phase 4.1 put them instantly:

| Trail | Rating | Avg fun |
|---|---|---|
| Flow Country | Red | 584 |
| Red Rocket | Black | 583 |

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Build state | `Way.Built`, `TrailFeature.Built` (default `true`: older saves load built) | progress from the job |
| Workers | `Crew`: `{id, name, jobId}` | positions (view) |
| Jobs | `Jobs` in priority order: kind, target, `Trees` + `TreesFelled`, `WorkMinutes`, `MainWorkType`, `Progress`, `Wood`, `WoodTaken` | workable / waiting |
| Wood | `WoodStock`, `CrewStats` (felled / bought / used) | – |
| Tools | `ToolTypes` (content), `OwnedToolIds` | speed per work type |
| Trees | `FelledTrees` (positions cut outside built corridors) | everything else, from `TerrainScatter` |
| Tuning | `CrewRules` (scenario) | – |

### Work

`Crew/WorkCosts.cs` is the only place that computes work and wood. The commands, the previews and the reports all use it.

- **Way.** First the trees in its corridor: `fellMinutesPerTree` each, at felling speed. Then digging:
  `trail|pathWorkMinutesPerMeter` × length, plus `steepExtraPermille` on segments steeper than the "steep" gradient.
- **Feature.** The type's `workMinutes`: carpentry for wood features, digging for dirt features. It also needs the
  type's `wood`.
- **Felling area.** `fellMinutesPerTree` × trees.
- **Speed.** A worker adds `1000 + best owned tool bonus` per minute for the work type needed now. Progress is
  counted in permille crew-minutes and restarts at 0 for each tree and for the main work.

### Trees

`Crew/Forest.cs` is used by both the sim and the view.

- A tree is identified by its scatter position. It is **gone** if it lies in a built way or lift corridor or is in
  `FelledTrees`. It is **claimed** if a job lists it.
- A new job's tree list is computed once, when the job is created, without the trees that are gone or claimed. The
  list is stored, so later changes can't make it count trees twice.
- **Felling areas** list their trees nearest the centre first. **Planned ways** list the trees in their corridor, in
  order along the way.
- Every felled tree goes into `FelledTrees` and adds `woodPerTree` at once.
- When a way is finished, its corridor covers its felled trees, so they are dropped from the list.

### JobSystem

`JobSystem` runs after `GuestSystem` and before `FinanceSystem`. It uses no randomness and does nothing without crew,
so state hashes for older scripts are unchanged. During work hours, every minute:

1. Workers whose job can't go on are freed.
2. Free workers, in hiring order, take the first workable job in the queue that has room: `maxWorkersPerJob`, or for
   a job that still has trees to fell one worker per remaining tree (so a big felling area uses the whole crew). When
   the trees are gone, workers above the limit move on. In the view each worker walks to their own tree.
   - A feature is workable once its trail is built and its wood is in stock. The wood is taken when the first worker
     starts.
   - A felling job is workable while trees remain.
   - A way job is always workable.
3. Every job with workers advances.
4. A finished job sets `Built`, bumps `WaysRevision` and publishes `JobCompleted`.

After the shift, a worker stays on for up to `overtimeMinutes` only if their job can be finished in the overtime left
(Phase 5.1); nobody starts new work. Outside work hours everyone goes home (`JobId` = 0). `FinanceSystem` pays `wagePerDayCents` per worker at the end of
each day.

### Network

Planned ways keep their geometry and nodes, so other ways can snap to them, features can go on them and the view can
draw them. They get no graph edges, are not in `WayNetwork.Trails` and don't clear their corridor. `FeaturesOn` lists
planned features too, so the planner sees them and nothing can overlap them. Only built ones change segment
difficulty and give riders fun.

### Cancel and delete

- Deleting a planned way or feature cancels its job and refunds any wood it took. Deleting a built way also cancels
  the jobs of its planned features.
- `cancelJob` on a build job deletes the planned target.
- `cancelJob` on a felling job keeps the trees cut so far.
- Deleting a built way or feature is still instant; there is no demolition job yet.

### Debug and demos

- `BuildWayCommand` and `PlaceTrailFeatureCommand` take `instant` (default false). Scenario ways are always built.
- The demo trail scripts use `"instant": true`. `demo_features.json` plans its features.
- `data/scripts/demo_crew.json` hires a third worker, buys the shovel set and the chainsaw, and marks a 30 m felling
  area east of the plateau (80 trees).
- Godot args:
  - `--instant`: the tools build at once.
  - `--demo-crew`: loads `demo_crew.json`.
  - `--demo-planned`: the demo trails become crew jobs.
- The scenario hires two workers at tick 0. Fresh-world ids shift by two because of this: the demo trails are now
  ways 11 and 12.

## Content (first values, tune freely)

`crewRules` in `demo_valley.json`:

| Rule | Value |
|---|---|
| Wage | €180 per day |
| Max crew | 12 |
| Max workers per job | 3 |
| Work hours | 07:30–17:30, up to 90 min overtime to finish a job (Phase 5.1) |
| Trail digging | 4 crew-min/m |
| Path digging | 3 crew-min/m |
| Steep extra | +50 % |
| Felling | 8 crew-min per tree, one worker per tree (the whole crew can fell at once) |
| Wood per tree | 2 |
| Wood price | €25 |
| Clearing radius | 5–40 m |
| Starting wood | 20 |

Feature work (`trail_features.json`):

| Feature | Work | Wood |
|---|---|---|
| berm | 200 crew-min | – |
| rollers | 240 crew-min | – |
| tabletop | 300 crew-min | – |
| double | 400 crew-min | – |
| wall-ride | 240 crew-min | 30 |
| kicker | 150 crew-min | 15 |
| drop | 200 crew-min | 25 |

Tools (`tools.json`):

| Tool | Work type | Price | Speed |
|---|---|---|---|
| Shovel & rake set | digging | €1,500 | +25 % |
| Mini excavator | digging | €18,000 | +100 % |
| Chainsaw | felling | €900 | +50 % |
| Cordless tool kit | carpentry | €1,200 | +40 % |

For scale:
- Flow Country (1.3 km) is ~5,200 crew-min of digging plus 19 trees in its corridor. Red Rocket (1 km) is
  ~3,900 crew-min plus 33 trees.
- Three workers without tools need about three working days for Flow Country and two and a half for Red Rocket.
  With the mini excavator and the chainsaw, Red Rocket takes just over one working day.

## Tests (`tests/Bikepark.Sim.Tests/CrewTests.cs`)

- **Content:** the shipped content loads, and bad tools or rules are rejected.
- **Planning:**
  - A planned trail isn't ridden until it is built.
  - A planned feature has no effect until built.
  - Instant and scenario ways are built at once.
- **Jobs:**
  - Work only happens in work hours, and a job finishes on the expected tick.
  - The shovel set speeds up digging.
  - Jobs fill in queue order up to the worker limit, and `prioritizeJob` works.
  - A wood feature waits for wood and takes it when work starts.
  - A feature waits for its trail.
- **Trees:**
  - Felling cuts trees one by one, each adds wood, and claimed trees can't be marked again.
  - `ClearingPlanner` checks the area and orders the trees.
  - A planned trail through forest fells its corridor first, then digs.
- **Money and crew:** purchases cost money and are rejected without enough. The crew limit holds and names are
  reused. Wages are paid per worker per day.
- **Cancel:** deleting a planned way cancels its job and its features' jobs and refunds wood. `cancelJob` removes the
  planned target. Cancelling felling keeps the cut trees.
- **Saves:**
  - Crew and jobs survive save/load, and the game continues identically.
  - A v2 save without the new fields loads fully built.
  - The command discriminators are stable.
- **Script:** `TestWorlds.Script()` plans features, hires crew, buys tools and wood, fells, prioritizes, cancels and
  dismisses, so `DeterminismTests` covers all of it.

## Not in Phase 4.2

- Crew skills, roles and individual speeds; travel time to job sites; crew energy.
- Demolition jobs and reshaping built trails.
- Maintenance and repair jobs (Phase 5, wear and weather), and rescue (Phase 6).
- Selling wood; wood quality; tree regrowth.
