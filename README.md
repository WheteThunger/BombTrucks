**BombTrucks** allows players to spawn modular cars that explode when destroyed.

## Commands

- `bombtruck` -- Spawn the "default" bomb truck.
- `bombtruck <name>` -- Spawn a named bomb truck.

You can also use the `bt` and `boomer` aliases.

The fuel system and engine modules of bomb trucks cannot be edited at a modular car lift. Instead, each bomb truck spawns with fuel and engine components. Additionally, when mounting a bomb truck, its fuel is automatically restored and its engine parts are repaied.

## Permissions

- `bombtrucks.spawn.<name>` -- Allows spawning a bomb truck of the given name. Must match a bomb truck name from the configuration file.
- `bombtrucks.underwater` -- Allows bomb trucks you spawn to be driven underwater.

## Configuration

```json
{
  "BombTrucks": [
    {
      "Name": "default",
      "CooldownSeconds": 3600,
      "SpawnLimitPerPlayer": 3,
      "EnginePartsTier": 3,
      "ExplosionSettings": {
        "BlastRadiusMult": 1.0,
        "DamageMult": 4.0,
        "Density": 0.4,
        "Radius": 5.0,
        "Speed": 7.5
      }
    },
    {
      "Name": "MiniNuke",
      "CooldownSeconds": 7200,
      "SpawnLimitPerPlayer": 2,
      "EnginePartsTier": 2,
      "ExplosionSettings": {
        "BlastRadiusMult": 1.0,
        "DamageMult": 5.0,
        "Density": 0.3,
        "Radius": 10.0,
        "Speed": 7.5
      }
    },
    {
      "Name": "Nuke",
      "CooldownSeconds": 10800,
      "SpawnLimitPerPlayer": 1,
      "EnginePartsTier": 1,
      "ExplosionSettings": {
        "BlastRadiusMult": 1.0,
        "DamageMult": 6.0,
        "Density": 0.25,
        "Radius": 15.0,
        "Speed": 7.5
      }
    }
  ],
  "DisableSpawnLimitEnforcement": true
}
```

- `BombTrucks` -- List of bomb truck definitions. You can add as many as you want. Each one has a separate permission, cooldown and per-player limit.
  - `Name` -- The name of the bomb truck. This will generate a permission like `bombtrucks.spawn.<name>` and allow it to be spawned with `bombtruck <name>`.
  - `CooldownSeconds` -- The number of seconds the player must wait before spawning another bomb truck of that type.
  - `SpawnLimitPerPlayer` -- The maximum number of bomb trucks of that name that each player is allowed to have spawned in at the same time.
  - `EnginePartsTier` (1-3) -- The quality of engine components that will be automatically added to the bomb truck's engine modules.
  - `ExplosionSettings` -- Settings to tune the bomb truck's explosion.
    - `Radius` -- Radius of the explosion in meters. Increasing this will increase the number of individual rocket explosions and also the time for the overall explosion to complete..
    - `Speed` -- Speed at which the explosion propagates in meters per second.
    - `Density` -- Density of the explosion. This affects the number of individual explosions that occur for a given `Radius`. **Increase with caution**.
    - `DamageMult` -- Damage multiplier of each individual rocket explosion.
    - `BlastRadiusMult` -- Blast radius of each individual rocket explosion. Only affects the radius at which nearby objects are damaged, not the visual radius of the explosion.
- `DisableSpawnLimitEnforcement` (`true` or `false`) -- Set to `true` to keep all modular cars between server restarts. Otherwise, the game will delete extra cars beyond the server's configured modular car population, which *may* delete player cars depending on how recently they were spawned.

## Localization
```json
{
  "Generic.Error.NoPermission": "You don't have permission to use this command.",
  "Generic.Error.BuildingBlocked": "Error: Cannot do that while building blocked.",
  "Generic.Error.Cooldown": "Please wait <color=red>{0}</color> and try again.",
  "Command.Spawn.Error.NotFound": "Truck <color=red>{0}</color> does not exist.",
  "Command.Spawn.Error.TooManyOfType": "Error: You may not have more than <color=red>{0}</color> of that truck.",
  "Command.Spawn.Error.Mounted": "You cannot do that while mounted.",
  "Command.Spawn.Error.NotOnGround": "You must be on the ground to do that.",
  "Command.Spawn.Error.Generic": "You cannot do that right now.",
  "Command.Spawn.Success": "Here is your bomb truck.",
  "Lift.Edit.Error": "Error: That vehicle cannot be edited.",
  "Command.Help": "<color=orange>BombTruck Command Usages</color>",
  "Command.Help.Spawn.Default": "<color=yellow>bt</color> - Spawn a bomb truck",
  "Command.Help.Spawn.Named": "<color=yellow>bt {0}</color> - Spawn a {0} truck",
  "Command.Help.LimitUsage": "<color=yellow>{0}/{1}</color>",
  "Command.Help.RemainingCooldown": "<color=red>{0}</color>"
}
```