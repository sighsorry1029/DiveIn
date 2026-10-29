using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Code = Mono.Cecil.Cil.Code;

// Read original assemblies only; never publicize, load a world or change game files.
// Run once per client/server in separate processes, so reflection cannot reuse another game's types.
if (args.Length is not (3 or 4))
    throw new ArgumentException("Usage: <merged DiveIn.dll> <original Managed directory> <BepInEx core directory> [PathOfValheiman.dll]");
string modPath = Path.GetFullPath(args[0]);
string managed = Path.GetFullPath(args[1]);
string core = Path.GetFullPath(args[2]);
string[] directories = args.Length == 4
    ? [managed, core, Path.GetDirectoryName(modPath)!, Path.GetDirectoryName(Path.GetFullPath(args[3]))!]
    : [managed, core, Path.GetDirectoryName(modPath)!];
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name);
    foreach (string directory in directories)
    {
        string path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return Assembly.LoadFrom(path);
    }
    return null;
};

var resolver = new DefaultAssemblyResolver();
foreach (string directory in directories) resolver.AddSearchDirectory(directory);
using var module = ModuleDefinition.ReadModule(modPath, new ReaderParameters { AssemblyResolver = resolver });
var gameAssemblies = new HashSet<string> { "assembly_valheim", "assembly_utils", "assembly_guiutils" };
int referenceCount = 0;
foreach (var type in module.GetTypes())
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.Operand is not MemberReference member ||
        !gameAssemblies.Contains(member.DeclaringType?.Scope.Name ?? "")) continue;
    switch (member)
    {
        case FieldReference field:
            var fieldDefinition = field.Resolve() ?? throw new Exception("Unresolved field: " + field.FullName);
            if (!fieldDefinition.IsPublic) throw new Exception("Direct non-public field access: " + field.FullName);
            if (fieldDefinition.IsLiteral) throw new Exception("Literal accessed as field: " + field.FullName);
            break;
        case MethodReference call:
            var definition = call.Resolve() ?? throw new Exception("Unresolved method: " + call.FullName);
            if (!definition.IsPublic) throw new Exception("Direct non-public method access: " + call.FullName);
            break;
        default: continue;
    }
    referenceCount++;
}
Console.WriteLine($"PASS: {referenceCount} game member operands resolve against original DLLs, with no direct non-public/literal access.");

Assembly mod = Assembly.LoadFrom(modPath);
Type access = mod.GetType("ServerSyncModTemplate.GameAccess", true)!;
RuntimeHelpers.RunClassConstructor(access.TypeHandle);
var accessors = access.GetFields(BindingFlags.Static | BindingFlags.NonPublic);
foreach (var field in accessors)
    if (field.GetValue(null) is not Delegate) throw new Exception("Accessor not initialized: " + field.Name);
Console.WriteLine($"PASS: {accessors.Length} cached Harmony accessors initialized against original DLLs (.NET 9 / HarmonyX 2.16 test host, not Unity).");

int patches = 0;
var targets = new Dictionary<string, MethodBase>();
foreach (var type in mod.GetTypes().Where(t => t.Namespace is "ServerSyncModTemplate" or "ServerSync"))
foreach (var patch in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
{
    if (patch.Name is not ("Prefix" or "Postfix" or "Transpiler" or "Finalizer")
        && !patch.IsDefined(typeof(HarmonyPrefix)) && !patch.IsDefined(typeof(HarmonyPostfix))
        && !patch.IsDefined(typeof(HarmonyTranspiler)) && !patch.IsDefined(typeof(HarmonyFinalizer))) continue;
    var attributes = type.GetCustomAttributes<HarmonyPatch>().Concat(patch.GetCustomAttributes<HarmonyPatch>()).ToArray();
    var declaringType = attributes.Select(a => a.info.declaringType).LastOrDefault(t => t != null);
    string? methodName = attributes.Select(a => a.info.methodName).LastOrDefault(n => n != null);
    if (declaringType == null || methodName == null) continue;
    var parameters = attributes.Select(a => a.info.argumentTypes).LastOrDefault(p => p != null);
    MethodInfo original = AccessTools.DeclaredMethod(declaringType, methodName, parameters)
                          ?? throw new Exception($"Missing patch target: {declaringType.FullName}.{methodName}");
    foreach (var parameter in patch.GetParameters())
    {
        if (patch.IsDefined(typeof(HarmonyTranspiler))) continue;
        if (parameter.Name == "__instance")
        {
            if (!parameter.ParameterType.IsAssignableFrom(declaringType)) throw new Exception("Invalid __instance: " + patch);
            continue;
        }
        if (parameter.Name == "__result")
        {
            Type resultType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            if (resultType != original.ReturnType) throw new Exception("Invalid __result: " + patch);
            continue;
        }
        if (parameter.Name!.StartsWith("___"))
        {
            var injectedField = AccessTools.Field(declaringType, parameter.Name.Substring(3))
                                ?? throw new Exception("Missing injected field: " + parameter.Name);
            Type valueType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            if (valueType != injectedField.FieldType) throw new Exception("Invalid injected field type: " + parameter.Name);
            continue;
        }
        if (parameter.Name.StartsWith("__")) continue;
        var gameParameter = original.GetParameters().SingleOrDefault(p => p.Name == parameter.Name)
                            ?? throw new Exception($"Missing injected parameter {parameter.Name} on {original}");
        Type PatchType(Type t) => t.IsByRef ? t.GetElementType()! : t;
        if (PatchType(parameter.ParameterType) != PatchType(gameParameter.ParameterType))
            throw new Exception("Injected parameter type mismatch: " + parameter.Name);
    }
    targets[declaringType.Name + "." + methodName] = original;
    patches++;
}
Console.WriteLine($"PASS: {patches} patch methods target {targets.Count} original methods; named parameter injection matches.");

Type equipment = mod.GetType("ServerSyncModTemplate.WaterEquipmentPatches", true)!;
MethodInfo matcher = equipment.GetMethod("TryFindSwimmingRestrictionInsertionPoint", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (string name in new[] { "Humanoid.UpdateEquipment", "Humanoid.EquipItem" })
{
    List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(targets[name]);
    object?[] arguments = [instructions, name, null, null];
    if (!(bool)matcher.Invoke(null, arguments)!) throw new Exception("Swimming restriction pattern mismatch: " + name);
    var cursor = (CodeMatcher)arguments[2]!;
    if (cursor.Pos < 6) throw new Exception("Bad insertion position");
    Console.WriteLine($"PASS: {name} swimming/ground branches share a target; insertion index {cursor.Pos}.");
}
CheckSwimHud(mod);
CheckWaterSurfaceScope(module);
PathOfValheimanChecks.Run(mod, module, args.Length == 4 ? Path.GetFullPath(args[3]) : null);
Console.WriteLine("Compatibility checks passed. Gameplay, Unity callbacks, rendering and multiplayer remain in-game checks.");

// Check retirement and retained call wiring only; this does not execute Unity material operations.
static void CheckWaterSurfaceScope(ModuleDefinition module)
{
    if (module.GetType("ServerSyncModTemplate.WaterColorSeamPatch") != null)
        throw new Exception("Retired water color seam patch is still present.");
    var instructions = module.GetTypes().Where(t => t.FullName.StartsWith("ServerSyncModTemplate.", StringComparison.Ordinal))
        .SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
    if (instructions.Any(i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, "_MainTex_ST")))
        throw new Exception("DiveIn still references the retired water UV uniform.");

    var renderer = module.GetType("ServerSyncModTemplate.UnderwaterSurfaceRenderer")
                   ?? throw new Exception("Missing separate underwater surface renderer.");
    var visuals = module.GetType("ServerSyncModTemplate.UnderwaterCameraPatches")
                  ?? throw new Exception("Missing underwater camera/material patches.");
    var visualCalls = visuals.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
        .Where(i => i.OpCode.Code is Code.Call or Code.Callvirt).Select(i => (MethodReference)i.Operand).ToArray();
    foreach (string name in new[] { "Apply", "Reset", "ResetAll", "ResetStale" })
    {
        var entry = renderer.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic && m.HasBody)
                    ?? throw new Exception("Missing underwater surface entry point: " + name);
        if (!visualCalls.Any(call => call.FullName == entry.FullName))
            throw new Exception("Underwater visual lifecycle no longer calls: " + name);
    }
    Console.WriteLine("PASS: retired water seam patch/uniform absent; separate underwater surface apply/reset entry points remain wired. Rendering still requires in-game checks.");
}

// Exercise the production row policy and config migration without constructing Unity objects.
static void CheckSwimHud(Assembly mod)
{
    Type plugin = mod.GetType("ServerSyncModTemplate.ServerSyncModTemplatePlugin", true)!;
    Type modeType = plugin.GetNestedType("SwimHudMode")!;
    Type hud = mod.GetType("ServerSyncModTemplate.FastSwimHud", true)!;
    MethodInfo getRows = hud.GetMethod("GetRows", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (string mode in new[] { "Full", "FastSwimOnly", "Off" })
    foreach (bool canFastSwim in new[] { false, true })
    {
        var rows = ((bool Controls, bool Status))getRows.Invoke(null, [Enum.Parse(modeType, mode), canFastSwim])!;
        if (rows.Controls != (mode == "Full") || rows.Status != (mode != "Off" && canFastSwim))
            throw new Exception($"HUD row policy mismatch: {mode}, canFastSwim={canFastSwim}");
    }
    Console.WriteLine("PASS: all 6 HUD mode/availability combinations (Full retains controls when Fast Swim is unavailable).");

    MethodInfo migrate = plugin.GetMethod("TakePersistedSwimHudMode", BindingFlags.NonPublic | BindingFlags.Static)!;
    MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethod
        && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(ConfigDefinition))
        .MakeGenericMethod(modeType);
    var definition = new ConfigDefinition("2 - Player Diving", "Swim HUD Mode");
    object full = Enum.Parse(modeType, "Full");
    (string Body, string? Serialized, string Expected)[] cases =
    [
        ("", null, "Full"),
        ("Show Fast Swim HUD = On", "Full", "Full"),
        ("Show Fast Swim HUD = Off", "Off", "Off"),
        ("Show Fast Swim HUD = 0", "Off", "Off"),
        ("Show Fast Swim HUD = invalid", null, "Full"),
        ("Show Fast Swim HUD = Off\nSwim HUD Mode = Full", "Full", "Full"),
        ("Show Fast Swim HUD = On\nSwim HUD Mode = Off", "Off", "Off"),
        ("Show Fast Swim HUD = Off\nSwim HUD Mode = FastSwimOnly", "FastSwimOnly", "FastSwimOnly"),
        ("Show Fast Swim HUD = Off\nSwim HUD Mode = ", "", "Full"),
        ("Show Fast Swim HUD = Off\nSwim HUD Mode = invalid", "invalid", "Full")
    ];
    string tempDirectory = Path.Combine(Path.GetTempPath(), "DiveIn-HudChecks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        foreach (var test in cases)
        foreach (bool saveOnSet in new[] { false, true })
        {
            string configPath = Path.Combine(tempDirectory, "test.cfg");
            try
            {
                string original = "[2 - Player Diving]\n" + test.Body + "\nUnrelated = keep\n";
                File.WriteAllText(configPath, original);
                var config = new ConfigFile(configPath, false) { SaveOnConfigSet = saveOnSet };
                string? serialized = (string?)migrate.Invoke(null, [config]);
                if (serialized != test.Serialized || config.SaveOnConfigSet != saveOnSet
                    || config.Count != 0 || File.ReadAllText(configPath) != original)
                    throw new Exception("HUD migration altered live file, lost flag restoration or read wrong value: " + test.Body);

                var entry = (ConfigEntryBase)bind.Invoke(config, [definition, full, null])!;
                if (serialized != null) entry.SetSerializedValue(serialized);
                if (entry.BoxedValue.ToString() != test.Expected || entry.DefaultValue.ToString() != "Full")
                    throw new Exception("HUD enum value/default mismatch: " + test.Body);
                config.Save();
                string saved = File.ReadAllText(configPath);
                if (saved.Contains("Show Fast Swim HUD") || !saved.Contains("Unrelated = keep"))
                    throw new Exception("HUD migration retained the retired key or lost unrelated settings.");
                var reloaded = new ConfigFile(configPath, false);
                if ((string?)migrate.Invoke(null, [reloaded]) != test.Expected)
                    throw new Exception("HUD migration was not stable after save/reload.");
            }
            finally
            {
                File.Delete(configPath);
            }
        }
    }
    finally
    {
        Directory.Delete(tempDirectory);
    }
    Console.WriteLine("PASS: 20 persisted HUD config cases: migration, new-key precedence, default, retired-key removal, unrelated values, no premature save and reload.");
}
