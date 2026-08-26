using RLoop.Core;

namespace RLoop.Tests;

public sealed class ApplyDocumentTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rloop-apply-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void LoadsNestedSlotsAndSymbolicComponentKeys()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion": "1",
              "ownership": { "key": "test" },
              "slot": { "key": "root", "name": "RootNode", "parent": "Root" },
              "children": [
                {
                  "slot": { "name": "Material" },
                  "components": [
                    { "key": "warm", "type": "FrooxEngine.PBS_Metallic", "fields": { "AlbedoColor": [1, 0.5, 0.2, 1] } }
                  ]
                }
              ]
            }
            """);

        var document = ApplyDocument.Load(_path);

        var child = Assert.Single(document.Children!);
        Assert.Equal("Material", child.Slot.Name);
        Assert.Equal("warm", Assert.Single(child.Components!).Key);
    }

    [Fact]
    public async Task RequiresSupportedSchemaOwnershipAndRootKey()
    {
        File.WriteAllText(_path, """
            {
              "slot": { "name": "RootNode", "parent": "Root" }
            }
            """);

        var result = await ApplyDocumentValidator.ValidateAsync(ApplyDocument.Load(_path));

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, x => x.Code == "APPLY_SCHEMA_VERSION_UNSUPPORTED");
        Assert.Contains(result.Issues, x => x.Code == "APPLY_OWNERSHIP_MISSING");
        Assert.Contains(result.Issues, x => x.Code == "APPLY_ROOT_KEY_MISSING");
    }

    [Fact]
    public void RejectsUnknownSchemaMembers()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion": "1",
              "ownership": { "key": "test" },
              "slot": { "key": "root", "name": "RootNode", "parent": "Root" },
              "typoField": true
            }
            """);

        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(_path));
        Assert.Equal("APPLY_DOCUMENT_INVALID", error.Code);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
