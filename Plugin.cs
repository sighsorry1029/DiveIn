using BepInEx;

namespace ServerSyncModTemplate;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("Searica.Valheim.UnderTheSea")]
[BepInIncompatibility("blacks7ar.VikingsDoSwim")]
public partial class ServerSyncModTemplatePlugin : BaseUnityPlugin
{
    internal const string ModName = "DiveIn";
    internal const string ModVersion = "1.1.0";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";
}
