using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

/// <summary>A SemVer 2.0 version. Numeric identifiers are compared without machine-sized integers.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant);
    private readonly string _text;
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private SemanticVersion(string text, string[] core, string[] prerelease) =>
        (_text, _core, _prerelease) = (text, core, prerelease);

    public static SemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = Pattern.Match(value);
        if (!match.Success) throw new FormatException("Expected a SemVer 2.0 version.");
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0'))
            throw new FormatException("Numeric prerelease identifiers must not have leading zeroes.");
        return new(value, [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], prerelease);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < _core.Length; i++)
        {
            var result = CompareNumeric(_core[i], other._core[i]);
            if (result != 0) return result;
        }
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return (_prerelease.Length == 0 ? 1 : 0).CompareTo(other._prerelease.Length == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var result = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public override string ToString() => _text;
    private static bool IsNumeric(string value) => value.All(c => c is >= '0' and <= '9');
    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
