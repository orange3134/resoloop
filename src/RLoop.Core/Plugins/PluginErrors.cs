namespace RLoop.Core.Plugins;

public static class PluginErrors
{
    public static RLoopException Create(string code, string message,
        IReadOnlyDictionary<string, object?>? context = null, IReadOnlyList<string>? suggestions = null,
        Exception? innerException = null) =>
        new(code, message, code switch
        {
            "PLUGIN_PROJECT_NOT_FOUND" => ExitCodes.ConfigurationError,
            "PLUGIN_GIT_REF_REQUIRED" or "PLUGIN_NAME_INVALID" => ExitCodes.InvalidArguments,
            "PLUGIN_SOURCE_NOT_FOUND" or "PLUGIN_GIT_REF_NOT_FOUND" or "PLUGIN_NOT_INSTALLED" => ExitCodes.NotFound,
            "PLUGIN_GIT_FAILED" => ExitCodes.ExternalToolFailed,
            "PLUGIN_PROJECT_BUSY" => ExitCodes.OperationFailed,
            "PLUGIN_SOURCE_INVALID" or "PLUGIN_MANIFEST_NOT_FOUND" or "PLUGIN_MANIFEST_INVALID"
                or "PLUGIN_SCHEMA_UNSUPPORTED" or "PLUGIN_INCOMPATIBLE_VERSION" or "PLUGIN_NAME_CONFLICT"
                or "PLUGIN_ALREADY_INSTALLED" or "PLUGIN_RECORD_INVALID"
                or "PLUGIN_MODIFIED" or "PLUGIN_REMOVE_REFUSED" or "PLUGIN_PATH_UNSAFE"
                or "PLUGIN_IGNORE_CONFLICT" or "CONFIRMATION_REQUIRED" => ExitCodes.ValidationFailed,
            _ => ExitCodes.OperationFailed
        }, context, suggestions, innerException);
}
