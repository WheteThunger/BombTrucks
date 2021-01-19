using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust.Modular;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Bomb Trucks", "WhiteThunder", "0.7.2")]
    [Description("Allow players to spawn bomb trucks.")]
    internal class BombTrucks : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin SpawnModularCar, NoEscape;

        private static BombTrucks BombTrucksInstance;

        private StoredData PluginData;
        private Configuration PluginConfig;

        private const int RfReservedRangeMin = 4760;
        private const int RfReservedRangeMax = 4790;

        private const string DefaultTruckConfigName = "default";

        private const string PermissionSpawnFormat = "bombtrucks.spawn.{0}";
        private const string PermissionGiveBombTruck = "bombtrucks.give";

        private const string PrefabExplosiveRocket = "assets/prefabs/ammo/rocket/rocket_basic.prefab";
        private const string PrefabRfReceiver = "assets/prefabs/deployable/playerioents/gates/rfreceiver/rfreceiver.prefab";

        private readonly Vector3 RfReceiverPosition = new Vector3(0, -0.1f, 0);
        private readonly Quaternion RfReceiverRotation = Quaternion.Euler(0, 180, 0);

        private readonly RFReceiverManager ReceiverManager = new RFReceiverManager();

        private bool PluginUnloaded = false;

        #endregion

        #region Hooks

        private void Init()
        {
            BombTrucksInstance = this;

            PluginData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);

            foreach (var truckConfig in PluginConfig.BombTrucks)
                permission.RegisterPermission(GetSpawnPermission(truckConfig.Name), this);

            permission.RegisterPermission(PermissionGiveBombTruck, this);
        }

        private void OnServerInitialized(bool initialBoot)
        {
            VerifyDependencies();
            CleanStaleTruckData();
            InitializeReceivers(initialBoot);
        }

        private void Unload()
        {
            BombTrucksInstance = null;

            // To signal coroutines to stop early (simpler than keeping track of them)
            PluginUnloaded = true;
        }

        private void OnNewSave() => ClearData();

        private void OnEntityDeath(ModularCar car)
        {
            if (IsBombTruck(car) && car.OwnerID != 0)
                DetonateBombTruck(car);
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

        // From older CarCodeLocks plugin
        object CanDeployCarCodeLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        object CanDeployVehicleCodeLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        object CanDeployVehicleKeyLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        private object CanLockVehicle(ModularCar car, BasePlayer player)
        {
            if (!IsBombTruck(car)) return null;

            if (player != null)
                player.ChatMessage(GetMessage(player.IPlayer, "Lock.Deploy.Error"));

            return false;
        }

        // Prevent receivers from taking damage
        private object OnEntityTakeDamage(RFReceiver receiver, HitInfo info)
        {
            var car = GetReceiverCar(receiver);
            if (car == null || !IsBombTruck(car)) return null;
            return false;
        }

        private void OnEntityKill(RFReceiver receiver) =>
            ReceiverManager.RemoveReceiver(receiver.GetFrequency(), receiver);

        private void OnRfBroadcasterAdded(IRFObject obj, int frequency) =>
            ReceiverManager.DetonateFrequency(frequency);

        private void OnRfListenerAdded(IRFObject obj, int frequency)
        {
            var receiver = obj as RFReceiver;
            if (receiver == null) return;

            // Need to delay checking for the car since the receiver is spawned unparented to mitigate rendering bug
            NextTick(() =>
            {
                var car = GetReceiverCar(receiver);
                if (car == null || !IsBombTruck(car)) return;

                ReceiverManager.AddReceiver(frequency, receiver);
            });
        }

        private void OnRfListenerRemoved(IRFObject obj, int frequency)
        {
            var receiver = obj as RFReceiver;
            if (receiver == null) return;

            var car = GetReceiverCar(receiver);
            if (car == null || !IsBombTruck(car)) return;

            ReceiverManager.RemoveReceiver(frequency, receiver);
        }

        // This hook is exposed by Claim Vehicle Ownership (ClaimVehicle).
        private object OnVehicleUnclaim(BasePlayer player, ModularCar car)
        {
            if (car == null || !IsBombTruck(car)) return null;

            ChatMessage(player, "Unclaim.Error");
            return false;
        }

        // This hook is exposed by Modular Car Turrets (CarTurrets).
        private object OnCarAutoTurretDeploy(BaseVehicleModule module, BasePlayer player)
        {
            if (module == null) return null;

            var car = module.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car)) return null;

            if (player != null)
                ChatMessage(player, "AutoTurret.Deploy.Error");

            return false;
        }

        // This hook is exposed by No Engine Parts (NoEngineParts).
        private object OnEngineLoadoutOverride(EngineStorage engineStorage)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car)) return null;

            return false;
        }

        // This hook is exposed by Engine Parts Durability (EnginePartsDurability).
        private object OnEngineDamageMultiplierChange(EngineStorage engineStorage, float desiredMultiplier)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car)) return null;

            return false;
        }

        // This hook is exposed by Auto Engine Parts (AutoEngineParts).
        private object OnEngineStorageFill(EngineStorage engineStorage, int enginePartsTier)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car)) return null;

            return false;
        }

        #endregion

        #region Commands

        [Command("bombtruck", "bt", "boomer")]
        private void SpawnBombTruckCommand(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer) return;

            if (args.Length == 0)
            {
                SubCommand_SpawnBombTruck(player, args);
                return;
            }

            switch (args[0].ToLower())
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
            var allowedTruckConfigs = PluginConfig.BombTrucks
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

            var basePlayer = player.Object as BasePlayer;

            TruckConfig truckConfig;
            if (!VerifyTruckConfigDefined(player, truckName, out truckConfig) ||
                !VerifyPermissionAny(player, GetSpawnPermission(truckName)) ||
                !VerifyOffCooldown(player, truckConfig) ||
                !VerifyBelowTruckLimit(player, truckConfig) ||
                !VerifyNotBuildingBlocked(player) ||
                !VerifyNotMounted(player) ||
                !VerifyOnGround(player) ||
                !VerifyNotParented(player) ||
                !VerifyNotRaidOrCombatBlocked(basePlayer) ||
                SpawnWasBlocked(basePlayer)) return;

            SpawnBombTruck(basePlayer, truckConfig, shouldTrack: true);
        }

        [Command("givebombtruck")]
        private void GiveBombTruckCommand(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer && !VerifyPermissionAny(player, PermissionGiveBombTruck)) return;

            if (args.Length < 1)
            {
                ReplyToPlayer(player, "Command.Give.Error.Syntax");
                return;
            }

            var playerNameOrIdArg = args[0];

            var truckName = DefaultTruckConfigName;
            if (args.Length > 1)
                truckName = args[1];

            var targetPlayer = BasePlayer.Find(playerNameOrIdArg);
            if (targetPlayer == null)
            {
                ReplyToPlayer(player, "Command.Give.Error.PlayerNotFound", playerNameOrIdArg);
                return;
            }

            TruckConfig truckConfig;
            if (!VerifyTruckConfigDefined(player, truckName, out truckConfig))
                return;

            SpawnBombTruck(targetPlayer, truckConfig, shouldTrack: false);
        }

        #endregion

        #region Helper Methods - Command Checks

        private bool SpawnWasBlocked(BasePlayer player)
        {
            object hookResult = Interface.CallHook("CanSpawnBombTruck", player);
            return hookResult is bool && (bool)hookResult == false;
        }

        private bool VerifyNotRaidOrCombatBlocked(BasePlayer player)
        {
            if (!PluginConfig.NoEscapeSettings.CanSpawnWhileRaidBlocked && IsRaidBlocked(player))
            {
                ChatMessage(player, "Command.Spawn.Error.RaidBlocked");
                return false;
            }

            if (!PluginConfig.NoEscapeSettings.CanSpawnWhileCombatBlocked && IsCombatBlocked(player))
            {
                ChatMessage(player, "Command.Spawn.Error.CombatBlocked");
                return false;
            }

            return true;
        }

        private bool IsRaidBlocked(BasePlayer player) =>
            NoEscape != null && (bool)NoEscape.Call("IsRaidBlocked", player);

        private bool IsCombatBlocked(BasePlayer player) =>
            NoEscape != null && (bool)NoEscape.Call("IsCombatBlocked", player);

        private bool VerifyPermissionAny(IPlayer player, params string[] permissionNames)
        {
            foreach (var perm in permissionNames)
                if (permission.UserHasPermission(player.Id, perm))
                    return true;

            ReplyToPlayer(player, "Generic.Error.NoPermission");
            return false;
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

        private bool VerifyDependencies()
        {
            if (SpawnModularCar == null)
            {
                LogError("SpawnModularCar is not loaded, get it at https://umod.org");
                return false;
            }
            return true;
        }

        private void InitializeReceivers(bool initialBoot = false)
        {
            foreach (var receiver in BaseNetworkable.serverEntities.OfType<RFReceiver>())
            {
                var car = GetReceiverCar(receiver);
                if (car == null || !IsBombTruck(car)) continue;
                ReceiverManager.AddReceiver(receiver.GetFrequency(), receiver);

                if (initialBoot)
                    RemoveProblemComponents(receiver);
            }
        }

        private string GetSpawnPermission(string truckName) =>
            string.Format(PermissionSpawnFormat, truckName);

        private int SortTruckNames(string a, string b) =>
            a.ToLower() == DefaultTruckConfigName ? -1 :
            b.ToLower() == DefaultTruckConfigName ? 1 :
            a.CompareTo(b);

        private bool IsBombTruck(ModularCar car) =>
            PluginData.PlayerData.Any(item => item.Value.BombTrucks.Any(data => data.ID == car.net.ID));

        private ModularCar SpawnBombTruck(BasePlayer player, TruckConfig truckConfig, bool shouldTrack = false)
        {
            if (!VerifyDependencies()) return null;

            var car = SpawnModularCar.Call("API_SpawnPresetCar", player, new Dictionary<string, object>
            {
                ["EnginePartsTier"] = truckConfig.EnginePartsTier,
                ["FuelAmount"] = -1,
                ["Modules"] = truckConfig.Modules
            }, new Action<ModularCar>(readyCar => OnCarReady(player, readyCar, truckConfig))) as ModularCar;

            if (car == null) return null;

            if (shouldTrack)
                UpdatePlayerCooldown(player.UserIDString, truckConfig.Name);

            GetPlayerData(player.UserIDString).BombTrucks.Add(new PlayerTruckData
            {
                Name = truckConfig.Name,
                ID = car.net.ID,
                Tracked = shouldTrack
            });

            SaveData();

            return car;
        }

        private void OnCarReady(BasePlayer player, ModularCar car, TruckConfig truckConfig)
        {
            car.fuelSystem.GetFuelContainer().SetFlag(BaseEntity.Flags.Locked, true);

            foreach (var module in car.AttachedModuleEntities)
            {
                var storageContainer = (module as VehicleModuleStorage)?.GetContainer();
                if (storageContainer != null)
                    storageContainer.inventory.SetLocked(true);
            }

            var message = GetMessage(player.IPlayer, "Command.Spawn.Success");

            if (truckConfig.AttachRFReceiver)
            {
                var receiver = AttachRFReceiver(car);
                if (receiver != null)
                    message += " " + GetMessage(player.IPlayer, "Command.Spawn.Success.Frequency", receiver.GetFrequency());
            }

            player.IPlayer.Reply(message);
        }

        private RFReceiver AttachRFReceiver(ModularCar car)
        {
            VehicleModuleSeating module = FindFirstDriverModule(car);
            if (module == null) return null;

            var receiver = GameManager.server.CreateEntity(PrefabRfReceiver, module.transform.TransformPoint(RfReceiverPosition), module.transform.rotation * RfReceiverRotation) as RFReceiver;
            if (receiver == null) return null;

            int frequency = Core.Random.Range(RFManager.minFreq, RFManager.maxFreq);
            if (frequency >= RfReservedRangeMin && frequency <= RfReservedRangeMax)
                frequency = RfReservedRangeMin - 1;

            receiver.frequency = frequency;
            receiver.pickup.enabled = false;

            RemoveProblemComponents(receiver);
            receiver.Spawn();
            receiver.SetParent(module, worldPositionStays: true);

            return receiver;
        }

        private ModularCar GetReceiverCar(RFReceiver receiver) =>
            (receiver.GetParentEntity() as VehicleModuleSeating)?.Vehicle as ModularCar;

        private void RemoveProblemComponents(BaseEntity entity)
        {
            foreach (var meshCollider in entity.GetComponentsInChildren<MeshCollider>())
                UnityEngine.Object.DestroyImmediate(meshCollider);

            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        private VehicleModuleSeating FindFirstDriverModule(ModularCar car)
        {
            for (int socketIndex = 0; socketIndex < car.TotalSockets; socketIndex++)
            {
                BaseVehicleModule module;
                if (car.TryGetModuleAt(socketIndex, out module))
                {
                    var seatingModule = module as VehicleModuleSeating;
                    if (seatingModule != null && seatingModule.HasADriverSeat())
                        return seatingModule;
                }
            }
            return null;
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

        internal class RFReceiverManager
        {
            private readonly Dictionary<int, List<RFReceiver>> Receivers = new Dictionary<int, List<RFReceiver>>();

            public void AddReceiver(int frequency, RFReceiver receiver)
            {
                if (!Receivers.ContainsKey(frequency))
                    Receivers.Add(frequency, new List<RFReceiver> { receiver });
                else if (!Receivers[frequency].Contains(receiver))
                    Receivers[frequency].Add(receiver);
            }

            public void RemoveReceiver(int frequency, RFReceiver receiver)
            {
                if (!Receivers.ContainsKey(frequency)) return;
                Receivers[frequency].Remove(receiver);
            }

            public void DetonateFrequency(int frequency)
            {
                if (!Receivers.ContainsKey(frequency)) return;
                foreach (var receiver in Receivers[frequency].ToArray())
                {
                    var car = BombTrucksInstance.GetReceiverCar(receiver);
                    if (car != null)
                        BombTrucksInstance.DetonateBombTruck(car);
                }
            }
        }

        #endregion

        #region Explosions

        private void DetonateBombTruck(ModularCar car)
        {
            var playerConfig = GetPlayerData(car.OwnerID.ToString());

            var netID = car.net.ID;
            var truckName = playerConfig.FindTruck(netID)?.Name;
            if (truckName == null)
            {
                LogError("Unable to determine truck name.");
                return;
            }

            var truckConfig = GetTruckConfig(truckName);
            if (truckConfig == null)
            {
                LogError("Unable to detonate '{0}' truck because its configuration is missing.", truckName);
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

        private void DetonateExplosion(ExplosionSpec spec, Vector3 origin) =>
            ServerMgr.Instance.StartCoroutine(ExplosionCoroutine(spec, origin));

        private IEnumerator ExplosionCoroutine(ExplosionSpec spec, Vector3 origin)
        {
            float rocketTravelTime = 0.3f;
            double totalTime = spec.Radius / spec.Speed;
            int numExplosions = (int)Math.Ceiling(spec.DensityCoefficient * Math.Pow(spec.Radius, spec.DensityExponent));

            float timeElapsed = 0;
            double prevDistance = 0;

            FireRocket(PrefabExplosiveRocket, origin, Vector3.forward, 0, spec.BlastRadiusMult, spec.DamageMult);

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

                // Skip over some space to reduce the frequency of rockets colliding with each other
                Vector3 skipDistance = rocketVector;

                rocketVector *= Convert.ToSingle(rocketSpeed);
                FireRocket(PrefabExplosiveRocket, origin + skipDistance, rocketVector + skipDistance, rocketTravelTime, spec.BlastRadiusMult, spec.DamageMult);

                float timeToNext = Convert.ToSingle(Math.Pow(i / spec.DensityCoefficient, 1.0 / spec.DensityExponent) / spec.Speed - timeElapsed);

                yield return new WaitForSeconds(timeToNext);
                prevDistance = stepDistance;
                timeElapsed += timeToNext;
            }
        }

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
            if (!PluginData.PlayerData.ContainsKey(userID))
                PluginData.PlayerData.Add(userID, new PlayerData());

            return PluginData.PlayerData[userID];
        }

        private void CleanStaleTruckData()
        {
            var cleanedCount = 0;

            // Clean up any stale truck IDs in case of a data file desync
            foreach (var playerData in PluginData.PlayerData.Values)
                cleanedCount += playerData.BombTrucks.RemoveAll(truckData => (BaseNetworkable.serverEntities.Find(truckData.ID) as ModularCar) == null);

            if (cleanedCount > 0)
                SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, PluginData);

        private void ClearData() => Interface.Oxide.DataFileSystem.WriteObject(Name, new StoredData());

        internal class StoredData
        {
            [JsonProperty("PlayerData")]
            public Dictionary<string, PlayerData> PlayerData = new Dictionary<string, PlayerData>();
        }

        internal class PlayerData
        {
            [JsonProperty("BombTrucks")]
            public List<PlayerTruckData> BombTrucks = new List<PlayerTruckData>();

            [JsonProperty("Cooldowns")]
            public Dictionary<string, long> Cooldowns = new Dictionary<string, long>();

            public void UpdateCooldown(string truckName, long time)
            {
                if (Cooldowns.ContainsKey(truckName))
                    Cooldowns[truckName] = time;
                else
                    Cooldowns.Add(truckName, time);
            }

            public int GetTruckCount(string truckName) =>
                BombTrucks.Count(truckData => truckData.Tracked && truckData.Name == truckName);

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
            [JsonProperty("ID")]
            public uint ID;

            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Tracked")]
            public bool Tracked = true;
        }

        #endregion

        #region Configuration

        private TruckConfig GetTruckConfig(string truckName) =>
            PluginConfig.BombTrucks.FirstOrDefault(truckConfig => truckConfig.Name.ToLower() == truckName.ToLower());

        internal class Configuration : SerializableConfiguration
        {
            [JsonProperty("BombTrucks")]
            public TruckConfig[] BombTrucks = new TruckConfig[0];

            [JsonProperty("NoEscapeSettings")]
            public NoEscapeSettings NoEscapeSettings = new NoEscapeSettings();
        }

        internal class TruckConfig
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("CooldownSeconds")]
            public long CooldownSeconds = 0;

            [JsonProperty("SpawnLimitPerPlayer")]
            public int SpawnLimit = 1;

            [JsonProperty("AttachRFReceiver")]
            public bool AttachRFReceiver = true;

            private int _enginePartsTier = 1;

            [JsonProperty("EnginePartsTier")]
            public int EnginePartsTier
            {
                get { return _enginePartsTier; }
                set { _enginePartsTier = Math.Min(Math.Max(value, 1), 3); }
            }

            [JsonProperty("Modules")]
            public object[] Modules = new object[]
            {
                "vehicle.1mod.cockpit.with.engine",
                "vehicle.2mod.fuel.tank"
            };

            [JsonProperty("ExplosionSettings")]
            public ExplosionSpec ExplosionSpec = new ExplosionSpec();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
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
                        Name = "Nuke",
                        EnginePartsTier = 1,
                        CooldownSeconds = 10800,
                        SpawnLimit = 1,
                        Modules = new object[]
                        {
                            "vehicle.1mod.engine",
                            "vehicle.1mod.cockpit.armored",
                            "vehicle.2mod.fuel.tank"
                        },
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

        internal class NoEscapeSettings
        {
            [JsonProperty("CanSpawnWhileRaidBlocked")]
            public bool CanSpawnWhileRaidBlocked = true;

            [JsonProperty("CanSpawnWhileCombatBlocked")]
            public bool CanSpawnWhileCombatBlocked = true;
        }

        #endregion

        #region Configuration Boilerplate

        protected override void LoadDefaultConfig() => PluginConfig = GetDefaultConfig();

        internal class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        internal static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(Configuration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                PluginConfig = Config.ReadObject<Configuration>();
                if (PluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(PluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(PluginConfig, true);
        }

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
                ["Command.Spawn.Success.Frequency"] = "Detonate it with frequency: {0}",
                ["Command.Help"] = "<color=orange>BombTruck Command Usages</color>",
                ["Command.Help.Spawn.Default"] = "<color=yellow>bt</color> - Spawn a bomb truck",
                ["Command.Help.Spawn.Named"] = "<color=yellow>bt {0}</color> - Spawn a {0} truck",
                ["Command.Help.LimitUsage"] = "<color=yellow>{0}/{1}</color>",
                ["Command.Help.RemainingCooldown"] = "<color=red>{0}</color>",
                ["Command.Spawn.Error.RaidBlocked"] = "Error: Cannot do that while raid blocked.",
                ["Command.Spawn.Error.CombatBlocked"] = "Error: Cannot do that while combat blocked.",
                ["Command.Give.Error.Syntax"] = "Syntax: <color=yellow>givebombtruck <player> <truck name></color>",
                ["Command.Give.Error.PlayerNotFound"] = "Error: Player <color=red>{0}</color> not found.",
                ["Lift.Edit.Error"] = "Error: That vehicle may not be edited.",
                ["Lock.Deploy.Error"] = "Error: Bomb trucks may not have locks.",
                ["Unclaim.Error"] = "Error: You cannot unclaim bomb trucks.",
                ["AutoTurret.Deploy.Error"] = "Error: You cannot deploy auto turrets to bomb trucks.",
            }, this, "en");
        }

        #endregion
    }
}
