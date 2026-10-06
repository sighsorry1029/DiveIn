# Vendored ServerSync compatibility repair

`ServerSync.dll` retains assembly version 1.0.0.0 and the original configuration/RPC implementation.
For Valheim 1.0.7, exactly three `ldsfld Int64 ZRoutedRpc::Everybody` instructions were replaced with `ldc.i8 0`, matching the original game's new public constant. This is not an upstream library upgrade.

- Original: git `fc682c3:Libs/ServerSync.dll`, 49,664 bytes, SHA-256 `166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60`.
- Repaired: 49,664 bytes, SHA-256 `C3D070A51C918118A1B8A75265D6326DE66A03494E1F04B088E44CA6F103FFFD`.
- Reproduction: obtain the original binary from that git revision, then run `Patch-ServerSync.ps1` with `-InputDll`, a separate `-OutputDll`, the 1.0.7 original `-GameDll` and Mono.Cecil 0.11.6 `-CecilDll`.

The script checks the exact input hash, original field type/value, opcode and expected edit count and uses deterministic MVID generation. It is a one-time developer tool, not a build hook. No game DLL or other mod's ServerSync copy is patched. `Verification/CompatibilityChecks.csproj` audits the final merged DiveIn DLL, including embedded ServerSync references and declarative Harmony patch targets.
