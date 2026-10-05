# Bikepark Manager

RollerCoaster-Tycoon-style bikepark management game. Godot 4.7 .NET (C#) client on top of a pure,
deterministic C# simulation library. Full rationale: [docs/design/architecture.md](docs/design/architecture.md).

## Layout

- `src/Bikepark.Sim/` – simulation library (WorldState, Simulation tick loop, SimRandom, commands, events, save/load, scenarios, KPIs)
  - `Terrain/` – `TerrainGenerator` (integer-only heightmap + layers), `TerrainGrid` (queries), `TerrainScatter` (trees/rocks)
- `src/Bikepark.SimRunner/` – headless console runner: KPIs as JSON, `terrain` subcommand renders top-down PNG maps
- `tests/Bikepark.Sim.Tests/` – xUnit tests (determinism, commands, persistence, RNG, terrain)
- `game/` – Godot project (`Bikepark.csproj`, `scripts/SimHost.cs` drives the sim, `scripts/Hud.cs` debug HUD,
  `scripts/terrain/` chunked terrain view, `scripts/camera/RtsCamera.cs`, `shaders/terrain.gdshader`)
- `data/` – JSON content (scenarios)
- `Bikepark.sln` – root solution; Godot uses it via `project/solution_directory="../"`

## Commands

`dotnet` lives at `/usr/local/share/dotnet` and may not be on PATH: `export PATH=/usr/local/share/dotnet:$PATH`.

```bash
dotnet build Bikepark.sln
dotnet test Bikepark.sln
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 30 [--seed N] [--commands file.json] [--daily] [--save out.json]
# Terrain: top-down PNG maps + stats (use this to check terrain changes, no Godot needed)
dotnet run --project src/Bikepark.SimRunner -- terrain --scenario data/scenarios/starter_valley.json --out out/map.png --mode all [--seed N] [--scatter]
# Godot (from game/): compile, then run headless for ~2 game days
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --build-solutions --quit
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --fixed-fps 60 --quit-after 18000
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

## Game controls (debug build)

WASD/arrows/screen edge/middle-drag pan · wheel or +/- zoom · Q/E or right-drag orbit · F1 cycles terrain overlay
(natural / slope / surface) · HUD shows terrain data under the cursor.

## Conventions

- C# latest, nullable enabled, warnings are errors (`Directory.Build.props`), file-scoped namespaces.
- Every new sim feature needs tests; keep `DeterminismTests` green and extend `TestWorlds.Script()` when
  adding commands.
- `game/Bikepark.csproj` must pin `<TargetFramework>net10.0</TargetFramework>`. Godot may re-add `net8.0` when
  it touches the csproj; remove it if so.
