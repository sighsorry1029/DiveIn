# Valheim 1.0.7 compatibility checks

Target: Windows x64 client build 25185596 and dedicated-server build 25185644.
The mod compiles against original game DLLs, not publicized assemblies.

```powershell
dotnet build .\DiveIn.sln -c Debug -p:DeployToGame=true
dotnet run --project .\Verification\CompatibilityChecks.csproj -- .\bin\Debug\DiveIn.dll 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed' 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core'
dotnet run --project .\Verification\CompatibilityChecks.csproj -- .\bin\Debug\DiveIn.dll 'D:\SteamLibrary\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed' 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core'
```

Use a separate process per target. An archived original Managed directory can replace either installed directory. No game DLL is executed as an application or modified by these checks.

## Per-target results (2026-09-09)

- 471 final merged-DLL game member operands resolved; no direct non-public access or static instructions against literal fields.
- 38 typed, cached accessors initialized using original types.
- 48 DiveIn/embedded ServerSync patch methods resolved to 34 original targets; named arguments, `__instance`, `__result` and injected field types checked.
- Both original Humanoid IL streams matched the existing swimming/ground common-target guard (UpdateEquipment insertion index 10, EquipItem index 33).
- Debug build: zero warnings/errors; final merged DLL automatically deployed to the game's plugins folder with equal SHA-256.

The verifier deliberately uses **.NET 9 + HarmonyX 2.16** only in its own process. The mod continues to use the game's installed BepInEx/Harmony reference; these packages are not shipped with DiveIn. The installed Harmony build could not initialize on .NET 9, and .NET Framework 4.8 could not load the new game's default-interface methods. Neither failed test-host attempt is evidence of a game failure. Accessor initialization and IL inspection in the working verifier do **not** demonstrate Unity/Mono gameplay compatibility.

## In-game checks still required

Use BepInExPack Valheim **5.4.2351** and restart after deploying the final DLL.

1. Client startup: no DiveIn initialization error, both water-equipment transpilers active. Loading a plugin name alone is not success.
2. Surface/midwater movement, bottom ascent, fast swim, stamina/eitr regeneration and delays, status effects, teleport into/out of an underwater dungeon; camera and water material restoration after exiting water or unloading.
3. Keyboard/gamepad controls, input blocked by menus/chat, achievements panel hiding hints; scene/language/layout changes without duplicated hints.
4. Blacklisted equipment remains item-scoped; normal armor does not hide unrestricted hand equipment. Equip/unequip and hidden-hand redraw must preserve inventory item counts.
5. Underwater launch applies damage/speed/TTL once; SpawnOnHit descendants inherit vanilla launch data without another multiplier. Check ownership and new projectile/grapple content separately.
6. Configured monsters: pursuit, shallow-water flee, crown fear, boss exception, saddle priority, despawn and state restoration.
7. Host and dedicated server with two clients: locked configuration/YAML sync, non-admin rejection, reconnect and monster/projectile ownership transfer. No duplicated resource handling or item loss/duplication.

The update keeps configuration keys/YAML, RPC policy, minimum mod-version policy and existing lifecycle cleanup. It adds no old-game compatibility or save migration. Release packaging, version bump, commit/push and uploader actions are outside this patch request.

## Swimming HUD patch checks (2026-10-06)

The HUD uses client-only `2 - Player Diving` / `Swim HUD Mode`: Full (default), FastSwimOnly or Off. It reads the existing local swim state without changing movement, input, stamina, RPCs or ownership. The setting is not synchronized or server-locked. Old Show Fast Swim HUD On/Off values migrate at startup only when the new key is absent. Existing key hints are suppressed separately for the controls and Fast Swim status rows, only while their replacement row is actually active.

Validation uses the installed original Valheim 1.0.16 client DLLs, matching archived client build 25527674, and the archived dedicated-server build 25527701 in separate verifier processes. Snapshot directories under `C:\Users\blizz\.codex\references\valheim\snapshots`:

- `client-b25527674-windows-x64-20260925T211211Z\original\valheim_Data\Managed`
- `dedicated-server-b25527701-windows-x64-20260925T211211Z-depot-restored\original\valheim_server_Data\Managed`

The verifier also exercises the production row policy (all 6 mode/availability combinations) and 20 config cases using temporary files and the installed BepInEx configuration implementation: no saved setting, old On/Off, old numeric Off, invalid values, new-key precedence (including empty/invalid values), stable Full default, retired-key removal, unrelated settings, no premature save, SaveOnConfigSet restoration and save/reload. It does not construct Unity UI objects or touch the player's configuration.

Final validation passed against both original targets: 529 game member operands, 38 cached accessors, 50 patch methods / 36 targets, both water-equipment IL checks, and all 26 HUD/config cases. Debug build and merge completed with zero warnings/errors; automatic deployment to the local game's `BepInEx/plugins/DiveIn.dll` matched SHA-256 `499BD42D2E03A561A76125B81D502B80BBC2844524EE32D8940EE3DF357A36BB`. These are build/metadata/isolated-host checks, not actual Unity or server execution. Style and binding polling is limited to four times per second while visible, with immediate row-mode changes; relative scale sampling pauses while the stamina bar hides or transitions.

In-game checks still required for this HUD:

1. Surface and underwater swimming, Toggle and Press, standing still, combat and empty stamina: On means selected mode, not guaranteed acceleration. Exhaustion must not reset Toggle; existing speed/drain behavior must be unchanged.
2. Switch Full/FastSwimOnly/Off while swimming. Full has controls on the old first-row position and status below; FastSwimOnly puts status on the old first row with no blank row. Verify immediate handoff for each corresponding vanilla hint, including Full to FastSwimOnly when status remains visible. With vanilla Key Hints disabled, the HUD still works and Off leaves all DiveIn hints hidden.
3. Full stamina after the bar fades: HUD should remain. Match both rows' font, size and rendered scale to the stamina number; check UI scale, long translations, keyboard rebinding, gamepad icons and switching active input device. Confirm placement with other UI mods, bar flash animations and that empty stamina changes only status color, not the two-state On/Off contract.
4. Land, menus/chat input, death, teleport and user-hidden HUD: both rows hidden. Encumbrance or Fast Swim speed multiplier 1: Full retains ascent/descent controls, while FastSwimOnly hides. Return to swimming, respawn and world reload: no stale or duplicate widget. Hud destruction and plugin cleanup must not create a new player controller.
5. Host and remote clients may choose different HUD settings despite server config locking. Dedicated server has no local-player widget and no new network traffic.

## Water color seam UV initialization (2026-10-06)

`WaterColorSeamPatch`, colocated in `UnderwaterSurfaceRenderer.cs`, adds one postfix on the original private `WaterVolume.SetupMaterial`. It skips a null graphics device or missing renderer/material/shader, and only handles `Custom/Water` without an exposed `_MainTex` property. Its only write is `_MainTex_ST = (1, 1, 0, 0)` on the per-renderer material that vanilla already initialized. `_MainTex_ST` is a compiled shader uniform but not an exposed shader property; checking `HasProperty("_MainTex_ST")` would incorrectly skip this fix.

This is permanent material initialization, separate from DiveIn's temporary underwater rotation/depth overrides. Surfacing and config reload do not revert it. There is no added config, per-frame callback, state registry, material clone, dependency, network write or gameplay-water change. Reapplying the same identity transform is idempotent, including alongside VCP. The optional VCP shore-tint adjustment is deliberately omitted. Arbitrary external material replacement without `SetupMaterial`, or another mod deliberately changing this same shader's UV transform, is outside the automatic setup repair contract.

Reference: supplied ValheimCommunityPatch 0.32.4 `WaterColorSeamPatch` (MidnightsFX, GPL-3.0), DLL SHA-256 `7E4DAAA1C25B4E73EE3172229EA0A6A5772315E25E9278EC2BB13ACCD84D7206`; see the source link in the root README credits. The preceding read-only review checked original 1.0.16 `Custom/Water`, `water.mat` and `water_lod.mat` metadata in bundles `eb7a4308` / `c4210710`: the compiled uniform exists without exposed/saved `_MainTex` or `_MainTex_ST` properties. That metadata inspection did not execute/disassemble GPU math or demonstrate an in-game result.

The verifier checks the new Harmony target and a bounded **static IL contract** for the guards, identity vector and write scope. These checks do not execute Unity material operations or prove rendered seam elimination.

Validation passed against both original 1.0.16 targets listed above: 531 game member operands, 38 cached accessors, 51 patch methods / 37 targets, both water-equipment IL checks, all 26 HUD/config cases, and the water seam static contract. The Debug build and merge completed with zero warnings/errors. The final `bin/Debug/DiveIn.dll` and automatically deployed local `BepInEx/plugins/DiveIn.dll` both have SHA-256 `451691A93D882EF307CE2D301E0E0CD5A919712574682ED3D4061510865269DC`. These are build/metadata/isolated-host checks, not actual Unity, graphics or dedicated-server execution. The mod version remains 1.2.3; no Release package was generated.

In-game checks still required:

1. Restart with the new DLL so water tiles run material setup. Compare the same shoreline zone boundary above water and while diving; verify the color transition rather than assuming any geometric gap is the same defect.
2. Re-enter unloaded zones and compare nearby/distant water, fixed-depth dungeon water, Ashlands and Deep North, with calm weather and large waves.
3. Check D3D11 and Vulkan, vanilla water and any custom-shader water; only the supported shader should receive the UV initialization.
4. Repeat with VCP absent and present. VCP's own shore-tint setting can still affect the appearance; DiveIn must not add another tint or interfere with either mod's material update path.
5. Confirm swimming, boat flotation and water heights remain unchanged, and that a dedicated server skips the material correction.

## Release 1.2.4 validation (2026-10-06)

- Updated `ModVersion` and the source manifest together through the existing `SetVersion` target.
- `dotnet build DiveIn.sln -c Release` passed with zero warnings/errors; ServerSync and YamlDotNet merged successfully, and the packaging version check passed.
- Ran the verifier against the final Release DLL in separate processes for both original 1.0.16 client/server targets listed above. Both passed: 531 game member operands, 38 cached accessors, 51 patch methods / 37 targets, both equipment IL checks, all 26 HUD/config cases, and the static water seam contract.
- Confirmed assembly version `1.2.4.0`, packaged manifest version `1.2.4` and BepInExPack dependency `5.4.2351`. Checked exact Thunderstore/Nexus ZIP entry sets and SHA-256 equality of every entry with its source file, including the final merged DLL and changelog.
- Release DLL SHA-256: `8ECF7AC50BE64D324031C43D9F6E75B47C255C2AF1AE6B73D39B5BF32834BE2D`.
- Thunderstore ZIP SHA-256: `D938BEAC9370DA48FB85BEA2B0B721FBA530A633BF5EB5548876472484BCE767`.
- Nexus ZIP SHA-256: `D0FA16FCFC21B2B6926584EB22321D2AED0BE07BBF547400FD040D059F4A311A`.

Actual Unity/GPU rendering and client/host/dedicated-server gameplay checks remain outstanding. This Release build does not replace the locally deployed Debug DLL. Uploader and site publication checks were not part of this request.
