# LootBouncer (Enhanced Edition)

**Version:** 1.4.2
**Enhanced by:** SeesAll

LootBouncer removes abandoned leftovers from partially looted Rust world containers so their spawn groups can recycle normally. The enhanced edition adds safer timers, event protection, roadside group handling, bounded discarded-world-loot cleanup, and rolling administrator statistics.

## Credits

LootBouncer builds on the original plugin by Sorrow and Arainrr, with uMod listing credit to VisEntities. The enhanced branch is maintained by SeesAll.

## Version 1.4 safety improvements

- Protects AirfieldEvent crates identified as `airfieldcrate`.
- Protects entities inside active RaidableBases event territory.
- Protects MonumentAddons-created loot containers.
- Provides `API_ProtectEntity`, `API_UnprotectEntity`, `CanLootBouncerProcess`, and `CanLootBouncerCleanDroppedItem` integration points for other plugins.
- Waits until the final active looter closes a container.
- Tracks active looters separately from players responsible for leaving loot.
- Detects partial removal from a stack by comparing total quantities, not only stack count.
- Revalidates entity eligibility, active looters, and timer generation before cleanup.
- Cleans disconnected-player tracking safely.
- Takes a stable snapshot of roadside group membership and retries group cleanup a bounded number of times.
- Uses an exact, configurable roadside-anchor allowlist. A confirmed static roadside pickup-truck anchor is seeded during migration; newly discovered candidates remain disabled.
- Newly discovered loot-container types default to disabled unless the administrator explicitly opts in.
- Backs up an unreadable configuration and disables cleanup for that load instead of silently continuing destructively.
- Maintains rolling 60-minute counters in memory without per-cleanup chat messages or an ever-growing log.

## Partial container cleanup

When a player removes any quantity from a world loot container and leaves items behind, LootBouncer schedules the container for cleanup. Reopening the container cancels the pending timer. Cleanup is scheduled again only after the last active looter closes it.

The default container delay is 30 seconds. Existing server values are preserved during upgrade.

## Event protection

Event safety is enabled by default for:

- AirfieldEvent
- RaidableBases active event territory
- MonumentAddons-created entities
- Trade boxes

Other plugins can veto cleanup by returning `false` from `CanLootBouncerProcess(LootContainer container)`, or can register individual entities through `API_ProtectEntity(BaseEntity entity)` and `API_UnprotectEntity(BaseEntity entity)`.

## Junkpiles and roadside vehicles

The first interaction records the original set of eligible containers in the spawn group. Later checks calculate progress against that stable snapshot, so containers already removed are still counted as looted.

If the configured threshold has not been met, the plugin retries after a configurable delay. After the configured maximum number of retries, an abandoned group may be recycled as a failsafe. An actively looted or event-protected group is never force-cleaned.

Roadside vehicle anchors use exact short-prefab names stored in the configuration instead of unrestricted name-fragment matching.

## Discarded world loot

Version 1.4 can shorten the lifetime of items discarded close to a LootBouncer-tracked world container. This is intended for unwanted items thrown beside monument and roadside crates.

The feature does not target backpacks. A dropped item is also preserved when:

- it is within the configured player-base exclusion radius;
- it is in protected RaidableBases territory;
- another plugin vetoes cleanup;
- it has moved beyond the configured tolerance;
- it is rare and rare-item protection is enabled; or
- its shortname is in the configurable exclusion list.

The default grace period is 120 seconds. Detection requires the item to be dropped within 8 metres of a currently tracked world-loot container. The default player-base exclusion radius is 30 metres.

The exclusion list is replaced as a complete JSON collection when read and is normalized on load. This prevents repeated plugin reloads from appending duplicate default entries while preserving custom shortnames.

Other plugins can veto this cleanup by returning `false` from `CanLootBouncerCleanDroppedItem(Item item, WorldItem worldItem)`.

## Administrator statistics

LootBouncer does not announce every cleanup. Instead, it stores one small aggregate bucket per minute in memory and retains only the most recent 60 minutes. No individual item history is stored and the counters are not written to disk.

Chat command:

```text
/lbstatus
```

Server or F1 console command:

```text
lootbouncer.status
```

The commands report container removals, cleaned stack and item quantities, recycled groups, discarded world items, protected skips, and reopen cancellations.

Access is granted to server administrators and users with:

```text
lootbouncer.admin
```

## Important configuration defaults

```json
{
  "Time before loot containers are emptied (seconds)": 30.0,
  "Empty the entire junkpile when automatically empty loot": true,
  "Junkpile cleanup threshold": 0.6,
  "Maximum cleanup radius for roadside groups": 12.0,
  "Empty the nearby loot when emptying junkpile": false,
  "Time before junkpiles are emptied (seconds)": 150.0,
  "Remove items instead of dropping them": true,
  "Enable newly discovered loot containers by default": false,
  "Protect AirfieldEvent containers": true,
  "Protect MonumentAddons containers": true,
  "Protect RaidableBases event territory": true,
  "Enable rolling 60-minute cleanup statistics": true,
  "Clean discarded world items near tracked loot containers": true,
  "Discarded world item cleanup delay (seconds)": 120.0,
  "Discarded world item trigger radius": 8.0,
  "Player base exclusion radius for discarded world items": 30.0,
  "Discarded world item movement tolerance": 3.0,
  "Preserve rare discarded world items": true,
  "Spawn-group retry delay (seconds)": 30.0,
  "Maximum spawn-group cleanup retries": 4
}
```

Existing loot-container choices and timing preferences are retained when upgrading. New fields are merged into the existing configuration.

## Installation

Place `LootBouncer.cs` in `oxide/plugins`. Oxide will compile and load it automatically. To reload manually:

```text
oxide.reload LootBouncer
```

The configuration is stored at `oxide/config/LootBouncer.json`.

## Performance

The plugin is event-driven. It performs localized pooled searches when a relevant interaction occurs and uses bounded timers rather than a permanent whole-map scan. Rolling statistics contain at most 60 aggregate entries.

## License

This repository uses the MIT License.
