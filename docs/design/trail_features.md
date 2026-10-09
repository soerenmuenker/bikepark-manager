# Trail features (Phase 4.1)

Status: implemented 2026-10-07. Rules that must not break are in `architecture.md` §13. Since Phase 4.2
([crew_and_jobs.md](crew_and_jobs.md)), placing a feature plans it and a crew job builds it, using wood for wood
features. The KPI below was measured with instant placement (now `"instant": true` on the command). The demo trails
are now ways 11 and 12, because the scenario hires two workers first.

## What the player sees

The Build menu gets a second row, **Trail features**, in two groups: **wood** (wall-ride, kicker, drop) and **dirt**
(berm, rollers, table, double). Picking one turns the cursor into a ghost of the feature that slides along the
nearest trail, facing the direction of travel. Green = can go here, red = the tool panel says why not ("a berm needs
a bend", "too close to the double at 140 m"). Click places it at once. Hovering a placed feature and pressing
Delete removes it. The Trails menu lists each trail's features ("2 berms · table · drop") with a remove button per
feature, and the trail's rating and fun update as features change.

Done when: open Build, drop features onto a demo trail, and the trail's feature list (and rating) in the Trails menu
update. Proof KPI (SimRunner, Starter Valley + `demo_lift_network.json` + `demo_features.json`, 3 days, seed 1337):

| Trail | Features | Rating (score) | Runs | Avg fun |
|---|---|---|---|---|
| Flow Country, without | – | Red (556) | 2,937 | 567 |
| Flow Country, with | 4 berms, 2 rollers, 3 tables | Red (556) | 3,153 | 584 |
| Red Rocket, without | – | Red (569) | 2,852 | 599 |
| Red Rocket, with | 3 wall-rides, 2 drops, kicker, 3 doubles, berm | Black (700) | 2,598 | 571 |

The drops make Red Rocket black, so fewer riders pick it and more ride the flow trail, which is now more fun.

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Feature catalog | `TrailFeatureTypes` (copied from `data/trail_features.json` via `trailFeaturesFile` in the scenario) | – |
| Placed feature | `Way.Features`: `{id, typeId, distanceCm}` on the trail, sorted by distance | position, heading, covered segments, effect on segment difficulty and rating |

- **Only input is stored** (rule 11): a feature is a type and a start distance along its trail. Trails are never
  reshaped, so the distance stays valid; deleting a trail deletes its features.
- **Placement validation lives in one place**: `Trails/FeaturePlanner.Plan(network, rules, types, wayId, typeId,
  distanceCm)` returns a `FeaturePlan` with `WayIssue`s (errors and warnings), used by the command and the live
  preview, exactly like `WayPlanner` / `StructurePlanner`.
- **Geometry includes features**: `WayGeometry.Build` takes the trail's features (resolved against the catalog).
  A covered segment's difficulty becomes `max(terrain difficulty, feature difficulty)`, and the trail's
  `DifficultyScore` becomes `max(overall steepness, steep sections, hardest feature)`, because a feature on the line
  is mandatory (see `architecture.md` for the steepness ramps).
  `WayPlanner` keeps calling it without features (a new trail has none). Placing or removing a feature bumps
  `WaysRevision`, so the network rebuilds. Shapes don't change, so riders' routes stay valid and nobody is reset.
- **Riders**: crossing a feature adds one fun sample (weighted by the type's `funWeight`): skill match against the
  feature's difficulty, plus the type's flow / technical affinity for the rider's style. The existing "scared"
  rule applies. Riders who are below a covered segment's (raised) difficulty slow down on it, as on any hard segment.
  There are no crashes yet (Phase 6 uses `difficulty` per feature).

## Catalog (`data/trail_features.json`, first values, tune freely)

Gradients are along the direction of travel in tenths of the -10..+10 score, checked on every covered segment.
Turn is the segment's `TurnPermille` (100 ≈ 37°).

| id | material | length | difficulty | gradient window | other rule | suits |
|---|---|---|---|---|---|---|
| `berm` | dirt | 12 m | 200 | -5.0 … +0.5 | needs turn ≥ 100 | flow |
| `rollers` | dirt | 15 m | 250 | -4.0 … 0.0 | – | flow |
| `table` | dirt | 10 m | 450 | -3.5 … -0.5 | – | flow, some tech |
| `double` | dirt | 12 m | 720 (black) | -3.5 … -0.5 | – | flow, tech |
| `wall_ride` | wood | 8 m | 550 | -4.0 … +0.5 | needs turn ≥ 100 | tech, flow |
| `kicker` | wood | 6 m | 500 | -4.0 … -0.5 | – | tech |
| `drop` | wood | 5 m | 600 (red: a small drop) | -6.0 … -2.0 | – | tech |

Entry fields: `id`, `name`, `material` (`wood`/`dirt`), `kind` (enum, drives the view's mesh), `lengthMeters`,
`difficulty` (0..1000), `minGradient`/`maxGradient`, `minTurn`, `flowAffinity`/`techAffinity` (0..1000),
`funWeight` (samples counted). Validated on load, unknown fields are errors. Phase 4.2 adds wood and crew-hours.

Global rules (in `TrailRules`, scenario-tunable): trails only (not access paths), at least `featureStartMarginMeters`
(10) from the trail start and `featureEndMarginMeters` (15) from its end, at least `featureGapMeters` (5) between two
features, at most `maxFeaturesPerTrail` (40).

## Implementation steps (as built)

1. **Content and state** (`src/Bikepark.Sim/Trails/TrailFeature.cs`): `TrailFeatureType` (+ `Validate`),
   `FeatureMaterial`, `FeatureKind`, `TrailFeature {Id, TypeId, DistanceCm}`; `Way.Features` (default `[]`);
   `WorldState.TrailFeatureTypes`; the new `TrailRules` fields; `ScenarioDefinition.TrailFeaturesFile` +
   `TrailFeatureTypes`, loaded and validated like the lift catalog (unique ids). Create `data/trail_features.json`
   and reference it from `starter_valley.json`.
2. **Planner** (`Trails/FeaturePlanner.cs`): resolve trail and type; check kind, margins, overlap and gap against
   existing features, per-segment gradient window and turn; return position and heading for the preview.
   Issue codes: `notATrail`, `unknownType`, `tooCloseToStart`, `tooCloseToEnd`, `overlaps`, `tooSteep`,
   `tooFlat`, `needsBend`, `tooManyFeatures`.
3. **Commands** (`Commands/FeatureCommands.cs`): `PlaceTrailFeatureCommand(WayId, TypeId, DistanceCm)`
   (`"placeTrailFeature"`) and `RemoveTrailFeatureCommand(WayId, FeatureId)` (`"removeTrailFeature"`); id from
   `AllocateEntityId`, list kept sorted by distance, `WaysRevision++`. Events `TrailFeaturePlaced` /
   `TrailFeatureRemoved`.
4. **Geometry and rating**: optional features parameter on `WayGeometry.Build`; `WaySegment` gains
   `FeatureDifficulty` (0 = none) so the view and later phases can tell terrain difficulty from feature difficulty;
   `WayNetwork.Build` passes each trail's features; `WayNetwork.FeaturesOn(wayId)` returns them resolved
   (type, start/end cm) for riders and the view.
5. **Riders**: in `RiderSystem`'s descent loop, score each feature whose start is crossed this tick
   (`FeatureFun`, beside `SegmentFun`). Trail choice needs no change because it already reads `DifficultyScore`.
6. **SimRunner**: per-trail output gains feature count and rating; add `data/scripts/demo_features.json`
   (berms + rollers on one demo trail, table, double and drop on the other) for the proof KPI.
7. **View: meshes** (`game/scripts/ways/FeatureMeshes.cs`): simple low-poly procedural shapes per `FeatureKind`,
   oriented by `PositionAt` and direction (wood = plank brown, dirt = trail dirt). `WayView` draws them on rebuild
   and subscribes to the two new events. `shaders/feature.gdshader` pulls them in depth a little more than the
   trail ribbons, so jumps on the trail are drawn over it.
8. **View: tool** (`game/scripts/ways/FeatureTool.cs`): mode = selected type id; raycast cursor →
   `network.NearestTrail` → distance; preview via `FeaturePlanner` every frame (ghost mesh green/red);
   left click enqueues the place command; Delete on a hovered feature enqueues remove; Esc leaves the tool. It is
   mutually exclusive with `WayTool` and `StructureTool`. The HUD tool panel shows type, "at 140 m of Trail 2",
   the first issues and "Click to place".
9. **View: menus**: `BuildPanel` second row with seven `ToolCard`s grouped Wood / Dirt (seven new drawn icons in
   `UiIcons`); `TrailsPanel` rows show a feature summary line and an expandable list with remove buttons (row
   signature includes the feature ids). Debug: Build menu "Demo features" card, `--demo --demo-features` (maps the
   script's way ids onto the demo trails by name) and the camera's `--look=<x>,<z>,<distance>` for screenshots.
10. **Docs**: this note → "implemented" with the measured KPI; `architecture.md` §13; `CLAUDE.md` layout, the
    new command discriminators and the controls line.

## Tests (`tests/Bikepark.Sim.Tests/FeatureTests.cs`)

- Catalog: the shipped file loads and validates; an unknown field, duplicate id or bad gradient window is rejected.
- Planner: valid placement; access path, unknown type, margins, overlap or gap, gradient window and missing bend
  each give their code; the preview and the command agree (same planner).
- Commands: place adds a sorted feature with a fresh id and bumps `WaysRevision`; remove deletes it; deleting the
  trail drops its features; riders on the trail keep their route.
- Effect: a double raises the covered segments' difficulty and the trail's `DifficultyScore` / rating; a flow
  rider scores a berm higher than a technical rider, and a drop the other way round.
- Determinism: extend `TestWorlds.Script()` with place and remove so `DeterminismTests` covers them.
- Persistence: round trip with features; a version-2 save without `features` / `trailFeatureTypes` still loads
  (new properties with defaults, so the version stays 2; such a save just has an empty catalog).

## Not in Phase 4.1

- Cost, wood, crew and build time: done in 4.2 ([crew_and_jobs.md](crew_and_jobs.md)).
- Wear on features and closures (5), crash chance per feature (6).
- Free-form placement off the trail line, feature size variants, editing a trail's shape under its features.
