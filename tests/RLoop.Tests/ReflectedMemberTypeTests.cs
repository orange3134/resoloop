using RLoop.Core;

namespace RLoop.Tests;

public sealed class ReflectedMemberTypeTests
{
    private static readonly ComponentTypeInfo Component = new("FrooxEngine.StaticTexture2D", null, null, false,
    [
        new("PreferredProfile", "field", null, "Nullable<><[Renderite.Shared]Renderite.Shared.ColorProfile>", null),
        new("URL", "field", null, "Uri", null),
        new("Ref", "reference", null, null, "FrooxEngine.Slot")
    ]);

    [Fact]
    public void UnwrapsNullableWithoutGuessingEnumAssembly()
    {
        Assert.Equal("[Renderite.Shared]Renderite.Shared.ColorProfile", ReflectedMemberType.ValueType(Component, "PreferredProfile"));
        Assert.Equal("Uri", ReflectedMemberType.ValueType(Component, "URL"));
    }

    [Fact]
    public void MissingOrNonfieldMemberHasAnActionableError()
    {
        Assert.Equal("MEMBER_NOT_FOUND", Assert.Throws<RLoopException>(() => ReflectedMemberType.ValueType(Component, "Missing")).Code);
        Assert.Equal("MEMBER_VALUE_TYPE_UNAVAILABLE", Assert.Throws<RLoopException>(() => ReflectedMemberType.ValueType(Component, "Ref")).Code);
    }
}
