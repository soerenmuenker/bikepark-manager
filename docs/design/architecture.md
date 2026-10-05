# Bikepark Manager – Architecture

Status: living document. Last updated 2026-10-05.

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
  2. run all systems in a **fixed, explicit order** (`ParkHours → GuestArrival → Guest → Finance`);
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

## Open questions / next steps

- Trail building model (grid vs. spline), terrain representation.
- Guest pathfinding and needs model; keep allocation-free and integer-based.
- Export pipeline for `/data`; possibly embed content as resources.
- Command-stream recording in `SimHost` for replays and bug reports.
