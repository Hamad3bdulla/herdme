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
        var normalizedCandidate = Normalize(candidate);
        var normalizedCurrent = Normalize(current);
        if (normalizedCandidate.Equals(normalizedCurrent, StringComparison.OrdinalIgnoreCase)) return false;

        if (Version.TryParse(normalizedCandidate, out var candidateVersion)
            && Version.TryParse(normalizedCurrent, out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        if (TryParseSemanticVersion(normalizedCandidate, out var candidateSemantic)
            && TryParseSemanticVersion(normalizedCurrent, out var currentSemantic))
        {
            return CompareSemanticVersions(candidateSemantic, currentSemantic) > 0;
        }

        return CompareNatural(normalizedCandidate, normalizedCurrent) > 0;
    }

    public static string Normalize(string version) => version.Trim().TrimStart('v');

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
        var withoutBuild = value.Split('+', 2)[0];
        var separator = withoutBuild.IndexOf('-');
        var core = separator < 0 ? withoutBuild : withoutBuild[..separator];
        var prerelease = separator < 0 ? null : withoutBuild[(separator + 1)..];
        var coreParts = core.Split('.');
        var numbers = new int[coreParts.Length];
        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!int.TryParse(coreParts[index], out numbers[index]))
            {
                version = default;
                return false;
            }
        }
        version = new SemanticVersion(numbers, prerelease);
        return numbers.Length > 0;
    }

    private static int CompareSemanticVersions(SemanticVersion left, SemanticVersion right)
    {
        var coreLength = Math.Max(left.Core.Length, right.Core.Length);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < left.Core.Length ? left.Core[index] : 0;
            var rightPart = index < right.Core.Length ? right.Core[index] : 0;
            if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
        }

        if (left.Prerelease is null) return right.Prerelease is null ? 0 : 1;
        if (right.Prerelease is null) return -1;
        var leftParts = left.Prerelease.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Prerelease.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Min(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftIsNumber = int.TryParse(leftParts[index], out var leftNumber);
            var rightIsNumber = int.TryParse(rightParts[index], out var rightNumber);
            if (leftIsNumber && rightIsNumber && leftNumber != rightNumber)
            {
                return leftNumber.CompareTo(rightNumber);
            }
            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
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

    private readonly record struct SemanticVersion(int[] Core, string? Prerelease);
}
