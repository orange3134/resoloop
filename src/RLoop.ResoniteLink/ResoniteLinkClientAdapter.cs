using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

public sealed class ResoniteLinkClientAdapter : IResoniteClient, IResoniteClientDiagnostics
{
    private readonly Link.LinkInterface _link = new();
    private readonly TimeSpan _requestTimeout;
    private readonly Dictionary<string, Link.ComponentDefinition> _componentDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableMetric> _metrics = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _allComponentTypes;
    private int _cacheHits;
    private Uri? _uri;

    public ResoniteLinkClientAdapter(TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(30);
    }

    public async Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _link.Connect(uri, timeoutCts.Token).ConfigureAwait(false);
            _uri = uri;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RLoopException("CONNECTION_TIMEOUT", $"Connection to '{uri}' timed out after {timeout.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?> { ["url"] = uri.ToString(), ["timeoutSeconds"] = timeout.TotalSeconds },
                ["Verify ResoniteLink is enabled for the active world and the port is current."], ex);
        }
        catch (Exception ex)
        {
            throw new RLoopException("CONNECTION_FAILED", $"Could not connect to ResoniteLink at '{uri}': {ex.Message}",
                ExitCodes.ConnectionFailed, new Dictionary<string, object?> { ["url"] = uri.ToString() },
                ["Verify Resonite is running, ResoniteLink is enabled, and RESONITE_LINK_URL contains the current port."], ex);
        }
    }

    public async Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetSessionData(), "session.get", cancellationToken);
        EnsureSuccess(response, "SESSION_INFO_FAILED");
        return new SessionInfo(_uri!.ToString(), true, response.ResoniteVersion, response.ResoniteLinkVersion, response.UniqueSessionId);
    }

    public async Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetSlotData(new Link.GetSlot { SlotID = id, Depth = depth, IncludeComponentData = includeComponentData }), "slot.get", cancellationToken);
        EnsureSuccess(response, "SLOT_NOT_FOUND", new Dictionary<string, object?> { ["slotId"] = id });
        return ModelMapper.MapSlot(response.Data);
    }

    public async Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = id }), "component.get", cancellationToken);
        EnsureSuccess(response, "COMPONENT_NOT_FOUND", new Dictionary<string, object?> { ["componentId"] = id });
        return ModelMapper.MapComponent(response.Data);
    }

    public async Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var slot = new Link.Slot
        {
            ID = request.RequestedId,
            Parent = new Link.Reference { TargetID = request.ParentId },
            Name = new Link.Field_string { Value = request.Name },
            Position = request.Position is null ? null : new Link.Field_float3 { Value = ToLink(request.Position) },
            Rotation = request.Rotation is null ? null : new Link.Field_floatQ { Value = ToLink(request.Rotation) },
            Scale = request.Scale is null ? null : new Link.Field_float3 { Value = ToLink(request.Scale) }
        };
        var response = await Wait(_link.AddSlot(new Link.AddSlot { Data = slot }), "slot.add", cancellationToken);
        EnsureSuccess(response, "SLOT_CREATE_FAILED", new Dictionary<string, object?> { ["parentId"] = request.ParentId, ["name"] = request.Name });
        return response.EntityId;
    }

    public async Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var slot = new Link.Slot
        {
            ID = request.Id,
            Parent = request.ParentId is null ? null : new Link.Reference { TargetID = request.ParentId },
            Name = request.Name is null ? null : new Link.Field_string { Value = request.Name },
            Position = request.Position is null ? null : new Link.Field_float3 { Value = ToLink(request.Position) },
            Rotation = request.Rotation is null ? null : new Link.Field_floatQ { Value = ToLink(request.Rotation) },
            Scale = request.Scale is null ? null : new Link.Field_float3 { Value = ToLink(request.Scale) }
        };
        var response = await Wait(_link.UpdateSlot(new Link.UpdateSlot { Data = slot }), "slot.update", cancellationToken);
        EnsureSuccess(response, "SLOT_UPDATE_FAILED", new Dictionary<string, object?> { ["slotId"] = request.Id });
    }

    public async Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveSlot(new Link.RemoveSlot { SlotID = id }), "slot.remove", cancellationToken);
        EnsureSuccess(response, "SLOT_DELETE_FAILED", new Dictionary<string, object?> { ["slotId"] = id });
    }

    public async Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var definition = await GetComponentDefinitionCachedAsync(componentType, cancellationToken);
        var resolvedType = definition.Type.FullTypeName;

        var members = new Dictionary<string, Link.Member>(StringComparer.Ordinal);
        foreach (var assignment in fields)
        {
            if (!definition.Members.TryGetValue(assignment.Key, out var memberDefinition))
                throw UnknownMember(resolvedType, assignment.Key, definition.Members.Keys);
            members[assignment.Key] = await ValueCodec.ParseAsync(_link, memberDefinition, assignment.Value, cancellationToken, _requestTimeout, RecordMetric);
        }

        var response = await Wait(_link.AddComponent(new Link.AddComponent
        {
            ContainerSlotId = slotId,
            Data = new Link.Component { ComponentType = resolvedType, Members = members }
        }), "component.add", cancellationToken);
        EnsureSuccess(response, "COMPONENT_ADD_FAILED", new Dictionary<string, object?> { ["slotId"] = slotId, ["componentType"] = resolvedType });
        return new ComponentCreateResult(response.EntityId, resolvedType);
    }

    public async Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var componentResponse = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = componentId }), "component.get", cancellationToken);
        EnsureSuccess(componentResponse, "COMPONENT_NOT_FOUND", new Dictionary<string, object?> { ["componentId"] = componentId });
        await SetComponentMembersAsync(componentId, componentResponse.Data.ComponentType,
            new Dictionary<string, string> { [member] = rawValue }, cancellationToken);
    }

    public async Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (fields.Count == 0) return;
        var definition = await GetComponentDefinitionCachedAsync(componentType, cancellationToken);
        var members = new Dictionary<string, Link.Member>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!definition.Members.TryGetValue(field.Key, out var memberDefinition))
                throw UnknownMember(definition.Type.FullTypeName, field.Key, definition.Members.Keys);
            members[field.Key] = await ValueCodec.ParseAsync(_link, memberDefinition, field.Value, cancellationToken, _requestTimeout, RecordMetric);
        }
        var response = await Wait(_link.UpdateComponent(new Link.UpdateComponent
        {
            Data = new Link.Component { ID = componentId, Members = members }
        }), "component.update", cancellationToken);
        EnsureSuccess(response, "COMPONENT_UPDATE_FAILED", new Dictionary<string, object?>
            { ["componentId"] = componentId, ["members"] = fields.Keys.ToArray() });
    }

    public async Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveComponent(new Link.RemoveComponent { ComponentID = componentId }), "component.remove", cancellationToken);
        EnsureSuccess(response, "COMPONENT_REMOVE_FAILED", new Dictionary<string, object?> { ["componentId"] = componentId });
    }

    public async Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var types = await GetAllComponentTypeNames(cancellationToken);
        return types
            .Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => SearchScore(x, query)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var definition = await GetComponentDefinitionCachedAsync(type, cancellationToken);
        var members = definition.Members.Select(x => ModelMapper.MapMemberDefinition(x.Key, x.Value)).ToArray();
        var methods = definition.Methods.Select(ModelMapper.MapMethodDefinition).ToArray();
        return new ComponentTypeInfo(definition.Type.FullTypeName, definition.CategoryPath,
            ModelMapper.Render(definition.Type.BaseType), definition.Type.IsGenericType, members, methods);
    }

    public async Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetTypeDefinition(type), "type.get", cancellationToken);
        if (!response.Success)
        {
            var resolved = await ResolveComponentTypeAsync(type, cancellationToken);
            response = await Wait(_link.GetTypeDefinition(resolved), "type.get", cancellationToken);
        }
        EnsureSuccess(response, "TYPE_NOT_FOUND", suggestions: await Suggestions(type, cancellationToken));
        IReadOnlyDictionary<string, long>? enumValues = null;
        bool? isFlags = null;
        if (response.Definition.IsEnum)
        {
            var enumResponse = await Wait(_link.GetEnumDefinition(response.Definition.FullTypeName), "enum.get", cancellationToken);
            EnsureSuccess(enumResponse, "ENUM_DESCRIBE_FAILED");
            enumValues = enumResponse.Definition.Values;
            isFlags = enumResponse.Definition.IsFlags;
        }
        return ModelMapper.MapType(response.Definition, enumValues, isFlags);
    }

    public async Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, JsonElement>? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var converted = new Dictionary<string, Link.Data>(StringComparer.Ordinal);
        foreach (var argument in arguments ?? new Dictionary<string, JsonElement>())
            converted[argument.Key] = ConvertMethodArgument(argument.Value);
        var response = await Wait(_link.CallMethod(new Link.CallSyncMethod
        {
            TargetID = componentId,
            MethodName = method,
            Arguments = converted
        }), "method.call", cancellationToken);
        return new SyncMethodCallResult(response.Success,
            response.Result is null ? null : JsonSerializer.SerializeToNode(response.Result, response.Result.GetType()),
            response.Success ? null : response.ErrorInfo);
    }

    public async Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        Link.AssetData response = asset.Kind.ToLowerInvariant() switch
        {
            "texture" or "texture2d" => await Wait(_link.ImportTexture(new Link.ImportTexture2DFile { FilePath = resolvedSource }), "asset.texture.import", cancellationToken),
            "audio" or "audioclip" => await Wait(_link.ImportAudioClip(new Link.ImportAudioClipFile { FilePath = resolvedSource }), "asset.audio.import", cancellationToken),
            "mesh" => await ImportMeshJson(resolvedSource, cancellationToken),
            _ => throw new RLoopException("ASSET_KIND_UNSUPPORTED", $"Asset kind '{asset.Kind}' is not importable. Use a resdb URI for material and other runtime assets.", ExitCodes.ValidationFailed)
        };
        EnsureSuccess(response, "ASSET_IMPORT_FAILED", new Dictionary<string, object?> { ["kind"] = asset.Kind, ["source"] = resolvedSource });
        return response.AssetURL?.ToString() ?? throw new RLoopException("ASSET_URL_MISSING", "Asset import succeeded without an AssetURL.", ExitCodes.OperationFailed);
    }

    private async Task<Link.AssetData> ImportMeshJson(string path, CancellationToken cancellationToken)
    {
        Link.ImportMeshJSON request;
        try { request = JsonSerializer.Deserialize<Link.ImportMeshJSON>(await File.ReadAllTextAsync(path, cancellationToken)) ?? throw new JsonException("Mesh JSON was empty."); }
        catch (JsonException ex) { throw new RLoopException("MESH_JSON_INVALID", $"Mesh asset '{path}' is not a ResoniteLink ImportMeshJSON document: {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }
        return await Wait(_link.ImportMesh(request), "asset.mesh.import", cancellationToken);
    }

    private static Link.Data ConvertMethodArgument(JsonElement value)
    {
        var suffix = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number when value.TryGetInt32(out _) => "int",
            JsonValueKind.Number => "float",
            _ => throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"Method argument kind '{value.ValueKind}' is not supported.", ExitCodes.ValidationFailed)
        };
        var type = typeof(Link.Data).Assembly.GetType("ResoniteLink.Data_" + suffix)
                   ?? throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"ResoniteLink has no Data_{suffix} wrapper.", ExitCodes.ValidationFailed);
        var data = (Link.Data)Activator.CreateInstance(type)!;
        var property = type.GetProperty("Value") ?? type.GetProperty("BoxedValue")
            ?? throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"ResoniteLink Data_{suffix} has no writable value.", ExitCodes.ValidationFailed);
        object converted = suffix switch
        {
            "string" => value.GetString() ?? string.Empty,
            "bool" => value.GetBoolean(),
            "int" => value.GetInt32(),
            _ => value.GetSingle()
        };
        property.SetValue(data, converted);
        return data;
    }

    private async Task<string> ResolveComponentTypeAsync(string query, CancellationToken cancellationToken)
    {
        var types = await GetAllComponentTypeNames(cancellationToken);
        var exact = types.FirstOrDefault(x => x.Equals(query, StringComparison.Ordinal) || StripAssembly(x).Equals(query, StringComparison.Ordinal));
        if (exact is not null) return exact;
        var matches = types.Where(x => StripAssembly(x).EndsWith('.' + query, StringComparison.Ordinal) ||
                                       StripAssembly(x).Equals(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        if (matches.Length == 1) return matches[0];
        throw new RLoopException("COMPONENT_TYPE_NOT_FOUND",
            matches.Length == 0 ? $"Component type '{query}' was not found." : $"Component type '{query}' is ambiguous.",
            ExitCodes.NotFound, new Dictionary<string, object?> { ["query"] = query }, matches.Take(10).ToArray());
    }

    private async Task<IReadOnlyList<string>> GetAllComponentTypeNames(CancellationToken cancellationToken)
    {
        if (_allComponentTypes is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return _allComponentTypes;
        }
        var response = await Wait(_link.GetAllComponentTypes(), "component-types.get-all", cancellationToken);
        EnsureSuccess(response, "TYPE_SEARCH_FAILED");
        if (response.ComponentTypes is { Count: > 0 }) return _allComponentTypes = response.ComponentTypes;

        var results = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await CollectCategory(string.Empty, results, visited, cancellationToken);
        return _allComponentTypes = results.ToArray();
    }

    private async Task CollectCategory(string category, HashSet<string> results, HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(category)) return;
        var response = await Wait(_link.GetComponentTypes(category), "component-types.get-category", cancellationToken);
        EnsureSuccess(response, "TYPE_SEARCH_FAILED", new Dictionary<string, object?> { ["category"] = category });
        foreach (var type in response.ComponentTypes ?? []) results.Add(type);
        foreach (var child in response.SubCategories ?? [])
        {
            var childPath = child.Contains('/') || string.IsNullOrEmpty(category) ? child : category + "/" + child;
            await CollectCategory(childPath, results, visited, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<string>> Suggestions(string query, CancellationToken cancellationToken) =>
        await SearchComponentTypesAsync(query.Split('.').Last(), 10, cancellationToken);

    private async Task<Link.ComponentDefinition> GetComponentDefinitionCachedAsync(string type,
        CancellationToken cancellationToken)
    {
        if (_componentDefinitions.TryGetValue(type, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        var response = await Wait(_link.GetComponentDefinition(type, true), "component-definition.get", cancellationToken);
        if (!response.Success)
        {
            var resolved = await ResolveComponentTypeAsync(type, cancellationToken);
            if (_componentDefinitions.TryGetValue(resolved, out cached))
            {
                _componentDefinitions[type] = cached;
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }
            response = await Wait(_link.GetComponentDefinition(resolved, true), "component-definition.get", cancellationToken);
        }
        if (!response.Success)
            EnsureSuccess(response, "COMPONENT_TYPE_NOT_FOUND", suggestions: await Suggestions(type, cancellationToken));
        var definition = response.Definition;
        _componentDefinitions[type] = definition;
        _componentDefinitions[definition.Type.FullTypeName] = definition;
        return definition;
    }

    private static RLoopException UnknownMember(string type, string member, IEnumerable<string> members)
    {
        var suggestions = members.Where(x => x.Contains(member, StringComparison.OrdinalIgnoreCase)).Take(10).ToArray();
        return new RLoopException("COMPONENT_MEMBER_NOT_FOUND", $"Member '{member}' does not exist on '{type}'.", ExitCodes.NotFound,
            new Dictionary<string, object?> { ["componentType"] = type, ["member"] = member }, suggestions);
    }

    private static int SearchScore(string type, string query)
    {
        var bare = StripAssembly(type);
        if (bare.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (bare.EndsWith('.' + query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (bare.Split('.').Last().StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string StripAssembly(string type)
    {
        var end = type.IndexOf(']');
        return end >= 0 ? type[(end + 1)..] : type;
    }

    private static Link.float3 ToLink(Vector3Value value) => new() { x = value.X, y = value.Y, z = value.Z };
    private static Link.floatQ ToLink(QuaternionValue value) => new() { x = value.X, y = value.Y, z = value.Z, w = value.W };

    private async Task<T> Wait<T>(Task<T> task, string operation, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            return await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RLoopException("REQUEST_TIMEOUT", $"ResoniteLink request '{operation}' timed out after {_requestTimeout.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["timeoutSeconds"] = _requestTimeout.TotalSeconds
                }, ["Retry after checking Resonite responsiveness; apply checkpoints make retry safe."], ex);
        }
        finally
        {
            stopwatch.Stop();
            RecordMetric(operation, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void RecordMetric(string operation, double elapsedMs)
    {
        lock (_metrics)
        {
            if (!_metrics.TryGetValue(operation, out var metric)) _metrics[operation] = metric = new MutableMetric();
            metric.Requests++;
            metric.ElapsedMs += elapsedMs;
        }
    }

    public void ResetMetrics()
    {
        lock (_metrics) _metrics.Clear();
        Interlocked.Exchange(ref _cacheHits, 0);
    }

    public ClientMetrics SnapshotMetrics()
    {
        lock (_metrics)
        {
            var operations = _metrics.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ClientOperationMetric(x.Key, x.Value.Requests, x.Value.ElapsedMs)).ToArray();
            return new ClientMetrics(operations.Sum(x => x.Requests), Volatile.Read(ref _cacheHits),
                operations.Sum(x => x.ElapsedMs), operations);
        }
    }

    private sealed class MutableMetric
    {
        public int Requests { get; set; }
        public double ElapsedMs { get; set; }
    }

    private void EnsureConnected()
    {
        if (!_link.IsConnected) throw new RLoopException("NOT_CONNECTED", "The ResoniteLink client is not connected.", ExitCodes.ConnectionFailed);
    }

    private static void EnsureSuccess(Link.Response response, string code,
        IReadOnlyDictionary<string, object?>? context = null, IReadOnlyList<string>? suggestions = null)
    {
        if (response.Success) return;
        throw new RLoopException(code, string.IsNullOrWhiteSpace(response.ErrorInfo) ? "ResoniteLink operation failed." : response.ErrorInfo,
            code.Contains("NOT_FOUND", StringComparison.Ordinal) ? ExitCodes.NotFound : ExitCodes.OperationFailed, context, suggestions);
    }

    public ValueTask DisposeAsync()
    {
        // ResoniteLink 0.13.1 dereferences its socket when Dispose is called
        // before Connect created one. Keep that Beta quirk inside this adapter.
        try { _link.Dispose(); }
        catch (NullReferenceException) when (!_link.IsConnected) { }
        return ValueTask.CompletedTask;
    }
}

internal static class ModelMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static SlotInfo MapSlot(Link.Slot slot) => new(
        slot.ID ?? string.Empty,
        slot.Name?.Value ?? string.Empty,
        slot.Parent?.TargetID,
        slot.Position is null ? null : new Vector3Value(slot.Position.Value.x, slot.Position.Value.y, slot.Position.Value.z),
        slot.Rotation is null ? null : new QuaternionValue(slot.Rotation.Value.x, slot.Rotation.Value.y, slot.Rotation.Value.z, slot.Rotation.Value.w),
        slot.Scale is null ? null : new Vector3Value(slot.Scale.Value.x, slot.Scale.Value.y, slot.Scale.Value.z),
        slot.IsActive?.Value,
        slot.IsPersistent?.Value,
        slot.Tag?.Value,
        slot.IsReferenceOnly,
        (slot.Components ?? []).Select(x => new ComponentSummary(
            x.ID ?? string.Empty,
            x.ComponentType ?? string.Empty,
            x.Members is null ? null : x.Members.ToDictionary(member => member.Key, member => MapMember(member.Value), StringComparer.Ordinal))).ToArray(),
        (slot.Children ?? []).Select(MapSlot).ToArray());

    public static ComponentInfo MapComponent(Link.Component component) => new(
        component.ID ?? string.Empty,
        component.ComponentType ?? string.Empty,
        (component.Members ?? []).ToDictionary(x => x.Key, x => MapMember(x.Value), StringComparer.Ordinal));

    public static MemberValue MapMember(Link.Member member) => member switch
    {
        Link.Field_Enum enumField => new MemberValue("field", enumField.ID, enumField.EnumType,
            JsonValue.Create(enumField.Value)),
        Link.Field_Nullable_Enum enumField => new MemberValue("field", enumField.ID, enumField.EnumType,
            JsonValue.Create(enumField.Value)),
        Link.Field field => new MemberValue("field", field.ID, field.ValueType.FullName,
            JsonSerializer.SerializeToNode(field.BoxedValue, field.BoxedValue?.GetType() ?? typeof(object), JsonOptions)),
        Link.Reference reference => new MemberValue("reference", reference.ID, TargetId: reference.TargetID, TargetType: reference.TargetType),
        Link.SyncDictionary dictionary => new MemberValue("dictionary", dictionary.ID,
            Members: MapDictionary(dictionary)),
        Link.SyncObject syncObject => new MemberValue("syncObject", syncObject.ID,
            Members: (syncObject.Members ?? []).ToDictionary(x => x.Key, x => MapMember(x.Value))),
        Link.SyncList list => new MemberValue("list", list.ID, Elements: (list.Elements ?? []).Select(MapMember).ToArray()),
        Link.EmptyElement empty => new MemberValue("empty", empty.ID),
        _ => new MemberValue(member.GetType().Name, member.ID, Value: JsonSerializer.SerializeToNode(member, member.GetType(), JsonOptions))
    };

    private static IReadOnlyDictionary<string, MemberValue> MapDictionary(Link.SyncDictionary dictionary)
    {
        var elements = dictionary.GetType().GetProperty("Elements")?.GetValue(dictionary) as System.Collections.IDictionary;
        var result = new Dictionary<string, MemberValue>(StringComparer.Ordinal);
        if (elements is null) return result;
        foreach (System.Collections.DictionaryEntry entry in elements)
            if (entry.Value is Link.Member member) result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = MapMember(member);
        return result;
    }

    public static MemberDefinitionInfo MapMemberDefinition(string name, Link.MemberDefinition definition) => definition switch
    {
        Link.FieldDefinition field => new MemberDefinitionInfo(name, "field", Render(field.Type), Render(field.ValueType), null),
        Link.ReferenceDefinition reference => new MemberDefinitionInfo(name, "reference", Render(reference.Type), null, Render(reference.TargetType)),
        Link.ListDefinition list => new MemberDefinitionInfo(name, "list", Render(list.Type), null, null),
        Link.ArrayDefinition array => new MemberDefinitionInfo(name, "array", Render(array.Type), null, null),
        Link.DictionaryDefinition dictionary => new MemberDefinitionInfo(name, "dictionary", Render(dictionary.Type), null, null),
        _ => new MemberDefinitionInfo(name, definition.GetType().Name, Render(definition.Type), null, null)
    };

    public static SyncMethodInfo MapMethodDefinition(Link.SyncMethodDefinition definition) => new(
        definition.Name,
        (definition.Parameters ?? []).ToDictionary(x => x.Key, x => Render(x.Value), StringComparer.Ordinal),
        Render(definition.ReturnType), definition.IsStatic, definition.IsAsync);

    public static TypeInfo MapType(Link.TypeDefinition type, IReadOnlyDictionary<string, long>? enumValues, bool? isFlags) => new(
        type.FullTypeName, type.AssemblyName, type.Namespace, type.Name, Render(type.BaseType), type.IsAbstract,
        type.IsInterface, type.IsGenericType, type.IsEnum, type.IsComponent, type.IsSyncObject, type.IsWorldElement,
        (type.GenericParameters ?? []).Select(x => x.Name).ToArray(),
        (type.Interfaces ?? []).Select(Render).Where(x => x is not null).Cast<string>().ToArray(), enumValues, isFlags);

    public static string? Render(Link.TypeReference? reference)
    {
        if (reference is null) return null;
        var args = reference.GenericArguments;
        return args is null || args.Count == 0 ? reference.Type : $"{reference.Type}<{string.Join(',', args.Select(Render))}>";
    }
}

public static class ValueCodec
{
    public static async Task<Link.Member> ParseAsync(Link.LinkInterface link, Link.MemberDefinition definition, string raw,
        CancellationToken cancellationToken = default, TimeSpan? requestTimeout = null,
        Action<string, double>? requestCompleted = null)
    {
        if (definition is Link.ReferenceDefinition) return new Link.Reference { TargetID = raw.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : raw };
        if (definition is Link.ListDefinition list) return await ParseListAsync(link, list, raw, cancellationToken, requestTimeout, requestCompleted);
        if (definition is Link.DictionaryDefinition dictionary) return await ParseDictionaryAsync(link, dictionary, raw, cancellationToken, requestTimeout, requestCompleted);
        if (definition is Link.SyncObjectMemberDefinition syncObject) return await ParseSyncObjectAsync(link, syncObject, raw, cancellationToken, requestTimeout, requestCompleted);
        if (definition is not Link.FieldDefinition field)
            throw new RLoopException("MEMBER_TYPE_UNSUPPORTED", $"Setting {definition.GetType().Name} members is not supported in v0.1.", ExitCodes.ValidationFailed);

        var type = ModelMapper.Render(field.ValueType) ?? string.Empty;
        var simple = SimpleType(type);
        try
        {
            return simple switch
            {
                "bool" or "boolean" => new Link.Field_bool { Value = bool.Parse(raw) },
                "byte" => new Link.Field_byte { Value = byte.Parse(raw, CultureInfo.InvariantCulture) },
                "short" or "int16" => new Link.Field_short { Value = short.Parse(raw, CultureInfo.InvariantCulture) },
                "ushort" or "uint16" => new Link.Field_ushort { Value = ushort.Parse(raw, CultureInfo.InvariantCulture) },
                "int" or "int32" => new Link.Field_int { Value = int.Parse(raw, CultureInfo.InvariantCulture) },
                "uint" or "uint32" => new Link.Field_uint { Value = uint.Parse(raw, CultureInfo.InvariantCulture) },
                "long" or "int64" => new Link.Field_long { Value = long.Parse(raw, CultureInfo.InvariantCulture) },
                "ulong" or "uint64" => new Link.Field_ulong { Value = ulong.Parse(raw, CultureInfo.InvariantCulture) },
                "float" or "single" => new Link.Field_float { Value = float.Parse(raw, CultureInfo.InvariantCulture) },
                "double" => new Link.Field_double { Value = double.Parse(raw, CultureInfo.InvariantCulture) },
                "string" => new Link.Field_string { Value = raw },
                "uri" => new Link.Field_Uri { Value = new Uri(raw, UriKind.RelativeOrAbsolute) },
                "type" => new Link.Field_Type { Type = raw },
                "float2" => Float2(raw),
                "float3" => Float3(raw),
                "float4" => Float4(raw),
                "floatq" or "quaternion" => FloatQ(raw),
                "color" => Color(raw),
                "colorx" => ColorX(raw),
                _ => await ParseEnumOrReflection(link, type, raw, cancellationToken, requestTimeout, requestCompleted)
            };
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is FormatException or OverflowException or UriFormatException)
        {
            throw new RLoopException("VALUE_CONVERSION_FAILED", $"Cannot convert '{raw}' to '{type}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["value"] = raw, ["targetType"] = type }, innerException: ex);
        }
    }

    private static async Task<Link.SyncDictionary> ParseDictionaryAsync(Link.LinkInterface link, Link.DictionaryDefinition definition,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted)
    {
        if (definition.ElementDefinition is null)
            throw new RLoopException("DICTIONARY_ELEMENT_TYPE_MISSING", "The runtime dictionary definition did not include a value type.", ExitCodes.ValidationFailed);
        JsonObject source;
        try { source = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Expected an object."); }
        catch (JsonException ex) { throw new RLoopException("DICTIONARY_VALUE_INVALID", "Dictionary values must use a JSON object.", ExitCodes.ValidationFailed, innerException: ex); }
        var keyType = ModelMapper.Render(definition.KeyType) ?? "string";
        var simpleKey = SimpleType(keyType);
        var concrete = typeof(Link.SyncDictionary).Assembly.GetTypes().FirstOrDefault(type =>
            !type.IsAbstract && typeof(Link.SyncDictionary).IsAssignableFrom(type) &&
            type.Name.Equals("SyncDictionary_" + simpleKey, StringComparison.OrdinalIgnoreCase));
        if (concrete is null)
            throw new RLoopException("DICTIONARY_KEY_UNSUPPORTED", $"Dictionary key type '{keyType}' is not supported by this ResoniteLink build.", ExitCodes.ValidationFailed);
        var result = (Link.SyncDictionary)Activator.CreateInstance(concrete)!;
        var property = concrete.GetProperty("Elements")!;
        var elements = (System.Collections.IDictionary)Activator.CreateInstance(property.PropertyType)!;
        var dictionaryKeyType = property.PropertyType.GetGenericArguments()[0];
        foreach (var pair in source)
        {
            var key = Convert.ChangeType(pair.Key, dictionaryKeyType, CultureInfo.InvariantCulture);
            var valueRaw = pair.Value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString() ?? "null";
            elements.Add(key!, await ParseAsync(link, definition.ElementDefinition, valueRaw, cancellationToken, requestTimeout, requestCompleted));
        }
        property.SetValue(result, elements);
        return result;
    }

    private static async Task<Link.SyncObject> ParseSyncObjectAsync(Link.LinkInterface link, Link.SyncObjectMemberDefinition member,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted)
    {
        JsonObject source;
        try { source = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Expected an object."); }
        catch (JsonException ex) { throw new RLoopException("SYNC_OBJECT_VALUE_INVALID", "SyncObject values must use a JSON object.", ExitCodes.ValidationFailed, innerException: ex); }
        var type = ModelMapper.Render(member.Type) ?? throw new RLoopException("SYNC_OBJECT_TYPE_MISSING", "SyncObject member has no runtime type.", ExitCodes.ValidationFailed);
        var response = await WaitValueRequest(link.GetSyncObjectDefinition(new Link.GetSyncObjectDefinition { SyncObjectType = type, Flattened = true }), "sync-object-definition.get", requestTimeout,
            cancellationToken, requestCompleted);
        if (!response.Success) throw new RLoopException("SYNC_OBJECT_DESCRIBE_FAILED", response.ErrorInfo, ExitCodes.OperationFailed);
        var definition = response.Definition;
        var members = new Dictionary<string, Link.Member>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (!definition.Members.TryGetValue(pair.Key, out var memberDefinition))
                throw new RLoopException("SYNC_OBJECT_MEMBER_NOT_FOUND", $"SyncObject member '{pair.Key}' was not found.", ExitCodes.ValidationFailed,
                    suggestions: definition.Members.Keys.Take(30).ToArray());
            var valueRaw = pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString() ?? "null";
            members[pair.Key] = await ParseAsync(link, memberDefinition, valueRaw, cancellationToken, requestTimeout, requestCompleted);
        }
        return new Link.SyncObject { Members = members };
    }

    private static async Task<Link.SyncList> ParseListAsync(Link.LinkInterface link, Link.ListDefinition definition,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted)
    {
        if (definition.ElementDefinition is null)
            throw new RLoopException("LIST_ELEMENT_TYPE_MISSING", "The runtime list definition did not include an element type.", ExitCodes.ValidationFailed);

        IReadOnlyList<string> values;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected an array.");
                values = document.RootElement.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText()).ToArray();
            }
            catch (JsonException ex)
            {
                throw new RLoopException("LIST_VALUE_INVALID", $"Cannot parse '{raw}' as a JSON array.", ExitCodes.ValidationFailed,
                    suggestions: ["Pass a JSON array such as [\"Reso_1\",\"Reso_2\"]."], innerException: ex);
            }
        }
        else
        {
            values = string.IsNullOrWhiteSpace(trimmed) ? [] : [trimmed];
        }

        var elements = new List<Link.Member>(values.Count);
        foreach (var value in values)
            elements.Add(await ParseAsync(link, definition.ElementDefinition, value, cancellationToken, requestTimeout, requestCompleted));
        return new Link.SyncList { Elements = elements };
    }

    private static async Task<Link.Member> ParseEnumOrReflection(Link.LinkInterface link, string type, string raw,
        CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted)
    {
        var reflected = TryParseReflectedField(type, raw);
        if (reflected is not null) return reflected;
        var typeResponse = await WaitValueRequest(link.GetTypeDefinition(type), "type.get", requestTimeout, cancellationToken, requestCompleted);
        if (typeResponse.Success && typeResponse.Definition.IsEnum)
        {
            var enumResponse = await WaitValueRequest(link.GetEnumDefinition(type), "enum.get", requestTimeout, cancellationToken, requestCompleted);
            if (!enumResponse.Success) throw new RLoopException("ENUM_DESCRIBE_FAILED", enumResponse.ErrorInfo, ExitCodes.OperationFailed);
            var values = enumResponse.Definition.Values;
            return new Link.Field_Enum { EnumType = type, Value = ValidateEnumValue(type, values, enumResponse.Definition.IsFlags, raw) };
        }
        throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Field type '{type}' is not supported by the v0.1 converter.", ExitCodes.ValidationFailed,
            suggestions: ["Use rloop type describe to confirm the runtime type, then open an issue with this type."]);
    }

    internal static string ValidateEnumValue(string type, IReadOnlyDictionary<string, long> values, bool isFlags, string raw)
    {
        var requested = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var unknown = requested.Where(x => !values.ContainsKey(x) && !long.TryParse(x, out _)).ToArray();
        if (unknown.Length > 0)
            throw new RLoopException("ENUM_VALUE_INVALID", $"'{string.Join(',', unknown)}' is not valid for '{type}'.", ExitCodes.ValidationFailed,
                suggestions: values.Keys.Take(30).ToArray());
        if (!isFlags && requested.Length > 1)
            throw new RLoopException("ENUM_FLAGS_INVALID", $"Enum '{type}' is not marked with Flags and accepts one value.", ExitCodes.ValidationFailed,
                suggestions: values.Keys.Take(30).ToArray());
        return string.Join(',', requested);
    }

    private static Link.Member? TryParseReflectedField(string type, string raw)
    {
        var nullable = type.Contains("Nullable", StringComparison.OrdinalIgnoreCase) || type.EndsWith("?", StringComparison.Ordinal);
        var underlying = type;
        var open = type.IndexOf('<');
        if (open >= 0 && type.EndsWith('>')) underlying = type[(open + 1)..^1];
        underlying = underlying.TrimEnd('?');
        var simple = SimpleType(underlying);
        var expectedName = "Field_" + (nullable ? "Nullable_" : string.Empty) + simple;
        var concrete = typeof(Link.Field).Assembly.GetTypes().FirstOrDefault(candidate =>
            !candidate.IsAbstract && typeof(Link.Field).IsAssignableFrom(candidate) &&
            candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
        if (concrete is null) return null;
        var field = (Link.Field)Activator.CreateInstance(concrete)!;
        var property = concrete.GetProperty("Value") ?? concrete.GetProperty("BoxedValue");
        if (property is null || !property.CanWrite) return null;
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            property.SetValue(field, null);
            return field;
        }
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? value;
        if (targetType == typeof(string)) value = raw;
        else if (targetType == typeof(Uri)) value = new Uri(raw, UriKind.RelativeOrAbsolute);
        else if (MemberValueSyntax.IsStructuredTuple(underlying, out _))
            value = ParseReflectedTuple(targetType, underlying, raw);
        else
        {
            var json = raw;
            if (targetType.IsEnum) value = Enum.Parse(targetType, raw, true);
            else value = JsonSerializer.Deserialize(json, targetType, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
        }
        property.SetValue(field, value);
        return field;
    }

    private static object ParseReflectedTuple(Type targetType, string tupleType, string raw)
    {
        var tuple = MemberValueSyntax.ParseTuple(tupleType, raw);
        var result = Activator.CreateInstance(targetType) ??
            throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Tuple type '{tupleType}' cannot be constructed.", ExitCodes.ValidationFailed);
        var names = SimpleType(tupleType) is "color" or "colorx"
            ? new[] { "r", "g", "b", "a" }
            : new[] { "x", "y", "z", "w" };
        for (var index = 0; index < tuple.Count; index++)
        {
            var member = targetType.GetField(names[index]) as System.Reflection.MemberInfo ?? targetType.GetProperty(names[index]);
            var memberType = member switch
            {
                System.Reflection.FieldInfo field => field.FieldType,
                System.Reflection.PropertyInfo property => property.PropertyType,
                _ => throw new RLoopException("VALUE_TYPE_UNSUPPORTED",
                    $"Tuple type '{tupleType}' does not expose '{names[index]}'.", ExitCodes.ValidationFailed)
            };
            var converted = JsonSerializer.Deserialize(tuple[index]!.ToJsonString(), memberType);
            if (member is System.Reflection.FieldInfo targetField) targetField.SetValue(result, converted);
            else ((System.Reflection.PropertyInfo)member).SetValue(result, converted);
        }
        return result;
    }

    private static async Task<T> WaitValueRequest<T>(Task<T> task, string operation, TimeSpan? timeout,
        CancellationToken cancellationToken, Action<string, double>? requestCompleted)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return timeout is { } value
                ? await task.WaitAsync(value, cancellationToken).ConfigureAwait(false)
                : await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new RLoopException("REQUEST_TIMEOUT", $"ResoniteLink request '{operation}' timed out after {timeout!.Value.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?> { ["operation"] = operation, ["timeoutSeconds"] = timeout.Value.TotalSeconds },
                ["Retry after checking Resonite responsiveness; apply checkpoints make retry safe."], ex);
        }
        finally
        {
            stopwatch.Stop();
            requestCompleted?.Invoke(operation, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static Link.Member Float2(string raw) { var v = Parts("float2", raw); return new Link.Field_float2 { Value = new Link.float2 { x = v[0], y = v[1] } }; }
    private static Link.Member Float3(string raw) { var v = Parts("float3", raw); return new Link.Field_float3 { Value = new Link.float3 { x = v[0], y = v[1], z = v[2] } }; }
    private static Link.Member Float4(string raw) { var v = Parts("float4", raw); return new Link.Field_float4 { Value = new Link.float4 { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member FloatQ(string raw) { var v = Parts("floatQ", raw); return new Link.Field_floatQ { Value = new Link.floatQ { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member Color(string raw) { var v = Parts("color", raw); return new Link.Field_color { Value = new Link.color { r = v[0], g = v[1], b = v[2], a = v[3] } }; }
    private static Link.Member ColorX(string raw) { var v = Parts("colorX", raw); return new Link.Field_colorX { Value = new Link.colorX { r = v[0], g = v[1], b = v[2], a = v[3] } }; }

    private static float[] Parts(string type, string raw)
    {
        var tuple = MemberValueSyntax.ParseTuple(type, raw);
        return tuple.Select(value => (float)(value?.GetValue<double>() ?? throw new FormatException())).ToArray();
    }

    private static string SimpleType(string type)
    {
        var end = type.IndexOf(']');
        if (end >= 0) type = type[(end + 1)..];
        return type.Split('.').Last().TrimEnd('?').ToLowerInvariant();
    }
}
