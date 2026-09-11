namespace RLoop.Core;

public static class ReflectedMemberType
{
    public static string ValueType(ComponentTypeInfo component, string memberName)
    {
        var member = component.Members.SingleOrDefault(m => m.Name == memberName)
            ?? throw new RLoopException("MEMBER_NOT_FOUND", $"Component '{component.FullTypeName}' has no member '{memberName}'.", ExitCodes.NotFound);
        if (member.ValueType is not { Length: > 0 } type)
            throw new RLoopException("MEMBER_VALUE_TYPE_UNAVAILABLE", $"Member '{memberName}' is {member.Kind}; select a field with a reflected valueType.", ExitCodes.ValidationFailed);
        return UnwrapNullable(type);
    }

    public static string UnwrapNullable(string type)
    {
        foreach (var prefix in new[] { "Nullable<><", "System.Nullable<><", "Nullable<", "System.Nullable<" })
            if (type.StartsWith(prefix, StringComparison.Ordinal) && type.EndsWith('>')) return type[prefix.Length..^1];
        if (type.EndsWith('?')) return type[..^1];
        return type;
    }
}
