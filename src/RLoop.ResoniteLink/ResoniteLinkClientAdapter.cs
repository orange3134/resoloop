using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

public sealed class ResoniteLinkClientAdapter : IResoniteClient
{
    private readonly Link.LinkInterface _link = new();
    private Uri? _uri;

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
        var response = await Wait(_link.GetSessionData(), cancellationToken);
        EnsureSuccess(response, "SESSION_INFO_FAILED");
        return new SessionInfo(_uri!.ToString(), true, response.ResoniteVersion, response.ResoniteLinkVersion, response.UniqueSessionId);
    }

    public async Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetSlotData(new Link.GetSlot { SlotID = id, Depth = depth, IncludeComponentData = includeComponentData }), cancellationToken);
        EnsureSuccess(response, "SLOT_NOT_FOUND", new Dictionary<string, object?> { ["slotId"] = id });
        return ModelMapper.MapSlot(response.Data);
    }

    public async Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = id }), cancellationToken);
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
        var response = await Wait(_link.AddSlot(new Link.AddSlot { Data = slot }), cancellationToken);
        EnsureSuccess(response, "SLOT_CREATE_FAILED", new Dictionary<string, object?> { ["parentId"] = request.ParentId, ["name"] = request.Name });
        return response.EntityId;
    }

    public async Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var slot = new Link.Slot
        {
            ID = request.Id,
            Name = request.Name is null ? null : new Link.Field_string { Value = request.Name },
            Position = request.Position is null ? null : new Link.Field_float3 { Value = ToLink(request.Position) },
            Rotation = request.Rotation is null ? null : new Link.Field_floatQ { Value = ToLink(request.Rotation) },
            Scale = request.Scale is null ? null : new Link.Field_float3 { Value = ToLink(request.Scale) }
        };
        var response = await Wait(_link.UpdateSlot(new Link.UpdateSlot { Data = slot }), cancellationToken);
        EnsureSuccess(response, "SLOT_UPDATE_FAILED", new Dictionary<string, object?> { ["slotId"] = request.Id });
    }

    public async Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveSlot(new Link.RemoveSlot { SlotID = id }), cancellationToken);
        EnsureSuccess(response, "SLOT_DELETE_FAILED", new Dictionary<string, object?> { ["slotId"] = id });
    }

    public async Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var definitionResponse = await Wait(_link.GetComponentDefinition(componentType, true), cancellationToken);
        var resolvedType = componentType;
        if (!definitionResponse.Success)
        {
            resolvedType = await ResolveComponentTypeAsync(componentType, cancellationToken);
            definitionResponse = await Wait(_link.GetComponentDefinition(resolvedType, true), cancellationToken);
        }
        EnsureSuccess(definitionResponse, "COMPONENT_TYPE_NOT_FOUND", suggestions: await Suggestions(componentType, cancellationToken));
        resolvedType = definitionResponse.Definition.Type.FullTypeName;

        var members = new Dictionary<string, Link.Member>(StringComparer.Ordinal);
        foreach (var assignment in fields)
        {
            if (!definitionResponse.Definition.Members.TryGetValue(assignment.Key, out var memberDefinition))
                throw UnknownMember(resolvedType, assignment.Key, definitionResponse.Definition.Members.Keys);
            members[assignment.Key] = await ValueCodec.ParseAsync(_link, memberDefinition, assignment.Value, cancellationToken);
        }

        var response = await Wait(_link.AddComponent(new Link.AddComponent
        {
            ContainerSlotId = slotId,
            Data = new Link.Component { ComponentType = resolvedType, Members = members }
        }), cancellationToken);
        EnsureSuccess(response, "COMPONENT_ADD_FAILED", new Dictionary<string, object?> { ["slotId"] = slotId, ["componentType"] = resolvedType });
        return new ComponentCreateResult(response.EntityId, resolvedType);
    }

    public async Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var componentResponse = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = componentId }), cancellationToken);
        EnsureSuccess(componentResponse, "COMPONENT_NOT_FOUND", new Dictionary<string, object?> { ["componentId"] = componentId });
        var type = componentResponse.Data.ComponentType;
        var definitionResponse = await Wait(_link.GetComponentDefinition(type, true), cancellationToken);
        EnsureSuccess(definitionResponse, "COMPONENT_TYPE_NOT_FOUND");
        if (!definitionResponse.Definition.Members.TryGetValue(member, out var memberDefinition))
            throw UnknownMember(type, member, definitionResponse.Definition.Members.Keys);
        var value = await ValueCodec.ParseAsync(_link, memberDefinition, rawValue, cancellationToken);
        var response = await Wait(_link.UpdateComponent(new Link.UpdateComponent
        {
            Data = new Link.Component { ID = componentId, Members = new Dictionary<string, Link.Member> { [member] = value } }
        }), cancellationToken);
        EnsureSuccess(response, "COMPONENT_UPDATE_FAILED", new Dictionary<string, object?> { ["componentId"] = componentId, ["member"] = member });
    }

    public async Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveComponent(new Link.RemoveComponent { ComponentID = componentId }), cancellationToken);
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
        var response = await Wait(_link.GetComponentDefinition(type, true), cancellationToken);
        if (!response.Success)
        {
            var resolvedType = await ResolveComponentTypeAsync(type, cancellationToken);
            response = await Wait(_link.GetComponentDefinition(resolvedType, true), cancellationToken);
        }
        EnsureSuccess(response, "COMPONENT_TYPE_NOT_FOUND", suggestions: await Suggestions(type, cancellationToken));
        var definition = response.Definition;
        var members = definition.Members.Select(x => ModelMapper.MapMemberDefinition(x.Key, x.Value)).ToArray();
        return new ComponentTypeInfo(definition.Type.FullTypeName, definition.CategoryPath,
            ModelMapper.Render(definition.Type.BaseType), definition.Type.IsGenericType, members);
    }

    public async Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetTypeDefinition(type), cancellationToken);
        if (!response.Success)
        {
            var resolved = await ResolveComponentTypeAsync(type, cancellationToken);
            response = await Wait(_link.GetTypeDefinition(resolved), cancellationToken);
        }
        EnsureSuccess(response, "TYPE_NOT_FOUND", suggestions: await Suggestions(type, cancellationToken));
        IReadOnlyDictionary<string, long>? enumValues = null;
        bool? isFlags = null;
        if (response.Definition.IsEnum)
        {
            var enumResponse = await Wait(_link.GetEnumDefinition(response.Definition.FullTypeName), cancellationToken);
            EnsureSuccess(enumResponse, "ENUM_DESCRIBE_FAILED");
            enumValues = enumResponse.Definition.Values;
            isFlags = enumResponse.Definition.IsFlags;
        }
        return ModelMapper.MapType(response.Definition, enumValues, isFlags);
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
        var response = await Wait(_link.GetAllComponentTypes(), cancellationToken);
        EnsureSuccess(response, "TYPE_SEARCH_FAILED");
        if (response.ComponentTypes is { Count: > 0 }) return response.ComponentTypes;

        var results = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await CollectCategory(string.Empty, results, visited, cancellationToken);
        return results.ToArray();
    }

    private async Task CollectCategory(string category, HashSet<string> results, HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(category)) return;
        var response = await Wait(_link.GetComponentTypes(category), cancellationToken);
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

    private static async Task<T> Wait<T>(Task<T> task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken).ConfigureAwait(false);

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
            JsonSerializer.SerializeToNode(field.BoxedValue, field.BoxedValue?.GetType() ?? typeof(object))),
        Link.Reference reference => new MemberValue("reference", reference.ID, TargetId: reference.TargetID, TargetType: reference.TargetType),
        Link.SyncObject syncObject => new MemberValue("syncObject", syncObject.ID,
            Members: (syncObject.Members ?? []).ToDictionary(x => x.Key, x => MapMember(x.Value))),
        Link.SyncList list => new MemberValue("list", list.ID, Elements: (list.Elements ?? []).Select(MapMember).ToArray()),
        Link.EmptyElement empty => new MemberValue("empty", empty.ID),
        _ => new MemberValue(member.GetType().Name, member.ID, Value: JsonSerializer.SerializeToNode(member, member.GetType()))
    };

    public static MemberDefinitionInfo MapMemberDefinition(string name, Link.MemberDefinition definition) => definition switch
    {
        Link.FieldDefinition field => new MemberDefinitionInfo(name, "field", Render(field.Type), Render(field.ValueType), null),
        Link.ReferenceDefinition reference => new MemberDefinitionInfo(name, "reference", Render(reference.Type), null, Render(reference.TargetType)),
        Link.ListDefinition list => new MemberDefinitionInfo(name, "list", Render(list.Type), null, null),
        Link.ArrayDefinition array => new MemberDefinitionInfo(name, "array", Render(array.Type), null, null),
        Link.DictionaryDefinition dictionary => new MemberDefinitionInfo(name, "dictionary", Render(dictionary.Type), null, null),
        _ => new MemberDefinitionInfo(name, definition.GetType().Name, Render(definition.Type), null, null)
    };

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
        CancellationToken cancellationToken = default)
    {
        if (definition is Link.ReferenceDefinition) return new Link.Reference { TargetID = raw.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : raw };
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
                _ => await ParseEnumOrThrow(link, type, raw, cancellationToken)
            };
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is FormatException or OverflowException or UriFormatException)
        {
            throw new RLoopException("VALUE_CONVERSION_FAILED", $"Cannot convert '{raw}' to '{type}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["value"] = raw, ["targetType"] = type }, innerException: ex);
        }
    }

    private static async Task<Link.Member> ParseEnumOrThrow(Link.LinkInterface link, string type, string raw, CancellationToken cancellationToken)
    {
        var typeResponse = await link.GetTypeDefinition(type).WaitAsync(cancellationToken);
        if (typeResponse.Success && typeResponse.Definition.IsEnum)
        {
            var enumResponse = await link.GetEnumDefinition(type).WaitAsync(cancellationToken);
            if (!enumResponse.Success) throw new RLoopException("ENUM_DESCRIBE_FAILED", enumResponse.ErrorInfo, ExitCodes.OperationFailed);
            var values = enumResponse.Definition.Values;
            var requested = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var unknown = requested.Where(x => !values.ContainsKey(x) && !long.TryParse(x, out _)).ToArray();
            if (unknown.Length > 0)
                throw new RLoopException("ENUM_VALUE_INVALID", $"'{string.Join(',', unknown)}' is not valid for '{type}'.", ExitCodes.ValidationFailed,
                    suggestions: values.Keys.Take(30).ToArray());
            return new Link.Field_Enum { EnumType = type, Value = raw };
        }
        throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Field type '{type}' is not supported by the v0.1 converter.", ExitCodes.ValidationFailed,
            suggestions: ["Use rloop type describe to confirm the runtime type, then open an issue with this type."]);
    }

    private static Link.Member Float2(string raw) { var v = Parts(raw, 2); return new Link.Field_float2 { Value = new Link.float2 { x = v[0], y = v[1] } }; }
    private static Link.Member Float3(string raw) { var v = Parts(raw, 3); return new Link.Field_float3 { Value = new Link.float3 { x = v[0], y = v[1], z = v[2] } }; }
    private static Link.Member Float4(string raw) { var v = Parts(raw, 4); return new Link.Field_float4 { Value = new Link.float4 { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member FloatQ(string raw) { var v = Parts(raw, 4); return new Link.Field_floatQ { Value = new Link.floatQ { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member Color(string raw) { var v = Parts(raw, 4); return new Link.Field_color { Value = new Link.color { r = v[0], g = v[1], b = v[2], a = v[3] } }; }

    private static float[] Parts(string raw, int count)
    {
        var parts = raw.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) throw new FormatException();
        return parts.Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    }

    private static string SimpleType(string type)
    {
        var end = type.IndexOf(']');
        if (end >= 0) type = type[(end + 1)..];
        return type.Split('.').Last().TrimEnd('?').ToLowerInvariant();
    }
}
