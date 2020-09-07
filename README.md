**BombTrucks** allows players to spawn preset modular cars that explode when destroyed.

Bomb trucks are designed to be a more balanced alternative (or supplement) to airstrikes. Players can see a bomb truck coming, so they can kill the driver, destroy the truck before it gets to their base, or even steal the bomb truck for their own use since bomb trucks cannot be locked.

You can define unlimited bomb trucks in the config, each with a separate permission, per-player limit, cooldown and explosion settings (e.g., damage, radius).

## Commands

- `bombtruck` -- Spawn the "default" bomb truck.
- `bombtruck <truck name>` -- Spawn a named bomb truck.
- `bombtruck help` -- List bomb trucks you are allowed to spawn, your current utilization and cooldown for each if applicable.
- `givebombtruck <player> <truck name>` -- Spawn a bomb truck for the target player. If truck name is not provided, "default" will be used. Bomb trucks spawned this way do not contribute to player limits or cooldowns.

The `bombtruck` command also comes with the built-in aliases: `bt` and `boomer`.

The fuel system and engine modules of bomb trucks cannot be edited at a modular car lift. Instead, each bomb truck spawns with fuel and engine components. Additionally, when mounting a bomb truck, its fuel is automatically restored and its engine parts are repaied.

## Permissions

- `bombtrucks.spawn.<name>` -- Allows spawning a bomb truck of the given name. Must match a bomb truck name from the configuration file.
- `bombtrucks.give` -- Required to use the `givebombtruck` command.

## Configuration

Default configuration:

```json
{
  "BombTrucks": [
    {
      "AttachRFReceiver": true,
      "CooldownSeconds": 3600,
      "EnginePartsTier": 3,
      "ExplosionSettings": {
        "BlastRadiusMult": 1.0,
        "DamageMult": 4.0,
        "DensityCoefficient": 1.0,
        "DensityExponent": 1.8,
        "Radius": 5.0,
        "Speed": 10.0
      },
      "Modules": [
        "vehicle.1mod.cockpit.with.engine",
        "vehicle.2mod.fuel.tank"
      ],
      "Name": "default",
      "SpawnLimitPerPlayer": 3
    },
    {
      "AttachRFReceiver": true,
      "CooldownSeconds": 10800,
      "EnginePartsTier": 1,
      "ExplosionSettings": {
        "BlastRadiusMult": 1.0,
        "DamageMult": 6.0,
        "DensityCoefficient": 1.0,
        "DensityExponent": 1.6,
        "Radius": 15.0,
        "Speed": 10.0
      },
      "Modules": [
        "vehicle.1mod.engine",
        "vehicle.1mod.cockpit.armored",
        "vehicle.2mod.fuel.tank"
      ],
      "Name": "Nuke",
      "SpawnLimitPerPlayer": 1
    }
  ]
}
```

`BombTrucks` contains a list of bomb truck definitions. You can add as many as you want. Each one has a separate permission, cooldown and per-player limit.
- `Name` -- The name of the bomb truck. This will generate a permission like `bombtrucks.spawn.<name>` and allow it to be spawned with `bombtruck <name>`.
- `CooldownSeconds` -- The number of seconds the player must wait before spawning another bomb truck of that name. Cooldowns are persisted across server restarts but not across wipes.
- `SpawnLimitPerPlayer` -- The maximum number of bomb trucks of that name that each player is allowed to have in the world at the same time. Players can still effectively steal bomb trucks from other players regardless of this limit.
- `AttachRFReceiver` -- Whether to attach an RF receiver to the first cockpit module. The initial frequency is random but can be changed by interacting with the RF receiver. Broadcasting the frequency will detonate the bomb truck. Multiple bomb trucks can be set to the same frequency to detonate them simultaneously.
- `EnginePartsTier` (`1`, `2` or `3`) -- The quality of engine components that will be automatically added to the bomb truck's engine modules.
- `Modules` -- List of module item short names for modules to place on the car. Item short names can be found on the [uMod item list page](https://umod.org/documentation/games/rust/definitions).
- `ExplosionSettings` -- Settings to tune the bomb truck's explosion.
  - `Radius` -- **Increase with caution**. Radius of the overall explosion in meters. Increasing this will increase the number of individual rocket explosions according to the `Density*` settings, as well as the time for the overall explosion to complete (which is also affected by `Speed`).
  - `Speed` (Minimum `0.1`) -- Speed at which the explosion propagates in meters per second. For example, with `Radius: 20` and `Speed: 10`, the overall explosion will take 2 seconds to complete.
  - `DensityCoefficient` (Minimum `0.01`) -- Simple multiplier on the number of individual explosions for a given `Radius`. Applied after the calculation takes into account `DensityExponent`.
  - `DensityExponent` (`1.0` - `3.0`) -- **Increase with caution**. Exponential rate at which the number of individual explosions will scale by `Radius`. Recommended to adjust by incremental decimal values like `0.1` while experimenting.
    - Setting to `1` will dractically reduce the density of individual explosions as the overall explosion moves outward. This maintains a consistent number of explosions per second.
    - Setting to `3` will maintain a consistent density of individual explosions per meter, but will heavily lag or freeze clients for anything but a very small `Radius` (e.g., 5m). Explosions per second will ramp up very quickly.
  - `DamageMult` -- Damage multiplier of each individual rocket explosion. Recommended to increase this while you are reducing explosion density so that you can maintain a similar overall damage output.
  - `BlastRadiusMult` -- Blast radius of each individual rocket explosion. Only affects the radius at which nearby objects are damaged, not the visual radius of the explosion. Raising this can cause explosions to destroy objects clearly outside of their visual blast radius, which may look strange to players. Raising this is only recommended if you are having performance problems and want to reduce the number of individual explosions via the `Density*` and `Radius` settings while maintaining a similar overall blast radius.

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
  "Command.Spawn.Success.Frequency": "Detonate it with frequency: {0}",
  "Command.Help": "<color=orange>BombTruck Command Usages</color>",
  "Command.Help.Spawn.Default": "<color=yellow>bt</color> - Spawn a bomb truck",
  "Command.Help.Spawn.Named": "<color=yellow>bt {0}</color> - Spawn a {0} truck",
  "Command.Help.LimitUsage": "<color=yellow>{0}/{1}</color>",
  "Command.Help.RemainingCooldown": "<color=red>{0}</color>",
  "Command.Give.Error.Syntax": "Syntax: <color=yellow>givebombtruck <player> <truck name></color>",
  "Command.Give.Error.PlayerNotFound": "Error: Player <color=red>{0}</color> not found.",
  "Lift.Edit.Error": "Error: That vehicle may not be edited.",
  "Lock.Deploy.Error": "Error: Bomb trucks may not have locks."
}
```

## Hooks

#### CanSpawnBombTruck

- Called when a player tries to spawn a bomb truck.
- Returning `false` will prevent the default behavior.
- Returning `null` will result in the default behavior.

```csharp
object CanSpawnBombTruck(BasePlayer player)
```
