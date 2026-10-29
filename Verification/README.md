# DiveIn compatibility checks

Current target: Valheim **1.0.21**, Windows x64 client build **26249040** and dedicated-server build **26266951**. Dated results below are historical and apply only to their stated versions.
The mod compiles against original game DLLs, not publicized assemblies.

```powershell
dotnet build .\DiveIn.sln -c Debug -p:DeployToGame=true
dotnet run --project .\Verification\CompatibilityChecks.csproj -- .\bin\Debug\DiveIn.dll 'C:\Users\blizz\.codex\references\valheim\snapshots\client-b26249040-windows-x64-20261029T094619Z-depot-restored\original\valheim_Data\Managed' 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core'
dotnet run --project .\Verification\CompatibilityChecks.csproj -- .\bin\Debug\DiveIn.dll 'C:\Users\blizz\.codex\references\valheim\snapshots\dedicated-server-b26266951-windows-x64-20261029T094713Z-depot-restored\original\valheim_server_Data\Managed' 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core'
```

Use a separate process per target. These archived original Managed directories pin the game inputs even after Steam updates; installed directories may be substituted after confirming the target version. No game DLL is executed as an application or modified by these checks. Supply an optional fourth argument pointing to an existing original `PathOfValheiman.dll` to check its actual transpiler composition. Without that argument, the verifier explicitly skips that check but still runs the synthetic API/policy cases. The previously supplied `asdfafd` profile DLL is absent as of 2026-10-29; an old successful PoV result is not evidence for the current installed combination.

## Valheim 1.0.21 maintenance scope (2026-10-29)

The manifest now requires BepInExPack Valheim **5.4.2360** (runtime **5.4.23.6**). The currently installed Steam core still reports BepInEx assembly version **5.4.23.5**; building or running the isolated verifier against it does not validate the new runtime.

`WaterColorSeamPatch` has been retired for the 1.0.21 baseline. The original `Custom/Water` D3D11 shader no longer performs the `_MainTex_ST` UV transform; the uniform name also disappeared from the restored Vulkan platform blob. `water.mat` and `water_lod.mat` typetrees are unchanged. This removes an obsolete material setter, not a confirmed crash fix. No game-version gate or replacement abstraction was added. The independent underwater surface rotation, `_depth`/`_UseGlobalWind` overrides and restoration remain unchanged.

The verifier now checks that the retired patch and `_MainTex_ST` reference are absent and that the separate renderer's Apply/Reset/ResetAll/ResetStale entry points remain connected to the camera/material patches. This checks compiled structure, not rendered correctness. Original shader evidence, snapshot paths, hashes and analysis limits are recorded in `C:\Users\blizz\.codex\references\valheim\comparisons\1.0.20--1.0.21-divein-20261029\review.json`. Vulkan SPIR-V and GPU behavior were not executed by that review.

Before claiming visual compatibility, restart the game and check DX11 and Vulkan separately: zone boundaries and near-water/LOD transitions, underwater versus surface view, storm waves, Ashlands boundaries, and dungeon entry/exit. Confirm underwater depth/orientation restore after surfacing and world reload. Shader-replacement mods and old restored shaders require separate checks; this patch does not preserve the old-game seam workaround.

### Pre-release Debug validation

- `dotnet build DiveIn.sln -c Debug -p:DeployToGame=true` and the separate Debug verifier build succeeded with zero warnings/errors. The final merged mod remains version **1.2.6.0**; no Release build or version bump was performed.
- The final `bin/Debug/DiveIn.dll` and automatically deployed `C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\DiveIn.dll` have identical SHA-256 **`EA742B6D4D543EDBC297B0250DF5D1491B6E29606392A3D37D1078D959B898B0`**.
- Separate client/server verifier processes against the original 1.0.21 snapshots above both passed: **555** game member operands, **38** cached accessors, **50** patch methods / **36** original targets, both equipment IL matchers, all **26** HUD/config cases, the new water-surface scope assertion and the synthetic PoV contract/policy cases.
- Negative control: the unchanged pre-patch Release 1.2.6 DLL was rejected specifically with `Retired water color seam patch is still present.` The existing Release DLL and both Release ZIPs were verified unchanged.
- The separate `UnderwaterSurfaceRenderer` implementation was compared with the pre-patch source and is unchanged. The pre-existing top-level README edits were preserved.
- These are build/metadata/isolated-host results using installed BepInEx assembly **5.4.23.5**, not Unity/Mono or new-runtime verification. Actual PoV composition was explicitly skipped without a supplied DLL. Game, host/dedicated-server, crossplay and visual checks remain unperformed.

### Release 1.2.7 validation (2026-10-29)

- The explicit `SetVersion` target updated `Plugin.cs` and the source manifest together to **1.2.7**. `dotnet build DiveIn.sln -c Release` completed with zero warnings/errors, merged the DLL, validated version agreement, and produced both existing package formats.
- Separate verifier processes against the original 1.0.21 client/server snapshots above both passed: **555** game member operands, **38** cached accessors, **50** patch methods / **36** targets, both equipment IL checks, all **26** HUD/config cases, the water-surface scope assertion and synthetic PoV contract/policy cases.
- Confirmed assembly version **1.2.7.0**, manifest version **1.2.7**, and packaged BepInExPack dependency **5.4.2360**. The Thunderstore ZIP contains exactly six expected entries and Nexus exactly two; every entry's SHA-256 matches its source file, including the final merged DLL.
- Final DLL: `bin/Release/DiveIn.dll`, SHA-256 `BEAB073F4961053262B03A05BD14A798C357CECFA95EF0149E84BCD5F48E53CA`.
- Thunderstore ZIP: `Thunderstore/DiveIn_v1.2.7.zip`, SHA-256 `D7A74D5FDB7651CC03BF367A7907C2F8BB92BDAF2905D54BCD0C3C0346D273F7`.
- Nexus ZIP: `Nexus/DiveIn_v1.2.7.zip`, SHA-256 `7389564CDA5EDE2BE75A3562CA0EA6F555A4DBB8B7F25786966DED545682183B`.
- Packaging used the pre-existing edited root README unchanged, SHA-256 `7BACF6612B27B0296BACEA2E8FC8E9E96BAB322D163C7B1E4B659348359F47FF`. That separate README edit is excluded from the release commit and remains in the working tree, so the ZIP's README differs from a clean-checkout rebuild of that commit.
- This Release build does not deploy to the local game. The tests used installed BepInEx assembly **5.4.23.5** in the isolated host, not runtime **5.4.23.6**. Actual PoV composition was skipped without a supplied DLL; actual Unity/Mono rendering, gameplay and multiplayer remain unverified. Uploader or site publication status was not inspected.

## Per-target results (2026-09-09)

Historical target: Valheim 1.0.7, Windows x64 client build 25185596 and dedicated-server build 25185644.

- 471 final merged-DLL game member operands resolved; no direct non-public access or static instructions against literal fields.
- 38 typed, cached accessors initialized using original types.
- 48 DiveIn/embedded ServerSync patch methods resolved to 34 original targets; named arguments, `__instance`, `__result` and injected field types checked.
- Both original Humanoid IL streams matched the existing swimming/ground common-target guard (UpdateEquipment insertion index 10, EquipItem index 33).
- Debug build: zero warnings/errors; final merged DLL automatically deployed to the game's plugins folder with equal SHA-256.

The verifier deliberately uses **.NET 9 + HarmonyX 2.16** only in its own process. The mod continues to use the game's installed BepInEx/Harmony reference; these packages are not shipped with DiveIn. The installed Harmony build could not initialize on .NET 9, and .NET Framework 4.8 could not load the new game's default-interface methods. Neither failed test-host attempt is evidence of a game failure. Accessor initialization and IL inspection in the working verifier do **not** demonstrate Unity/Mono gameplay compatibility.

## In-game checks still required

Use BepInExPack Valheim **5.4.2360** and restart after deploying the final DLL.

1. Client startup: no DiveIn initialization error, both water-equipment transpilers active. Loading a plugin name alone is not success.
2. Surface/midwater movement, bottom ascent, fast swim, stamina/eitr regeneration and delays, status effects, teleport into/out of an underwater dungeon; camera and water material restoration after exiting water or unloading.
3. Keyboard/gamepad controls, input blocked by menus/chat, achievements panel hiding hints; scene/language/layout changes without duplicated hints.
4. Blacklisted equipment remains item-scoped; normal armor does not hide unrestricted hand equipment. Equip/unequip and hidden-hand redraw must preserve inventory item counts.
5. Underwater launch applies damage/speed/TTL once; SpawnOnHit descendants inherit vanilla launch data without another multiplier. Check ownership and new projectile/grapple content separately.
6. Configured monsters: pursuit, shallow-water flee, crown fear, boss exception, saddle priority, despawn and state restoration.
7. Host and dedicated server with two clients: locked configuration/YAML sync, non-admin rejection, reconnect and monster/projectile ownership transfer. No duplicated resource handling or item loss/duplication.

The update keeps configuration keys/YAML, RPC policy, minimum mod-version policy and existing lifecycle cleanup. It adds no old-game compatibility or save migration. The original maintenance patch did not include a release; subsequent explicitly requested releases are recorded separately above.

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

Historical implementation and results: the following setup-only workaround was removed for the 1.0.21 baseline described above. Its earlier test results and provenance are preserved here, not claimed as the current implementation.

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

## Path of Valheiman water-combat integration (2026-10-17)

Scope: optional Path of Valheiman 5.0.4 (`treextr.pathofvalheiman`) integration, not a general game-version migration. The supplied profile uses Valheim 1.0.20. Its original client build 25909357 and dedicated-server build 25909431 keep the same two Humanoid gate bodies as 1.0.16. PoV's `WaterCombat` transpilers replace `IsSwimming()` before DiveIn runs, explaining the original pattern-not-found warnings.

- PoV DLL inspected without publicizing: `C:\Users\blizz\AppData\Roaming\com.kesomannen.gale\valheim\profiles\asdfzcvzcvv\BepInEx\plugins\treextr-PathOfValheiman\PathOfValheiman\PathOfValheiman.dll`, SHA-256 `36E45B65B44667AD596446CAC2AE989041669948C11710B05185E4FE16697DB4`.
- `PathOfValheimanCompat.cs` isolates the optional binding, live predicates and same-signature IL adapter. It adds one file to navigate, but keeps external API names/failure handling out of normal swimming and avoids duplicating PoV's tree, item classifications or progression state.
- A BepInEx soft dependency orders initialization; no manifest dependency, PoV assembly reference or bundled external DLL is added. The two equipment transpilers run after PoV and replace exactly one known PoV helper call each. Branches, stack signatures, labels and exception blocks remain intact. PoV is still called on every decision; only the cached delegates, not the unlock result, are retained.
- `Enabled(Character)` is PoV's private live-node predicate; there is no equivalent public node-only predicate in the inspected `WaterCombat` type. It is bound explicitly once via Harmony after checking the static signature and boolean return type. Public `ShouldBlockWaterEquip`, `ShouldHideForSwimming`, `IsMeleeWeapon` and `AllowsAttack` supply the existing policy.
- PoV owns water-combat unlock and melee/shield/attack rules. DiveIn only adds its existing item blacklist for the local swimming player. Armor/accessory entries do not become a global hand-equipment ban; listed hand equipment retains the existing both-hands hiding semantics. Original grounded/dead/non-player branch checks remain unchanged.
- DiveIn's Hide/Block restoration checks every hidden item before calling `ShowHandItems`, which otherwise clears hidden references even if equipping fails. A melee weapon must be present in the resulting hand set; PoV immediately hides shield-only equipment, so DiveIn must not repeatedly restore it. No inventory remove/add/clone calls are added.
- Movement suppression and guard-animation assistance follow the current node and permitted weapon/attack, retaining grounded behavior. DiveIn does not replace PoV's `Attack.Start` patch. Node refund and character changes are observed through live PoV calls. Diving, Fast Swim, resource tuning, visuals, HUD, creature movement and projectile ownership/penalty paths are untouched.
- Missing/changed APIs or an unexpected gate stop the integration rather than enabling DiveIn's unrestricted bypass. Already integrated gates conservatively use the swimming restriction; other mod patches are not removed. A runtime binding/predicate failure logs once, stops further optional calls and does not disable diving. Cleanup unpatches DiveIn before releasing cached delegates.

The optional fourth verifier argument is the original PoV DLL. Tests must run in separate processes per original game target, without loading a game world. The supplied DLL is neither modified nor copied into this repository or the game.

Validation completed:

- `dotnet build DiveIn.sln -c Debug -p:DeployToGame=true`: zero warnings/errors; final merged Debug DLL and default Steam deployment both SHA-256 `CD9B7C977486075EF12D0A999B42CF543F05CDD21ADAF9835A8213F74C9EE226`.
- Final Debug DLL against original 1.0.20 client/server in separate processes: 555 direct game member operands, 38 cached game accessors, 51 declarative patches / 37 targets, both vanilla equipment pattern checks, 26 HUD/config cases and the static water-shader contract passed. No direct non-public/literal game access and no hard PoV assembly reference were found.
- Actual PoV 5.0.4 DLL: all five predicate signatures bound, including the private live-node predicate. Its two equipment transpilers followed by DiveIn's transpilers change only the expected helper operand, preserving all other instructions, branches, labels and exception blocks. Fresh Harmony-style rebuilds and zero/two-helper-call mismatch checks passed. Soft-dependency and `HarmonyAfter` metadata were checked.
- Isolated mock-context policy checks passed: absent/locked/unlocked/refunded state, no cached unlock results, partial binding rejection, delegate failure, original PoV restriction OR blacklist truth table, local/remote and water/land scope, non-hand armor blacklist isolation, allowed melee/shield restoration, forbidden/blacklisted mixed hidden pairs without changing either hidden reference, shield-only denial, primary/secondary/ranged decisions and grounded exemption.
- These tests use .NET 9 / HarmonyX 2.16, not the game's Mono host. Logger reads, native animation hashing, local-player identity, blacklist lookup, ground/current-weapon context and PoV gameplay predicates are isolated or mocked for policy tests. Actual PoV transpilers and signature binding are tested separately. No original game DLL is rewritten or publicized. Passing these tests is not proof of actual Unity, node progression, graphics or multiplayer execution.

Original target directories under `C:\Users\blizz\.codex\references\valheim\snapshots`:

- `client-b25909357-windows-x64-20261015T103728Z\original\valheim_Data\Managed`
- `dedicated-server-b25909431-windows-x64-20261015T103728Z-depot-restored\original\valheim_server_Data\Managed`

In-game checks still required:

1. Start the affected Gale profile with the patched DiveIn DLL. Confirm the compatibility info message and no DiveIn pattern-not-found warnings for either equipment method, including later Harmony rebuilds by Homestead/SecondaryAttacks.
2. With PoV absent, confirm ordinary DiveIn water equipment, Hide/Block restoration, combat, diving and Fast Swim still work. With PoV present but the node locked, confirm diving/HUD/regen work while equipment follows PoV restrictions.
3. Unlock `Combat in the Depths`: test melee plus shield, offhand melee, primary vs throwing secondary attacks, ranged/magic weapons, and shield alone. Rejected combat inputs must not trigger DiveIn's swim-movement suppression.
4. Test blacklisted weapon, shield, armor and accessory separately; an armor blacklist must not hide unrelated hands. Hide and Block with mixed allowed/forbidden hidden items must not erase hidden references or lose/duplicate inventory items.
5. Refund the node while equipped/hidden, swap characters, die/respawn and reconnect. Check surface swimming, diving, standing on the seabed and transitions to/from land. Existing attacks/animations are not forcibly aborted by this integration.
6. Host plus two clients with different node states and a dedicated server: no unlock leakage between players, unchanged server blacklist locking and no new RPCs or ownership writes. This client-side compatibility is not a new server-side anti-cheat system.

The Debug deployment target remains the default Steam `BepInEx/plugins/DiveIn.dll`; the supplied Gale profile is not overwritten by this build. Restart and install the tested DLL into the profile before its in-game checks. No Release build, version bump or publication is part of this patch.

## Release 1.2.5 validation (2026-10-17)

The subsequent explicit release request raised `ModVersion` and the source manifest together from 1.2.4 to 1.2.5 through `SetVersion`, and added the PoV compatibility changelog.

- `dotnet build DiveIn.sln -c Release`: zero warnings/errors, successful three-assembly merge and packaging version validation.
- Final Release DLL passed the verifier against both original 1.0.20 targets listed above, each with the supplied PoV 5.0.4 DLL: 555 game member operands, 38 cached accessors, 51 patches / 37 targets, original equipment IL checks, 26 HUD/config cases, water-shader static contract, optional/mocked PoV policy checks, and actual PoV-to-DiveIn transpiler composition/rebuild/mismatch checks.
- Confirmed assembly version `1.2.5.0`, packaged manifest version `1.2.5`, BepInExPack dependency `5.4.2351`, exact Thunderstore/Nexus entry sets and SHA-256 equality of every packaged entry with its source file.
- Release DLL SHA-256: `8AF591CBBACEF3DE9C71AF35BC9134FC75CF6D7E47955467A76C242F26404604`.
- Thunderstore ZIP SHA-256: `09A082A0DAD38B47B2E61C7A3AC2B384094D3ECACACC31B40D088215AC31CBB5`.
- Nexus ZIP SHA-256: `4E3BA11FB7345FD34B36AF64C0C13C69E005D9C365D0C2B0FD243FC7FDC30BFE`.

The build did not replace local Steam/Gale plugin DLLs. Actual Unity/Mono gameplay, node progression and multiplayer checks remain outstanding as described above. Uploader and site publication checks were not part of this release request.
