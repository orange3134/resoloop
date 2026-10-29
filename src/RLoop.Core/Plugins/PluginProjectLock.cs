namespace RLoop.Core.Plugins;

/// <summary>Cooperative, process-scoped ownership of an existing project's plugin area.</summary>
internal static class PluginProjectLock
{
    public static IDisposable Acquire(string pluginsDirectory)
    {
        PluginPathPolicy.EnsureNoLinks(pluginsDirectory, "PLUGIN_PATH_UNSAFE");
        if (!Directory.Exists(pluginsDirectory))
            throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Plugin lock requires an existing directory.");
        var path = Path.Combine(pluginsDirectory, ".lock");
        PluginPathPolicy.EnsureNoLinks(path, "PLUGIN_PATH_UNSAFE", allowMissing: true);
        if (Directory.Exists(path)) throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Plugin lock is not a regular file.");
        try
        {
            // Keep the inode/name: unlinking a released lock could split ownership across processes.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
        {
            throw PluginErrors.Create("PLUGIN_PROJECT_BUSY", "Another operation is using this project's plugins.", innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Could not acquire the project plugin lock.", innerException: ex);
        }
    }
}
