using System.Reflection;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class LiveCaptureWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-live-capture-test-" + Guid.NewGuid().ToString("N"));

    public LiveCaptureWorkflowTests() => Directory.CreateDirectory(_root);

    private ApplyDocument Document()
    {
        var path = Path.Combine(_root, "scene.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"capture-test"},"slot":{"key":"root","name":"Scene"},
             "cameras":{"main":{"position":[0,2,-6],"target":[0,1,0],"width":640,"height":360}}}
            """);
        return ApplyDocument.Load(path);
    }

    [Fact]
    public async Task RequiresRuntimeCaptureMethodBeforeCreatingContent()
    {
        var client = DispatchProxy.Create<IResoniteClient, CaptureClient>();
        var fake = (CaptureClient)client;
        fake.HasCapture = false;
        var error = await Assert.ThrowsAsync<RLoopException>(() => new LiveCaptureService(client)
            .CaptureAsync(Document(), "main", Path.Combine(_root, "out.jpg"), _root));
        Assert.Equal("CAPTURE_METHOD_UNAVAILABLE", error.Code);
        Assert.Null(fake.Created);
    }

    [Fact]
    public async Task FailedTriggerCleansOnlyCreatedCamera()
    {
        var client = DispatchProxy.Create<IResoniteClient, CaptureClient>();
        var fake = (CaptureClient)client;
        var error = await Assert.ThrowsAsync<RLoopException>(() => new LiveCaptureService(client)
            .CaptureAsync(Document(), "main", Path.Combine(_root, "out.jpg"), _root));
        Assert.Equal("CAPTURE_TRIGGER_FAILED", error.Code);
        Assert.Equal("capture-slot", Assert.Single(fake.Deleted));
        Assert.StartsWith("ResoLoop_Test_Capture_", fake.Created!.Name);
        Assert.Equal("Root", fake.Created.ParentId);
        Assert.Equal("false", fake.Fields!["SpawnPhotoInWorld"]);
        Assert.Equal("Manual", fake.Fields["PositioningMode"]);
    }

    [Fact]
    public async Task CancellationStillCleansWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        var client = DispatchProxy.Create<IResoniteClient, CaptureClient>();
        var fake = (CaptureClient)client;
        fake.OnCapture = () => { cancellation.Cancel(); return new SyncMethodCallResult(true, null, null); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LiveCaptureService(client)
            .CaptureAsync(Document(), "main", Path.Combine(_root, "out.jpg"), _root, cancellationToken: cancellation.Token));
        Assert.Equal("capture-slot", Assert.Single(fake.Deleted));
    }

    [Fact]
    public async Task RefusesCleanupIfIdentityChangedAndReportsBothFailures()
    {
        var client = DispatchProxy.Create<IResoniteClient, CaptureClient>();
        var fake = (CaptureClient)client;
        fake.RenameOnCleanup = true;
        var error = await Assert.ThrowsAsync<RLoopException>(() => new LiveCaptureService(client)
            .CaptureAsync(Document(), "main", Path.Combine(_root, "out.jpg"), _root));
        Assert.Equal("CAPTURE_CLEANUP_FAILED", error.Code);
        Assert.Equal("trigger rejected", error.Context["captureError"]);
        Assert.Equal("capture-slot", error.Context["slotId"]);
        Assert.Empty(fake.Deleted);
    }

    public class CaptureClient : DispatchProxy
    {
        public bool HasCapture = true;
        public bool RenameOnCleanup;
        public SlotCreateRequest? Created;
        public IReadOnlyDictionary<string, string>? Fields;
        public List<string> Deleted = [];
        public Func<SyncMethodCallResult>? OnCapture;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            if (args.LastOrDefault() is CancellationToken ct) ct.ThrowIfCancellationRequested();
            switch (method!.Name)
            {
                case nameof(IResoniteClient.DescribeComponentTypeAsync):
                    return Task.FromResult(new ComponentTypeInfo("[FrooxEngine]FrooxEngine.InteractiveCamera", null, null, false, [],
                        HasCapture ? [new SyncMethodInfo("Capture", new Dictionary<string, string?>(), "void", false, false)] : []));
                case nameof(IResoniteClient.CreateSlotAsync):
                    Created = (SlotCreateRequest)args[0]!;
                    return Task.FromResult("capture-slot");
                case nameof(IResoniteClient.AddComponentAsync):
                    Fields = (IReadOnlyDictionary<string, string>)args[2]!;
                    return Task.FromResult(new ComponentCreateResult("interactive", "[FrooxEngine]FrooxEngine.InteractiveCamera"));
                case nameof(IResoniteClient.GetComponentAsync):
                    return Task.FromResult((string)args[0]! == "interactive"
                        ? new ComponentInfo("interactive", "[FrooxEngine]FrooxEngine.InteractiveCamera", new Dictionary<string, MemberValue> { ["MainCamera"] = new("reference", TargetId: "main") })
                        : new ComponentInfo("main", "[FrooxEngine]FrooxEngine.Camera", new Dictionary<string, MemberValue>()));
                case nameof(IResoniteClient.GetSlotAsync):
                    var name = RenameOnCleanup && (int)args[1]! == 0 ? "User content" : Created!.Name;
                    return Task.FromResult(new SlotInfo("capture-slot", name, "Root", null, null, null, true, false, null, false,
                        [new ComponentSummary("main", "[FrooxEngine]FrooxEngine.Camera")], []));
                case nameof(IResoniteClient.SetComponentMemberAsync): return Task.CompletedTask;
                case nameof(IResoniteClient.CallComponentMethodAsync):
                    return Task.FromResult(OnCapture?.Invoke() ?? new SyncMethodCallResult(false, null, "trigger rejected"));
                case nameof(IResoniteClient.DeleteSlotAsync):
                    Deleted.Add((string)args[0]!);
                    return Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
