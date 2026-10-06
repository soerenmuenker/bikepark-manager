# Game content

JSON content loaded by `Bikepark.Sim`. Conventions:

- camelCase property names; `//` comments and trailing commas are allowed.
- Unknown properties are **errors**, so typos fail on load.
- Money is in integer cents (`...Cents`), ratios in permille (0..1000), times of day in minutes since midnight.
- Every file is validated by `PersistenceTests.ShippedScenarios_LoadAndValidate`.

## scenarios/

One file per scenario (`ScenarioDefinition`): `id`, `name`, `description`, `seed`, `parkName`,
`startingMoneyCents`, `entryFeeCents`, `rules` (`ParkRules`), and optional scripted `commands`:

```json
"commands": [
  { "tick": 2880, "command": { "type": "setEntryFee", "feeCents": 2000 } }
]
```

The same `{tick, command}` list format is accepted by `Bikepark.SimRunner --commands <file>`.

`trailRules` (`TrailRules`, all optional): building limits and rider tuning. Gradients use the game's -10..+10
scale (1 point = 9°, 0 flat, ±10 vertical) in tenths: `pathMaxGradient` 40 / `pathSteepGradient` 20,
`trailMaxDropGradient` 80 / `trailSteepDropGradient` 50, `trailMaxClimbGradient` 30 / `trailSteepClimbGradient` 15
("max" = can't build, "steep" = warning). Also `pathGradingMeters` (gravel smoothing), `snapRadiusMeters`,
min/max length, corridor widths, climb/descent speeds in cm/s, energy costs, `tiredEnergy`.

`terrain` (`TerrainSettings`, all optional): `seed` (defaults to the scenario seed), `sizeMeters`,
`baseElevationCm`, `reliefCm`, `peakXMeters`, `peakZMeters`, `peakRadiusMeters`, `ridgeCount`,
`ridgeStrengthPermille`, `roughnessPermille`, `treeLineCm`, `forestCoveragePermille`.
Preview with `dotnet run --project src/Bikepark.SimRunner -- terrain --scenario <file> --out out/map.png --mode all`.

`liftTypesFile` (path relative to the scenario, e.g. `../lift_types.json`) loads the lift catalog; `liftTypes` can
also be given inline. `operators`: lift companies with `bikeAccessTiers` (`name`, `bikeCarrierPermille` strictly
increasing, `dailyFeeCents`). `liftRules` (`LiftRules`, all optional): walking speed, guests per car, parking
size per space, station reach, pad cut/fill limit and embankment gradient, queue grace/mood penalty, rest energy,
and the routing costs of a lift minute and a queue minute.

Starter Valley's tick-0 `commands` build what is already there: the gondola (`buildLift` with
`operatorId`/`initialTier`), the valley parking (`buildParkingLot`) and the old hiking route (`buildWay` with
`"origin": "scenario"`).

## lift_types.json

Lift catalog (`LiftType`): carrier capacity, `bikesPerCarrier`, `intervalSeconds`, `speedCmPerS`, min/max length,
`maxGradient` (tenths), station and default plateau size in meters, `corridorCm` cleared under the line.

## scripts/

Command scripts: a JSON list of `{tick, command}` (same format as scenario `commands`), used with
`--commands <file>` (SimRunner run and `terrain`), by tests, and by the game's "Demo trails" button.
`demo_lift_network.json` builds two trails (Flow Country, Red Rocket) from Starter Valley's plateau down to the
valley station. `demo_network.json` (Phase 2, for worlds without a lift; used by tests) builds a 2.7 km switchback gravel path from the base up to ~1010 m and two trails
(Blue Line, Red Rocket) on Starter Valley. Coordinates are in cm:

```json
{ "tick": 0, "command": { "type": "buildWay", "kind": "trail", "name": "Red Rocket", "points": [{ "x": 50913, "z": 59087 }, ...] } }
```
