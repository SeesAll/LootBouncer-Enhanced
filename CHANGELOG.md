# Changelog

## 1.4.1 - 2026-09-26

- Restricted the initial roadside-anchor migration to the confirmed `shreddable_pickuptruck` prefab.
- Prevented broad legacy fragments such as `van` from matching unrelated names including `advanced`, `vanity`, and `vanilla`.

## 1.4.0 - 2026-09-26

### Added

- AirfieldEvent, RaidableBases, and MonumentAddons protection.
- General entity protection APIs and cleanup-veto hooks for other plugins.
- Rolling in-memory 60-minute cleanup statistics.
- Admin-only `/lbstatus` and `lootbouncer.status` commands.
- Optional cleanup of discarded world items near tracked world-loot containers.
- Player-base, event-territory, movement, rarity, and shortname safeguards for discarded items.
- Stable spawn-group membership snapshots and bounded retry controls.
- Exact configurable roadside vehicle-anchor allowlist.
- Backup and safe-load behaviour for unreadable configuration files.

### Changed

- Container changes are now quantity-aware, including partial stack removal.
- Cleanup waits until the last active looter closes the container.
- Active looters and responsible players are tracked separately.
- Timers revalidate generation, active-looter state, configuration, and event protection before cleanup.
- Newly discovered loot containers and roadside anchors default to disabled after the initial migration.
- Junkpile threshold calculations retain containers already removed from the world.
- Group cleanup retries are bounded and never override active-looter or event protection.
- Configuration values are range-validated while preserving existing administrator choices.

### Fixed

- Prevented one player from scheduling destructive cleanup while another player still had the container open.
- Fixed partial stack withdrawals not being recognized as looting.
- Fixed stale player tracking after disconnects and partial-loot closes.
- Fixed junkpile threshold calculations forgetting previously removed containers.
- Fixed the documented failsafe ending permanently after its first unsuccessful check.

## 1.3.1

- Added current Facepunch pooled-list API compatibility.
- Improved roadside vehicle and junkpile handling.
- Improved timer and unload cleanup.
