using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static void VerifyTinkerContracts(string repositoryRoot, string supportRoot)
    {
        VerifyTinkerScripts();
        VerifyTinkerHistory(Path.Combine(supportRoot, "tinker-history", "tinker-history.json"));
        VerifyTinkerSource(repositoryRoot);
    }

    private static void VerifyTinkerScripts()
    {
        var code = TinkerScript.BuildCodeFile("use App\\Models\\User;\r\nuse Illuminate\\Support\\Str as S;\r\n$count = User::count();\r\nreturn $count;");
        var lines = code.Split('\n');
        Check(
            lines[0].StartsWith(
                "<?php use App\\Models\\User; use Illuminate\\Support\\Str as S; return (static function () use ($app) { ",
                StringComparison.Ordinal
            )
                && lines[2] == "$count = User::count();"
                && lines[3] == "return $count;"
                && code.EndsWith("\n})();\n", StringComparison.Ordinal)
                && !code.Contains('\r'),
            "Tinker hoists use imports and keeps the editor's line numbers"
        );
        var tagged = TinkerScript.BuildCodeFile("<?php\nreturn 1;\n?>");
        Check(
            tagged.Split('\n')[1] == "return 1;" && !tagged.Contains("?>", StringComparison.Ordinal),
            "Tinker accepts code with PHP open and close tags"
        );
        Check(
            TinkerScript.BuildCodeFile("// note\nreturn 2;").Split('\n')[1] == "return 2;",
            "Tinker keeps comments in place"
        );
        Throws<ArgumentException>(() => TinkerScript.Normalize("   \n  "), "Tinker rejects empty code");
        Throws<ArgumentException>(() => TinkerScript.Normalize("<?php"), "Tinker rejects an open tag without code");
        Throws<ArgumentException>(
            () => TinkerScript.Normalize(new string('x', TinkerScript.MaximumCodeCharacters + 1)),
            "Tinker bounds code size"
        );
        Throws<ArgumentException>(() => TinkerScript.Normalize("echo 1;\0"), "Tinker rejects NUL characters");

        var runner = TinkerScript.BuildRunner("/srv/it's", "/tmp/code.php");
        Check(
            runner.Contains(TinkerScript.PhpString(Path.GetFullPath("/srv/it's")), StringComparison.Ordinal)
                && runner.Contains("vendor/autoload.php", StringComparison.Ordinal)
                && runner.Contains("Illuminate\\Contracts\\Console\\Kernel::class)->bootstrap()", StringComparison.Ordinal)
                && runner.Contains("catch (\\Throwable", StringComparison.Ordinal),
            "Tinker boots Composer and Laravel and escapes the project path"
        );
        Check(
            TinkerScript.PhpString("a\\'b") == "'a\\\\\\'b'",
            "Tinker PHP strings escape backslashes and quotes"
        );
    }

    private static void VerifyTinkerHistory(string path)
    {
        var store = new TinkerHistoryStore(path);
        Check(store.Load().Count == 0, "Tinker history starts empty");
        for (var index = 0; index < TinkerHistoryStore.MaximumEntries + 5; index++) store.Add($"return {index};");
        store.Add("return 3;");
        var entries = store.Load();
        Check(
            entries.Count == TinkerHistoryStore.MaximumEntries
                && entries[0] == "return 3;"
                && entries.Count(entry => entry == "return 3;") == 1
                && entries[1] == $"return {TinkerHistoryStore.MaximumEntries + 4};",
            "Tinker history keeps the newest unique snippets"
        );
        store.Add("return 1;\r\nreturn 2;");
        Check(store.Load()[0] == "return 1;\nreturn 2;", "Tinker history stores LF line endings");
        File.WriteAllText(path, "{ not json");
        Check(store.Load().Count == 0, "damaged Tinker history reads as empty");
        store.Clear();
        Check(!File.Exists(path), "Tinker history can be cleared");
    }

    private static void VerifyTinkerSource(string repositoryRoot)
    {
        var root = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var runner = File.ReadAllText(Path.Combine(root, "Services", "TinkerRunner.cs"));
        var artisan = File.ReadAllText(Path.Combine(root, "Services", "ArtisanCommandRunner.cs"));
        Check(
            runner.Contains("ArtisanCommandRunner.RunPhpAsync", StringComparison.Ordinal)
                && artisan.Contains("CreateNoWindow = true", StringComparison.Ordinal)
                && artisan.Contains("WindowsJobObject.TryAttach", StringComparison.Ordinal)
                && !runner.Contains("new ProcessStartInfo", StringComparison.Ordinal),
            "Tinker runs PHP through the hidden, job-bound Artisan process runner"
        );
        var page = File.ReadAllText(Path.Combine(root, "Pages", "TinkerPage.xaml.cs"));
        Check(
            page.Contains("PrepareLaunchAsync", StringComparison.Ordinal)
                && page.Contains("ManagedEnvironment", StringComparison.Ordinal)
                && page.Contains("cancellation?.Cancel()", StringComparison.Ordinal),
            "Tinker validates PHP, uses the managed environment, and cancels when the page closes"
        );
    }
}
