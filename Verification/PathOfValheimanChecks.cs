using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

// Executes production transpilers and policy bodies with explicitly mocked engine context in this process only.
// Player/ItemData fixtures bypass constructors; no Unity scene, native object, world or player config is created.
internal static class PathOfValheimanChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Guid = "treextr.pathofvalheiman";
    private static bool _node;
    private static bool _gate;
    private static bool _throw;
    private static int _reads;
    private static readonly ManualLogSource TestLog = new("DiveIn.CompatibilityChecks");
    private static object? _localPlayer;
    private static object? _blacklistedItem;
    private static object? _currentWeapon;
    private static bool _onGround;
    private static object? _meleeItem;
    private static object? _allowedAttack;
    private static object? _blockedItem;

    internal static void Run(Assembly mod, ModuleDefinition module, string? povPath)
    {
        Type compat = mod.GetType("ServerSyncModTemplate.PathOfValheimanCompat", true)!;
        Type equipment = mod.GetType("ServerSyncModTemplate.WaterEquipmentPatches", true)!;
        Type plugin = mod.GetType("ServerSyncModTemplate.ServerSyncModTemplatePlugin", true)!;
        Type humanoid = AccessTools.TypeByName("Humanoid")!;
        // Logging must not initialize the plugin/ServerSync, whose threading helper belongs to Unity.
        var isolation = new Harmony("DiveIn.Verification.PoV");
        isolation.Patch(Method(compat, "Fail"), transpiler: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(IsolateLogger))));
        isolation.Patch(Method(equipment, "InsertWaterEquipmentBypass"), transpiler: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(IsolateLogger))));
        try
        {
            if (module.AssemblyReferences.Any(r => r.Name == "PathOfValheiman"))
                throw new Exception("Optional PoV integration gained a hard assembly reference.");
            if (!plugin.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency"
                && Equals(a.ConstructorArguments[0].Value, Guid)
                && a.ConstructorArguments.Count == 2 && Convert.ToInt32(a.ConstructorArguments[1].Value) == 2))
                throw new Exception("Missing PoV soft-dependency load ordering.");

            MethodInfo[] transpilers = [Method(equipment, "HumanoidUpdateEquipmentTranspiler"), Method(equipment, "HumanoidEquipItemTranspiler")];
            foreach (MethodInfo transpiler in transpilers)
                if (!transpiler.GetCustomAttributes<HarmonyAfter>().Any(a => a.info.after.Contains(Guid)))
                    throw new Exception("Equipment transpiler must run after PoV: " + transpiler.Name);

            Method(compat, "Reset").Invoke(null, null);
            CheckDefaults(compat);
            for (int index = 0; index < transpilers.Length; index++)
            {
                string target = index == 0 ? "UpdateEquipment" : "EquipItem";
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(humanoid, target));
                List<CodeInstruction> changed = Rewrite(transpilers[index], original);
                if (changed.Count <= original.Count || !changed.Any(i => i.operand is MethodInfo m && m.Name == "IsSwimming"))
                    throw new Exception("PoV absence changed the original DiveIn water bypass: " + target);
            }
            Type player = AccessTools.TypeByName("Player")!;
            Type character = AccessTools.TypeByName("Character")!;
            Type utils = mod.GetType("ServerSyncModTemplate.PlayerDiveUtils", true)!;
            isolation.Patch(AccessTools.Method(AccessTools.TypeByName("ZSyncAnimation"), "GetHash"), prefix: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(AnimationHashPrefix))));
            isolation.Patch(Method(utils, "IsValidLocalPlayer"), prefix: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(LocalPlayerPrefix))));
            isolation.Patch(Method(equipment, "IsWaterRestrictedItem"), prefix: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(BlacklistPrefix))));
            isolation.Patch(AccessTools.Method(character, "IsOnGround"), prefix: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(OnGroundPrefix))));
            isolation.Patch(AccessTools.Method(humanoid, "GetCurrentWeapon"), prefix: new HarmonyMethod(Method(typeof(PathOfValheimanChecks), nameof(CurrentWeaponPrefix))));
            _localPlayer = RuntimeHelpers.GetUninitializedObject(player);
            CheckManagedPolicies(compat);
            Console.WriteLine("PASS: PoV optional/absent bypass, live node/refund and failure policies, original gate OR blacklist, hidden-hand restore and attack/block policy cases (mocked player/blacklist/ground/weapon context, no Unity execution).");

            if (povPath == null)
            {
                Console.WriteLine("SKIP: actual PoV transpiler composition (supply an optional fourth DLL argument).");
                return;
            }

            Assembly pov = Assembly.LoadFrom(povPath);
            Type waterCombat = pov.GetType("PassiveTree.WaterCombat", true)!;
            foreach (int index in new[] { 0, 1 })
            {
                bool equip = index == 1;
                Bind(compat, waterCombat);
                string target = equip ? "EquipItem" : "UpdateEquipment";
                string helper = equip ? "ShouldBlockWaterEquip" : "ShouldHideForSwimming";
                Type povPatch = waterCombat.GetNestedType(equip ? "EquipMeleeInWater" : "KeepMeleeHands", BindingFlags.NonPublic)!;
                MethodInfo povTranspiler = Method(povPatch, "Transpiler");
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(humanoid, target));
                List<CodeInstruction> povCode = Rewrite(povTranspiler, original);
                MethodInfo originalGate = Method(waterCombat, helper);
                int gateIndex = povCode.FindIndex(i => i.Calls(originalGate));
                if (gateIndex < 0 || povCode.Count(i => i.Calls(originalGate)) != 1)
                    throw new Exception("PoV no longer emits the expected unique gate: " + target);

                // Inject markers onto the exact replaced instruction, not only at surrounding branch targets.
                var generator = new DynamicMethod("MetadataProbe", typeof(void), Type.EmptyTypes).GetILGenerator();
                povCode[gateIndex].labels.Add(generator.DefineLabel());
                povCode[gateIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                var before = Snapshot(povCode);
                List<CodeInstruction> composed = Rewrite(transpilers[index], povCode);
                CheckSameStream(before, composed, gateIndex, Method(compat, helper));
                if (composed.Any(i => i.Calls(originalGate)) || composed.Count(i => i.Calls(Method(compat, helper))) != 1)
                    throw new Exception("PoV gate was not replaced exactly once: " + target);

                // Harmony rebuilds from original IL whenever another mod patches this target.
                Bind(compat, waterCombat);
                List<CodeInstruction> repeated = Rewrite(transpilers[index], Rewrite(povTranspiler,
                    PatchProcessor.GetOriginalInstructions(AccessTools.Method(humanoid, target))));
                if (repeated.Count(i => i.Calls(Method(compat, helper))) != 1)
                    throw new Exception("PoV composition is not stable on a fresh Harmony rebuild: " + target);

                foreach (int callCount in new[] { 0, 2 })
                {
                    Bind(compat, waterCombat);
                    var mismatch = Enumerable.Range(0, callCount).Select(_ => new CodeInstruction(OpCodes.Call, originalGate)).ToList();
                    mismatch.Add(new CodeInstruction(OpCodes.Ret));
                    var untouched = Snapshot(mismatch);
                    CheckSameStream(untouched, Rewrite(transpilers[index], mismatch));
                    if (!(bool)Field(compat, "_failed").GetValue(null)!)
                        throw new Exception("Mismatched PoV gate did not fail closed: " + target);
                    if ((bool)Method(compat, "HasNode").Invoke(null, [null])!)
                        throw new Exception("Mismatch retained forced combat permission: " + target);
                }
                Console.WriteLine($"PASS: actual PoV -> DiveIn Humanoid.{target} transpilers compose; only the exact gate operand changes, all labels/blocks/branches retained; rebuild and 0/2-call mismatch checks pass.");
            }
            Method(compat, "Reset").Invoke(null, null);
            Console.WriteLine("PASS: actual PoV WaterCombat signatures bind to all five cached delegates, including private live node predicate; no PoV gameplay or Unity methods executed.");
        }
        finally
        {
            Method(compat, "Reset").Invoke(null, null);
            isolation.UnpatchSelf();
        }
    }

    private static void CheckDefaults(Type compat)
    {
        if ((bool)compat.GetProperty("Installed", Static)!.GetValue(null)!) throw new Exception("PoV unexpectedly installed in isolated host.");
        foreach (string name in new[] { "CanRestoreHiddenItems", "CanUseBlockInput" })
            if (!(bool)Method(compat, name).Invoke(null, [null])!) throw new Exception("PoV absence blocks " + name);
        foreach (bool secondary in new[] { false, true })
            if (!(bool)Method(compat, "CanUseAttackInput").Invoke(null, [null, secondary])!)
                throw new Exception("PoV absence blocks attack input.");
    }

    private static void CheckManagedPolicies(Type compat)
    {
        Installed(compat, true);
        AssertLocked(compat);
        _node = false;
        _throw = false;
        _reads = 0;
        SetStub(compat, "_enabled", nameof(ReadNode));
        foreach (bool unlocked in new[] { false, true, true, false, true, false })
        {
            _node = unlocked;
            if ((bool)Method(compat, "HasNode").Invoke(null, [null])! != unlocked)
                throw new Exception("PoV node state was cached or refund ignored.");
        }
        if (_reads != 6) throw new Exception("PoV node predicate was not read on every check.");
        _node = false;
        AssertLocked(compat);
        CheckContextPolicies(compat);

        // An authoritative PoV block must short-circuit before any local-player/blacklist/Unity access.
        _gate = true;
        SetStub(compat, "_shouldHide", nameof(ReadGate));
        SetStub(compat, "_shouldBlockEquip", nameof(ReadEquipGate));
        if (!(bool)Method(compat, "ShouldHideForSwimming").Invoke(null, [null])!
            || !(bool)Method(compat, "ShouldBlockWaterEquip").Invoke(null, [null, null])!)
            throw new Exception("DiveIn overrode a true PoV restriction.");

        _throw = true;
        if ((bool)Method(compat, "HasNode").Invoke(null, [null])! || !(bool)Field(compat, "_failed").GetValue(null)!)
            throw new Exception("PoV delegate exception did not fail closed.");
        AssertLocked(compat);
        if (!(bool)Method(compat, "ShouldBlockWaterEquip").Invoke(null, [_localPlayer, null])!
            || !(bool)Method(compat, "ShouldHideForSwimming").Invoke(null, [_localPlayer])!)
            throw new Exception("Failed PoV delegates bypass swimming restrictions.");
        _throw = false;
        Method(compat, "Reset").Invoke(null, null);
        Installed(compat, true);
        try
        {
            Method(compat, "Bind").Invoke(null, [IncompleteApi(compat)]);
            throw new Exception("Unknown PoV API was accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is MissingMethodException) { }
        if (compat.GetFields(Static).Any(f => typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(null) != null))
            throw new Exception("Failed PoV binding published partial delegates.");
        AssertLocked(compat);
        Method(compat, "Reset").Invoke(null, null);
        CheckDefaults(compat);
    }

    private static void AssertLocked(Type compat)
    {
        foreach (string name in new[] { "CanRestoreHiddenItems", "CanUseBlockInput" })
            if ((bool)Method(compat, name).Invoke(null, [_localPlayer])!) throw new Exception("PoV locked state permits " + name);
        foreach (bool secondary in new[] { false, true })
            if ((bool)Method(compat, "CanUseAttackInput").Invoke(null, [_localPlayer, secondary])!)
                throw new Exception("PoV locked state permits attack input.");
    }

    private static void Bind(Type compat, Type waterCombat)
    {
        Method(compat, "Reset").Invoke(null, null);
        Installed(compat, true);
        Method(compat, "Bind").Invoke(null, [waterCombat]);
        foreach (var (field, method) in new[] { ("_enabled", "Enabled"), ("_isMeleeWeapon", "IsMeleeWeapon"),
                     ("_allowsAttack", "AllowsAttack"), ("_shouldHide", "ShouldHideForSwimming"), ("_shouldBlockEquip", "ShouldBlockWaterEquip") })
            if (Field(compat, field).GetValue(null) is not Delegate predicate || predicate.Method != Method(waterCombat, method))
                throw new Exception("PoV delegate did not bind actual policy: " + method);
    }

    private static void Installed(Type compat, bool value) => compat.GetProperty("Installed", Static)!.SetValue(null, value);
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Static) ?? throw new Exception("Missing method " + type.FullName + "." + name);
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Static) ?? throw new Exception("Missing field " + type.FullName + "." + name);
    private static List<CodeInstruction> Rewrite(MethodInfo transpiler, List<CodeInstruction> code) => ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, [code])!).ToList();
    private static void SetStub(Type compat, string fieldName, string methodName)
    {
        FieldInfo field = Field(compat, fieldName);
        field.SetValue(null, Method(typeof(PathOfValheimanChecks), methodName).CreateDelegate(field.FieldType));
    }
    private static bool ReadNode(object? _) { _reads++; if (_throw) throw new InvalidOperationException("Verifier node failure"); return _node; }
    private static bool ReadGate(object? _) => _gate;
    private static bool ReadEquipGate(object? _, object? item) => _gate || item != null && ReferenceEquals(item, _blockedItem);

    private static Type IncompleteApi(Type compat)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DiveIn.IncompletePoV"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("API").DefineType("IncompleteWaterCombat", System.Reflection.TypeAttributes.Public);
        // Fail at the final required method, proving no first-four delegates leaked out of an incomplete bind.
        foreach (var (field, name) in new[] { ("_enabled", "Enabled"), ("_isMeleeWeapon", "IsMeleeWeapon"),
                     ("_allowsAttack", "AllowsAttack"), ("_shouldHide", "ShouldHideForSwimming") })
        {
            MethodInfo signature = Field(compat, field).FieldType.GetMethod("Invoke")!;
            MethodBuilder method = type.DefineMethod(name, System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
                typeof(bool), signature.GetParameters().Select(p => p.ParameterType).ToArray());
            method.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
            method.GetILGenerator().Emit(OpCodes.Ret);
        }
        return type.CreateType()!;
    }

    private sealed record InstructionState(OpCode Opcode, object? Operand, Label[] Labels, (ExceptionBlockType, Type)[] Blocks);
    private static InstructionState[] Snapshot(List<CodeInstruction> code) => code.Select(i => new InstructionState(i.opcode, i.operand,
        i.labels.ToArray(), i.blocks.Select(b => (b.blockType, b.catchType)).ToArray())).ToArray();
    private static void CheckSameStream(InstructionState[] before, List<CodeInstruction> after, int replaced = -1, MethodInfo? wrapper = null)
    {
        if (before.Length != after.Count) throw new Exception("PoV composition inserted or removed instructions.");
        for (int index = 0; index < before.Length; index++)
        {
            var expected = before[index];
            var actual = after[index];
            if (expected.Opcode != actual.opcode || !Equals(index == replaced ? wrapper : expected.Operand, actual.operand)
                || !expected.Labels.SequenceEqual(actual.labels)
                || !expected.Blocks.SequenceEqual(actual.blocks.Select(b => (b.blockType, b.catchType))))
                throw new Exception("PoV composition changed instruction metadata or control flow at " + index);
        }
    }

    private static IEnumerable<CodeInstruction> IsolateLogger(IEnumerable<CodeInstruction> code)
    {
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.opcode == OpCodes.Ldsfld && instruction.operand is FieldInfo field
                && field.Name == "ServerSyncModTemplateLogger")
                instruction.operand = Field(typeof(PathOfValheimanChecks), nameof(TestLog));
            yield return instruction;
        }
    }
    private static bool LocalPlayerPrefix(object __0, ref bool __result) { __result = ReferenceEquals(__0, _localPlayer); return false; }
    private static bool AnimationHashPrefix(ref int __result) { __result = 0; return false; }
    private static bool BlacklistPrefix(object? __0, ref bool __result) { __result = __0 != null && ReferenceEquals(__0, _blacklistedItem); return false; }
    private static bool OnGroundPrefix(ref bool __result) { __result = _onGround; return false; }
    private static bool CurrentWeaponPrefix(ref object? __result) { __result = _currentWeapon; return false; }
    private static bool ReadMelee(object? item) => item != null && ReferenceEquals(item, _meleeItem);
    private static bool ReadAttack(object? attack, object? item) => attack != null && ReferenceEquals(attack, _allowedAttack) && ReadMelee(item);

    private static void CheckContextPolicies(Type compat)
    {
        Type playerType = _localPlayer!.GetType();
        Type itemType = AccessTools.TypeByName("ItemDrop+ItemData")!;
        Type attackType = AccessTools.TypeByName("Attack")!;
        object melee = RuntimeHelpers.GetUninitializedObject(itemType);
        object shield = RuntimeHelpers.GetUninitializedObject(itemType);
        object ranged = RuntimeHelpers.GetUninitializedObject(itemType);
        object armor = RuntimeHelpers.GetUninitializedObject(itemType);
        FieldInfo sharedField = AccessTools.Field(itemType, "m_shared");
        foreach (object item in new[] { melee, shield, ranged })
        {
            object shared = RuntimeHelpers.GetUninitializedObject(sharedField.FieldType);
            sharedField.SetValue(item, shared);
            AccessTools.Field(shared.GetType(), "m_blockable").SetValue(shared, true);
            AccessTools.Field(shared.GetType(), "m_attack").SetValue(shared, RuntimeHelpers.GetUninitializedObject(attackType));
            AccessTools.Field(shared.GetType(), "m_secondaryAttack").SetValue(shared, RuntimeHelpers.GetUninitializedObject(attackType));
        }
        object sharedMelee = sharedField.GetValue(melee)!;
        object primary = AccessTools.Field(sharedMelee.GetType(), "m_attack").GetValue(sharedMelee)!;
        _meleeItem = melee;
        _allowedAttack = primary;
        SetStub(compat, "_isMeleeWeapon", nameof(ReadMelee));
        SetStub(compat, "_allowsAttack", nameof(ReadAttack));
        SetStub(compat, "_shouldHide", nameof(ReadGate));
        SetStub(compat, "_shouldBlockEquip", nameof(ReadEquipGate));
        _node = true;
        _gate = false;
        _onGround = false;
        AccessTools.Field(playerType, "m_swimTimer").SetValue(_localPlayer, 0f);
        bool Call(string name, params object?[] arguments) => (bool)Method(compat, name).Invoke(null, arguments)!;
        void Hands(object? right, object? left)
        {
            AccessTools.Field(playerType, "m_hiddenRightItem").SetValue(_localPlayer, right);
            AccessTools.Field(playerType, "m_hiddenLeftItem").SetValue(_localPlayer, left);
        }
        foreach (bool gate in new[] { false, true })
            foreach (bool restricted in new[] { false, true })
            {
                _gate = gate;
                _blacklistedItem = restricted ? melee : null;
                Hands(melee, shield);
                if (Call("ShouldBlockWaterEquip", _localPlayer, melee) != (gate || restricted)
                    || Call("ShouldHideForSwimming", _localPlayer) != (gate || restricted))
                    throw new Exception("PoV gate OR item blacklist truth table changed.");
            }
        _gate = false;
        _blacklistedItem = melee;
        AccessTools.Field(playerType, "m_swimTimer").SetValue(_localPlayer, 1f);
        if (Call("ShouldBlockWaterEquip", _localPlayer, melee) || Call("ShouldHideForSwimming", _localPlayer))
            throw new Exception("PoV blacklist wrapper affected land.");
        AccessTools.Field(playerType, "m_swimTimer").SetValue(_localPlayer, 0f);
        object remote = RuntimeHelpers.GetUninitializedObject(playerType);
        if (Call("ShouldBlockWaterEquip", remote, melee) || Call("ShouldHideForSwimming", remote))
            throw new Exception("PoV blacklist wrapper affected a nonlocal player.");
        _blacklistedItem = null;
        Hands(melee, shield);
        if (!Call("CanRestoreHiddenItems", _localPlayer)) throw new Exception("Allowed melee/shield pair cannot restore.");
        _blacklistedItem = armor;
        if (Call("ShouldHideForSwimming", _localPlayer) || !Call("ShouldBlockWaterEquip", _localPlayer, armor))
            throw new Exception("Blacklisted non-hand armor hides unrelated hand equipment or can be equipped.");
        _blacklistedItem = shield;
        if (Call("CanRestoreHiddenItems", _localPlayer)) throw new Exception("Blacklisted hidden partner can restore.");
        if (!ReferenceEquals(AccessTools.Field(playerType, "m_hiddenRightItem").GetValue(_localPlayer), melee)
            || !ReferenceEquals(AccessTools.Field(playerType, "m_hiddenLeftItem").GetValue(_localPlayer), shield))
            throw new Exception("Blacklisted restore check changed hidden hand references.");
        _blacklistedItem = null;
        _blockedItem = ranged;
        Hands(melee, ranged);
        if (Call("CanRestoreHiddenItems", _localPlayer)) throw new Exception("PoV-forbidden hidden partner can restore.");
        if (!ReferenceEquals(AccessTools.Field(playerType, "m_hiddenRightItem").GetValue(_localPlayer), melee)
            || !ReferenceEquals(AccessTools.Field(playerType, "m_hiddenLeftItem").GetValue(_localPlayer), ranged))
            throw new Exception("PoV-forbidden restore check changed hidden hand references.");
        _blockedItem = null;
        Hands(null, shield);
        if (Call("CanRestoreHiddenItems", _localPlayer) || Call("CanUseBlockInput", _localPlayer))
            throw new Exception("Shield-only hidden loadout bypasses PoV's visible-hands gate.");
        Hands(melee, shield);
        _currentWeapon = null;
        if (!Call("CanUseAttackInput", _localPlayer, false) || Call("CanUseAttackInput", _localPlayer, true))
            throw new Exception("Hidden melee restoration candidate / primary vs secondary PoV attack policy changed.");
        if (!Call("CanUseBlockInput", _localPlayer)) throw new Exception("Allowed hidden melee/shield cannot block.");
        Hands(ranged, null);
        if (Call("CanUseAttackInput", _localPlayer, false)) throw new Exception("Ranged hidden weapon bypasses PoV melee policy.");
        _node = false;
        AssertLocked(compat);
        _onGround = true;
        if (!Call("CanUseAttackInput", _localPlayer, false) || !Call("CanUseBlockInput", _localPlayer))
            throw new Exception("Grounded combat exemption changed.");
        _onGround = false;
        Hands(null, null);
        _currentWeapon = null;
        _blacklistedItem = null;
    }
}
