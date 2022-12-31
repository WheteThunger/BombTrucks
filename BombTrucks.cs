using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    [Info("Bomb Trucks", "WhiteThunder", "0.8.3")]
    [Description("Allow players to spawn bomb trucks.")]
    internal class BombTrucks : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin NoEngineParts, NoEscape, SpawnModularCar;

        private const int RfReservedRangeMin = 4760;
        private const int RfReservedRangeMax = 4790;

        private const string DefaultTruckConfigName = "default";

        private const string PermissionSpawnFormat = "bombtrucks.spawn.{0}";
        private const string PermissionGiveBombTruck = "bombtrucks.give";
        private const string PermissionFreeDetonator = "bombtrucks.freedetonator";

        private const string PrefabExplosiveRocket = "assets/prefabs/ammo/rocket/rocket_basic.prefab";
        private const string PrefabRfReceiver = "assets/prefabs/deployable/playerioents/gates/rfreceiver/rfreceiver.prefab";

        private const int DetonatorItemId = 596469572;
        private const int InvalidFrequency = -1;

        private readonly object False = false;

        private readonly Vector3 RfReceiverPosition = new Vector3(0, -0.1f, 0);
        private readonly Quaternion RfReceiverRotation = Quaternion.Euler(0, 180, 0);

        private readonly RFReceiverManager _receiverManager;
        private readonly BombTruckTracker _bombTruckTracker;

        private StoredData _pluginData;
        private Configuration _pluginConfig;
        private ProtectionProperties _immortalProtection;
        private bool _pluginUnloaded;

        public BombTrucks()
        {
            _receiverManager = new RFReceiverManager(this);
            _bombTruckTracker = new BombTruckTracker(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);

            foreach (var truckConfig in _pluginConfig.BombTrucks)
            {
                permission.RegisterPermission(GetSpawnPermission(truckConfig.Name), this);
            }

            permission.RegisterPermission(PermissionGiveBombTruck, this);
            permission.RegisterPermission(PermissionFreeDetonator, this);
        }

        private void OnServerInitialized()
        {
            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "BombTrucksProtection";
            _immortalProtection.Add(1);

            VerifyDependencies();
            CleanStaleTruckData();
            InitializeBombTrucks();
        }

        private void Unload()
        {
            UnityEngine.Object.Destroy(_immortalProtection);

            _bombTruckTracker.Unload();

            // This is used to signal coroutines to stop early (simpler than keeping track of them).
            _pluginUnloaded = true;
        }

        private void OnNewSave() => ClearData();

        private void OnEntityDeath(ModularCar car)
        {
            if (IsBombTruck(car) && car.OwnerID != 0)
            {
                DetonateBombTruck(car);
            }
        }

        private void OnEntityMounted(ModularCarSeat seat, BasePlayer player)
        {
            var car = (seat.GetParentEntity() as BaseVehicleModule)?.GetParentEntity() as ModularCar;
            if (car == null || !IsBombTruck(car))
                return;

            var fuelContainer = car.GetFuelSystem().GetFuelContainer();
            fuelContainer.inventory.AddItem(fuelContainer.allowedItem, fuelContainer.allowedItem.stackable);
        }

        private object CanLootEntity(BasePlayer player, ModularCarGarage carLift)
        {
            if (!carLift.PlatformIsOccupied)
                return null;

            var car = carLift.carOccupant;
            if (car != null && IsBombTruck(car))
            {
                ChatMessage(player, "Lift.Edit.Error");
                return False;
            }

            return null;
        }

        // This hook is exposed by the deprecated plugin Modular Car Code Locks (CarCodeLocks).
        private object CanDeployCarCodeLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        private object CanDeployVehicleCodeLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        private object CanDeployVehicleKeyLock(ModularCar car, BasePlayer player) =>
            CanLockVehicle(car, player);

        private object CanLockVehicle(ModularCar car, BasePlayer player)
        {
            if (!IsBombTruck(car))
                return null;

            if (player != null)
            {
                player.ChatMessage(GetMessage(player.IPlayer, "Lock.Deploy.Error"));
            }

            return False;
        }

        private void OnRfBroadcasterAdded(IRFObject obj, int frequency) =>
            _receiverManager.DetonateFrequency(frequency);

        private void OnRfListenerAdded(IRFObject obj, int frequency)
        {
            var receiver = obj as RFReceiver;
            if (receiver != null)
            {
                var frequency2 = frequency;
                var receiver2 = receiver;

                // Need to delay checking for the car since the receiver is spawned unparented to mitigate rendering bug.
                NextTick(() =>
                {
                    var car = GetReceiverCar(receiver2);
                    if (car == null || !IsBombTruck(car))
                        return;

                    _receiverManager.AddReceiver(frequency2, receiver2);
                });
            }
        }

        private void OnRfListenerRemoved(IRFObject obj, int frequency)
        {
            var receiver = obj as RFReceiver;
            if (receiver == null)
                return;

            var car = GetReceiverCar(receiver);
            if (car == null || !IsBombTruck(car))
                return;

            _receiverManager.RemoveReceiver(frequency, receiver);
        }

        // This hook is exposed by Claim Vehicle Ownership (ClaimVehicle).
        private object OnVehicleUnclaim(BasePlayer player, ModularCar car)
        {
            if (car == null || !IsBombTruck(car))
                return null;

            ChatMessage(player, "Unclaim.Error");
            return False;
        }

        // This hook is exposed by Modular Car Turrets (CarTurrets).
        private object OnCarAutoTurretDeploy(BaseVehicleModule module, BasePlayer player, bool automatedDeployment)
        {
            if (module == null)
                return null;

            var car = module.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car))
                return null;

            if (player != null && !automatedDeployment)
            {
                ChatMessage(player, "AutoTurret.Deploy.Error");
            }

            return False;
        }

        // This hook is exposed by No Engine Parts (NoEngineParts).
        private object OnEngineLoadoutOverride(EngineStorage engineStorage)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car))
                return null;

            return False;
        }

        // This hook is exposed by Engine Parts Durability (EnginePartsDurability).
        private object OnEngineDamageMultiplierChange(EngineStorage engineStorage, float desiredMultiplier)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car))
                return null;

            return False;
        }

        // This hook is exposed by Auto Engine Parts (AutoEngineParts).
        private object OnEngineStorageFill(EngineStorage engineStorage, int enginePartsTier)
        {
            var car = engineStorage.GetEngineModule()?.Vehicle as ModularCar;
            if (car == null || !IsBombTruck(car))
                return null;

            return False;
        }

        #endregion

        #region Commands

        [Command("bombtruck", "bt", "boomer")]
        private void SpawnBombTruckCommand(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer)
                return;

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
            var allowedTruckConfigs = _pluginConfig.BombTrucks
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
                !VerifyHasPermission(player, GetSpawnPermission(truckName)) ||
                !VerifyOffCooldown(player, truckConfig) ||
                !VerifyBelowTruckLimit(player, truckConfig) ||
                !VerifyNotBuildingBlocked(player) ||
                !VerifyNotMounted(player) ||
                !VerifyOnGround(player) ||
                !VerifyNotParented(player) ||
                !VerifyNotRaidOrCombatBlocked(basePlayer) ||
                SpawnWasBlocked(basePlayer))
                return;

            SpawnBombTruck(basePlayer, truckConfig, shouldTrack: true);
        }

        [Command("givebombtruck")]
        private void GiveBombTruckCommand(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer && !VerifyHasPermission(player, PermissionGiveBombTruck))
                return;

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

        private static bool SpawnWasBlocked(BasePlayer player)
        {
            var hookResult = Interface.CallHook("CanSpawnBombTruck", player);
            return hookResult is bool && (bool)hookResult == false;
        }

        private bool VerifyNotRaidOrCombatBlocked(BasePlayer player)
        {
            if (!_pluginConfig.NoEscapeSettings.CanSpawnWhileRaidBlocked && IsRaidBlocked(player))
            {
                ChatMessage(player, "Command.Spawn.Error.RaidBlocked");
                return false;
            }

            if (!_pluginConfig.NoEscapeSettings.CanSpawnWhileCombatBlocked && IsCombatBlocked(player))
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

        private bool VerifyHasPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
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

        private static void DisableEnginePartDamage(ModularCar car)
        {
            foreach (var module in car.AttachedModuleEntities)
            {
                var engineStorage = GetEngineStorage(module);
                if (engineStorage == null)
                    continue;

                engineStorage.internalDamageMultiplier = 0;
            }
        }

        private static EngineStorage GetEngineStorage(BaseVehicleModule module)
        {
            var engineModule = module as VehicleModuleEngine;
            if (engineModule == null)
                return null;

            return engineModule.GetContainer() as EngineStorage;
        }

        private static string GetSpawnPermission(string truckName) =>
            string.Format(PermissionSpawnFormat, truckName);

        private static int SortTruckNames(string a, string b) =>
            a.ToLower() == DefaultTruckConfigName ? -1 :
            b.ToLower() == DefaultTruckConfigName ? 1 :
            string.Compare(a, b, StringComparison.Ordinal);

        private static ModularCar GetReceiverCar(RFReceiver receiver) =>
            (receiver.GetParentEntity() as VehicleModuleSeating)?.Vehicle as ModularCar;

        private static RFReceiver GetBombTruckReceiver(ModularCar car)
        {
            var driverModule = FindFirstDriverModule(car);
            if (driverModule == null)
                return null;

            return GetChildOfType<RFReceiver>(driverModule);
        }

        private static T GetChildOfType<T>(BaseEntity entity) where T : BaseEntity
        {
            foreach (var child in entity.children)
            {
                var childOfType = child as T;
                if (childOfType != null)
                    return childOfType;
            }
            return null;
        }

        private static void RemoveProblemComponents(BaseEntity entity)
        {
            foreach (var meshCollider in entity.GetComponentsInChildren<MeshCollider>())
                UnityEngine.Object.DestroyImmediate(meshCollider);

            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        private static VehicleModuleSeating FindFirstDriverModule(ModularCar car)
        {
            for (var socketIndex = 0; socketIndex < car.TotalSockets; socketIndex++)
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

        private static bool HasExistingDetonator(ItemContainer container, out int frequency)
        {
            var hasDetonator = false;
            frequency = InvalidFrequency;

            for (var slot = 0; slot < container.capacity; slot++)
            {
                var item = container.GetSlot(slot);
                if (item == null || item.info.itemid != DetonatorItemId)
                    continue;

                hasDetonator = true;

                frequency = item.instanceData?.dataInt ?? InvalidFrequency;
                if (frequency != InvalidFrequency)
                {
                    // Only exit early if the detonator has a valid frequency.
                    // Otherwise, keep searching for detonators with a valid frequency.
                    return true;
                }
            }

            return hasDetonator;
        }

        private static bool HasExistingDetonator(BasePlayer player, out int frequency)
        {
            var hasDetonator = false;
            frequency = -1;

            var activeDetonator = player.GetActiveItem();
            if (activeDetonator != null && activeDetonator.info.itemid == DetonatorItemId)
            {
                frequency = activeDetonator.instanceData?.dataInt ?? -1;
                hasDetonator = true;
            }

            // Only exit early if one of the belt detonators had a valid frequency.
            // Otherwise, keep searching for a detonator with a valid frequency.
            if (hasDetonator && frequency != InvalidFrequency)
                return true;

            if (HasExistingDetonator(player.inventory.containerBelt, out frequency))
                hasDetonator = true;

            if (hasDetonator && frequency != InvalidFrequency)
                return true;

            if (HasExistingDetonator(player.inventory.containerMain, out frequency))
                hasDetonator = true;

            return hasDetonator;
        }

        private static Item CreateRFTransmitter(int frequency)
        {
            var detonatorItem = ItemManager.CreateByItemID(DetonatorItemId);
            if (detonatorItem == null)
                return null;

            if (detonatorItem.instanceData == null)
            {
                detonatorItem.instanceData = new ProtoBuf.Item.InstanceData { ShouldPool = false };
            }

            detonatorItem.instanceData.dataInt = frequency;

            var detonator = detonatorItem.GetHeldEntity() as Detonator;
            if (detonator != null)
                detonator.frequency = frequency;

            return detonatorItem;
        }

        private static int GenerateRandomFrequency()
        {
            var frequency = Core.Random.Range(RFManager.minFreq, RFManager.maxFreq);
            return frequency >= RfReservedRangeMin && frequency <= RfReservedRangeMax
                ? RfReservedRangeMin - 1
                : frequency;
        }

        private static string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString("g");

        private bool VerifyDependencies()
        {
            if (SpawnModularCar == null)
            {
                LogError("SpawnModularCar is not loaded, get it at https://umod.org");
                return false;
            }

            var requiredVersion = new VersionNumber(5, 0, 1);
            if (SpawnModularCar.Version < requiredVersion)
            {
                LogError($"SpawnModularCar {requiredVersion} or newer is required, get it at https://umod.org");
                return false;
            }

            return true;
        }

        private void SetupReceiver(RFReceiver receiver)
        {
            receiver.pickup.enabled = false;
            receiver.baseProtection = _immortalProtection;
            RemoveProblemComponents(receiver);
        }

        private void InitializeBombTrucks()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var car = entity as ModularCar;
                if (car == null || !IsBombTruck(car))
                    continue;

                _bombTruckTracker.TrackBombTruck(car);

                DisableEnginePartDamage(car);
                var receiver = GetBombTruckReceiver(car);
                if (receiver != null)
                {
                    _receiverManager.AddReceiver(receiver.GetFrequency(), receiver);
                    SetupReceiver(receiver);
                }
            }
        }

        private bool IsBombTruck(ModularCar car) =>
            _pluginData.PlayerData.Any(item => item.Value.BombTrucks.Any(data => data.ID == car.net.ID));

        private ModularCar SpawnBombTruck(BasePlayer player, TruckConfig truckConfig, bool shouldTrack = false)
        {
            if (!VerifyDependencies())
                return null;

            var car = SpawnModularCar.Call("API_SpawnPreset", new Dictionary<string, object>
            {
                ["EnginePartsTier"] = truckConfig.EnginePartsTier,
                ["FuelAmount"] = -1,
                ["Modules"] = truckConfig.Modules
            }, player) as ModularCar;

            if (car == null)
                return null;

            _bombTruckTracker.TrackBombTruck(car);

            car.GetFuelSystem().GetFuelContainer().SetFlag(BaseEntity.Flags.Locked, true);

            foreach (var module in car.AttachedModuleEntities)
            {
                var engineStorage = GetEngineStorage(module);
                if (engineStorage != null)
                {
                    engineStorage.inventory.SetLocked(true);
                    engineStorage.internalDamageMultiplier = 0;
                }
            }

            var message = GetMessage(player.IPlayer, "Command.Spawn.Success");

            if (truckConfig.AttachRFReceiver)
            {
                int frequency;
                var hasDetonator = HasExistingDetonator(player, out frequency);

                var receiver = AttachRFReceiver(car, frequency);
                if (receiver != null)
                {
                    // Get the current frequency of the RF receiver, in case a new one was generated.
                    frequency = receiver.GetFrequency();

                    if (!hasDetonator && permission.UserHasPermission(player.UserIDString, PermissionFreeDetonator))
                    {
                        var detonatorItem = CreateRFTransmitter(frequency);
                        if (detonatorItem != null)
                            player.GiveItem(detonatorItem);
                    }

                    message += " " + GetMessage(player.IPlayer, "Command.Spawn.Success.Frequency", frequency);
                }
            }

            player.IPlayer.Reply(message);

            if (shouldTrack)
            {
                UpdatePlayerCooldown(player.UserIDString, truckConfig.Name);
            }

            GetPlayerData(player.UserIDString).BombTrucks.Add(new PlayerTruckData
            {
                Name = truckConfig.Name,
                ID = car.net.ID,
                Tracked = shouldTrack
            });

            SaveData();

            if (NoEngineParts != null)
            {
                // Refresh engine stats on next tick to override NoEngineParts.
                // This has to be done on the next tick after the bomb truck id has been registered.
                NextTick(() =>
                {
                    if (car == null || car.IsDestroyed)
                        return;

                    foreach (var module in car.AttachedModuleEntities)
                    {
                        var engineStorage = GetEngineStorage(module);
                        if (engineStorage != null)
                        {
                            engineStorage.RefreshLoadoutData();
                        }
                    }
                });
            }

            return car;
        }

        private RFReceiver AttachRFReceiver(ModularCar car, int frequency = -1)
        {
            var module = FindFirstDriverModule(car);
            if (module == null)
                return null;

            var receiver = GameManager.server.CreateEntity(PrefabRfReceiver, module.transform.TransformPoint(RfReceiverPosition), module.transform.rotation * RfReceiverRotation) as RFReceiver;
            if (receiver == null)
                return null;

            if (frequency == -1)
                frequency = GenerateRandomFrequency();

            receiver.frequency = frequency;

            SetupReceiver(receiver);
            receiver.Spawn();
            receiver.SetParent(module, worldPositionStays: true);

            return receiver;
        }

        private double GetPlayerRemainingCooldownSeconds(string userID, TruckConfig truckConfig)
        {
            var playerCooldowns = GetPlayerData(userID).Cooldowns;
            if (!playerCooldowns.ContainsKey(truckConfig.Name))
                return 0;

            var lastUsed = playerCooldowns[truckConfig.Name];
            var cooldownDuration = truckConfig.CooldownSeconds;
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return lastUsed + cooldownDuration - currentTime;
        }

        private void UpdatePlayerCooldown(string userID, string truckName) =>
            GetPlayerData(userID).UpdateCooldown(truckName, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        #endregion

        #region Unity Components

        private class BombTruckComponent : FacepunchBehaviour
        {
            public static BombTruckComponent AddToCar(BombTruckTracker tracker, ModularCar car)
            {
                var component = car.gameObject.AddComponent<BombTruckComponent>();
                component._tracker = tracker;
                component.Car = car;
                component.NetId = car.net.ID;
                component.OwnerId = car.OwnerID;
                return component;
            }

            public ModularCar Car { get; private set; }
            public uint NetId { get; private set; }
            public ulong OwnerId { get; private set; }
            private BombTruckTracker _tracker;

            private void OnDestroy()
            {
                _tracker.HandleBombTruckDestroyed(this);
            }
        }

        private class BombTruckTracker
        {
            private BombTrucks _plugin;
            private HashSet<Component> _bombTruckComponents = new HashSet<Component>();

            public BombTruckTracker(BombTrucks plugin)
            {
                _plugin = plugin;
            }

            public void TrackBombTruck(ModularCar car)
            {
                _bombTruckComponents.Add(BombTruckComponent.AddToCar(this, car));
            }

            public void HandleBombTruckDestroyed(BombTruckComponent component)
            {
                _bombTruckComponents.Remove(component);

                if (component.Car == null || component.Car.IsDestroyed)
                {
                    _plugin.GetPlayerData(component.OwnerId.ToString()).RemoveTruck(component.NetId);
                    _plugin.SaveData();
                }
            }

            public void Unload()
            {
                foreach (var component in _bombTruckComponents.ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }
        }

        #endregion

        #region RF Receiver Manager

        private class RFReceiverManager
        {
            private BombTrucks _plugin;
            private readonly Dictionary<int, List<RFReceiver>> Receivers = new Dictionary<int, List<RFReceiver>>();

            public RFReceiverManager(BombTrucks plugin)
            {
                _plugin = plugin;
            }

            public void AddReceiver(int frequency, RFReceiver receiver)
            {
                List<RFReceiver> receiverList;
                if (Receivers.TryGetValue(frequency, out receiverList))
                {
                    receiverList.Add(receiver);
                }
                else
                {
                    Receivers.Add(frequency, new List<RFReceiver> { receiver });
                }
            }

            public void RemoveReceiver(int frequency, RFReceiver receiver)
            {
                List<RFReceiver> receiverList;
                if (Receivers.TryGetValue(frequency, out receiverList))
                {
                    receiverList.Remove(receiver);
                }
            }

            public void DetonateFrequency(int frequency)
            {
                List<RFReceiver> receiverList;
                if (Receivers.TryGetValue(frequency, out receiverList))
                {
                    for (var i = receiverList.Count - 1; i >= 0; i--)
                    {
                        var receiver = receiverList[i];
                        var car = GetReceiverCar(receiver);
                        if (car != null)
                        {
                            _plugin.DetonateBombTruck(car);
                        }
                    }
                }
            }
        }

        #endregion

        #region Explosions

        private static void FireRocket(string rocketPrefab, Vector3 origin, Vector3 direction, float time, float damageRadiusMult = 1.0f, float damageMult = 1.0f)
        {
            var rocket = GameManager.server.CreateEntity(rocketPrefab, origin);
            var rocketProjectile = rocket.GetComponent<ServerProjectile>();
            var rocketExplosion = rocket.GetComponent<TimedExplosive>();

            rocketProjectile.gravityModifier = 0;
            rocketExplosion.explosionRadius *= damageRadiusMult;
            rocketExplosion.timerAmountMin = time;
            rocketExplosion.timerAmountMax = time;

            for (var i = 0; i < rocketExplosion.damageTypes.Count; i++)
            {
                rocketExplosion.damageTypes[i].amount *= damageMult;
            }

            rocket.SendMessage("InitializeVelocity", direction);
            rocket.Spawn();
        }

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

            // Remove the engine parts.
            foreach (var module in car.AttachedModuleEntities)
            {
                var engineStorage = GetEngineStorage(module);
                if (engineStorage != null)
                {
                    engineStorage.inventory.Kill();
                }
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
                if (_pluginUnloaded)
                    yield break;

                double timeFraction = timeElapsed / totalTime;
                double stepDistance = spec.Radius * timeFraction;

                double stepStartDistance = prevDistance;
                double stepEndDistance = stepDistance;

                double rocketDistance = Core.Random.Range(stepStartDistance, stepEndDistance);
                double rocketSpeed = rocketDistance / rocketTravelTime;

                Vector3 rocketVector = MakeRandomDomeVector();

                // Skip over some space to reduce the frequency of rockets colliding with each other.
                Vector3 skipDistance = rocketVector;

                rocketVector *= Convert.ToSingle(rocketSpeed);
                FireRocket(PrefabExplosiveRocket, origin + skipDistance, rocketVector + skipDistance, rocketTravelTime, spec.BlastRadiusMult, spec.DamageMult);

                float timeToNext = Convert.ToSingle(Math.Pow(i / spec.DensityCoefficient, 1.0 / spec.DensityExponent) / spec.Speed - timeElapsed);

                yield return CoroutineEx.waitForSeconds(timeToNext);
                prevDistance = stepDistance;
                timeElapsed += timeToNext;
            }
        }

        private Vector3 MakeRandomDomeVector() =>
            new Vector3(Core.Random.Range(-1f, 1f), Core.Random.Range(0, 1f), Core.Random.Range(-1f, 1f)).normalized;

        #endregion

        #region Data Management

        private PlayerData GetPlayerData(string userID)
        {
            if (!_pluginData.PlayerData.ContainsKey(userID))
                _pluginData.PlayerData.Add(userID, new PlayerData());

            return _pluginData.PlayerData[userID];
        }

        private void CleanStaleTruckData()
        {
            var cleanedCount = 0;

            // Clean up any stale truck IDs in case of a data file desync.
            foreach (var playerData in _pluginData.PlayerData.Values)
            {
                cleanedCount += playerData.BombTrucks.RemoveAll(truckData =>
                    (BaseNetworkable.serverEntities.Find(truckData.ID) as ModularCar) == null);
            }

            if (cleanedCount > 0)
            {
                SaveData();
            }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _pluginData);

        private void ClearData() => Interface.Oxide.DataFileSystem.WriteObject(Name, new StoredData());

        private class StoredData
        {
            [JsonProperty("PlayerData")]
            public Dictionary<string, PlayerData> PlayerData = new Dictionary<string, PlayerData>();
        }

        private class PlayerData
        {
            [JsonProperty("BombTrucks")]
            public List<PlayerTruckData> BombTrucks = new List<PlayerTruckData>();

            [JsonProperty("Cooldowns")]
            public Dictionary<string, long> Cooldowns = new Dictionary<string, long>();

            public void UpdateCooldown(string truckName, long time)
            {
                if (Cooldowns.ContainsKey(truckName))
                {
                    Cooldowns[truckName] = time;
                }
                else
                {
                    Cooldowns.Add(truckName, time);
                }
            }

            public int GetTruckCount(string truckName) =>
                BombTrucks.Count(truckData => truckData.Tracked && truckData.Name == truckName);

            public PlayerTruckData FindTruck(uint netID) =>
                BombTrucks.FirstOrDefault(truckData => truckData.ID == netID);

            public void RemoveTruck(uint netID)
            {
                BombTrucks.RemoveAll(truckData => truckData.ID == netID);
            }
        }

        private class PlayerTruckData
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
            _pluginConfig.BombTrucks.FirstOrDefault(truckConfig => truckConfig.Name.ToLower() == truckName.ToLower());

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("BombTrucks")]
            public TruckConfig[] BombTrucks = new TruckConfig[0];

            [JsonProperty("NoEscapeSettings")]
            public NoEscapeSettings NoEscapeSettings = new NoEscapeSettings();
        }

        private class TruckConfig
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("CooldownSeconds")]
            public long CooldownSeconds;

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
            public object[] Modules =
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
                BombTrucks = new[]
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

        private class ExplosionSpec
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

        private class NoEscapeSettings
        {
            [JsonProperty("CanSpawnWhileRaidBlocked")]
            public bool CanSpawnWhileRaidBlocked = true;

            [JsonProperty("CanSpawnWhileCombatBlocked")]
            public bool CanSpawnWhileCombatBlocked = true;
        }

        #endregion

        #region Configuration Boilerplate

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
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

        private bool MaybeUpdateConfig(SerializableConfiguration config)
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

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
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
