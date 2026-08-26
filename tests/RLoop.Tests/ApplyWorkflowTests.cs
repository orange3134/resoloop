using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ApplyWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rloop-workflow-" + Guid.NewGuid().ToString("N"));

    public ApplyWorkflowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OfflineValidationAcceptsForwardReferencesAndRejectsUnknownKeys()
    {
        var valid = Document("forward", """
            [
              { "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:target" } },
              { "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }
            ]
            """);
        var result = await ApplyDocumentValidator.ValidateAsync(valid);
        Assert.True(result.Valid);
        Assert.Equal(1, result.References);

        var invalid = Document("invalid", """
            [{ "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:missing" } }]
            """);
        var invalidResult = await ApplyDocumentValidator.ValidateAsync(invalid);
        Assert.False(invalidResult.Valid);
        Assert.Contains(invalidResult.Issues, x => x.Code == "APPLY_REFERENCE_NOT_FOUND");
    }

    [Fact]
    public async Task ApplyResolvesForwardReferenceAndSecondRunHasNoWrites()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("forward", """
            [
              { "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:target" } },
              { "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }
            ]
            """);
        var state = Path.Combine(_root, "forward.state.json");

        var first = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(1, first.SlotsCreated);
        Assert.Equal(2, first.ComponentsAdded);
        Assert.True(File.Exists(state));
        Assert.Equal(1, client.BatchUpdates);
        Assert.NotNull(first.Profile);

        client.ResetWriteCounts();
        var second = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(0, second.SlotsCreated);
        Assert.Equal(0, second.SlotsUpdated);
        Assert.Equal(0, second.ComponentsAdded);
        Assert.Equal(0, second.ComponentsUpdated);
        Assert.Equal(1, second.SlotsUnchanged);
        Assert.Equal(2, second.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ExistingRootRequiresExplicitAdoption()
    {
        var client = new FakeResoniteClient();
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        client.ResetWriteCounts();
        var service = new WorldService(client);
        var document = Document("adopt", "[]");
        var state = Path.Combine(_root, "adopt.state.json");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, new ApplyOptions(state)));
        Assert.Equal("APPLY_OWNERSHIP_UNVERIFIED", error.Code);
        Assert.Equal(0, client.Writes);

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state, Adopt: true));
        Assert.Single(plan.Operations);
        Assert.Equal("update", plan.Operations[0].Action);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CheckpointAllowsResumeWithoutDuplicateSlot()
    {
        var client = new FakeResoniteClient { CancelAfterWrites = 1 };
        using var cancellation = new CancellationTokenSource();
        client.Cancellation = cancellation;
        var service = new WorldService(client);
        var document = Document("resume", """
            [{ "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }]
            """);
        var state = Path.Combine(_root, "resume.state.json");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state), cancellation.Token));
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.True((int)error.Context["remaining"]! > 0);
        Assert.True(File.Exists(state));

        client.CancelAfterWrites = null;
        client.Cancellation = null;
        var resumed = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, resumed.SlotsCreated);
        Assert.Equal(1, resumed.ComponentsAdded);
        Assert.Single(client.Root.Children);
    }

    [Fact]
    public async Task PendingCheckpointRecoversWhenCreateResponseIsLost()
    {
        var client = new FakeResoniteClient { LoseNextSlotCreateResponse = true };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "lost-response.state.json");
        var document = Document("lost-response", "[]");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.Single(client.Root.Children);

        var resumed = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, resumed.SlotsCreated);
        Assert.Single(client.Root.Children);
    }

    [Fact]
    public async Task StrictValidationChecksRuntimeMembersBeforeMutation()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("strict", """
            [{ "key": "target", "type": "Test.Target", "fields": { "Missing": true } }]
            """);

        var validation = await service.ValidateApplyAsync(document, true);
        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, x => x.Code == "COMPONENT_MEMBER_NOT_FOUND");
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document,
            new ApplyOptions(Path.Combine(_root, "strict.state.json"))));
        Assert.Equal("APPLY_VALIDATION_FAILED", error.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task StableKeysResolveDuplicateTypesAfterSessionChange()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("duplicates", """
            [
              { "key": "first", "type": "Test.Target", "fields": { "Enabled": true } },
              { "key": "second", "type": "Test.Target", "fields": { "Enabled": false } }
            ]
            """);
        var state = Path.Combine(_root, "duplicates.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));

        client.SessionId = "session-2";
        client.ResetWriteCounts();
        var reapplied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(0, reapplied.ComponentsAdded);
        Assert.Equal(2, reapplied.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ExplicitSlotKeySupportsRenameAfterSessionChange()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var state = Path.Combine(_root, "rename.state.json");
        await service.ApplyAsync(Document("rename", "[]"), new ApplyOptions(state));

        client.SessionId = "session-2";
        client.ResetWriteCounts();
        var renamed = await service.ApplyAsync(Document("rename", "[]", "ManagedRenamed"), new ApplyOptions(state));

        Assert.Equal(1, renamed.SlotsUpdated);
        Assert.Equal("ManagedRenamed", Assert.Single(client.Root.Children).Name);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task HouseFixtureHasBoundedRequestsAndZeroWriteSecondRun()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "house-world.json"));
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "house.state.json");

        var first = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(69, first.SlotsCreated);
        Assert.Equal(148, first.ComponentsAdded);
        Assert.Equal(217, client.Writes);

        client.ResetWriteCounts();
        var second = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(217, second.Profile!.NoOps);
        Assert.InRange(second.Profile.Client.Requests, 1, 12);
        Assert.Equal(0, client.Writes);
    }

    private ApplyDocument Document(string ownership, string components, string name = "Managed")
    {
        var path = Path.Combine(_root, ownership + ".json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "1",
              "ownership": { "key": "{{ownership}}" },
              "slot": { "key": "root", "name": "{{name}}", "parent": "Root", "position": [0, 1, 2] },
              "components": {{components}}
            }
            """);
        return ApplyDocument.Load(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeResoniteClient : IResoniteClient, IResoniteClientDiagnostics
    {
        private int _nextSlot = 1;
        private int _nextComponent = 1;
        private readonly Dictionary<string, FakeSlot> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeComponent> _components = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _knownMembers = new(StringComparer.Ordinal);
        private int _requests;
        public FakeSlot Root { get; }
        public int Writes { get; private set; }
        public int BatchUpdates { get; private set; }
        public int? CancelAfterWrites { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public string SessionId { get; set; } = "session-1";
        public bool LoseNextSlotCreateResponse { get; set; }

        public FakeResoniteClient(ApplyDocument? definitions = null)
        {
            Root = new FakeSlot("Root", "Root", null, null, null, null);
            _slots[Root.Id] = Root;
            _knownMembers["Test.Source"] = ["Target"];
            _knownMembers["Test.Target"] = ["Enabled"];
            if (definitions is not null) RegisterDefinitions(definitions);
        }

        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(new SessionInfo("ws://fake", true, "test", "test", SessionId)));

        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests++;
            if (!_slots.TryGetValue(id, out var slot)) throw new RLoopException("SLOT_NOT_FOUND", id, ExitCodes.NotFound);
            return Task.FromResult(Map(slot, depth, includeComponentData));
        }

        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
        {
            _requests++;
            var component = _components[id];
            return Task.FromResult(new ComponentInfo(component.Id, component.Type, component.Members));
        }

        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
        {
            Write();
            var id = "S" + _nextSlot++;
            var slot = new FakeSlot(id, request.Name, request.ParentId, request.Position, request.Rotation, request.Scale);
            _slots[id] = slot;
            _slots[request.ParentId].Children.Add(slot);
            if (LoseNextSlotCreateResponse)
            {
                LoseNextSlotCreateResponse = false;
                throw new OperationCanceledException("Simulated lost create response.");
            }
            return Task.FromResult(id);
        }

        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Write();
            var slot = _slots[request.Id];
            if (request.Name is not null) slot.Name = request.Name;
            if (request.Position is not null) slot.Position = request.Position;
            if (request.Rotation is not null) slot.Rotation = request.Rotation;
            if (request.Scale is not null) slot.Scale = request.Scale;
            return Task.CompletedTask;
        }

        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
        {
            Write();
            var id = "C" + _nextComponent++;
            var component = new FakeComponent(id, componentType);
            foreach (var member in _knownMembers.GetValueOrDefault(componentType) ?? [])
                component.Members[member] = new MemberValue("field", id + ":" + member, "bool", JsonValue.Create(false));
            SetFields(component, fields);
            _components[id] = component;
            _slots[slotId].Components.Add(component);
            return Task.FromResult(new ComponentCreateResult(id, componentType));
        }

        public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) =>
            SetComponentMembersAsync(componentId, _components[componentId].Type, new Dictionary<string, string> { [member] = rawValue }, cancellationToken);

        public Task SetComponentMembersAsync(string componentId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
        {
            Write();
            BatchUpdates++;
            SetFields(_components[componentId], fields);
            return Task.CompletedTask;
        }

        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Read(_knownMembers.Keys.Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray()));

        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
        {
            _requests++;
            if (!_knownMembers.TryGetValue(type, out var known))
                throw new RLoopException("COMPONENT_TYPE_NOT_FOUND", type, ExitCodes.NotFound);
            IReadOnlyList<MemberDefinitionInfo> members = known.Select(name =>
                new MemberDefinitionInfo(name, name is "Target" or "Mesh" or "TargetValue" ? "reference" : "field",
                    null, "bool", null)).ToArray();
            return Task.FromResult(new ComponentTypeInfo(type, null, null, false, members));
        }

        public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ResetMetrics() => _requests = 0;
        public ClientMetrics SnapshotMetrics() => new(_requests, 0, 0,
            [new ClientOperationMetric("fake", _requests, 0)]);
        public void ResetWriteCounts() { Writes = 0; BatchUpdates = 0; }

        private void Write()
        {
            Writes++;
            if (CancelAfterWrites == Writes) Cancellation?.Cancel();
        }

        private static void SetFields(FakeComponent component, IReadOnlyDictionary<string, string> fields)
        {
            foreach (var field in fields)
            {
                var id = component.Id + ":" + field.Key;
                if (field.Value.StartsWith("C", StringComparison.Ordinal))
                    component.Members[field.Key] = new MemberValue("reference", id, TargetId: field.Value);
                else
                {
                    JsonNode? value;
                    try { value = JsonNode.Parse(field.Value); }
                    catch (JsonException) { value = JsonValue.Create(field.Value); }
                    component.Members[field.Key] = new MemberValue("field", id, "value", value);
                }
            }
        }

        private T Read<T>(T value)
        {
            _requests++;
            return value;
        }

        private void RegisterDefinitions(ApplyDocument document)
        {
            var keyedTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
            {
                foreach (var component in components ?? [])
                {
                    if (!_knownMembers.TryGetValue(component.Type, out var members))
                        _knownMembers[component.Type] = members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in component.Fields?.Keys ?? []) members.Add(field);
                    if (!string.IsNullOrWhiteSpace(component.Key)) keyedTypes[component.Key] = component.Type;
                }
                foreach (var child in children ?? []) Visit(child.Slot, child.Components, child.Children);
            }
            Visit(document.Slot!, document.Components, document.Children);

            void Scan(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).StartsWith("$member:", StringComparison.Ordinal))
                {
                    var selector = value.GetString()![8..];
                    var separator = selector.LastIndexOf('.');
                    if (separator > 0 && keyedTypes.TryGetValue(selector[..separator], out var type))
                        _knownMembers[type].Add(selector[(separator + 1)..]);
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Scan(item);
            }
            void ScanNodes(IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
            {
                foreach (var component in components ?? []) foreach (var value in component.Fields?.Values ?? []) Scan(value);
                foreach (var child in children ?? []) ScanNodes(child.Components, child.Children);
            }
            ScanNodes(document.Components, document.Children);
        }

        private static SlotInfo Map(FakeSlot slot, int depth, bool members) => new(slot.Id, slot.Name, slot.ParentId,
            slot.Position, slot.Rotation, slot.Scale, true, true, null, false,
            slot.Components.Select(x => new ComponentSummary(x.Id, x.Type, members ? x.Members : null)).ToArray(),
            depth == 0 ? [] : slot.Children.Select(x => Map(x, depth < 0 ? -1 : depth - 1, members)).ToArray());

        public sealed class FakeSlot(string id, string name, string? parentId, Vector3Value? position,
            QuaternionValue? rotation, Vector3Value? scale)
        {
            public string Id { get; } = id;
            public string Name { get; set; } = name;
            public string? ParentId { get; } = parentId;
            public Vector3Value? Position { get; set; } = position;
            public QuaternionValue? Rotation { get; set; } = rotation;
            public Vector3Value? Scale { get; set; } = scale;
            public List<FakeSlot> Children { get; } = [];
            public List<FakeComponent> Components { get; } = [];
        }

        public sealed class FakeComponent(string id, string type)
        {
            public string Id { get; } = id;
            public string Type { get; } = type;
            public Dictionary<string, MemberValue> Members { get; } = new(StringComparer.Ordinal);
        }
    }
}
