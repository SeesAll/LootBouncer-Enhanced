/*
Modified & Updated Branch of Lootbouncer by VisEntities. The readme notes can be found at https://github.com/SeesAll/
I take no credit for the original plugin found on uMod. I merely changed a few things to improve upon it.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;

namespace Oxide.Plugins
{
    [Info("Loot Bouncer", "Sorrow/Arainrr, enhanced by SeesAll", "1.4.1")]
    [Description("Automatically clears abandoned loot containers and optional junkpiles when players leave items behind")]
    public class LootBouncer : RustPlugin
    {
        #region Fields

        [PluginReference]
        private Plugin Slap, Trade, MonumentAddons, RaidableBases;

        private const string AdminPermission = "lootbouncer.admin";

        private readonly Dictionary<ulong, LootTrackData> _trackedLoot = new Dictionary<ulong, LootTrackData>();
        private readonly Dictionary<ulong, Timer> _junkPileCleanupTimers = new Dictionary<ulong, Timer>();
        private readonly Dictionary<ulong, Timer> _roadsideVehicleCleanupTimers = new Dictionary<ulong, Timer>();
        private readonly Dictionary<ulong, JunkPile> _junkPileAssociations = new Dictionary<ulong, JunkPile>();
        private readonly Dictionary<ulong, BaseEntity> _roadsideVehicleAssociations = new Dictionary<ulong, BaseEntity>();
        private readonly Dictionary<ulong, GroupCleanupState> _junkPileCleanupStates = new Dictionary<ulong, GroupCleanupState>();
        private readonly Dictionary<ulong, GroupCleanupState> _roadsideVehicleCleanupStates = new Dictionary<ulong, GroupCleanupState>();
        private readonly Dictionary<ulong, Timer> _droppedItemCleanupTimers = new Dictionary<ulong, Timer>();
        private readonly HashSet<ulong> _protectedEntityIds = new HashSet<ulong>();
        private readonly HashSet<string> _barrelShortPrefabNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly SortedDictionary<long, CleanupStats> _hourlyStats = new SortedDictionary<long, CleanupStats>();
        private bool _configRecoveryMode;

        #endregion Fields

        #region Data Classes

        private class LootTrackData
        {
            public LootContainer Container;
            public InventorySnapshot InitialInventory;
            public bool PendingCleanup;
            public double LastInteractionTime;
            public int TimerGeneration;
            public readonly HashSet<ulong> ActiveLooterIds = new HashSet<ulong>();
            public readonly HashSet<ulong> ResponsiblePlayerIds = new HashSet<ulong>();
            public Timer CleanupTimer;
        }

        private struct InventorySnapshot
        {
            public int StackCount;
            public long ItemAmount;
        }

        private class GroupCleanupState
        {
            public readonly HashSet<ulong> OriginalContainerIds = new HashSet<ulong>();
            public int RetryCount;
        }

        private class CleanupStats
        {
            public long ContainersRemoved;
            public long ItemStacksRemoved;
            public long ItemAmountRemoved;
            public long JunkPilesRecycled;
            public long RoadsideGroupsRecycled;
            public long DroppedWorldItemsRemoved;
            public long DroppedWorldItemAmountRemoved;
            public long ProtectedSkips;
            public long ReopenCancellations;
        }

        private enum CleanupReason
        {
            Container,
            JunkPile,
            RoadsideVehicle
        }

        #endregion Data Classes

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(AdminPermission, this);
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnPlayerAttack));
        }

        private void OnServerInitialized()
        {
            RefreshContainerCaches();

            if (configData.slapPlayer && Slap == null)
            {
                PrintError("Slap is not loaded, get it at https://umod.org/plugins/slap");
            }

            if (_barrelShortPrefabNames.Any(shortPrefabName => IsContainerEnabled(shortPrefabName)))
            {
                Subscribe(nameof(OnEntityDeath));
                Subscribe(nameof(OnPlayerAttack));
            }
        }

        private void Unload()
        {
            foreach (var track in _trackedLoot.Values)
            {
                track.CleanupTimer?.Destroy();
            }

            foreach (var cleanupTimer in _junkPileCleanupTimers.Values)
            {
                cleanupTimer?.Destroy();
            }

            foreach (var cleanupTimer in _roadsideVehicleCleanupTimers.Values)
            {
                cleanupTimer?.Destroy();
            }

            foreach (var cleanupTimer in _droppedItemCleanupTimers.Values)
            {
                cleanupTimer?.Destroy();
            }

            _trackedLoot.Clear();
            _junkPileCleanupTimers.Clear();
            _roadsideVehicleCleanupTimers.Clear();
            _junkPileAssociations.Clear();
            _roadsideVehicleAssociations.Clear();
            _junkPileCleanupStates.Clear();
            _roadsideVehicleCleanupStates.Clear();
            _droppedItemCleanupTimers.Clear();
            _protectedEntityIds.Clear();
            _barrelShortPrefabNames.Clear();
            _hourlyStats.Clear();
        }

        private void OnLootEntity(BasePlayer player, LootContainer lootContainer)
        {
            if (!CanProcessLootContainer(player, lootContainer))
            {
                return;
            }

            var entityId = lootContainer.net.ID.Value;
            var track = GetOrCreateTrack(entityId);
            track.Container = lootContainer;
            if (track.ActiveLooterIds.Count == 0)
            {
                track.InitialInventory = GetInventorySnapshot(lootContainer);
            }
            track.LastInteractionTime = GetNow();
            track.ActiveLooterIds.Add(player.userID);
            if (track.CleanupTimer != null && !track.CleanupTimer.Destroyed)
            {
                AddStat(stats => stats.ReopenCancellations++);
            }
            CancelLootCleanupTimer(track);
        }

        private void OnLootEntityEnd(BasePlayer player, LootContainer lootContainer)
        {
            if (player == null || lootContainer == null || lootContainer.net == null)
            {
                return;
            }

            var entityId = lootContainer.net.ID.Value;
            LootTrackData track;
            if (!_trackedLoot.TryGetValue(entityId, out track))
            {
                return;
            }

            track.LastInteractionTime = GetNow();
            track.ActiveLooterIds.Remove(player.userID);

            var currentInventory = GetInventorySnapshot(lootContainer);
            if (currentInventory.StackCount <= 0)
            {
                track.PendingCleanup = false;
                ClearLootTrackingIfUnused(entityId, track);
                return;
            }

            var removedItems = HasInventoryDecreased(track.InitialInventory, currentInventory);
            if (removedItems)
            {
                track.PendingCleanup = true;
                track.ResponsiblePlayerIds.Add(player.userID);
            }

            if (removedItems || track.PendingCleanup)
            {
                if (track.ActiveLooterIds.Count == 0)
                {
                    ScheduleLootCleanup(entityId, lootContainer, track);
                    EvaluateAssociatedSpawnBlocking(lootContainer);
                    ScheduleAssociatedGroupCleanup(lootContainer);
                }
                return;
            }

            ClearLootTrackingIfUnused(entityId, track);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            foreach (var entry in _trackedLoot)
            {
                var track = entry.Value;
                if (!track.ActiveLooterIds.Remove(player.userID) || track.ActiveLooterIds.Count > 0 || !track.PendingCleanup)
                {
                    continue;
                }

                if (track.Container != null && !track.Container.IsDestroyed)
                {
                    ScheduleLootCleanup(entry.Key, track.Container, track);
                }
            }
        }

        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if (!configData.cleanupDroppedWorldLoot || item == null || entity == null || entity.net == null)
            {
                return;
            }

            var worldItem = entity as WorldItem;
            if (worldItem == null || IsExcludedDroppedItem(item))
            {
                return;
            }

            NextTick(() => TryScheduleDroppedWorldItemCleanup(item, worldItem));
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || !attacker.userID.IsSteamId())
            {
                return;
            }

            var lootContainer = info?.HitEntity as LootContainer;
            if (lootContainer == null || lootContainer.net == null)
            {
                return;
            }

            if (!_barrelShortPrefabNames.Contains(lootContainer.ShortPrefabName) || !IsEligibleForCleanup(lootContainer, true))
            {
                return;
            }

            var entityId = lootContainer.net.ID.Value;
            var track = GetOrCreateTrack(entityId);
            track.Container = lootContainer;
            track.ResponsiblePlayerIds.Add(attacker.userID);
            track.LastInteractionTime = GetNow();
            ScheduleLootCleanup(entityId, lootContainer, track);
            EvaluateAssociatedSpawnBlocking(lootContainer);
            ScheduleAssociatedGroupCleanup(lootContainer);
        }

        private void OnEntityDeath(LootContainer lootContainer, HitInfo info)
        {
            if (lootContainer == null || lootContainer.net == null)
            {
                return;
            }

            if (!_barrelShortPrefabNames.Contains(lootContainer.ShortPrefabName))
            {
                return;
            }

            var attacker = info?.InitiatorPlayer;
            if (attacker == null || !attacker.userID.IsSteamId())
            {
                return;
            }

            LootTrackData track;
            if (!_trackedLoot.TryGetValue(lootContainer.net.ID.Value, out track))
            {
                return;
            }

            track.ActiveLooterIds.Remove(attacker.userID);
            ClearLootTrackingIfUnused(lootContainer.net.ID.Value, track);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var lootContainer = entity as LootContainer;
            if (lootContainer != null && lootContainer.net != null)
            {
                _protectedEntityIds.Remove(lootContainer.net.ID.Value);
                HandleLootContainerKill(lootContainer);
                return;
            }

            var junkPile = entity as JunkPile;
            if (junkPile != null && junkPile.net != null)
            {
                HandleJunkPileKill(junkPile);
                return;
            }

            var baseEntity = entity as BaseEntity;
            if (baseEntity != null && baseEntity.net != null)
            {
                CancelDroppedItemCleanupTimer(baseEntity.net.ID.Value);
            }

            if (baseEntity != null && baseEntity.net != null && LooksLikeRoadsideVehicleAnchor(baseEntity.ShortPrefabName))
            {
                HandleRoadsideVehicleKill(baseEntity);
            }
        }

        [ChatCommand("lbstatus")]
        private void CommandLootBouncerStatus(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                Print(player, Lang("NoPermission", player.UserIDString));
                return;
            }

            Print(player, BuildHourlyStatus());
        }

        [ConsoleCommand("lootbouncer.status")]
        private void ConsoleLootBouncerStatus(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player != null && !player.IsAdmin && !permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                arg.ReplyWith(Lang("NoPermission", player.UserIDString));
                return;
            }

            arg.ReplyWith(BuildHourlyStatus());
        }

        #endregion Oxide Hooks

        #region Methods

        private LootTrackData GetOrCreateTrack(ulong entityId)
        {
            LootTrackData track;
            if (!_trackedLoot.TryGetValue(entityId, out track))
            {
                track = new LootTrackData();
                _trackedLoot.Add(entityId, track);
            }

            return track;
        }

        private bool CanProcessLootContainer(BasePlayer player, LootContainer lootContainer)
        {
            if (player == null || lootContainer == null || lootContainer.net == null)
            {
                return false;
            }

            return IsEligibleForCleanup(lootContainer, true);
        }

        private bool IsEligibleForCleanup(LootContainer lootContainer, bool countProtectedSkip)
        {
            if (_configRecoveryMode || lootContainer == null || lootContainer.net == null || lootContainer.IsDestroyed)
            {
                return false;
            }

            if (!IsContainerEnabled(lootContainer.ShortPrefabName))
            {
                return false;
            }

            var tradeResult = Trade?.Call("IsTradeBox", lootContainer);
            if (tradeResult is bool && (bool)tradeResult)
            {
                return false;
            }

            if (IsEventProtected(lootContainer))
            {
                if (countProtectedSkip)
                {
                    AddStat(stats => stats.ProtectedSkips++);
                }
                return false;
            }

            return true;
        }

        private bool IsEventProtected(LootContainer lootContainer)
        {
            var entityId = lootContainer.net.ID.Value;
            if (_protectedEntityIds.Contains(entityId))
            {
                return true;
            }

            if (configData.protectAirfieldEventContainers
                && string.Equals(lootContainer.name, "airfieldcrate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (configData.protectMonumentAddonsContainers && MonumentAddons != null)
            {
                var monumentResult = MonumentAddons.Call("API_IsMonumentEntity", lootContainer);
                if (monumentResult is bool && (bool)monumentResult)
                {
                    return true;
                }
            }

            if (configData.protectRaidableBasesContainers && RaidableBases != null)
            {
                var raidResult = RaidableBases.Call("EventTerritory", lootContainer.transform.position, 0f);
                if (raidResult is bool && (bool)raidResult)
                {
                    return true;
                }
            }

            var hookResult = Interface.CallHook("CanLootBouncerProcess", lootContainer);
            return hookResult is bool && !(bool)hookResult;
        }

        private bool IsContainerEnabled(string shortPrefabName)
        {
            bool enabled;
            return configData.lootContainers.TryGetValue(shortPrefabName, out enabled)
                ? enabled
                : configData.enableNewContainersByDefault;
        }

        private InventorySnapshot GetInventorySnapshot(LootContainer lootContainer)
        {
            var snapshot = new InventorySnapshot();
            var items = lootContainer?.inventory?.itemList;
            if (items == null)
            {
                return snapshot;
            }

            snapshot.StackCount = items.Count;
            foreach (var item in items)
            {
                if (item != null)
                {
                    snapshot.ItemAmount += Math.Max(0, item.amount);
                }
            }
            return snapshot;
        }

        private int GetInventoryItemCount(LootContainer lootContainer)
        {
            return GetInventorySnapshot(lootContainer).StackCount;
        }

        private static bool HasInventoryDecreased(InventorySnapshot before, InventorySnapshot after)
        {
            return after.StackCount < before.StackCount || after.ItemAmount < before.ItemAmount;
        }

        [HookMethod("API_ProtectEntity")]
        public bool API_ProtectEntity(BaseEntity entity)
        {
            return entity != null && entity.net != null && _protectedEntityIds.Add(entity.net.ID.Value);
        }

        [HookMethod("API_UnprotectEntity")]
        public bool API_UnprotectEntity(BaseEntity entity)
        {
            return entity != null && entity.net != null && _protectedEntityIds.Remove(entity.net.ID.Value);
        }

        private double GetNow()
        {
            return Interface.Oxide.Now;
        }

        private void AddStat(Action<CleanupStats> update)
        {
            if (!configData.enableHourlyStatistics || update == null)
            {
                return;
            }

            var currentMinute = (long)Math.Floor(GetNow() / 60d);
            CleanupStats stats;
            if (!_hourlyStats.TryGetValue(currentMinute, out stats))
            {
                stats = new CleanupStats();
                _hourlyStats[currentMinute] = stats;
            }

            update(stats);
            PruneHourlyStats(currentMinute);
        }

        private void PruneHourlyStats(long currentMinute)
        {
            var oldestMinute = currentMinute - 59L;
            while (_hourlyStats.Count > 0 && _hourlyStats.First().Key < oldestMinute)
            {
                _hourlyStats.Remove(_hourlyStats.First().Key);
            }
        }

        private CleanupStats GetHourlyStats()
        {
            var currentMinute = (long)Math.Floor(GetNow() / 60d);
            PruneHourlyStats(currentMinute);
            var total = new CleanupStats();
            foreach (var stats in _hourlyStats.Values)
            {
                total.ContainersRemoved += stats.ContainersRemoved;
                total.ItemStacksRemoved += stats.ItemStacksRemoved;
                total.ItemAmountRemoved += stats.ItemAmountRemoved;
                total.JunkPilesRecycled += stats.JunkPilesRecycled;
                total.RoadsideGroupsRecycled += stats.RoadsideGroupsRecycled;
                total.DroppedWorldItemsRemoved += stats.DroppedWorldItemsRemoved;
                total.DroppedWorldItemAmountRemoved += stats.DroppedWorldItemAmountRemoved;
                total.ProtectedSkips += stats.ProtectedSkips;
                total.ReopenCancellations += stats.ReopenCancellations;
            }
            return total;
        }

        private string BuildHourlyStatus()
        {
            if (!configData.enableHourlyStatistics)
            {
                return "Hourly cleanup statistics are disabled in the configuration.";
            }

            var stats = GetHourlyStats();
            return string.Format(
                "Last 60 minutes: {0:N0} container(s) removed, {1:N0} container stack(s) / {2:N0} total container item(s) cleaned, {3:N0} junkpile(s) and {4:N0} roadside group(s) recycled. Discarded world loot: {5:N0} stack(s) / {6:N0} item(s). Protected skips: {7:N0}. Reopen cancellations: {8:N0}.",
                stats.ContainersRemoved,
                stats.ItemStacksRemoved,
                stats.ItemAmountRemoved,
                stats.JunkPilesRecycled,
                stats.RoadsideGroupsRecycled,
                stats.DroppedWorldItemsRemoved,
                stats.DroppedWorldItemAmountRemoved,
                stats.ProtectedSkips,
                stats.ReopenCancellations);
        }

        private bool IsExcludedDroppedItem(Item item)
        {
            if (item?.info == null)
            {
                return true;
            }

            if (configData.excludedDroppedItemShortNames.Contains(item.info.shortname, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!configData.preserveRareDroppedItems)
            {
                return false;
            }

            var rarity = item.info.rarity.ToString();
            return rarity.IndexOf("Rare", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TryScheduleDroppedWorldItemCleanup(Item item, WorldItem worldItem)
        {
            if (item == null || worldItem == null || worldItem.IsDestroyed || worldItem.net == null
                || IsExcludedDroppedItem(item) || IsPositionInPlayerBase(worldItem.transform.position)
                || IsDroppedItemEventProtected(item, worldItem))
            {
                return;
            }

            if (!IsNearTrackedWorldLootContainer(worldItem.transform.position))
            {
                return;
            }

            var entityId = worldItem.net.ID.Value;
            CancelDroppedItemCleanupTimer(entityId);
            var originalPosition = worldItem.transform.position;
            var originalItem = item;
            _droppedItemCleanupTimers[entityId] = timer.Once(Math.Max(30f, configData.droppedWorldLootLifetime), () =>
            {
                _droppedItemCleanupTimers.Remove(entityId);
                if (worldItem == null || worldItem.IsDestroyed || worldItem.net == null
                    || originalItem == null || !ReferenceEquals(worldItem.item, originalItem)
                    || IsExcludedDroppedItem(originalItem))
                {
                    return;
                }

                if (UnityEngine.Vector3.Distance(originalPosition, worldItem.transform.position) > configData.droppedWorldLootMovementTolerance
                    || IsPositionInPlayerBase(worldItem.transform.position)
                    || IsDroppedItemEventProtected(originalItem, worldItem))
                {
                    return;
                }

                var amount = Math.Max(0, originalItem.amount);
                AddStat(stats =>
                {
                    stats.DroppedWorldItemsRemoved++;
                    stats.DroppedWorldItemAmountRemoved += amount;
                });
                worldItem.Kill();
            });
        }

        private bool IsNearTrackedWorldLootContainer(UnityEngine.Vector3 position)
        {
            var containers = Pool.Get<List<LootContainer>>();
            try
            {
                Vis.Entities(position, Math.Max(1f, configData.droppedWorldLootTriggerRadius), containers, Layers.Solid);
                foreach (var container in containers)
                {
                    if (container != null && container.net != null
                        && _trackedLoot.ContainsKey(container.net.ID.Value)
                        && IsEligibleForCleanup(container, false))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref containers);
            }
            return false;
        }

        private bool IsPositionInPlayerBase(UnityEngine.Vector3 position)
        {
            var entities = Pool.Get<List<BaseEntity>>();
            try
            {
                Vis.Entities(position, Math.Max(5f, configData.droppedWorldLootBaseExclusionRadius), entities, Layers.Solid);
                return entities.Any(entity => entity is BuildingBlock || entity is BuildingPrivlidge);
            }
            finally
            {
                Pool.FreeUnmanaged(ref entities);
            }
        }

        private bool IsDroppedItemEventProtected(Item item, WorldItem worldItem)
        {
            if (configData.protectRaidableBasesContainers && RaidableBases != null)
            {
                var raidResult = RaidableBases.Call("EventTerritory", worldItem.transform.position, 0f);
                if (raidResult is bool && (bool)raidResult)
                {
                    return true;
                }
            }

            var hookResult = Interface.CallHook("CanLootBouncerCleanDroppedItem", item, worldItem);
            return hookResult is bool && !(bool)hookResult;
        }

        private void CancelDroppedItemCleanupTimer(ulong entityId)
        {
            Timer cleanupTimer;
            if (!_droppedItemCleanupTimers.TryGetValue(entityId, out cleanupTimer))
            {
                return;
            }

            cleanupTimer?.Destroy();
            _droppedItemCleanupTimers.Remove(entityId);
        }

        private float GetRoadsideCleanupRadius()
        {
            return Math.Max(1f, configData.maximumCleanupRadiusForRoadsideGroups);
        }

        private double GetStaleLootAgeThreshold()
        {
            return Math.Max(5d, configData.timeBeforeLootEmpty);
        }

        private void CancelJunkPileCleanupTimer(ulong junkPileId)
        {
            Timer cleanupTimer;
            if (!_junkPileCleanupTimers.TryGetValue(junkPileId, out cleanupTimer))
            {
                return;
            }

            cleanupTimer?.Destroy();
            _junkPileCleanupTimers.Remove(junkPileId);
        }

        private void CancelRoadsideVehicleCleanupTimer(ulong entityId)
        {
            Timer cleanupTimer;
            if (!_roadsideVehicleCleanupTimers.TryGetValue(entityId, out cleanupTimer))
            {
                return;
            }

            cleanupTimer?.Destroy();
            _roadsideVehicleCleanupTimers.Remove(entityId);
        }

        private static SpawnGroup GetSpawnGroup(BaseEntity entity)
        {
            return entity?.GetComponent<SpawnPointInstance>()?.parentSpawnPointUser as SpawnGroup;
        }

        private bool IsTrackedContainerStale(LootContainer lootContainer, LootTrackData track, double now)
        {
            if (track == null || lootContainer == null || lootContainer.IsDestroyed || GetInventoryItemCount(lootContainer) <= 0)
            {
                return false;
            }

            if (track.PendingCleanup)
            {
                return true;
            }

            return now - track.LastInteractionTime >= GetStaleLootAgeThreshold();
        }

        private bool TryGetJunkPileContainers(JunkPile junkPile, List<LootContainer> results)
        {
            if (junkPile == null || junkPile.net == null || junkPile.IsDestroyed || results == null)
            {
                return false;
            }

            results.Clear();
            Vis.Entities(junkPile.transform.position, GetRoadsideCleanupRadius(), results, Layers.Solid);
            return true;
        }

        private bool IsContainerInJunkPileGroup(LootContainer lootContainer, JunkPile junkPile)
        {
            if (lootContainer == null || junkPile == null || junkPile.spawngroups == null)
            {
                return false;
            }

            var lootSpawnGroup = GetSpawnGroup(lootContainer);
            return lootSpawnGroup != null && junkPile.spawngroups.Contains(lootSpawnGroup);
        }

        private static bool IsEntityInSpawnGroup(BaseEntity entity, SpawnGroup spawnGroup)
        {
            return entity != null && spawnGroup != null && ReferenceEquals(GetSpawnGroup(entity), spawnGroup);
        }

        private bool LooksLikeRoadsideVehicleAnchor(string shortPrefabName)
        {
            bool enabled;
            return !string.IsNullOrEmpty(shortPrefabName)
                && configData.roadsideVehicleAnchors.TryGetValue(shortPrefabName, out enabled)
                && enabled;
        }

        private static bool IsKnownRoadsideVehicleAnchorSeed(string shortPrefabName)
        {
            // Seed only a confirmed static roadside anchor. Broad fragments such as
            // "van" also match unrelated names like "advanced" and "vanity".
            return string.Equals(shortPrefabName, "shreddable_pickuptruck", StringComparison.Ordinal);
        }

        private bool TryFindAssociatedRoadsideVehicleAnchor(LootContainer lootContainer, out BaseEntity anchorEntity, out SpawnGroup spawnGroup)
        {
            anchorEntity = null;
            spawnGroup = GetSpawnGroup(lootContainer);
            if (lootContainer == null || lootContainer.net == null || spawnGroup == null)
            {
                return false;
            }

            var entityId = lootContainer.net.ID.Value;
            if (_roadsideVehicleAssociations.TryGetValue(entityId, out anchorEntity))
            {
                if (anchorEntity != null && !anchorEntity.IsDestroyed && anchorEntity.net != null && IsEntityInSpawnGroup(anchorEntity, spawnGroup))
                {
                    return true;
                }

                _roadsideVehicleAssociations.Remove(entityId);
                anchorEntity = null;
            }

            var nearbyEntities = Pool.Get<List<BaseEntity>>();
            nearbyEntities.Clear();

            try
            {
                Vis.Entities(lootContainer.transform.position, GetRoadsideCleanupRadius(), nearbyEntities, Layers.Solid);

                foreach (var nearbyEntity in nearbyEntities)
                {
                    if (nearbyEntity == null || nearbyEntity.IsDestroyed || nearbyEntity.net == null)
                    {
                        continue;
                    }

                    if (nearbyEntity == lootContainer || !LooksLikeRoadsideVehicleAnchor(nearbyEntity.ShortPrefabName) || !IsEntityInSpawnGroup(nearbyEntity, spawnGroup))
                    {
                        continue;
                    }

                    anchorEntity = nearbyEntity;
                    _roadsideVehicleAssociations[entityId] = anchorEntity;
                    return true;
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyEntities);
            }

            return false;
        }

        private bool TryGetRoadsideVehicleGroupContainers(BaseEntity anchorEntity, SpawnGroup spawnGroup, List<LootContainer> results)
        {
            if (anchorEntity == null || anchorEntity.net == null || anchorEntity.IsDestroyed || spawnGroup == null || results == null)
            {
                return false;
            }

            results.Clear();
            Vis.Entities(anchorEntity.transform.position, GetRoadsideCleanupRadius(), results, Layers.Solid);
            results.RemoveAll(container => container == null || container.IsDestroyed || container.net == null || !IsEntityInSpawnGroup(container, spawnGroup));
            return true;
        }

        private void ScheduleAssociatedSpawnBlockingCleanup(IEnumerable<LootContainer> nearbyLootContainers, Func<LootContainer, bool> belongsToGroup)
        {
            if (nearbyLootContainers == null || belongsToGroup == null)
            {
                return;
            }

            var now = GetNow();
            foreach (var nearbyLootContainer in nearbyLootContainers)
            {
                if (nearbyLootContainer == null || nearbyLootContainer.IsDestroyed || nearbyLootContainer.net == null)
                {
                    continue;
                }

                if (!belongsToGroup(nearbyLootContainer) || !IsEligibleForCleanup(nearbyLootContainer, false))
                {
                    continue;
                }

                var relatedEntityId = nearbyLootContainer.net.ID.Value;
                LootTrackData relatedTrack;
                if (!_trackedLoot.TryGetValue(relatedEntityId, out relatedTrack))
                {
                    continue;
                }

                if (!IsTrackedContainerStale(nearbyLootContainer, relatedTrack, now))
                {
                    continue;
                }

                ScheduleLootCleanup(relatedEntityId, nearbyLootContainer, relatedTrack);
            }
        }

        private void EvaluateAssociatedSpawnBlocking(LootContainer sourceContainer)
        {
            var junkPile = FindAssociatedJunkPile(sourceContainer);
            if (junkPile != null)
            {
                var nearbyLootContainers = Pool.Get<List<LootContainer>>();
                try
                {
                    if (!TryGetJunkPileContainers(junkPile, nearbyLootContainers))
                    {
                        return;
                    }

                    ScheduleAssociatedSpawnBlockingCleanup(nearbyLootContainers, nearbyLootContainer => IsContainerInJunkPileGroup(nearbyLootContainer, junkPile));
                    return;
                }
                finally
                {
                    Pool.FreeUnmanaged(ref nearbyLootContainers);
                }
            }

            BaseEntity anchorEntity;
            SpawnGroup spawnGroup;
            if (!TryFindAssociatedRoadsideVehicleAnchor(sourceContainer, out anchorEntity, out spawnGroup))
            {
                return;
            }

            var nearbyVehicleLootContainers = Pool.Get<List<LootContainer>>();
            try
            {
                if (!TryGetRoadsideVehicleGroupContainers(anchorEntity, spawnGroup, nearbyVehicleLootContainers))
                {
                    return;
                }

                ScheduleAssociatedSpawnBlockingCleanup(nearbyVehicleLootContainers, nearbyLootContainer => IsEntityInSpawnGroup(nearbyLootContainer, spawnGroup));
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyVehicleLootContainers);
            }
        }

        private void ScheduleLootCleanup(ulong entityId, LootContainer lootContainer, LootTrackData track)
        {
            if (track == null || lootContainer == null || lootContainer.IsDestroyed)
            {
                return;
            }

            track.PendingCleanup = true;
            track.Container = lootContainer;

            if (track.ActiveLooterIds.Count > 0)
            {
                return;
            }

            if (track.CleanupTimer != null && !track.CleanupTimer.Destroyed)
            {
                return;
            }

            var generation = ++track.TimerGeneration;
            track.CleanupTimer = timer.Once(Math.Max(1f, configData.timeBeforeLootEmpty), () =>
            {
                track.CleanupTimer = null;

                LootTrackData currentTrack;
                if (!_trackedLoot.TryGetValue(entityId, out currentTrack)
                    || !ReferenceEquals(currentTrack, track)
                    || currentTrack.TimerGeneration != generation
                    || currentTrack.ActiveLooterIds.Count > 0)
                {
                    return;
                }

                if (!IsEligibleForCleanup(lootContainer, true))
                {
                    currentTrack.PendingCleanup = false;
                    ClearLootTrackingIfUnused(entityId, currentTrack);
                    return;
                }

                DropItems(lootContainer, CleanupReason.Container);
            });
        }

        private void CancelLootCleanupTimer(LootTrackData track)
        {
            if (track == null || track.CleanupTimer == null)
            {
                return;
            }

            track.CleanupTimer.Destroy();
            track.CleanupTimer = null;
            track.TimerGeneration++;
        }

        private void ClearLootTrackingIfUnused(ulong entityId, LootTrackData track)
        {
            if (track == null)
            {
                return;
            }

            if (track.ActiveLooterIds.Count > 0)
            {
                return;
            }

            if (track.PendingCleanup)
            {
                return;
            }

            if (track.CleanupTimer != null && !track.CleanupTimer.Destroyed)
            {
                return;
            }

            _trackedLoot.Remove(entityId);
            _junkPileAssociations.Remove(entityId);
            _roadsideVehicleAssociations.Remove(entityId);
        }

        private void HandleLootContainerKill(LootContainer lootContainer)
        {
            var entityId = lootContainer.net.ID.Value;
            LootTrackData track;
            if (_trackedLoot.TryGetValue(entityId, out track))
            {
                CancelLootCleanupTimer(track);
                _trackedLoot.Remove(entityId);

                if (configData.slapPlayer && Slap != null)
                {
                    foreach (var playerId in track.ResponsiblePlayerIds)
                    {
                        var player = BasePlayer.FindByID(playerId);
                        if (player == null || player.IPlayer == null)
                        {
                            continue;
                        }

                        Slap.Call("SlapPlayer", player.IPlayer);
                        Print(player, Lang("SlapMessage", player.UserIDString));
                    }
                }
            }

            _junkPileAssociations.Remove(entityId);
            _roadsideVehicleAssociations.Remove(entityId);
        }

        private void HandleJunkPileKill(JunkPile junkPile)
        {
            var junkPileId = junkPile.net.ID.Value;

            CancelJunkPileCleanupTimer(junkPileId);
            _junkPileCleanupStates.Remove(junkPileId);

            var associatedContainerIds = Pool.Get<List<ulong>>();
            associatedContainerIds.Clear();

            try
            {
                foreach (var entry in _junkPileAssociations)
                {
                    if (entry.Value == junkPile)
                    {
                        associatedContainerIds.Add(entry.Key);
                    }
                }

                foreach (var containerId in associatedContainerIds)
                {
                    _junkPileAssociations.Remove(containerId);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref associatedContainerIds);
            }
        }

        private void HandleRoadsideVehicleKill(BaseEntity anchorEntity)
        {
            var entityId = anchorEntity.net.ID.Value;
            CancelRoadsideVehicleCleanupTimer(entityId);
            _roadsideVehicleCleanupStates.Remove(entityId);

            var associatedContainerIds = Pool.Get<List<ulong>>();
            associatedContainerIds.Clear();

            try
            {
                foreach (var entry in _roadsideVehicleAssociations)
                {
                    if (entry.Value == anchorEntity)
                    {
                        associatedContainerIds.Add(entry.Key);
                    }
                }

                foreach (var containerId in associatedContainerIds)
                {
                    _roadsideVehicleAssociations.Remove(containerId);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref associatedContainerIds);
            }
        }

        private void DropItems(LootContainer lootContainer, CleanupReason reason)
        {
            if (lootContainer == null || lootContainer.IsDestroyed)
            {
                return;
            }

            if (!IsEligibleForCleanup(lootContainer, true))
            {
                return;
            }

            if (lootContainer.net != null)
            {
                LootTrackData track;
                if (_trackedLoot.TryGetValue(lootContainer.net.ID.Value, out track))
                {
                    track.PendingCleanup = false;
                }
            }

            var inventory = lootContainer.inventory;
            var removedStacks = 0;
            long removedAmount = 0;
            if (inventory != null)
            {
                var snapshot = GetInventorySnapshot(lootContainer);
                removedStacks = snapshot.StackCount;
                removedAmount = snapshot.ItemAmount;

                if (configData.removeItems)
                {
                    inventory.Clear();
                }
                else if (inventory.itemList != null && inventory.itemList.Count > 0)
                {
                    DropUtil.DropItems(inventory, lootContainer.GetDropPosition());
                }
            }

            AddStat(stats =>
            {
                stats.ContainersRemoved++;
                stats.ItemStacksRemoved += removedStacks;
                stats.ItemAmountRemoved += removedAmount;
            });
            lootContainer.RemoveMe();
        }

        private void ScheduleAssociatedGroupCleanup(LootContainer lootContainer)
        {
            if (!configData.emptyJunkpile)
            {
                return;
            }

            if (FindAssociatedJunkPile(lootContainer) != null)
            {
                ScheduleJunkPileCleanup(lootContainer);
                return;
            }

            ScheduleRoadsideVehicleCleanup(lootContainer);
        }

        private bool HasProtectedOrActiveJunkPileContainer(JunkPile junkPile, IEnumerable<LootContainer> containers, out bool hasProtected, out bool hasActive)
        {
            hasProtected = false;
            hasActive = false;
            foreach (var container in containers)
            {
                if (container == null || container.net == null || !IsContainerInJunkPileGroup(container, junkPile))
                {
                    continue;
                }

                if (IsEventProtected(container))
                {
                    hasProtected = true;
                }

                LootTrackData track;
                if (_trackedLoot.TryGetValue(container.net.ID.Value, out track) && track.ActiveLooterIds.Count > 0)
                {
                    hasActive = true;
                }
            }
            return hasProtected || hasActive;
        }

        private bool HasProtectedOrActiveRoadsideContainer(SpawnGroup spawnGroup, IEnumerable<LootContainer> containers, out bool hasProtected, out bool hasActive)
        {
            hasProtected = false;
            hasActive = false;
            foreach (var container in containers)
            {
                if (container == null || container.net == null || !IsEntityInSpawnGroup(container, spawnGroup))
                {
                    continue;
                }

                if (IsEventProtected(container))
                {
                    hasProtected = true;
                }

                LootTrackData track;
                if (_trackedLoot.TryGetValue(container.net.ID.Value, out track) && track.ActiveLooterIds.Count > 0)
                {
                    hasActive = true;
                }
            }
            return hasProtected || hasActive;
        }

        private void RecordImplicitGroupCleanup(IEnumerable<LootContainer> containers, Func<LootContainer, bool> belongsToGroup)
        {
            long containerCount = 0;
            long stackCount = 0;
            long itemAmount = 0;
            foreach (var container in containers)
            {
                if (container == null || container.IsDestroyed || container.net == null
                    || !belongsToGroup(container) || !IsEligibleForCleanup(container, false))
                {
                    continue;
                }

                var snapshot = GetInventorySnapshot(container);
                containerCount++;
                stackCount += snapshot.StackCount;
                itemAmount += snapshot.ItemAmount;
            }

            AddStat(stats =>
            {
                stats.ContainersRemoved += containerCount;
                stats.ItemStacksRemoved += stackCount;
                stats.ItemAmountRemoved += itemAmount;
            });
        }

        private void ScheduleJunkPileCleanup(LootContainer lootContainer)
        {
            if (!configData.emptyJunkpile)
            {
                return;
            }

            var junkPile = FindAssociatedJunkPile(lootContainer);
            if (junkPile == null || junkPile.net == null || junkPile.IsDestroyed)
            {
                return;
            }

            var junkPileId = junkPile.net.ID.Value;
            GroupCleanupState state;
            if (!_junkPileCleanupStates.TryGetValue(junkPileId, out state))
            {
                state = CaptureJunkPileState(junkPile);
                _junkPileCleanupStates[junkPileId] = state;
            }

            ScheduleJunkPileTimer(junkPile, state, configData.timeBeforeJunkpileEmpty);
        }

        private GroupCleanupState CaptureJunkPileState(JunkPile junkPile)
        {
            var state = new GroupCleanupState();
            var containers = Pool.Get<List<LootContainer>>();
            try
            {
                if (TryGetJunkPileContainers(junkPile, containers))
                {
                    foreach (var container in containers)
                    {
                        if (container != null && container.net != null
                            && IsContainerInJunkPileGroup(container, junkPile)
                            && IsEligibleForCleanup(container, false))
                        {
                            state.OriginalContainerIds.Add(container.net.ID.Value);
                        }
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref containers);
            }
            return state;
        }

        private void ScheduleJunkPileTimer(JunkPile junkPile, GroupCleanupState state, float delay)
        {
            if (junkPile == null || junkPile.net == null || junkPile.IsDestroyed)
            {
                return;
            }

            var junkPileId = junkPile.net.ID.Value;
            Timer existingTimer;
            if (_junkPileCleanupTimers.TryGetValue(junkPileId, out existingTimer))
            {
                if (existingTimer != null && !existingTimer.Destroyed)
                {
                    return;
                }

                _junkPileCleanupTimers.Remove(junkPileId);
            }

            _junkPileCleanupTimers.Add(junkPileId, timer.Once(Math.Max(5f, delay), () =>
            {
                _junkPileCleanupTimers.Remove(junkPileId);

                if (junkPile == null || junkPile.IsDestroyed || junkPile.net == null)
                {
                    return;
                }

                var nearbyLootContainers = Pool.Get<List<LootContainer>>();
                try
                {
                    if (!TryGetJunkPileContainers(junkPile, nearbyLootContainers))
                    {
                        return;
                    }

                    if (HasProtectedOrActiveJunkPileContainer(junkPile, nearbyLootContainers, out var hasProtected, out var hasActive))
                    {
                        if (hasProtected)
                        {
                            _junkPileCleanupStates.Remove(junkPileId);
                            return;
                        }

                        if (hasActive)
                        {
                            ScheduleJunkPileTimer(junkPile, state, configData.groupRetryDelay);
                            return;
                        }
                    }

                    var remainingContainersWithLoot = 0;

                    foreach (var nearbyLootContainer in nearbyLootContainers)
                    {
                        if (nearbyLootContainer == null || nearbyLootContainer.IsDestroyed || nearbyLootContainer.net == null)
                        {
                            continue;
                        }

                        if (!IsContainerInJunkPileGroup(nearbyLootContainer, junkPile)
                            || !state.OriginalContainerIds.Contains(nearbyLootContainer.net.ID.Value)
                            || !IsEligibleForCleanup(nearbyLootContainer, false))
                        {
                            continue;
                        }

                        if (GetInventoryItemCount(nearbyLootContainer) > 0)
                        {
                            remainingContainersWithLoot++;
                        }
                    }

                    var originalCount = state.OriginalContainerIds.Count;
                    var lootedCount = Math.Max(0, originalCount - remainingContainersWithLoot);
                    var lootedRatio = originalCount == 0 ? 1f : (float)lootedCount / originalCount;
                    var shouldDestroyJunkPile = remainingContainersWithLoot == 0
                        || lootedRatio >= configData.junkPileCleanupThreshold
                        || state.RetryCount >= configData.maximumGroupCleanupRetries;

                    if (!shouldDestroyJunkPile)
                    {
                        state.RetryCount++;
                        ScheduleJunkPileTimer(junkPile, state, configData.groupRetryDelay);
                        return;
                    }

                    if (configData.dropNearbyLoot)
                    {
                        DropNearbyJunkPileLoot(junkPile);
                    }
                    else
                    {
                        RecordImplicitGroupCleanup(nearbyLootContainers, container => IsContainerInJunkPileGroup(container, junkPile));
                    }

                    CancelJunkPileCleanupTimer(junkPileId);
                    _junkPileCleanupStates.Remove(junkPileId);
                    AddStat(stats => stats.JunkPilesRecycled++);
                    junkPile.SinkAndDestroy();
                }
                finally
                {
                    Pool.FreeUnmanaged(ref nearbyLootContainers);
                }
            }));
        }

        private void ScheduleRoadsideVehicleCleanup(LootContainer lootContainer)
        {
            BaseEntity anchorEntity;
            SpawnGroup spawnGroup;
            if (!TryFindAssociatedRoadsideVehicleAnchor(lootContainer, out anchorEntity, out spawnGroup) || anchorEntity == null || anchorEntity.net == null)
            {
                return;
            }

            var anchorEntityId = anchorEntity.net.ID.Value;
            GroupCleanupState state;
            if (!_roadsideVehicleCleanupStates.TryGetValue(anchorEntityId, out state))
            {
                state = CaptureRoadsideVehicleState(anchorEntity, spawnGroup);
                _roadsideVehicleCleanupStates[anchorEntityId] = state;
            }

            ScheduleRoadsideVehicleTimer(anchorEntity, spawnGroup, state, configData.timeBeforeJunkpileEmpty);
        }

        private GroupCleanupState CaptureRoadsideVehicleState(BaseEntity anchorEntity, SpawnGroup spawnGroup)
        {
            var state = new GroupCleanupState();
            var containers = Pool.Get<List<LootContainer>>();
            try
            {
                if (TryGetRoadsideVehicleGroupContainers(anchorEntity, spawnGroup, containers))
                {
                    foreach (var container in containers)
                    {
                        if (container != null && container.net != null && IsEligibleForCleanup(container, false))
                        {
                            state.OriginalContainerIds.Add(container.net.ID.Value);
                        }
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref containers);
            }
            return state;
        }

        private void ScheduleRoadsideVehicleTimer(BaseEntity anchorEntity, SpawnGroup spawnGroup, GroupCleanupState state, float delay)
        {
            if (anchorEntity == null || anchorEntity.net == null || anchorEntity.IsDestroyed)
            {
                return;
            }

            var anchorEntityId = anchorEntity.net.ID.Value;
            Timer existingTimer;
            if (_roadsideVehicleCleanupTimers.TryGetValue(anchorEntityId, out existingTimer))
            {
                if (existingTimer != null && !existingTimer.Destroyed)
                {
                    return;
                }

                _roadsideVehicleCleanupTimers.Remove(anchorEntityId);
            }

            _roadsideVehicleCleanupTimers.Add(anchorEntityId, timer.Once(Math.Max(5f, delay), () =>
            {
                _roadsideVehicleCleanupTimers.Remove(anchorEntityId);

                if (anchorEntity == null || anchorEntity.IsDestroyed || anchorEntity.net == null)
                {
                    return;
                }

                var nearbyLootContainers = Pool.Get<List<LootContainer>>();
                try
                {
                    if (!TryGetRoadsideVehicleGroupContainers(anchorEntity, spawnGroup, nearbyLootContainers))
                    {
                        return;
                    }

                    if (HasProtectedOrActiveRoadsideContainer(spawnGroup, nearbyLootContainers, out var hasProtected, out var hasActive))
                    {
                        if (hasProtected)
                        {
                            _roadsideVehicleCleanupStates.Remove(anchorEntityId);
                            return;
                        }

                        if (hasActive)
                        {
                            ScheduleRoadsideVehicleTimer(anchorEntity, spawnGroup, state, configData.groupRetryDelay);
                            return;
                        }
                    }

                    var remainingContainersWithLoot = 0;

                    foreach (var nearbyLootContainer in nearbyLootContainers)
                    {
                        if (nearbyLootContainer == null || nearbyLootContainer.IsDestroyed || nearbyLootContainer.net == null)
                        {
                            continue;
                        }

                        if (!IsEntityInSpawnGroup(nearbyLootContainer, spawnGroup)
                            || !state.OriginalContainerIds.Contains(nearbyLootContainer.net.ID.Value)
                            || !IsEligibleForCleanup(nearbyLootContainer, false))
                        {
                            continue;
                        }

                        if (GetInventoryItemCount(nearbyLootContainer) > 0)
                        {
                            remainingContainersWithLoot++;
                        }
                    }

                    var originalCount = state.OriginalContainerIds.Count;
                    var lootedCount = Math.Max(0, originalCount - remainingContainersWithLoot);
                    var lootedRatio = originalCount == 0 ? 1f : (float)lootedCount / originalCount;
                    var shouldDestroyGroup = remainingContainersWithLoot == 0
                        || lootedRatio >= configData.junkPileCleanupThreshold
                        || state.RetryCount >= configData.maximumGroupCleanupRetries;

                    if (!shouldDestroyGroup)
                    {
                        state.RetryCount++;
                        ScheduleRoadsideVehicleTimer(anchorEntity, spawnGroup, state, configData.groupRetryDelay);
                        return;
                    }

                    DropNearbyRoadsideVehicleLoot(anchorEntity, spawnGroup);
                    DestroyRoadsideVehicleAnchors(anchorEntity, spawnGroup);
                    CancelRoadsideVehicleCleanupTimer(anchorEntityId);
                    _roadsideVehicleCleanupStates.Remove(anchorEntityId);
                    AddStat(stats => stats.RoadsideGroupsRecycled++);
                }
                finally
                {
                    Pool.FreeUnmanaged(ref nearbyLootContainers);
                }
            }));
        }

        private void DestroyRoadsideVehicleAnchors(BaseEntity anchorEntity, SpawnGroup spawnGroup)
        {
            if (anchorEntity == null || anchorEntity.IsDestroyed || spawnGroup == null)
            {
                return;
            }

            var nearbyEntities = Pool.Get<List<BaseEntity>>();
            nearbyEntities.Clear();

            try
            {
                Vis.Entities(anchorEntity.transform.position, GetRoadsideCleanupRadius(), nearbyEntities, Layers.Solid);

                foreach (var nearbyEntity in nearbyEntities)
                {
                    if (nearbyEntity == null || nearbyEntity.IsDestroyed || nearbyEntity.net == null)
                    {
                        continue;
                    }

                    if (LooksLikeRoadsideVehicleAnchor(nearbyEntity.ShortPrefabName) && IsEntityInSpawnGroup(nearbyEntity, spawnGroup))
                    {
                        nearbyEntity.Kill();
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyEntities);
            }
        }

        private void DropNearbyRoadsideVehicleLoot(BaseEntity anchorEntity, SpawnGroup spawnGroup)
        {
            if (anchorEntity == null || anchorEntity.IsDestroyed || spawnGroup == null)
            {
                return;
            }

            var nearbyLootContainers = Pool.Get<List<LootContainer>>();
            nearbyLootContainers.Clear();

            try
            {
                if (!TryGetRoadsideVehicleGroupContainers(anchorEntity, spawnGroup, nearbyLootContainers))
                {
                    return;
                }

                foreach (var nearbyLootContainer in nearbyLootContainers)
                {
                    if (nearbyLootContainer == null || nearbyLootContainer.IsDestroyed || nearbyLootContainer.net == null)
                    {
                        continue;
                    }

                    if (IsEntityInSpawnGroup(nearbyLootContainer, spawnGroup))
                    {
                        DropItems(nearbyLootContainer, CleanupReason.RoadsideVehicle);
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyLootContainers);
            }
        }

        private JunkPile FindAssociatedJunkPile(LootContainer lootContainer)
        {
            if (lootContainer == null || lootContainer.net == null)
            {
                return null;
            }

            var entityId = lootContainer.net.ID.Value;
            JunkPile junkPile;
            if (_junkPileAssociations.TryGetValue(entityId, out junkPile))
            {
                if (junkPile != null && !junkPile.IsDestroyed && junkPile.net != null)
                {
                    return junkPile;
                }

                _junkPileAssociations.Remove(entityId);
            }

            var spawnGroup = lootContainer.GetComponent<SpawnPointInstance>()?.parentSpawnPointUser as SpawnGroup;
            if (spawnGroup == null)
            {
                return null;
            }

            var nearbyJunkPiles = Pool.Get<List<JunkPile>>();
            nearbyJunkPiles.Clear();

            JunkPile matchedJunkPile = null;
            try
            {
                Vis.Entities(lootContainer.transform.position, GetRoadsideCleanupRadius(), nearbyJunkPiles, Layers.Solid);

                foreach (var nearbyJunkPile in nearbyJunkPiles)
                {
                    if (nearbyJunkPile == null || nearbyJunkPile.spawngroups == null)
                    {
                        continue;
                    }

                    if (nearbyJunkPile.spawngroups.Contains(spawnGroup))
                    {
                        matchedJunkPile = nearbyJunkPile;
                        break;
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyJunkPiles);
            }

            if (matchedJunkPile != null)
            {
                _junkPileAssociations[entityId] = matchedJunkPile;
            }

            return matchedJunkPile;
        }

        private void DropNearbyJunkPileLoot(JunkPile junkPile)
        {
            var nearbyLootContainers = Pool.Get<List<LootContainer>>();
            nearbyLootContainers.Clear();

            try
            {
                Vis.Entities(junkPile.transform.position, GetRoadsideCleanupRadius(), nearbyLootContainers, Layers.Solid);

                foreach (var nearbyLootContainer in nearbyLootContainers)
                {
                    if (nearbyLootContainer == null || nearbyLootContainer.IsDestroyed)
                    {
                        continue;
                    }

                    if (IsContainerInJunkPileGroup(nearbyLootContainer, junkPile))
                    {
                        DropItems(nearbyLootContainer, CleanupReason.JunkPile);
                    }
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref nearbyLootContainers);
            }
        }

        private void RefreshContainerCaches()
        {
            _barrelShortPrefabNames.Clear();

            var configChanged = false;
            foreach (var prefab in GameManifest.Current.entities)
            {
                var prefabObject = GameManager.server.FindPrefab(prefab.ToLowerInvariant());
                var baseEntity = prefabObject?.GetComponent<BaseEntity>();
                if (baseEntity != null
                    && !string.IsNullOrEmpty(baseEntity.ShortPrefabName)
                    && IsKnownRoadsideVehicleAnchorSeed(baseEntity.ShortPrefabName)
                    && !configData.roadsideVehicleAnchors.ContainsKey(baseEntity.ShortPrefabName))
                {
                    configData.roadsideVehicleAnchors.Add(baseEntity.ShortPrefabName, !configData.roadsideVehicleAnchorsInitialized);
                    configChanged = true;
                }

                var lootContainer = prefabObject?.GetComponent<LootContainer>();
                if (lootContainer == null || string.IsNullOrEmpty(lootContainer.ShortPrefabName))
                {
                    continue;
                }

                if (LooksLikeBarrelOrRoadsign(lootContainer.ShortPrefabName))
                {
                    _barrelShortPrefabNames.Add(lootContainer.ShortPrefabName);
                }

                if (configData.lootContainers.ContainsKey(lootContainer.ShortPrefabName))
                {
                    continue;
                }

                configData.lootContainers.Add(lootContainer.ShortPrefabName,
                    configData.enableNewContainersByDefault && !LooksLikeDisabledByDefault(lootContainer.ShortPrefabName));
                configChanged = true;
            }

            if (!configData.roadsideVehicleAnchorsInitialized)
            {
                configData.roadsideVehicleAnchorsInitialized = true;
                configChanged = true;
            }

            if (configChanged)
            {
                SaveConfig();
            }
        }

        private static bool LooksLikeBarrelOrRoadsign(string shortPrefabName)
        {
            return shortPrefabName.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0
                || shortPrefabName.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeDisabledByDefault(string shortPrefabName)
        {
            return shortPrefabName.IndexOf("stocking", StringComparison.OrdinalIgnoreCase) >= 0
                || shortPrefabName.IndexOf("roadsign", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion Methods

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Time before loot containers are emptied (seconds)")]
            public float timeBeforeLootEmpty = 30f;

            [JsonProperty(PropertyName = "Empty the entire junkpile when automatically empty loot")]
            public bool emptyJunkpile = true;

            [JsonProperty(PropertyName = "Junkpile cleanup threshold")]
            public float junkPileCleanupThreshold = 0.6f;

            [JsonProperty(PropertyName = "Maximum cleanup radius for roadside groups")]
            public float maximumCleanupRadiusForRoadsideGroups = 12f;

            [JsonProperty(PropertyName = "Empty the nearby loot when emptying junkpile")]
            public bool dropNearbyLoot = false;

            [JsonProperty(PropertyName = "Time before junkpiles are emptied (seconds)")]
            public float timeBeforeJunkpileEmpty = 150f;

            [JsonProperty(PropertyName = "Slaps players who don't empty containers")]
            public bool slapPlayer = false;

            [JsonProperty(PropertyName = "Remove items instead of dropping them")]
            public bool removeItems = true;

            [JsonProperty(PropertyName = "Enable newly discovered loot containers by default")]
            public bool enableNewContainersByDefault;

            [JsonProperty(PropertyName = "Protect AirfieldEvent containers")]
            public bool protectAirfieldEventContainers = true;

            [JsonProperty(PropertyName = "Protect MonumentAddons containers")]
            public bool protectMonumentAddonsContainers = true;

            [JsonProperty(PropertyName = "Protect RaidableBases event territory")]
            public bool protectRaidableBasesContainers = true;

            [JsonProperty(PropertyName = "Enable rolling 60-minute cleanup statistics")]
            public bool enableHourlyStatistics = true;

            [JsonProperty(PropertyName = "Clean discarded world items near tracked loot containers")]
            public bool cleanupDroppedWorldLoot = true;

            [JsonProperty(PropertyName = "Discarded world item cleanup delay (seconds)")]
            public float droppedWorldLootLifetime = 120f;

            [JsonProperty(PropertyName = "Discarded world item trigger radius")]
            public float droppedWorldLootTriggerRadius = 8f;

            [JsonProperty(PropertyName = "Player base exclusion radius for discarded world items")]
            public float droppedWorldLootBaseExclusionRadius = 30f;

            [JsonProperty(PropertyName = "Discarded world item movement tolerance")]
            public float droppedWorldLootMovementTolerance = 3f;

            [JsonProperty(PropertyName = "Preserve rare discarded world items")]
            public bool preserveRareDroppedItems = true;

            [JsonProperty(PropertyName = "Discarded world item shortname exclusions")]
            public List<string> excludedDroppedItemShortNames = new List<string>
            {
                "scrap", "metal.refined", "explosives", "gunpowder", "sulfur", "sulfur.ore",
                "metal.fragments", "metal.ore", "lowgradefuel", "crude.oil", "techparts",
                "riflebody", "smgbody", "semibody", "targeting.computer", "cctv.camera"
            };

            [JsonProperty(PropertyName = "Spawn-group retry delay (seconds)")]
            public float groupRetryDelay = 30f;

            [JsonProperty(PropertyName = "Maximum spawn-group cleanup retries")]
            public int maximumGroupCleanupRetries = 4;

            [JsonProperty(PropertyName = "Roadside vehicle anchor settings")]
            public Dictionary<string, bool> roadsideVehicleAnchors = new Dictionary<string, bool>(StringComparer.Ordinal);

            [JsonProperty(PropertyName = "Roadside vehicle anchor list initialized")]
            public bool roadsideVehicleAnchorsInitialized;

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings chat = new ChatSettings();

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Chat Prefix")]
                public string prefix = "<color=#00FFFF>[Loot Bouncer]</color>: ";

                [JsonProperty(PropertyName = "Chat SteamID Icon")]
                public ulong steamIDIcon;
            }

            [JsonProperty(PropertyName = "Loot container settings")]
            public Dictionary<string, bool> lootContainers = new Dictionary<string, bool>(StringComparer.Ordinal);

            [JsonProperty(PropertyName = "Version")]
            public VersionNumber version;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            var configChanged = false;
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                    configChanged = true;
                }
                else
                {
                    configChanged = UpdateConfigValues();
                }
            }
            catch (Exception ex)
            {
                PrintError($"The configuration file is corrupted. \n{ex}");
                BackupCorruptedConfig();
                LoadDefaultConfig();
                configChanged = true;
                _configRecoveryMode = true;
                PrintError("Cleanup has been disabled for this load. Review the regenerated configuration and reload LootBouncer to resume cleanup.");
            }

            if (configChanged)
            {
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData
            {
                version = Version
            };
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        private bool UpdateConfigValues()
        {
            var configChanged = false;

            if (configData.chat == null)
            {
                configData.chat = new ConfigData.ChatSettings();
                configChanged = true;
            }

            if (configData.lootContainers == null)
            {
                configData.lootContainers = new Dictionary<string, bool>(StringComparer.Ordinal);
                configChanged = true;
            }

            if (configData.roadsideVehicleAnchors == null)
            {
                configData.roadsideVehicleAnchors = new Dictionary<string, bool>(StringComparer.Ordinal);
                configData.roadsideVehicleAnchorsInitialized = false;
                configChanged = true;
            }

            if (configData.excludedDroppedItemShortNames == null)
            {
                configData.excludedDroppedItemShortNames = new ConfigData().excludedDroppedItemShortNames;
                configChanged = true;
            }

            var validatedLootDelay = Math.Max(1f, configData.timeBeforeLootEmpty);
            if (Math.Abs(configData.timeBeforeLootEmpty - validatedLootDelay) > 0.001f)
            {
                configData.timeBeforeLootEmpty = validatedLootDelay;
                configChanged = true;
            }

            var validatedGroupDelay = Math.Max(5f, configData.timeBeforeJunkpileEmpty);
            if (Math.Abs(configData.timeBeforeJunkpileEmpty - validatedGroupDelay) > 0.001f)
            {
                configData.timeBeforeJunkpileEmpty = validatedGroupDelay;
                configChanged = true;
            }

            var validatedRetryDelay = Math.Max(5f, configData.groupRetryDelay);
            if (Math.Abs(configData.groupRetryDelay - validatedRetryDelay) > 0.001f)
            {
                configData.groupRetryDelay = validatedRetryDelay;
                configChanged = true;
            }

            var validatedRetries = Math.Max(0, Math.Min(20, configData.maximumGroupCleanupRetries));
            if (configData.maximumGroupCleanupRetries != validatedRetries)
            {
                configData.maximumGroupCleanupRetries = validatedRetries;
                configChanged = true;
            }

            var validatedDroppedLifetime = Math.Max(30f, configData.droppedWorldLootLifetime);
            if (Math.Abs(configData.droppedWorldLootLifetime - validatedDroppedLifetime) > 0.001f)
            {
                configData.droppedWorldLootLifetime = validatedDroppedLifetime;
                configChanged = true;
            }

            var validatedTriggerRadius = Math.Max(1f, Math.Min(20f, configData.droppedWorldLootTriggerRadius));
            if (Math.Abs(configData.droppedWorldLootTriggerRadius - validatedTriggerRadius) > 0.001f)
            {
                configData.droppedWorldLootTriggerRadius = validatedTriggerRadius;
                configChanged = true;
            }

            var validatedBaseRadius = Math.Max(5f, Math.Min(100f, configData.droppedWorldLootBaseExclusionRadius));
            if (Math.Abs(configData.droppedWorldLootBaseExclusionRadius - validatedBaseRadius) > 0.001f)
            {
                configData.droppedWorldLootBaseExclusionRadius = validatedBaseRadius;
                configChanged = true;
            }

            var validatedMovementTolerance = Math.Max(0.5f, Math.Min(20f, configData.droppedWorldLootMovementTolerance));
            if (Math.Abs(configData.droppedWorldLootMovementTolerance - validatedMovementTolerance) > 0.001f)
            {
                configData.droppedWorldLootMovementTolerance = validatedMovementTolerance;
                configChanged = true;
            }

            var validatedThreshold = Math.Min(1f, Math.Max(0f, configData.junkPileCleanupThreshold));
            if (Math.Abs(configData.junkPileCleanupThreshold - validatedThreshold) > 0.001f)
            {
                configData.junkPileCleanupThreshold = validatedThreshold;
                configChanged = true;
            }

            if (configData.maximumCleanupRadiusForRoadsideGroups <= 0f)
            {
                configData.maximumCleanupRadiusForRoadsideGroups = 12f;
                configChanged = true;
            }

            if (configData.version < Version)
            {
                if (configData.version <= default(VersionNumber))
                {
                    string prefix;
                    string prefixColor;
                    if (GetConfigValue(out prefix, "Chat prefix") && GetConfigValue(out prefixColor, "Chat prefix color"))
                    {
                        configData.chat.prefix = $"<color={prefixColor}>{prefix}</color>: ";
                    }

                    ulong steamId;
                    if (GetConfigValue(out steamId, "Chat steamID icon"))
                    {
                        configData.chat.steamIDIcon = steamId;
                    }
                }

                configData.version = Version;
                configChanged = true;
            }

            return configChanged;
        }

        private void BackupCorruptedConfig()
        {
            try
            {
                var sourcePath = Path.Combine(Interface.Oxide.ConfigDirectory, Name + ".json");
                if (!File.Exists(sourcePath))
                {
                    return;
                }

                var backupPath = Path.Combine(
                    Interface.Oxide.ConfigDirectory,
                    string.Format("{0}.corrupt.{1:yyyyMMdd-HHmmss}.json", Name, DateTime.UtcNow));
                File.Copy(sourcePath, backupPath, false);
                PrintWarning("The unreadable configuration was backed up to " + backupPath);
            }
            catch (Exception ex)
            {
                PrintError("Unable to back up the corrupted configuration: " + ex.Message);
            }
        }

        private bool GetConfigValue<T>(out T value, params string[] path)
        {
            var configValue = Config.Get(path);
            if (configValue == null)
            {
                value = default(T);
                return false;
            }

            value = Config.ConvertValue<T>(configValue);
            return true;
        }

        #endregion ConfigurationFile

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, configData.chat.prefix, configData.chat.steamIDIcon);
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            try
            {
                return string.Format(lang.GetMessage(key, this, id), args);
            }
            catch (Exception)
            {
                PrintError($"Error in the language formatting of '{key}'. (userid: {id}. lang: {lang.GetLanguage(id)}. args: {string.Join(" ,", args)})");
                throw;
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["SlapMessage"] = "You left loot behind, so the container slapped you.",
                ["NoPermission"] = "You do not have permission to view LootBouncer statistics."
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["SlapMessage"] = "wdnmd，不清空容器，给你个大耳刮子",
                ["NoPermission"] = "You do not have permission to view LootBouncer statistics."
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}
