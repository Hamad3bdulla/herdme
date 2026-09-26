using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static void VerifyProfilerContracts(string supportRoot)
    {
        VerifyCachegrindParsing();
        VerifyProfileStore(Path.Combine(supportRoot, "profiles-store"));
        VerifyProfilerPhpOptions(Path.Combine(supportRoot, "profiles-php"));
    }

    private static void VerifyCachegrindParsing()
    {
        const string fixture = """
            version: 1
            creator: xdebug 3.4.0 (PHP 8.4.1)
            cmd: C:\Sites\shop\public\index.php
            part: 1
            positions: line

            events: Time_(10ns) Memory_(bytes)

            fl=(1) C:\Sites\shop\app\Slow.php
            fn=(1) App\Slow->work
            12 30000 2048

            fl=(1)
            fn=(2) {main}
            1 1000 512
            cfl=(1)
            cfn=(1)
            calls=3 0 0
            5 30000 2048

            summary: 31000 4096
            """;
        var profile = CachegrindParser.Parse(new StringReader(fixture));
        var main = profile.Functions.Single(function => function.Name == "{main}");
        var slow = profile.Functions.Single(function => function.Name == "App\\Slow->work");
        Check(
            profile.Command == @"C:\Sites\shop\public\index.php"
                && profile.TotalTime == TimeSpan.FromTicks(3_100)
                && profile.PeakMemory == 4_096,
            "cachegrind profiles read the script, total time in 10ns units, and peak memory"
        );
        Check(
            main.SelfTime == TimeSpan.FromTicks(100)
                && main.InclusiveTime == TimeSpan.FromTicks(3_100)
                && main.Calls == 1
                && main.InclusiveMemory == 2_560,
            "cachegrind call costs count toward the caller's inclusive cost only"
        );
        Check(
            slow.Calls == 3
                && slow.SelfTime == TimeSpan.FromTicks(3_000)
                && slow.File == @"C:\Sites\shop\app\Slow.php"
                && profile.Functions[0] == main,
            "cachegrind name compression resolves files and functions and sorts by inclusive time"
        );

        var legacy = CachegrindParser.Parse(new StringReader("events: Time Memory\nfn=main\n1 5 0\n"));
        Check(
            legacy.Functions.Single().SelfTime == TimeSpan.FromTicks(50)
                && legacy.TotalTime == TimeSpan.FromTicks(50),
            "cachegrind profiles in microseconds convert correctly"
        );
        Throws<InvalidDataException>(
            () => CachegrindParser.Parse(new StringReader("<?php echo 'not a profile';\n")),
            "files without cachegrind events are rejected"
        );
    }

    private static void VerifyProfileStore(string root)
    {
        var store = new XdebugProfileStore(root);
        Check(store.List().Count == 0, "a missing profiles folder lists no profiles");
        Directory.CreateDirectory(root);
        var start = DateTime.UtcNow.AddHours(-1);
        for (var index = 0; index < XdebugProfileStore.MaximumProfiles + 3; index++)
        {
            var path = Path.Combine(root, $"cachegrind.out.{index}.test");
            File.WriteAllText(path, "events: Time_(10ns)\n");
            File.SetLastWriteTimeUtc(path, start.AddSeconds(index));
        }
        var other = Path.Combine(root, "notes.txt");
        File.WriteAllText(other, "keep");
        var listed = store.List();
        Check(
            listed.Count == XdebugProfileStore.MaximumProfiles
                && listed[0].Name == $"cachegrind.out.{XdebugProfileStore.MaximumProfiles + 2}.test"
                && !File.Exists(Path.Combine(root, "cachegrind.out.0.test"))
                && File.Exists(other),
            "profiles list newest first and prune the oldest beyond the limit"
        );
        Check(
            store.Load(listed[0].Path).Functions.Count == 0,
            "profiles in the HerdMe folder can be opened"
        );
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "cachegrind.out.outside");
        File.WriteAllText(outside, "events: Time\n");
        Throws<ArgumentException>(() => store.Delete(outside), "profiles outside the HerdMe folder are never deleted");
        Throws<ArgumentException>(() => store.Load(other), "only cachegrind files are opened as profiles");
        Throws<ArgumentException>(
            () => store.Delete(Path.Combine(root, "..", "cachegrind.out.outside")),
            "profile paths cannot escape the HerdMe folder"
        );
        store.Delete(listed[^1].Path);
        Check(store.List().Count == XdebugProfileStore.MaximumProfiles - 1, "a single profile can be deleted");
        store.DeleteAll();
        Check(
            store.List().Count == 0 && File.Exists(other) && File.Exists(outside),
            "deleting all profiles removes only HerdMe profiles"
        );
    }

    private static void VerifyProfilerPhpOptions(string supportPath)
    {
        var settings = new PhpRuntimeSettings
        {
            PhpCycle = "8.4",
            Debugger = new DebuggerSettings { Enabled = true, DetectBreakpoints = false, ProfilerEnabled = true }
        };
        var extension = Path.Combine(supportPath, "Extensions", "php", "8.4", "php_xdebug.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(extension)!);
        File.WriteAllBytes(extension, [1]);
        var options = PhpRuntimePolicy.BuildPhpOptions(settings, requireDebuggerExtension: true, supportPath);
        var profiles = XdebugProfileStore.DirectoryFor(supportPath);
        Check(
            options["xdebug.mode"] == "debug,profile"
                && options["xdebug.start_with_request"] == "trigger"
                && options["xdebug.output_dir"] == profiles
                && options["xdebug.profiler_output_name"] == XdebugProfileStore.OutputNamePattern
                && Directory.Exists(profiles),
            "the Xdebug profiler writes to the HerdMe profiles folder only for triggered requests"
        );
        settings.Debugger.ProfilerEnabled = false;
        var withoutProfiler = PhpRuntimePolicy.BuildPhpOptions(settings, requireDebuggerExtension: true, supportPath);
        Check(
            withoutProfiler["xdebug.mode"] == "debug"
                && withoutProfiler["xdebug.start_with_request"] == "yes"
                && !withoutProfiler.ContainsKey("xdebug.output_dir"),
            "the Xdebug profiler stays off unless the user enables it"
        );
        var site = new SiteRecord { Name = "shop", Domain = "shop.test", Path = supportPath };
        var uri = SitePresentation.ProfileUri(site, true, 80, 443, "VSCODE");
        Check(
            uri.Query == "?XDEBUG_PROFILE=VSCODE" && uri.Host == "shop.test" && uri.IsDefaultPort,
            "profile requests open the site with the profiler trigger and no port"
        );
    }
}
