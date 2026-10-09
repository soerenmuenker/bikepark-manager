# Daily rhythm (Phase 5.1)

Status: implemented 2026-10-07. Rules that must not break are in `architecture.md` §15. Phase 5.2 (wear and weather)
builds on it.

Plan row: *"Continuous sim time (no closing-time freeze) with an RCT3-style day skip: dead hours between closing and the
next morning's pre-opening work are fast-forwarded automatically. A daily routine within operating hours: staff arrive
before opening and may still be working after closing; guests take a lunch peak around noon; morning, noon and evening
light moods."*

Decisions:
- **Phase 5 is split** like 4.1/4.2: 5.1 is the daily rhythm, 5.2 adds wear, weather, closures and maintenance.
- **The night skip is a host feature**, not sim state. The sim still simulates every minute (so the tier booking at
  opening, the day's books at 23:59 and determinism are untouched); the host only steps faster.
- **Staff day = shift + overtime**, no new task types: the crew is out before opening, and a worker stays after the
  shift only to finish a job that fits in the overtime left.
- **Closing is continuous**: riders already on a lap finish it, the gondola empties its queue, then the park is empty.

## What the player sees

- **Clock chip** shows the phase: `PRE-OPENING` (crew on shift or gondola warming up), `OPEN`, `LAST RIDES`,
  `CLOSED`, and `NIGHT ⏩` while the night is skipped.
- **Night skip.** Once the park is closed and empty and no worker is out, time races (300 game minutes per second)
  to the next crew shift or gondola warm-up, then continues at the speed it had. Paused games stay paused. Toggle:
  Game menu → "Skip nights" (debug arg `--no-night-skip`). The manual "Skip to opening" / "Turbo till closing" buttons
  stay.
- **Morning.** The crew starts at 07:30. The gondola runs empty cabins from 08:30. Guests arrive from 09:00 in a
  morning rush that tapers off; nobody arrives after ~16:30.
- **Lunch.** Guests plan lunch between 11:00 and 13:00 and take it at their next break, so most eat 12:00–13:30. They stop riding for a 30–50 min lunch break next to their bike, around where
  their last run ended (warm-tinted figures in a ring around the valley station). Riding and lift queues dip; lunch
  sales (€14) show in the Finances menu. Riders menu: "Lunch break" count.
- **Evening.** At 18:00 guests between laps go home; riders in the queue, on the gondola or on a trail finish their
  lap (`LAST RIDES`, 45 min). Workers whose job is nearly done keep going after their 17:30 shift end, up to 19:00.
- **Light.** The sun moves east → south → west (sunrise ~06:00, sunset ~21:00): warm low light in the morning and
  evening, white at noon, dark blue moonlight at night. Sky, fog and ambient light follow.

## Done-when KPI

```bash
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 3 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json --hourly
```

`--hourly` reports the last simulated day minute by minute, averaged per hour (day 3, seed 1337; measured before
Phase 5.2 turned on weather and wear, which shift the numbers slightly):

| Hour | Phase at start | Guests | On trails | Queuing | Eating | Runs finished | Crew working | Lift minutes |
|---|---|---|---|---|---|---|---|---|
| 07:00 | Night | 0 | 0 | 0 | 0 | 0 | 1.5 | 0 |
| 08:00 | PreOpening | 0 | 0 | 0 | 0 | 0 | 3 | 30 |
| 09:00 | Open | 33 | 8 | 17 | 0 | 119 | 3 | 60 |
| 10:00 | Open | 93 | 12 | 55 | 0 | 172 | 3 | 60 |
| 11:00 | Open | 131 | 16 | 60 | 10 | 239 | 3 | 60 |
| 12:00 | Open | 148 | 12 | 56 | 58 | **190** | 3 | 60 |
| 13:00 | Open | 148 | 12 | 60 | 45 | **187** | 3 | 60 |
| 14:00 | Open | 120 | 15 | 65 | 0 | 234 | 3 | 60 |
| 15:00 | Open | 82 | 11 | 58 | 0 | 174 | 3 | 60 |
| 16:00 | Open | 62 | 10 | 44 | 0 | 152 | 3 | 60 |
| 17:00 | Open | 44 | 10 | 26 | 0 | 148 | 1.5 | 60 |
| 18:00 | LastRides | 3 | 1 | 0 | 0 | 31 | 0 | 8 |
| 19:00 | Night | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

- **Lunch dip:** runs drop from 239/h to ~190/h at 12–14 h while ~50 guests eat, and recover to 234/h at 14 h.
- **Pre-opening:** crew out from 07:30, gondola warm-up 08:30–09:00.
- **Overtime after closing:** the crew finishes the Tabletop on Flow Country at day 2 18:41 and the Wall-ride on Red
  Rocket at day 5 18:29 (see the 10-day run in [crew_and_jobs.md](crew_and_jobs.md)).
- **Night:** from ~18:45 (last rides over, crew off) until 07:30 nothing is scheduled; the host skips it.
- Daily totals stay close to before: ~280 visitors/day (was ~285), ~€1,900/day lunch sales on top of tickets.

In Godot: `--demo` starts at 00:00 and the night skip jumps to the crew's 07:30 start; screenshots with
`--advance=465|750|1150|1380 --speed=0 --no-night-skip --screenshot=…` show the morning, noon, evening and night light.

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Timetable | `ParkRules`: `OpenMinute`, `CloseMinute`, `LastRideMinutes`, `LiftWarmupMinutes`; `CrewRules`: `WorkStart/EndMinute`, `OvertimeMinutes` | `ParkSchedule.Phase`, `IsQuiet`, `NextWakeTick`, `LiftRunning` |
| Arrivals | `ParkRules.ArrivalProfile` (`{minute, permille}` points, linear) | rate at a minute |
| Lunch | `ParkRules.Lunch*`; `Guest.LunchMinute`, `HadLunch`, `BusyUntilTick`; `RiderActivity.Eating` | – |
| Lunch sales | `FinanceState.TotalFoodCents` (in the revenue) | – |
| Night skip | – (host setting `SimHost.AutoSkipNights`) | – |
| Light | – | from the minute of the day (view) |

All new rules default to "off" (no last rides, no warm-up, flat arrivals, no lunch, no overtime), so older saves and
scenarios behave as before. New fields have defaults, so `SaveGame.CurrentVersion` stays 2 (an older save loads with
`LunchMinute = -1`: no lunch planned for guests already in the park).

### Rules

- **`ParkSchedule`** (`Systems/ParkSchedule.cs`) is the only place that knows the timetable. Phases: `Night`,
  `PreOpening` (crew at work or gondola warm-up before opening), `Open`, `LastRides`, `AfterHours` (crew still at work
  after closing). `IsQuiet` = `Night`. `NextWakeTick` = the next crew shift start (with crew) or warm-up start.
- **Arrivals** (`GuestArrivalSystem`): expected arrivals per minute = base × fee factor × profile. Same RNG pattern.
- **Lunch** (`GuestArrivalSystem.PlanLunch`, `RiderSystem.TryStartLunch`): at arrival a guest plans a lunch minute in
  the rest of the window, triangular (average of two uniform rolls), so the breaks peak in the middle. At their first
  break between laps after that minute (park open), they eat where they are for `lunchMin..MaxMinutes`, pay
  `lunchPriceCents` if they can, and gain `lunchEnergy` and `lunchHappiness`. A per-minute chance does not work here:
  riders are between laps for a single minute, most of their time is spent queuing.
- **Closing** (`GuestSystem`, `LiftSystem`): during the last rides, guests between laps (idle, eating, wandering) leave
  with `ParkClosed`; riders on a lap (incl. queue and gondola) carry on and leave at the bottom. The gondola dispatches
  while it has a queue. At `close + lastRideMinutes` everyone left goes home. Eating guests never leave mid-meal
  (only at closing).
- **Lifts** dispatch while `ParkSchedule.LiftRunning`: open, warm-up (empty cabins), or last rides with a queue or
  riders aboard. Booked tiers still switch at the opening minute.
- **Overtime** (`JobSystem`): in `[workEnd, workEnd + overtime)` there is no assignment; a worker keeps their job only
  while `RemainingMinutes × 1000 ≤ workers on it × speed × overtime minutes left`. Wages stay per day.

### View

- `SimHost`: `AutoSkipNights`, `NightSkipTicksPerSecond` (300); `IsSkippingNight`; skips restore the previous speed
  (manual skips end at 1x as before).
- `world/DayLight.cs` (node `DayLight` in `Main.tscn`): keyframes for sun energy and colour, sky top/horizon and
  ambient energy, smoothstepped; sun arc from the time; moon direction at night; shadows off below energy 0.2; fog
  colour = horizon desaturated by half.
- `RiderView` draws `Eating` guests in a golden-angle ring (10–28 m) around their spot, tinted warm. `LiftView` moves
  cabins per lift while it runs. `CrewView` shows idle workers only during the shift.

## Tests

`DailyRhythmTests`: phases and wake ticks at the edges (with and without crew), defaults keep the old day, rule
validation, the arrival profile (interpolation, no arrivals at 0, morning > afternoon), lunch (window, once per guest,
paid, energy, ends after the break), last rides (riders finish, park empty at 18:45, all leave as `ParkClosed`),
gondola warm-up, overtime finishing a nearly-done job and dropping a long one. `DeterminismTests` adds save/load at
12:30 (lunch) and 18:10 (last rides) in Starter Valley.
