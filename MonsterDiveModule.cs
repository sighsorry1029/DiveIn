namespace ServerSyncModTemplate;

public partial class ServerSyncModTemplatePlugin
{
    private void InitializeMonsterDiveModule()
    {
        InitializeMonsterDiveYaml();
        ClearRuntimeCaches();
    }

    private void StartMonsterDiveModule()
    {
        SetupMonsterDiveYamlWatcher();
    }

    private void DisposeMonsterDiveModule()
    {
        int restoredMonsterCount = RestoreAllTrackedMonsterDiveFlags();
        if (restoredMonsterCount > 0)
        {
            ServerSyncModTemplateLogger.LogInfo($"Restored original dive flags for {restoredMonsterCount} monster instances.");
        }

        ClearRuntimeCaches();
        DisposeMonsterDiveYamlWatcher();
    }

    private static void ReloadMonsterDiveModule()
    {
        ClearRuntimeCaches();
    }
}
