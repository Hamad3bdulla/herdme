using System.Text.Json;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyShellIntegrationContractsAsync(string repositoryRoot, string supportRoot)
    {
        VerifyShellCommands();
        VerifyTerminalFragment(supportRoot);
        VerifyShellIntegrationSources(repositoryRoot);
        await VerifySiteRootWatcherAsync(supportRoot);
    }

    private static void VerifyShellCommands()
    {
        const string executable = @"C:\Apps\HerdMe\HerdMe.Windows.exe";
        Check(
            WindowsShellIntegration.ExplorerCommand(executable) == "\"C:\\Apps\\HerdMe\\HerdMe.Windows.exe\" --link \"%V\"",
            "the Explorer verb passes the folder to --link"
        );
        Check(
            WindowsShellIntegration.ProtocolCommand(executable) == "\"C:\\Apps\\HerdMe\\HerdMe.Windows.exe\" \"%1\"",
            "the herdme:// handler passes the link as one argument"
        );
        foreach (var key in new[]
        {
            WindowsShellIntegration.DirectoryVerbKey,
            WindowsShellIntegration.BackgroundVerbKey,
            WindowsShellIntegration.ProtocolKey
        })
        {
            Check(key.StartsWith(@"Software\Classes\", StringComparison.Ordinal), $"{key} is a per-user class key");
            Check(key.Contains("HerdMe", StringComparison.OrdinalIgnoreCase), $"{key} is named for HerdMe");
        }
    }

    private static void VerifyTerminalFragment(string supportRoot)
    {
        var localData = Path.Combine(supportRoot, "local-app-data");
        var integration = new WindowsShellIntegration(Path.Combine(supportRoot, "HerdMe"), localData);
        Check(
            integration.TerminalFragmentPath == Path.Combine(localData, "Microsoft", "Windows Terminal", "Fragments", "HerdMe", "herdme.json"),
            "the Windows Terminal profile is a HerdMe fragment"
        );
        var fragment = WindowsShellIntegration.BuildTerminalFragment(@"C:\Apps\HerdMe", @"C:\Users\O'Brien\AppData\Local\HerdMe", null);
        using var document = JsonDocument.Parse(fragment);
        var profile = document.RootElement.GetProperty("profiles")[0];
        Check(profile.GetProperty("name").GetString() == "HerdMe", "the Terminal profile is named HerdMe");
        var commandLine = profile.GetProperty("commandline").GetString()!;
        Check(commandLine.Contains("powershell.exe -NoLogo -NoExit", StringComparison.Ordinal), "the Terminal profile opens Windows PowerShell");
        Check(commandLine.Contains("O''Brien", StringComparison.Ordinal), "the Terminal profile escapes single quotes in paths");
        Check(commandLine.Contains("herdme.cmd' status", StringComparison.Ordinal), "the Terminal profile prints herdme status");
        Check(profile.GetProperty("startingDirectory").GetString() == "%USERPROFILE%", "the Terminal profile starts in the user folder without a parked folder");
        Check(
            profile.GetProperty("icon").GetString()!.EndsWith(Path.Combine("Assets", "HerdMe.ico"), StringComparison.Ordinal),
            "the Terminal profile uses the HerdMe icon"
        );
        var withRoot = WindowsShellIntegration.BuildTerminalFragment(@"C:\Apps\HerdMe", @"C:\Data\HerdMe", @"C:\Projects");
        using var rooted = JsonDocument.Parse(withRoot);
        Check(
            rooted.RootElement.GetProperty("profiles")[0].GetProperty("startingDirectory").GetString() == @"C:\Projects",
            "the Terminal profile starts in the first parked folder"
        );
    }

    private static void VerifyShellIntegrationSources(string repositoryRoot)
    {
        var windows = Path.Combine(repositoryRoot, "Windows");
        var installer = File.ReadAllText(Path.Combine(windows, "installer.iss"));
        foreach (var key in new[]
        {
            @"Software\Classes\herdme""; Flags: dontcreatekey uninsdeletekey",
            @"Software\Classes\Directory\shell\HerdMe.Link""; Flags: dontcreatekey uninsdeletekey",
            @"Software\Classes\Directory\Background\shell\HerdMe.Link""; Flags: dontcreatekey uninsdeletekey",
            @"{localappdata}\Microsoft\Windows Terminal\Fragments\HerdMe"
        })
        {
            Check(installer.Contains(key, StringComparison.Ordinal), $"uninstall removes {key}");
        }
        var general = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "Pages", "GeneralPage.xaml"));
        foreach (var toggle in new[] { "ExplorerLinkToggle", "UriProtocolToggle", "TerminalProfileToggle" })
        {
            Check(general.Contains($"x:Name=\"{toggle}\"", StringComparison.Ordinal), $"General has the {toggle} opt-in switch");
        }
        var generalCode = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "Pages", "GeneralPage.xaml.cs"));
        Check(
            generalCode.Contains("shellIntegration.IsEnabled(ShellIntegrationFeature.ExplorerLink)", StringComparison.Ordinal),
            "the Explorer switch shows the real registry state"
        );
        var app = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "App.xaml.cs"));
        Check(app.Contains("StopAndLogAsync(\"site folder watcher\", StopSiteRootWatcherAsync)", StringComparison.Ordinal), "exit stops the site folder watcher");
        var integration = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "Services", "WindowsShellIntegration.cs"));
        Check(!integration.Contains("Registry.LocalMachine", StringComparison.Ordinal), "shell integration never writes machine-wide keys");
        Check(!integration.Contains("Registry.ClassesRoot", StringComparison.Ordinal), "shell integration writes HKCU classes only");
    }

    private static async Task VerifySiteRootWatcherAsync(string supportRoot)
    {
        var first = Path.Combine(supportRoot, "watch-a");
        var second = Path.Combine(supportRoot, "watch-b");
        Directory.CreateDirectory(first);
        var roots = new List<string> { first, Path.Combine(supportRoot, "missing-root") };
        var changes = 0;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var watcher = new SiteRootWatcher(
            () => roots.ToList(),
            _ =>
            {
                Interlocked.Increment(ref changes);
                changed.TrySetResult();
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200)
        ))
        {
            watcher.Start();
            Check(watcher.WatchedRoots.Count == 1, "missing parked folders are not watched");
            Directory.CreateDirectory(Path.Combine(first, "new-project"));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Volatile.Read(ref changes) >= 1, "a new project folder triggers a rescan");

            Directory.CreateDirectory(second);
            roots.Add(second);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (watcher.WatchedRoots.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Check(watcher.WatchedRoots.Count == 2, "a parked folder added later is watched");
            roots.Remove(first);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (watcher.WatchedRoots.Count > 1 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Check(watcher.WatchedRoots.Count == 1, "a removed parked folder is no longer watched");
        }
    }
}
