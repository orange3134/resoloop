namespace RLoop.Core;

public interface IResoniteClient : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default);
    Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default);
    Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default);
    Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default);
    Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default);
    Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default);
    Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default);
    Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type,
        CancellationToken cancellationToken = default);
    Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default);
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
    string? HelperPath);

public sealed record FluxResult(bool Success, int ExitCode, string StandardOutput, string StandardError,
    string? OutputPath = null);

public sealed record FluxToolStatus(bool Available, string Executable, string? Version);
