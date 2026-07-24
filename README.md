# DiveIn

DiveIn adds configurable diving and swimming for players and configured creatures in Valheim.

Normally, creatures cannot follow a diving player far below the surface. DiveIn lets configured creatures chase, patrol, and attack underwater while adding player depth controls, resource tuning, water combat, key hints, and underwater visuals.

![Serpent chase](https://i.ibb.co/xKPFy4bv/serpentchase.gif)

![Underwater serpent chase](https://i.ibb.co/pHZrL1W/serpentchase2.gif)

## Player diving

- Ascend and descend with configurable keyboard shortcuts or the corresponding gamepad controls.
- Surface and midwater stamina regeneration are configured separately.
- Idle underwater stamina drain and moving swim drain can scale with liquid depth.
- Fast Swim supports press or toggle input, with separate speed and stamina multipliers.
- Swim skill and encumbrance have independent speed multipliers.
- Surface swimming retains the vanilla swim depth; DiveIn changes depth only while diving.
- Attacking, secondary attacking, and guarding temporarily take priority over swim movement.
- Player-owned underwater projectiles can use synchronized lifetime, speed, and damage multipliers.
- Equipment is usable in water by default; a synchronized prefab blacklist keeps restrictions on listed items without propagating armor or accessory entries to hand equipment. Listed hand items retain vanilla hand-item hiding.
- Swimming key hints use the active keyboard or gamepad bindings and supported Valheim languages.

## Creature diving

- Configured creature prefabs stop avoiding water and can navigate underwater.
- Passive creatures patrol within per-group minimum, center, and maximum depths.
- Alerted creatures follow targets within their active depth limits.
- Optional spawn-depth preservation prevents underwater spawns from first floating to the surface.
- Optional shallow-water flee behavior makes a creature retreat until it clears the configured depth buffer.
- Per-group avoidance steering can be disabled for prefabs that jitter with angled obstacle sampling.
- Serpent movement retains the vanilla stopping distance and swoop movement while using DiveIn depth navigation.

## `DiveIn.yaml`

The server/source-of-truth instance creates `BepInEx/config/DiveIn.yaml` when it is missing. Clients use the synchronized server value while connected.

```yaml
# Unknown or duplicate keys are errors and keep the previous applied settings.
surface_patrol:
  passive_min_depth: 0
  passive_center_depth: 10
  passive_max_depth: 20
  active_min_depth: 0.25
  active_depth_adjust_speed: 2
  shallow_water_flee_depth: 0
  preserve_spawn_depth: false
  avoidance_steering: true
  prefabs:
    - Leech
    - Abomination
    - Serpent
    - BonemawSerpent

# Disable angled avoidance only for prefabs that need it.
mods_surface_nosteering:
  passive_min_depth: 0
  passive_center_depth: 10
  passive_max_depth: 20
  active_min_depth: 0.25
  active_depth_adjust_speed: 2
  shallow_water_flee_depth: 0
  preserve_spawn_depth: false
  avoidance_steering: false
  prefabs:
    - SA_WhiteShark
```

Rules:

- A prefab may be assigned only once; the first assignment wins.
- Missing optional fields use DiveIn defaults.
- Negative and out-of-range depths are normalized and logged.
- `NaN`, infinity, malformed YAML, unknown keys, and duplicate keys are rejected without replacing the last valid configuration.
- A local YAML change is ignored while remote synchronized values are active.

## BepInEx configuration

DiveIn creates `BepInEx/config/sighsorry.DiveIn.cfg`. That generated file is the source of truth for the complete setting list, descriptions, defaults, ranges, and synchronization markers. The README intentionally does not duplicate the generated configuration block, so code and documentation cannot drift independently.

The configuration is grouped into:

- General server locking
- Player controls and water equipment
- Surface and midwater stamina/eitr regeneration
- Idle, depth, base, and Fast Swim stamina drain
- Swim skill, Fast Swim, and encumbered swim speed
- Underwater projectile lifetime, speed, and damage
- Underwater darkness and murkiness

The ascend key, descend key, and Fast Swim input mode are client-side. Settings marked `[Synced with Server]` are controlled by the server when configuration locking is enabled.

## Building

```powershell
dotnet build DiveIn.sln -c Debug
```

For a non-default Valheim installation, pass the game root explicitly:

```powershell
dotnet build DiveIn.sln -c Debug -p:GamePath="D:\SteamLibrary\steamapps\common\Valheim"
```

A normal build writes only to the project output directories. To copy the merged DLL to the BepInEx plugin path derived from `GamePath`, opt in explicitly:

```powershell
dotnet build DiveIn.sln -c Debug -p:DeployToGame=true
```

Change the release version with one explicit command:

```powershell
dotnet msbuild DiveIn.csproj -t:SetVersion -p:ReleaseVersion=1.2.0
```

This updates both `Plugin.cs` `ModVersion` and the tracked `Thunderstore/manifest.json`. It does not build the project.

On Windows, `dotnet build DiveIn.sln -c Release` verifies that the compiled assembly, `ModVersion`, and the tracked manifest have the same version before creating the Thunderstore and Nexus archives. A mismatch fails the build before either ZIP is written.

## Credits

The player diving implementation includes code derived and modified from [UnderTheSea](https://github.com/searica/UnderTheSea).

Original DiveIn repository: [sighsorry1029/DiveIn](https://github.com/sighsorry1029/DiveIn)
