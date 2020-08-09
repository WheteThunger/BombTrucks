using System;

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust.Modular;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ModularCar;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Bomb Trucks", "WhiteThunder", "0.4.0")]
    [Description("Allow players to spawn bomb trucks.")]
    internal class BombTrucks : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin SpawnModularCar;

        private static BombTrucks BombTrucksInstance;

        private PluginData BombTrucksData;
        private PluginConfig BombTrucksConfig;

        private const string DefaultTruckConfigName = "default";

        private const string PermissionSpawnFormat = "bombtrucks.spawn.{0}";

        private const string PrefabExplosiveRocket = "assets/prefabs/ammo/rocket/rocket_basic.prefab";

        private bool PluginUnloaded = false;

        #endregion

        #region Hooks

        private void Init()
        {
            BombTrucksInstance = this;

            BombTrucksConfig = Config.ReadObject<PluginConfig>();
            BombTrucksData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);

            foreach (var truckConfig in BombTrucksConfig.BombTrucks)
                permission.RegisterPermission(GetSpawnPermission(truckConfig.Name), this);
        }

        private void OnServerInitialized() =>
            CleanStaleTruckData();

        private void Unload()
        {
            // To signal coroutines to stop early (simpler than keeping track of them)
            PluginUnloaded = true;
        }

        private void OnNewSave() => ClearData();

        private void OnEntityDeath(ModularCar car)
        {
            if (IsBombTruck(car) && car.OwnerID != 0)
            {
                var playerConfig = GetPlayerData(car.OwnerID.ToString());

                var netID = car.net.ID;
                var truckName = playerConfig.FindTruck(netID)?.Name;
                if (truckName == null)
                {
                    PrintWarning("Unable to determine truck name on death.");
                    return;
                }

                var truckConfig = GetTruckConfig(truckName);
                if (truckConfig == null)
                {
                    PrintError("Unable to detonate '{0}' truck because its configuration is missing.", truckName);
                    return;
                }

                playerConfig.RemoveTruck(netID);

                // Clean up the engine parts
                foreach (var module in car.AttachedModuleEntities)
                {
                    var engineStorage = (module as VehicleModuleEngine)?.GetContainer() as EngineStorage;
                    engineStorage?.inventory?.Kill();
                }

                var carPosition = car.CenterPoint();
                car.Kill();
                DetonateExplosion(truckConfig.ExplosionSpec, carPosition);
            }
        }

        private void OnEntityKill(ModularCar car)
        {
            // This handles the case when the entity was killed without dying first
            if (IsBombTruck(car) && car.OwnerID != 0)
                GetPlayerData(car.OwnerID.ToString()).RemoveTruck(car.net.ID);
        }

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            var car = (mountable as BaseVehicleMountPoint)?.GetVehicleParent() as ModularCar;
            if (car == null || !IsBombTruck(car)) return;

            // Repair engine parts and restore fuel since the containers are locked
            RepairEngineParts(car);
            car.fuelSystem.AdminFillFuel();
        }

        object CanLootEntity(BasePlayer player, ModularCarGarage carLift)
        {
            if (!carLift.PlatformIsOccupied) return null;

            var car = carLift.carOccupant;
            if (car != null && IsBombTruck(car))
            {
                ChatMessage(player, "Lift.Edit.Error");
                return false;
            }

            return null;
        }

        object CanDeployCarCodeLock(ModularCar car, BasePlayer player)
        {
            if (!IsBombTruck(car)) return null;

            if (player != null)
                player.ChatMessage(GetMessage(player.IPlayer, "CodeLock.Deploy.Error"));

            return false;
        }

        #endregion

        #region Commands

        [Command("bombtruck", "bt", "boomer")]
        private void SpawnBombTruckCommand(IPlayer player, string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                SubCommand_SpawnBombTruck(player, args);
                return;
            }

            switch (args[0])
            {
                case "help":
                    SubCommand_Help(player, args.Skip(1).ToArray());
                    return;

                default:
                    SubCommand_SpawnBombTruck(player, args);
                    return;
            }
        }

        private void SubCommand_Help(IPlayer player, string[] args)
        {
            var allowedTruckConfigs = BombTrucksConfig.BombTrucks
                .Where(config => permission.UserHasPermission(player.Id, GetSpawnPermission(config.Name)))
                .OrderBy(truckConfig => truckConfig.Name, Comparer<string>.Create(SortTruckNames))
                .ToList();

            if (allowedTruckConfigs.Count == 0)
            {
                ReplyToPlayer(player, "Generic.Error.NoPermission");
                return;
            }

            var messages = new List<string> { GetMessage(player, "Command.Help") };

            messages.AddRange(allowedTruckConfigs.Select(truckConfig =>
            {
                var message = (truckConfig.Name == DefaultTruckConfigName) ?
                    GetMessage(player, "Command.Help.Spawn.Default") :
                    GetMessage(player, "Command.Help.Spawn.Named", truckConfig.Name);

                var limitUsage = GetPlayerData(player.Id).GetTruckCount(truckConfig.Name);
                message += " - " + GetMessage(player, "Command.Help.LimitUsage", limitUsage, truckConfig.SpawnLimit);

                var remainingCooldown = GetPlayerRemainingCooldownSeconds(player.Id, truckConfig);
                if (remainingCooldown > 0)
                    message += " - " + GetMessage(player, "Command.Help.RemainingCooldown", FormatTime(remainingCooldown));

                return message;
            }));

            player.Reply(string.Join("\n", messages));
        }

        private void SubCommand_SpawnBombTruck(IPlayer player, string[] args)
        {
            var truckName = DefaultTruckConfigName;

            if (args.Length > 0)
                truckName = args[0];

            TruckConfig truckConfig;
            if (!VerifyTruckConfigDefined(player, truckName, out truckConfig) ||
                !VerifyPermissionAny(player, GetSpawnPermission(truckName)) ||
                !VerifyOffCooldown(player, truckConfig) ||
                !VerifyBelowTruckLimit(player, truckConfig) ||
                !VerifyNotBuildingBlocked(player) ||
                !VerifyNotMounted(player) ||
                !VerifyOnGround(player) ||
                !VerifyNotParented(player)) return;

            SpawnBombTruck(player.Object as BasePlayer, truckConfig);
            ReplyToPlayer(player, "Command.Spawn.Success");
        }

        #endregion

        #region Helper Methods - Command Checks

        private bool VerifyPermissionAny(IPlayer player, params string[] permissionNames)
        {
            foreach (var perm in permissionNames)
            {
                if (!permission.UserHasPermission(player.Id, perm))
                {
                    ReplyToPlayer(player, "Generic.Error.NoPermission");
                    return false;
                }
            }
            return true;
        }

        private bool VerifyTruckConfigDefined(IPlayer player, string truckName, out TruckConfig truckConfig)
        {
            truckConfig = GetTruckConfig(truckName);
            if (truckConfig == null)
            {
                ReplyToPlayer(player, "Command.Spawn.Error.NotFound", truckName);
                return false;
            }
            return true;
        }

        private bool VerifyNotBuildingBlocked(IPlayer player)
        {
            if ((player.Object as BasePlayer).IsBuildingBlocked())
            {
                ReplyToPlayer(player, "Generic.Error.BuildingBlocked");
                return false;
            }
            return true;
        }

        private bool VerifyOffCooldown(IPlayer player, TruckConfig truckConfig)
        {
            var secondsRemaining = GetPlayerRemainingCooldownSeconds(player.Id, truckConfig);
            if (secondsRemaining > 0)
            {
                ReplyToPlayer(player, "Generic.Error.Cooldown", FormatTime(secondsRemaining));
                return false;
            }
            return true;
        }

        private bool VerifyNotMounted(IPlayer player)
        {
            if ((player.Object as BasePlayer).isMounted)
            {
                ReplyToPlayer(player, "Command.Spawn.Error.Mounted");
                return false;
            }
            return true;
        }

        private bool VerifyOnGround(IPlayer player)
        {
            if (!(player.Object as BasePlayer).IsOnGround())
            {
                ReplyToPlayer(player, "Command.Spawn.Error.NotOnGround");
                return false;
            }
            return true;
        }

        private bool VerifyNotParented(IPlayer player)
        {
            if ((player.Object as BasePlayer).HasParent())
            {
                ReplyToPlayer(player, "Command.Spawn.Error.Generic");
                return false;
            }
            return true;
        }

        private bool VerifyBelowTruckLimit(IPlayer player, TruckConfig truckConfig)
        {
            if (GetPlayerData(player.Id).GetTruckCount(truckConfig.Name) >= truckConfig.SpawnLimit)
            {
                ReplyToPlayer(player, "Command.Spawn.Error.TooManyOfType", truckConfig.SpawnLimit);
                return false;
            }
            return true;
        }

        #endregion

        #region Helper Methods - Misc

        private string GetSpawnPermission(string truckName) =>
            string.Format(PermissionSpawnFormat, truckName);

        private int SortTruckNames(string a, string b) =>
            a.ToLower() == DefaultTruckConfigName ? -1 :
            b.ToLower() == DefaultTruckConfigName ? 1 :
            a.CompareTo(b);

        private bool IsBombTruck(ModularCar car) => 
            BombTrucksData.PlayerData.Any(item => item.Value.BombTrucks.Any(data => data.ID == car.net.ID));

        private void SpawnBombTruck(BasePlayer player, TruckConfig truckConfig)
        {
            var car = SpawnModularCar.Call("API_SpawnPresetCar", player, new Dictionary<string, object>
            {
                ["EnginePartsTier"] = truckConfig.EnginePartsTier,
                ["FuelAmount"] = -1,
                ["Modules"] = new object[] { 
                    "vehicle.1mod.cockpit.with.engine",
                    "vehicle.2mod.fuel.tank"
                },
            }, new Action<ModularCar>(OnCarReady)) as ModularCar;

            if (car == null) return;

            UpdatePlayerCooldown(player.UserIDString, truckConfig.Name);
            GetPlayerData(player.UserIDString).BombTrucks.Add(new PlayerTruckData
            { 
                Name = truckConfig.Name,
                ID = car.net.ID 
            });
            SaveData();
        }

        private void OnCarReady(ModularCar car)
        {
            car.fuelSystem.GetFuelContainer().SetFlag(BaseEntity.Flags.Locked, true);

            foreach (var module in car.AttachedModuleEntities)
            {
                var storageContainer = (module as VehicleModuleStorage)?.GetContainer();
                if (storageContainer != null)
                    storageContainer.inventory.SetLocked(true);
            }
        }

        private void RepairEngineParts(ModularCar car)
        {
            foreach (var module in car.AttachedModuleEntities)
            {
                var engineStorage = (module as VehicleModuleEngine)?.GetContainer() as EngineStorage;
                if (engineStorage == null) continue;

                for (var i = 0; i < engineStorage.inventory.capacity; i++)
                {
                    var item = engineStorage.inventory.GetSlot(i);
                    if (item != null)
                        item.condition = item.maxCondition;
                }

                // This makes sure the engine detects repaired broken parts
                engineStorage.RefreshLoadoutData();
            }
        }

        private double GetPlayerRemainingCooldownSeconds(string userID, TruckConfig truckConfig)
        {
            var playerCooldowns = GetPlayerData(userID).Cooldowns;
            if (!playerCooldowns.ContainsKey(truckConfig.Name))
                return 0;

            long lastUsed = playerCooldowns[truckConfig.Name];
            long cooldownDuration = truckConfig.CooldownSeconds;
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return lastUsed + cooldownDuration - currentTime;
        }

        private void UpdatePlayerCooldown(string userID, string truckName) =>
            GetPlayerData(userID).UpdateCooldown(truckName, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        private string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString("g");

        #endregion

        #region Explosions

        private IEnumerator ExplosionCoroutine(ExplosionSpec spec, Vector3 origin)
        {
            float rocketTravelTime = 0.3f;
            double totalTime = spec.Radius / spec.Speed;
            int numExplosions = (int)Math.Ceiling(spec.DensityCoefficient * Math.Pow(spec.Radius, spec.DensityExponent));

            float timeElapsed = 0;
            double prevDistance = 0;

            for (var i = 1; i <= numExplosions; i++)
            {
                if (PluginUnloaded) yield break;

                double timeFraction = timeElapsed / totalTime;
                double stepDistance = spec.Radius * timeFraction;

                double stepStartDistance = prevDistance;
                double stepEndDistance = stepDistance;

                double rocketDistance = Core.Random.Range(stepStartDistance, stepEndDistance);
                double rocketSpeed = rocketDistance / rocketTravelTime;

                Vector3 rocketVector = MakeRandomDomeVector();

                // Skip over some space to prevent rockets from colliding with each other
                Vector3 skipDistance = rocketVector;

                rocketVector *= Convert.ToSingle(rocketSpeed);
                FireRocket(PrefabExplosiveRocket, origin + skipDistance, rocketVector + skipDistance, rocketTravelTime, spec.BlastRadiusMult, spec.DamageMult);

                float timeToNext = Convert.ToSingle(Math.Pow(i / spec.DensityCoefficient, 1.0 / spec.DensityExponent) / spec.Speed - timeElapsed);

                yield return new WaitForSeconds(timeToNext);
                prevDistance = stepDistance;
                timeElapsed += timeToNext;
            }
        }

        private void DetonateExplosion(ExplosionSpec spec, Vector3 origin) =>
            ServerMgr.Instance.StartCoroutine(ExplosionCoroutine(spec, origin));

        private Vector3 MakeRandomDomeVector() =>
            new Vector3(Core.Random.Range(-1f, 1f), Core.Random.Range(0, 1f), Core.Random.Range(-1f, 1f)).normalized;

        private void FireRocket(string rocketPrefab, Vector3 origin, Vector3 direction, float time, float damageRadiusMult = 1.0f, float damageMult = 1.0f)
        {
            var rocket = GameManager.server.CreateEntity(rocketPrefab, origin);
            var rocketProjectile = rocket.GetComponent<ServerProjectile>();
            var rocketExplosion = rocket.GetComponent<TimedExplosive>();

            rocketProjectile.gravityModifier = 0;
            rocketExplosion.explosionRadius *= damageRadiusMult;
            rocketExplosion.timerAmountMin = time;
            rocketExplosion.timerAmountMax = time;

            for (var i = 0; i < rocketExplosion.damageTypes.Count; i++)
                rocketExplosion.damageTypes[i].amount *= damageMult;

            rocket.SendMessage("InitializeVelocity", direction);
            rocket.Spawn();
        }

        #endregion

        #region Data Management

        private PlayerData GetPlayerData(string userID)
        {
            if (!BombTrucksData.PlayerData.ContainsKey(userID))
                BombTrucksData.PlayerData.Add(userID, new PlayerData());

            return BombTrucksData.PlayerData[userID];
        }

        private void CleanStaleTruckData()
        {
            var cleanedCount = 0;

            // Clean up any stale truck IDs in case of a data file desync
            foreach (var playerData in BombTrucksData.PlayerData.Values)
                cleanedCount += playerData.BombTrucks.RemoveAll(truckData => (BaseNetworkable.serverEntities.Find(truckData.ID) as ModularCar) == null);

            if (cleanedCount > 0)
                SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, BombTrucksData);

        private void ClearData() => Interface.Oxide.DataFileSystem.WriteObject(Name, new PluginData());

        internal class PluginData
        {
            [JsonProperty("PlayerData")]
            public Dictionary<string, PlayerData> PlayerData = new Dictionary<string, PlayerData>();
        }

        internal class PlayerData
        {
            public List<PlayerTruckData> BombTrucks = new List<PlayerTruckData>();
            public Dictionary<string, long> Cooldowns = new Dictionary<string, long>();

            public void UpdateCooldown(string truckName, long time)
            {
                if (Cooldowns.ContainsKey(truckName))
                    Cooldowns[truckName] = time;
                else
                    Cooldowns.Add(truckName, time);
            }

            public int GetTruckCount(string truckName) =>
                BombTrucks.Count(truckData => truckData.Name == truckName);

            public PlayerTruckData FindTruck(uint netID) =>
                BombTrucks.FirstOrDefault(truckData => truckData.ID == netID);

            public void RemoveTruck(uint netID)
            {
                BombTrucks.RemoveAll(truckData => truckData.ID == netID);
                BombTrucksInstance.SaveData();
            }
        }

        internal class PlayerTruckData
        {
            public string Name;
            public uint ID;
        }

        #endregion

        #region Configuration

        private TruckConfig GetTruckConfig(string truckName) =>
            BombTrucksConfig.BombTrucks.FirstOrDefault(truckConfig => truckConfig.Name.ToLower() == truckName.ToLower());

        internal class PluginConfig
        {
            [JsonProperty("BombTrucks")]
            public TruckConfig[] BombTrucks = new TruckConfig[0];
        }

        internal class TruckConfig
        {
            private int _enginePartsTier = 1;

            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("EnginePartsTier")]
            public int EnginePartsTier
            {
                get { return _enginePartsTier; }
                set { _enginePartsTier = Math.Min(Math.Max(value, 1), 3); }
            }

            [JsonProperty("CooldownSeconds")]
            public long CooldownSeconds = 0;

            [JsonProperty("SpawnLimitPerPlayer")]
            public int SpawnLimit = 1;

            [JsonProperty("ExplosionSettings")]
            public ExplosionSpec ExplosionSpec = new ExplosionSpec();
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                BombTrucks = new TruckConfig[]
                {
                    new TruckConfig
                    {
                        Name = DefaultTruckConfigName,
                        EnginePartsTier = 3,
                        CooldownSeconds = 3600,
                        SpawnLimit = 3,
                        ExplosionSpec = new ExplosionSpec
                        {
                            BlastRadiusMult = 1,
                            DamageMult = 4.0f,
                            DensityCoefficient = 1,
                            DensityExponent = Math.Round(1.8f * 100) / 100,
                            Radius = 5,
                            Speed = 10,
                        }
                    },
                    new TruckConfig
                    {
                        Name = "MiniNuke",
                        EnginePartsTier = 2,
                        CooldownSeconds = 7200,
                        SpawnLimit = 2,
                        ExplosionSpec = new ExplosionSpec
                        {
                            BlastRadiusMult = 1,
                            DamageMult = 5.0f,
                            DensityCoefficient = 1,
                            DensityExponent = Math.Round(1.7f * 100) / 100,
                            Radius = 10,
                            Speed = 10,
                        }
                    },
                    new TruckConfig
                    {
                        Name = "Nuke",
                        EnginePartsTier = 1,
                        CooldownSeconds = 10800,
                        SpawnLimit = 1,
                        ExplosionSpec = new ExplosionSpec
                        {
                            BlastRadiusMult = 1,
                            DamageMult = 6.0f,
                            DensityCoefficient = 1,
                            DensityExponent = Math.Round(1.6f * 100) / 100,
                            Radius = 15,
                            Speed = 10,
                        }
                    }
                }
            };
        }

        internal class ExplosionSpec
        {
            private double _speed = 10;
            private double _densityCoefficient = 1;
            private double _densityExponent = 2;

            [JsonProperty("Radius")]
            public double Radius = 10;

            [JsonProperty("DensityCoefficient")]
            public double DensityCoefficient
            {
                get { return _densityCoefficient; }
                set { _densityCoefficient = Math.Max(value, 0.01); }
            }

            [JsonProperty("DensityExponent")]
            public double DensityExponent
            {
                get { return _densityExponent; }
                set { _densityExponent = Math.Min(Math.Max(value, 1), 3); }
            }

            [JsonProperty("Speed")]
            public double Speed
            {
                get { return _speed; }
                set { _speed = Math.Max(value, 0.1); }
            }

            [JsonProperty("BlastRadiusMult")]
            public float BlastRadiusMult = 1;

            [JsonProperty("DamageMult")]
            public float DamageMult = 1;
        }

        protected override void LoadDefaultConfig() => Config.WriteObject(GetDefaultConfig(), true);

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, player.Id);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Generic.Error.NoPermission"] = "You don't have permission to use this command.",
                ["Generic.Error.BuildingBlocked"] = "Error: Cannot do that while building blocked.",
                ["Generic.Error.Cooldown"] = "Please wait <color=red>{0}</color> and try again.",
                ["Command.Spawn.Error.NotFound"] = "Truck <color=red>{0}</color> does not exist.",
                ["Command.Spawn.Error.TooManyOfType"] = "Error: You may not have more than <color=red>{0}</color> of that truck.",
                ["Command.Spawn.Error.Mounted"] = "You cannot do that while mounted.",
                ["Command.Spawn.Error.NotOnGround"] = "You must be on the ground to do that.",
                ["Command.Spawn.Error.Generic"] = "You cannot do that right now.",
                ["Command.Spawn.Success"] = "Here is your bomb truck.",
                ["Command.Help"] = "<color=orange>BombTruck Command Usages</color>",
                ["Command.Help.Spawn.Default"] = "<color=yellow>bt</color> - Spawn a bomb truck",
                ["Command.Help.Spawn.Named"] = "<color=yellow>bt {0}</color> - Spawn a {0} truck",
                ["Command.Help.LimitUsage"] = "<color=yellow>{0}/{1}</color>",
                ["Command.Help.RemainingCooldown"] = "<color=red>{0}</color>",
                ["Lift.Edit.Error"] = "Error: That vehicle may not be edited.",
                ["CodeLock.Deploy.Error"] = "Error: Bomb trucks may not have code locks.",
            }, this, "en");
        }

        #endregion
    }
}
