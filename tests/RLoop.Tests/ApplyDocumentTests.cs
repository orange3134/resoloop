using RLoop.Core;

namespace RLoop.Tests;

public sealed class ApplyDocumentTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "resoloop-apply-" + Guid.NewGuid().ToString("N") + ".json");

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

    [Fact]
    public void UnknownProbePropertySuggestsArguments()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion":"1", "ownership":{"key":"test"},
              "slot":{"key":"root","name":"RootNode","parent":"Root"},
              "tests":[{"name":"probe","assertions":[],"probe":{
                "kind":"method","target":"$component:target","method":"Run","argumnts":{}
              }}]
            }
            """);

        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(_path));

        Assert.Equal("APPLY_DOCUMENT_INVALID", error.Code);
        Assert.Contains(error.Suggestions, suggestion => suggestion.Contains("'arguments'", StringComparison.Ordinal));
    }

    [Fact]
    public void ChildKeyErrorExplainsTheSlotWrapper()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion":"1", "ownership":{"key":"test"},
              "slot":{"key":"root","name":"RootNode","parent":"Root"},
              "children":[{"key":"target","name":"Target"}]
            }
            """);

        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(_path));

        Assert.Contains(error.Suggestions, suggestion => suggestion.Contains("wraps Slot properties", StringComparison.Ordinal));
    }

    [Fact]
    public void ComponentReferencesErrorExplainsFieldsSyntax()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion":"1", "ownership":{"key":"test"},
              "slot":{"key":"root","name":"RootNode","parent":"Root"},
              "components":[{"key":"tool","type":"FrooxEngine.RawDataTool",
                "references":{"TipReference":"$slot:muzzle"}}]
            }
            """);

        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(_path));

        Assert.Contains(error.Suggestions, suggestion => suggestion.Contains("Component 'fields' object", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuralOnlyTestReportIsPartialRatherThanVerified()
    {
        var report = new ApplyTestReport(true, true, 1, 1,
            [new ApplyTestCaseResult("gun", true, true, false, "Structure only", [])]);

        Assert.Equal("partial", report.Verification);
        Assert.Equal("partial", report.Tests[0].Verification);
    }

    [Fact]
    public async Task MissingAssertionsReturnsStructuredValidationIssue()
    {
        File.WriteAllText(_path, """
            {
              "schemaVersion":"1", "ownership":{"key":"test"},
              "slot":{"key":"root","name":"RootNode","parent":"Root"},
              "tests":[{"name":"missing","assertions":null}]
            }
            """);

        var result = await ApplyDocumentValidator.ValidateAsync(ApplyDocument.Load(_path));

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "APPLY_TEST_ASSERTIONS_MISSING");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
