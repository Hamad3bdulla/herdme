namespace HerdMe.Windows.Services;

public readonly record struct RuntimeInstallAction(
    bool IsVisible,
    string Label,
    bool IsUpdateAvailable
);

public static class RuntimeVersionComparison
{
    public static bool IsNewer(string candidate, string current)
    {
        return Compare(candidate, current) > 0;
    }

    public static int Compare(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal)) return 0;

        if (TryParseSemanticVersion(normalizedLeft, out var leftSemantic)
            && TryParseSemanticVersion(normalizedRight, out var rightSemantic))
        {
            return CompareSemanticVersions(leftSemantic, rightSemantic);
        }

        if (Version.TryParse(normalizedLeft, out var leftVersion)
            && Version.TryParse(normalizedRight, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return CompareNatural(normalizedLeft, normalizedRight);
    }

    public static bool IsSemanticVersion(string value) =>
        TryParseSemanticVersion(Normalize(value), out _);

    public static string Normalize(string version)
    {
        var trimmed = version.Trim();
        return trimmed.Length > 0 && trimmed[0] is 'v' or 'V' ? trimmed[1..] : trimmed;
    }

    public static RuntimeInstallAction InstallAction(
        bool isInstalled,
        string? installedVersion,
        string? latestVersion
    )
    {
        if (!isInstalled) return new RuntimeInstallAction(true, "Install", false);
        var updateAvailable = installedVersion is not null
            && latestVersion is not null
            && IsNewer(latestVersion, installedVersion);
        return new RuntimeInstallAction(updateAvailable, "Update", updateAvailable);
    }

    private static bool TryParseSemanticVersion(string value, out SemanticVersion version)
    {
        var buildParts = value.Split('+');
        if (buildParts.Length > 2
            || buildParts.Length == 2 && !ValidIdentifiers(buildParts[1], true))
        {
            version = default;
            return false;
        }
        var withoutBuild = buildParts[0];
        var separator = withoutBuild.IndexOf('-');
        var core = separator < 0 ? withoutBuild : withoutBuild[..separator];
        var prerelease = separator < 0 ? null : withoutBuild[(separator + 1)..];
        var coreParts = core.Split('.');
        if (coreParts.Length == 0
            || coreParts.Any(part => !ValidNumericIdentifier(part, false))
            || prerelease is not null && !ValidIdentifiers(prerelease, false))
        {
            version = default;
            return false;
        }
        version = new SemanticVersion(coreParts, prerelease?.Split('.'));
        return true;
    }

    private static int CompareSemanticVersions(SemanticVersion left, SemanticVersion right)
    {
        var coreLength = Math.Max(left.Core.Length, right.Core.Length);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < left.Core.Length ? left.Core[index] : "0";
            var rightPart = index < right.Core.Length ? right.Core[index] : "0";
            var coreOrder = CompareNumeric(leftPart, rightPart);
            if (coreOrder != 0) return coreOrder;
        }

        if (left.Prerelease is null) return right.Prerelease is null ? 0 : 1;
        if (right.Prerelease is null) return -1;
        var count = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = left.Prerelease[index];
            var rightPart = right.Prerelease[index];
            if (leftPart == rightPart) continue;
            var leftIsNumber = leftPart.All(char.IsAsciiDigit);
            var rightIsNumber = rightPart.All(char.IsAsciiDigit);
            if (leftIsNumber && rightIsNumber)
            {
                return CompareNumeric(leftPart, rightPart);
            }
            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            var comparison = StringComparer.Ordinal.Compare(leftPart, rightPart);
            if (comparison != 0) return comparison;
        }
        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static bool ValidIdentifiers(string value, bool numericLeadingZeros)
    {
        var identifiers = value.Split('.');
        return identifiers.Length > 0 && identifiers.All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (!identifier.All(char.IsAsciiDigit)
                || ValidNumericIdentifier(identifier, numericLeadingZeros))
        );
    }

    private static bool ValidNumericIdentifier(string value, bool allowLeadingZeros)
    {
        return value.Length > 0
            && value.All(char.IsAsciiDigit)
            && (allowLeadingZeros || value.Length == 1 || value[0] != '0');
    }

    private static int CompareNumeric(string left, string right)
    {
        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');
        if (normalizedLeft.Length == 0) normalizedLeft = "0";
        if (normalizedRight.Length == 0) normalizedRight = "0";
        if (normalizedLeft.Length != normalizedRight.Length)
        {
            return normalizedLeft.Length.CompareTo(normalizedRight.Length);
        }
        return string.CompareOrdinal(normalizedLeft, normalizedRight);
    }

    private static int CompareNatural(string left, string right)
    {
        var leftParts = NaturalParts(left);
        var rightParts = NaturalParts(right);
        var count = Math.Min(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            var leftPart = leftParts[index];
            var rightPart = rightParts[index];
            if (char.IsDigit(leftPart[0]) && char.IsDigit(rightPart[0]))
            {
                var leftNumber = leftPart.TrimStart('0');
                var rightNumber = rightPart.TrimStart('0');
                if (leftNumber.Length == 0) leftNumber = "0";
                if (rightNumber.Length == 0) rightNumber = "0";
                if (leftNumber.Length != rightNumber.Length)
                {
                    return leftNumber.Length.CompareTo(rightNumber.Length);
                }
                var numberComparison = string.CompareOrdinal(leftNumber, rightNumber);
                if (numberComparison != 0) return numberComparison;
                continue;
            }
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftPart, rightPart);
            if (comparison != 0) return comparison;
        }
        return leftParts.Count.CompareTo(rightParts.Count);
    }

    private static List<string> NaturalParts(string value)
    {
        if (value.Length == 0) return [];
        var parts = new List<string>();
        var start = 0;
        var digits = char.IsDigit(value[0]);
        for (var index = 1; index < value.Length; index++)
        {
            var nextDigits = char.IsDigit(value[index]);
            if (nextDigits == digits) continue;
            parts.Add(value[start..index]);
            start = index;
            digits = nextDigits;
        }
        parts.Add(value[start..]);
        return parts;
    }

    private readonly record struct SemanticVersion(string[] Core, string[]? Prerelease);
}
