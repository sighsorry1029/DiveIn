using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Code = Mono.Cecil.Cil.Code;

// Read original assemblies only; never publicize, load a world or change game files.
// Run once per client/server in separate processes, so reflection cannot reuse another game's types.
if (args.Length != 3)
    throw new ArgumentException("Usage: <merged DiveIn.dll> <original Managed directory> <BepInEx core directory>");
string modPath = Path.GetFullPath(args[0]);
string managed = Path.GetFullPath(args[1]);
string core = Path.GetFullPath(args[2]);
string[] directories = [managed, core, Path.GetDirectoryName(modPath)!];
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
CheckWaterColorSeamPatch(module);
Console.WriteLine("Compatibility checks passed. Gameplay, Unity callbacks, rendering and multiplayer remain in-game checks.");

// Bound the compiled patch's scope without calling Unity native APIs or claiming rendered-shader validation.
static void CheckWaterColorSeamPatch(ModuleDefinition module)
{
    var type = module.GetType("ServerSyncModTemplate.WaterColorSeamPatch")
               ?? throw new Exception("Missing water color seam patch.");
    if (type.Fields.Count != 0 || type.Methods.Count != 1)
        throw new Exception("Water seam patch gained state, a constructor or an additional hook.");
    var patch = type.Methods.Single();
    if (patch.Name != "Postfix" || !patch.IsPrivate || !patch.IsStatic || !patch.HasBody
        || patch.ReturnType.FullName != "System.Void" || patch.Parameters.Count != 1
        || patch.Parameters[0].ParameterType.FullName != "WaterVolume")
        throw new Exception("Unexpected water seam patch signature.");

    var instructions = patch.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToArray();
    var calls = instructions.Where(i => i.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj).ToArray();
    var expectedCalls = new Dictionary<string, int>
    {
        ["UnityEngine.Rendering.GraphicsDeviceType UnityEngine.SystemInfo::get_graphicsDeviceType()"] = 1,
        ["System.Boolean UnityEngine.Object::op_Equality(UnityEngine.Object,UnityEngine.Object)"] = 3,
        ["UnityEngine.Material UnityEngine.Renderer::get_material()"] = 1,
        ["UnityEngine.Shader UnityEngine.Material::get_shader()"] = 2,
        ["System.String UnityEngine.Object::get_name()"] = 1,
        ["System.Boolean System.String::op_Inequality(System.String,System.String)"] = 1,
        ["System.Boolean UnityEngine.Material::HasProperty(System.String)"] = 1,
        ["System.Void UnityEngine.Vector4::.ctor(System.Single,System.Single,System.Single,System.Single)"] = 1,
        ["System.Void UnityEngine.Material::SetVector(System.String,UnityEngine.Vector4)"] = 1
    };
    foreach (var group in calls.GroupBy(i => ((MethodReference)i.Operand).FullName))
        if (!expectedCalls.Remove(group.Key, out int count) || group.Count() != count)
            throw new Exception("Unexpected water seam patch call/count: " + group.Key);
    if (expectedCalls.Count != 0) throw new Exception("Missing water seam guard or identity setter.");

    foreach (var instruction in instructions)
    {
        if (instruction.Operand is FieldReference field
            && (instruction.OpCode.Code != Code.Ldfld || field.FullName != "UnityEngine.MeshRenderer WaterVolume::m_waterSurface"))
            throw new Exception("Water seam patch reads or writes unrelated state: " + field.FullName);
        if (instruction.OpCode.Name.StartsWith("stind") || instruction.OpCode.Name.StartsWith("stelem")
            || instruction.OpCode.Code is Code.Stfld or Code.Stsfld or Code.Stobj or Code.Cpobj
                or Code.Initobj or Code.Cpblk or Code.Initblk or Code.Calli)
            throw new Exception("Water seam patch contains an unexpected state write or indirect call.");
        if (instruction.OpCode.Code == Code.Newobj
            && ((MethodReference)instruction.Operand).DeclaringType.FullName != "UnityEngine.Vector4")
            throw new Exception("Water seam patch allocates an unexpected object.");
    }
    var strings = instructions.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => (string)i.Operand).ToArray();
    if (!strings.SequenceEqual(new[] { "Custom/Water", "_MainTex", "_MainTex_ST" }))
        throw new Exception("Water seam patch changed its shader/property scope.");

    int CallIndex(string declaringType, string name) => Array.FindIndex(instructions,
        i => i.Operand is MethodReference m && m.DeclaringType.FullName == declaringType && m.Name == name);
    int graphics = CallIndex("UnityEngine.SystemInfo", "get_graphicsDeviceType");
    int material = CallIndex("UnityEngine.Renderer", "get_material");
    var graphicsType = ((MethodReference)instructions[graphics].Operand).ReturnType.Resolve();
    int nullGraphics = Convert.ToInt32(graphicsType.Fields.Single(f => f.Name == "Null").Constant);
    var nullConstant = instructions[graphics + 1];
    int? actualNull = nullConstant.OpCode.Code switch
    {
        Code.Ldc_I4 => (int)nullConstant.Operand,
        Code.Ldc_I4_S => (sbyte)nullConstant.Operand,
        var code when (int)code >= (int)Code.Ldc_I4_M1 && (int)code <= (int)Code.Ldc_I4_8 => (int)code - (int)Code.Ldc_I4_0,
        _ => null
    };
    if (actualNull != nullGraphics || graphics >= material
        || !instructions.Skip(graphics).Take(material - graphics).Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch))
        throw new Exception("Water seam patch lacks the early headless guard before material access.");
    foreach (var call in calls.Where(i => ((MethodReference)i.Operand).Name == "op_Equality"))
        if (instructions[Array.IndexOf(instructions, call) - 1].OpCode.Code != Code.Ldnull)
            throw new Exception("Water seam patch changed a renderer/material/shader null guard.");
    int shaderNameCheck = CallIndex("System.String", "op_Inequality");
    int mainTextureCheck = CallIndex("UnityEngine.Material", "HasProperty");
    int setter = CallIndex("UnityEngine.Material", "SetVector");
    if (!Equals(instructions[shaderNameCheck - 1].Operand, "Custom/Water")
        || !Equals(instructions[mainTextureCheck - 1].Operand, "_MainTex")
        || mainTextureCheck >= setter
        || !instructions.Skip(mainTextureCheck).Take(setter - mainTextureCheck).Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch))
        throw new Exception("Water seam patch lacks the expected shader/main-texture guards.");
    if (setter < 6 || !Equals(instructions[setter - 6].Operand, "_MainTex_ST")
        || instructions[setter - 1].OpCode.Code != Code.Newobj)
        throw new Exception("Water seam patch does not set a direct identity vector.");
    float[] identity = [1f, 1f, 0f, 0f];
    for (int i = 0; i < identity.Length; i++)
        if (instructions[setter - 5 + i].OpCode.Code != Code.Ldc_R4
            || !Equals(instructions[setter - 5 + i].Operand, identity[i]))
            throw new Exception("Water seam patch changed the identity UV transform.");
    Console.WriteLine("PASS: static water seam patch contract: guarded setup-only identity UV write, no extra calls or state writes. Branch semantics/rendering still require review/in-game checks.");
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
