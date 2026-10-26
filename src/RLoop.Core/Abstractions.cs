namespace RLoop.Core;

public interface IResoniteClient : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default);
    // World-session identity, distinct from the connection-scoped SessionInfo.UniqueSessionId.
    // Null means safe reuse of imported instance bindings cannot be established.
    Task<string?> GetWorldSessionIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default);
    Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default);
    Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default);
    Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default);
    Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default);
    Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default);
    Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type,
        CancellationToken cancellationToken = default);
    Task<MemberDefinitionInfo> DescribeComponentMemberPathAsync(string componentType, IReadOnlyList<MemberPathStep> path,
        CancellationToken cancellationToken = default) => Task.FromException<MemberDefinitionInfo>(
            new RLoopException("MEMBER_PATH_REFLECTION_UNAVAILABLE", "This adapter does not expose nested member Reflection.", ExitCodes.OperationFailed));
    Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default);
    // Read-only conversion preflight; no world mutation or asset import.
    Task ValidateComponentMemberAsync(string componentType, string member, string rawValue,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments = null,
        CancellationToken cancellationToken = default) => Task.FromException<SyncMethodCallResult>(
            new RLoopException("SYNC_METHOD_UNAVAILABLE", "This Resonite adapter does not expose SyncMethod calls.", ExitCodes.OperationFailed));
    Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default) => Task.FromException<string>(
            new RLoopException("ASSET_IMPORT_UNAVAILABLE", "This Resonite adapter does not expose asset imports.", ExitCodes.OperationFailed));
    Task<string> ImportTexturePixelsAsync(TexturePixels pixels, string? colorProfile,
        CancellationToken cancellationToken = default) => Task.FromException<string>(
            new RLoopException("ASSET_IMPORT_UNAVAILABLE", "This Resonite adapter does not expose raw texture imports.", ExitCodes.OperationFailed));
}

public interface IResoniteClientDiagnostics
{
    void ResetMetrics();
    ClientMetrics SnapshotMetrics();
}

public interface IFluxTool
{
    Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxResult> DeployAsync(FluxDeployRequest request, CancellationToken cancellationToken = default);
    Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public interface IFluxDeployer
{
    Task<FluxResult> DeployAsync(FluxDeployRequest request, CancellationToken cancellationToken = default);
}

public sealed record FluxBuildRequest(
    string Source,
    string? ProjectDirectory,
    string? Output,
    string? LibraryPath,
    bool CompactErrors = true);

public sealed record FluxDeployRequest(
    string ProjectDirectory,
    string Module,
    string ParentSlotId,
    Uri Url,
    string? LibraryPath,
    string? HelperPath,
    IReadOnlyDictionary<string, string>? InputMap = null,
    IReadOnlyDictionary<string, string>? OutputMap = null);

public sealed record FluxDiagnostic(
    string? File,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    string Severity,
    string Message,
    string Channel,
    string Category,
    bool IsPrimary);

public sealed record FluxResult(bool Success, int ExitCode, string StandardOutput, string StandardError,
    string? OutputPath = null,
    IReadOnlyList<FluxDiagnostic>? Diagnostics = null,
    IReadOnlyList<FluxDiagnostic>? PrimaryDiagnostics = null);

public sealed record FluxToolStatus(bool Available, string Executable, string? Version);
