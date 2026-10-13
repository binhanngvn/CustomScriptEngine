# V2 changes for this task

## CustomScriptEngine

- Startup reference discovery enabled by default.
- Scans every `*_Data/Managed` folder directly under the game root.
- Scans `BepInEx/core`.
- Recursively scans `BepInEx/plugins`.
- Scans `BepInEx/scripts/libs`.
- Writes absolute paths to `BepInEx/scripts/reference-cache.txt`.
- Uses the cache for compilation and also reads `scripts/libs/*.dll` live so newly added manual DLLs work immediately.
- Keeps the original `.script.json`, inline `@reference`, `@define`, watcher, debounce, F6 reload, compile-error fallback and Mono functional hot reload behavior.

## DevTools V2

- Keeps place/map/player/camera workflow from the previous DevTools.
- Prefab registry now scans Resources plus prefab-bearing fields/collections from scene components and loaded ScriptableObjects.
- This specifically covers the supplied game's `EntitySpawner`, `PreplacedEnemySpawner`, `SpawnDirectorSimple`, `SpawnDirector_Scrap`, `SpawnMultipleInRandomRangeAroundPoint`, `SpawnCategory`/`SpawnCard` data and weapon prefab collections by their serialized/reference fields.
- Adds Enemy/Entity/Map/Object filtering.
- Adds Fly mode with physics/movement suppression and WASD/arrow movement.
- Adds Noclip collider mode.
- Adds optional player weapon suppression.
- Adds runtime-only and persistent Unlock All Equipables.
- Adds free camera movement.
- Adds a debug panel and player component/weapon logging.
- Dev maps still use in-game prefab references only; no external map import is added.
