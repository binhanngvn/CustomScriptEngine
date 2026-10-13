# DevTools 2.1

Implemented before the cosmetic UI pass:

- Spawn/place is anchored to the bottom of the Player collider (Player feet), not the mouse.
- Prefab registry scans Resources plus runtime Scene/ScriptableObject references, including common spawner/spawn-card fields.
- Player fly/noclip retained.
- Player weapon-disable debug retained.
- Test Area: visible rectangle anchored to Player feet, adjustable size, inspect DevTools records inside the area.
- God Mode is AI stealth, not invulnerability. It redirects common enemy target fields to a private decoy and adds a private true blocker to common chase/movement states.
- Known target components covered from the provided game source: AI_Dog, AI_Walker, AI_Flyer, State_Chase, State_ChaseFacehuggerBig, State_FlyTowards, State_Pounce, Raper.
- Enemy tuning: select nearest enemy, freeze/unfreeze, speed multiplier, reset changes.
- Unlock all Equipables: runtime only or runtime + SaveManager.Dirty().

Important:

- Test Area is currently a runtime developer region overlay, not a whole Unity Scene/Room asset copier.
- DevTools intentionally uses reflection for private game fields. Exact behavior still depends on the current game's runtime objects and initialization.
- The project was not compiled in this environment because a C#/dotnet build toolchain is not installed here.
