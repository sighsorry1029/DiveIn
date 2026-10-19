using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using HarmonyLogger = HarmonyLib.Tools.Logger;

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
    private static bool _gateThrows;
    private static bool? _receivedSwimming;
    private static int _gateReads;
    private static int _fixtureNumber;
    private static readonly (string Field, string Method)[] RequiredBindings =
        [("_enabled", "Enabled"), ("_isMeleeWeapon", "IsMeleeWeapon"), ("_allowsAttack", "AllowsAttack")];
    private static readonly (string Field, string Method)[] GateBindings =
        [("_shouldHide", "ShouldHideForSwimming"), ("_shouldHideWithSwimming", "ShouldHideForSwimming"),
         ("_shouldBlockEquip", "ShouldBlockWaterEquip"), ("_shouldBlockEquipWithSwimming", "ShouldBlockWaterEquip")];

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
            CheckApiShapes(compat, transpilers);
            CheckSwimmingPipeline(compat);
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
                Type povPatch = waterCombat.GetNestedType(equip ? "EquipMeleeInWater" : "KeepMeleeHands", BindingFlags.NonPublic)!;
                MethodInfo povTranspiler = Method(povPatch, "Transpiler");
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(AccessTools.Method(humanoid, target));
                List<CodeInstruction> povCode = Rewrite(povTranspiler, original);
                MethodInfo[] supportedGates = BoundGates(compat, equip);
                int gateIndex = povCode.FindIndex(i => supportedGates.Any(i.Calls));
                if (gateIndex < 0 || povCode.Count(i => supportedGates.Any(i.Calls)) != 1)
                    throw new Exception("PoV no longer emits the expected unique gate: " + target);
                MethodInfo originalGate = (MethodInfo)povCode[gateIndex].operand;
                MethodInfo wrapper = Wrapper(compat, originalGate);

                // Inject markers onto the exact replaced instruction, not only at surrounding branch targets.
                var generator = new DynamicMethod("MetadataProbe", typeof(void), Type.EmptyTypes).GetILGenerator();
                povCode[gateIndex].labels.Add(generator.DefineLabel());
                povCode[gateIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                var before = Snapshot(povCode);
                List<CodeInstruction> composed = Rewrite(transpilers[index], povCode);
                CheckSameStream(before, composed, gateIndex, wrapper);
                if (composed.Any(i => i.Calls(originalGate)) || composed.Count(i => i.Calls(wrapper)) != 1)
                    throw new Exception("PoV gate was not replaced exactly once: " + target);

                // Harmony rebuilds from original IL whenever another mod patches this target.
                Bind(compat, waterCombat);
                List<CodeInstruction> repeated = Rewrite(transpilers[index], Rewrite(povTranspiler,
                    PatchProcessor.GetOriginalInstructions(AccessTools.Method(humanoid, target))));
                if (repeated.Count(i => i.Calls(wrapper)) != 1)
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
            Console.WriteLine("PASS: actual PoV WaterCombat signatures bind the three required policies and supported gate shapes without version checks or optional-lookup warnings; no PoV gameplay or Unity methods executed.");
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
        CheckContextPolicies(compat, withSwimming: true);

        // An authoritative PoV block must short-circuit before any local-player/blacklist/Unity access.
        _gate = true;
        Field(compat, "_shouldHideWithSwimming").SetValue(null, null);
        Field(compat, "_shouldBlockEquipWithSwimming").SetValue(null, null);
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
        var warnings = new List<string>();
        HarmonyLogger.LogChannel oldFilter = HarmonyLogger.ChannelFilter;
        void Capture(object? sender, HarmonyLogger.LogEventArgs args)
        {
            if ((args.LogChannel & (HarmonyLogger.LogChannel.Warn | HarmonyLogger.LogChannel.Error)) != 0)
                warnings.Add(args.Message);
        }
        HarmonyLogger.ChannelFilter |= HarmonyLogger.LogChannel.Warn | HarmonyLogger.LogChannel.Error;
        HarmonyLogger.MessageReceived += Capture;
        try { Method(compat, "Bind").Invoke(null, [waterCombat]); }
        finally
        {
            HarmonyLogger.MessageReceived -= Capture;
            HarmonyLogger.ChannelFilter = oldFilter;
        }
        if (warnings.Count != 0)
            throw new Exception("Supported PoV API emitted optional-lookup warnings: " + string.Join(" | ", warnings));
        foreach (var (field, method) in RequiredBindings.Concat(GateBindings))
        {
            Type[] parameters = Field(compat, field).FieldType.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();
            MethodInfo? expected = waterCombat.GetMethod(method, Static, null, parameters, null);
            Delegate? predicate = Field(compat, field).GetValue(null) as Delegate;
            if (predicate?.Method != expected || RequiredBindings.Any(b => b.Field == field) && predicate == null)
                throw new Exception("PoV delegate did not bind the exact available policy: " + field);
        }
        foreach (bool equip in new[] { false, true })
            if (BoundGates(compat, equip).Length == 0) throw new Exception("No supported PoV gate bound.");
    }

    private static MethodInfo[] BoundGates(Type compat, bool equip) =>
        new[] { equip ? "_shouldBlockEquip" : "_shouldHide", equip ? "_shouldBlockEquipWithSwimming" : "_shouldHideWithSwimming" }
            .Select(name => Field(compat, name).GetValue(null) as Delegate).OfType<Delegate>().Select(d => d.Method).ToArray();
    private static MethodInfo Wrapper(Type compat, MethodInfo gate) =>
        Method(compat, gate.Name + (gate.GetParameters()[0].ParameterType == typeof(bool) ? "WithSwimming" : ""));

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
    private static bool ReadGateWithSwimming(bool swimming, object? actor)
    {
        _receivedSwimming = swimming;
        _gateReads++;
        if (_gateThrows) throw new InvalidOperationException("Verifier gate failure");
        return ReadGate(actor);
    }
    private static bool ReadEquipGateWithSwimming(bool swimming, object? actor, object? item) =>
        ReadGateWithSwimming(swimming, actor) || item != null && ReferenceEquals(item, _blockedItem);

    private enum GateShape { None, Legacy, WithSwimming, Both }

    private static Type IncompleteApi(Type compat) => ApiFixture(compat, GateShape.Legacy, GateShape.None);

    private static Type ApiFixture(Type compat, GateShape hide, GateShape equip, string? invalidField = null, string? invalidKind = null)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DiveIn.PoVShape" + ++_fixtureNumber), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("API").DefineType("FixtureWaterCombat", System.Reflection.TypeAttributes.Public);
        foreach (var (field, name) in RequiredBindings.Concat(GateBindings))
        {
            if (GateBindings.Any(b => b.Field == field))
            {
                GateShape shape = name == "ShouldHideForSwimming" ? hide : equip;
                bool withSwimming = field.EndsWith("WithSwimming", StringComparison.Ordinal);
                if (shape == GateShape.None || shape != GateShape.Both && (shape == GateShape.WithSwimming) != withSwimming) continue;
            }
            MethodInfo signature = Field(compat, field).FieldType.GetMethod("Invoke")!;
            Type[] parameters = signature.GetParameters().Select(p => p.ParameterType).ToArray();
            bool invalid = field == invalidField;
            if (invalid && invalidKind == "parameter") parameters[^1] = typeof(object);
            MethodBuilder method = type.DefineMethod(name,
                (field == "_enabled" ? System.Reflection.MethodAttributes.Private : System.Reflection.MethodAttributes.Public)
                | (invalid && invalidKind == "instance" ? 0 : System.Reflection.MethodAttributes.Static),
                invalid && invalidKind == "return" ? typeof(int) : typeof(bool), parameters);
            if (invalid && invalidKind == "generic") method.DefineGenericParameters("T");
            method.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
            method.GetILGenerator().Emit(OpCodes.Ret);
        }
        return type.CreateType()!;
    }

    private static void CheckApiShapes(Type compat, MethodInfo[] transpilers)
    {
        foreach (var (hide, equip) in new[]
                 { (GateShape.Legacy, GateShape.Legacy), (GateShape.WithSwimming, GateShape.WithSwimming),
                   (GateShape.Legacy, GateShape.WithSwimming), (GateShape.WithSwimming, GateShape.Legacy),
                   (GateShape.Both, GateShape.Both) })
        {
            Type fixture = ApiFixture(compat, hide, equip);
            Bind(compat, fixture);
            for (int index = 0; index < transpilers.Length; index++)
            {
                MethodInfo[] gates = BoundGates(compat, index == 1);
                foreach (MethodInfo gate in gates)
                {
                    Bind(compat, fixture);
                    var instructions = new List<CodeInstruction> { new(OpCodes.Nop), new(OpCodes.Call, gate), new(OpCodes.Ret) };
                    var generator = new DynamicMethod("GateShapeProbe", typeof(void), Type.EmptyTypes).GetILGenerator();
                    instructions[1].labels.Add(generator.DefineLabel());
                    instructions[1].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                    var before = Snapshot(instructions);
                    CheckSameStream(before, Rewrite(transpilers[index], instructions), 1, Wrapper(compat, gate));
                    CheckSameStream(before, instructions); // The input stream belongs to Harmony/other transpilers.
                    if ((bool)Field(compat, "_failed").GetValue(null)!)
                        throw new Exception("Supported emitted PoV gate was rejected: " + gate);
                }
                foreach (int callCount in new[] { 0, 2 })
                {
                    Bind(compat, fixture);
                    var instructions = Enumerable.Range(0, callCount)
                        .Select(n => new CodeInstruction(OpCodes.Call, gates[n % gates.Length])).ToList();
                    instructions.Add(new CodeInstruction(OpCodes.Ret));
                    CheckSameStream(Snapshot(instructions), Rewrite(transpilers[index], instructions));
                    if (!(bool)Field(compat, "_failed").GetValue(null)!)
                        throw new Exception("Ambiguous/missing gate did not fail closed across supported shapes.");
                }
                Bind(compat, fixture);
            }
        }
        foreach (var (hide, equip) in new[]
                 { (GateShape.None, GateShape.Legacy), (GateShape.WithSwimming, GateShape.None), (GateShape.None, GateShape.None) })
            Reject(ApiFixture(compat, hide, equip));
        foreach (string invalidKind in new[] { "return", "instance", "generic", "parameter" })
            foreach (string invalidField in new[] { "_enabled", "_shouldHide", "_shouldBlockEquipWithSwimming" })
                Reject(ApiFixture(compat, GateShape.Legacy, GateShape.WithSwimming, invalidField, invalidKind));

        void Reject(Type fixture)
        {
            Method(compat, "Reset").Invoke(null, null);
            Installed(compat, true);
            try
            {
                Method(compat, "Bind").Invoke(null, [fixture]);
                throw new Exception("Malformed or incomplete PoV contract was accepted: " + fixture.Assembly.FullName);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is MissingMethodException) { }
            if (compat.GetFields(Static).Any(f => typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(null) != null))
                throw new Exception("Rejected PoV shape published partial delegates.");
            AssertLocked(compat);
        }
        Method(compat, "Reset").Invoke(null, null);
        CheckDefaults(compat);
        Console.WriteLine("PASS: synthetic legacy/new/mixed/both-overload PoV signatures; exact emitted-call selection, input/label/block preservation, 0/2-call rejection, malformed contract atomicity and warning-free supported lookup (no version gate).");
    }

    private static void CheckSwimmingPipeline(Type compat)
    {
        Type player = _localPlayer!.GetType();
        object item = RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("ItemDrop+ItemData")!);
        object remote = RuntimeHelpers.GetUninitializedObject(player);
        AccessTools.Field(player, "m_hiddenRightItem").SetValue(_localPlayer, item);
        AccessTools.Field(player, "m_hiddenRightItem").SetValue(remote, item);
        _blockedItem = null;
        _gateThrows = false;

        foreach (bool physical in new[] { false, true })
            foreach (bool input in new[] { false, true })
                foreach (bool gate in new[] { false, true })
                    foreach (bool restricted in new[] { false, true })
                        foreach (bool local in new[] { false, true })
                        {
                            Prepare(physical, restricted);
                            _gate = gate;
                            object actor = local ? _localPlayer : remote;
                            CheckGates(input, actor, gate || physical && restricted && local, expectRead: true);
                        }

        // Gameplay policy checks do not have an incoming IL predicate: only then sample IsSwimming once.
        foreach (bool physical in new[] { false, true })
        {
            Prepare(physical, false);
            _gate = false;
            foreach (bool equip in new[] { false, true })
            {
                _receivedSwimming = null;
                _gateReads = 0;
                string name = equip ? "ShouldBlockWaterEquip" : "ShouldHideForSwimming";
                Method(compat, name).Invoke(null, equip ? [_localPlayer, item] : [_localPlayer]);
                if (_receivedSwimming != physical || _gateReads != 1)
                    throw new Exception("Manual policy query did not supply actual swimming to the new gate exactly once.");
            }
        }

        // Existing IL delegates and the new overload may coexist. Never route old calls through the new gate.
        Prepare(true, false);
        SetStub(compat, "_shouldHide", nameof(ReadGate));
        SetStub(compat, "_shouldBlockEquip", nameof(ReadEquipGate));
        _gateReads = 0;
        Method(compat, "ShouldHideForSwimming").Invoke(null, [_localPlayer]);
        Method(compat, "ShouldBlockWaterEquip").Invoke(null, [_localPlayer, item]);
        if (_gateReads != 0) throw new Exception("Legacy gate call was routed to the new overload while both existed.");

        foreach (string failure in new[] { "failed", "missing", "exception" })
            foreach (bool physical in new[] { false, true })
                foreach (bool input in new[] { false, true })
                    foreach (bool restricted in new[] { false, true })
                        foreach (bool equip in new[] { false, true })
                        {
                            Prepare(physical, restricted);
                            _gate = !input; // On failure, this result must not replace the incoming pipeline predicate.
                            Field(compat, "_failed").SetValue(null, failure == "failed");
                            if (failure == "missing")
                            {
                                Field(compat, "_shouldHideWithSwimming").SetValue(null, null);
                                Field(compat, "_shouldBlockEquipWithSwimming").SetValue(null, null);
                            }
                            _gateThrows = failure == "exception";
                            CheckGate(equip, input, _localPlayer, input || physical && restricted, failure == "exception");
                            if (failure != "missing" && !(bool)Field(compat, "_failed").GetValue(null)!)
                                throw new Exception("New gate failure did not disable combat assists.");
                            AssertLocked(compat);
                        }
        _gateThrows = false;
        _blacklistedItem = null;
        AccessTools.Field(player, "m_hiddenRightItem").SetValue(_localPlayer, null);
        AccessTools.Field(player, "m_swimTimer").SetValue(_localPlayer, 0f);
        Method(compat, "Reset").Invoke(null, null);
        CheckDefaults(compat);
        Console.WriteLine("PASS: incoming swimming predicate is preserved independently of physical water state, with local-only blacklist OR and missing/failed/throwing-gate fallback; manual queries and legacy precedence checked.");

        void Prepare(bool physical, bool restricted)
        {
            Method(compat, "Reset").Invoke(null, null);
            Installed(compat, true);
            SetStub(compat, "_shouldHideWithSwimming", nameof(ReadGateWithSwimming));
            SetStub(compat, "_shouldBlockEquipWithSwimming", nameof(ReadEquipGateWithSwimming));
            AccessTools.Field(player, "m_swimTimer").SetValue(_localPlayer, physical ? 0f : 1f);
            AccessTools.Field(player, "m_swimTimer").SetValue(remote, physical ? 0f : 1f);
            _blacklistedItem = restricted ? item : null;
            _gateThrows = false;
        }
        void CheckGates(bool input, object actor, bool expected, bool expectRead)
        {
            CheckGate(false, input, actor, expected, expectRead);
            CheckGate(true, input, actor, expected, expectRead);
        }
        void CheckGate(bool equip, bool input, object actor, bool expected, bool expectRead)
        {
            _receivedSwimming = null;
            _gateReads = 0;
            string name = equip ? "ShouldBlockWaterEquipWithSwimming" : "ShouldHideForSwimmingWithSwimming";
            object?[] arguments = equip ? [input, actor, item] : [input, actor];
            if ((bool)Method(compat, name).Invoke(null, arguments)! != expected)
                throw new Exception("Incoming swimming / physical water / blacklist / fallback policy changed: " + name);
            if (_gateReads != (expectRead ? 1 : 0) || _receivedSwimming != (expectRead ? input : (bool?)null))
                throw new Exception("Incoming swimming value was resampled, lost or passed more than once: " + name);
        }
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

    private static void CheckContextPolicies(Type compat, bool withSwimming = false)
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
        Field(compat, withSwimming ? "_shouldHide" : "_shouldHideWithSwimming").SetValue(null, null);
        Field(compat, withSwimming ? "_shouldBlockEquip" : "_shouldBlockEquipWithSwimming").SetValue(null, null);
        SetStub(compat, withSwimming ? "_shouldHideWithSwimming" : "_shouldHide", withSwimming ? nameof(ReadGateWithSwimming) : nameof(ReadGate));
        SetStub(compat, withSwimming ? "_shouldBlockEquipWithSwimming" : "_shouldBlockEquip", withSwimming ? nameof(ReadEquipGateWithSwimming) : nameof(ReadEquipGate));
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
