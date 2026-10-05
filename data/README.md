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
