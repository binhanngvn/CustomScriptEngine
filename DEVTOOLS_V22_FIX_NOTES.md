# DevTools 2.2.0 FIX

The previous V2 DevTools was kept deliberately too invasive and broke several old behaviors.

This version restores the stable camera behavior from the original DevTools and removes the
weapon-disable and free-camera features entirely.

## Fixed
- Player discovery no longer depends only on the Unity Player tag.
  - tries the tag
  - PlayerInputComponent -> Entity(id=Player)
  - PlayerWeaponComponent -> Entity(id=Player)
  - MovementComponent -> Entity(id=Player)
  - final scan of Entity(id=Player)
- Spawn/place always uses the player's feet as the anchor.
- Added an explicit SPAWN UNDER PLAYER FEET button.
- Prefab registry explicitly reads CelSector EntitySpawner._entityPrefab and
  PreplacedEnemySpawner._enemyPrefab, and also scans SpawnDirectorSimple/SimpleSpawnCard.
- Noclip is the original collider toggle behavior.
- Fly disables player movement/physics temporarily and restores previous state.
- God Mode is AI-stealth only. It does not change Player HP.
  It redirects the known enemy target fields and adds a true blocker to known AI components,
  including AI_MoveTowards (which the earlier version omitted).
- Unlock All Equipables uses SaveManager's _ownedEquipables/_allEquipables and can optionally call Dirty().
- Camera is restored to the original ortho-size override only. No free-camera mode.
- There is no weapon-disable mode in this build.

## Important
The Custom Script Engine itself is not being changed here. Your log showed InfiniteAmmoMod.cs compiling successfully,
so the immediate failure was in DevTools.cs runtime/reference usage, not in the Script Engine compiler.
