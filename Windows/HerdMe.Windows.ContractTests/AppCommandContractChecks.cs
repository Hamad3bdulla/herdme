using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyAppCommandContractsAsync(string repositoryRoot, string supportRoot)
    {
        VerifyCommandUris();
        VerifyLaunchArguments();
        VerifyCommandValidation();
        VerifyCliArguments();
        await VerifyCommandFramingAsync();
        VerifyCommandShim(supportRoot);
        VerifyJumpListEntries();
        VerifyCommandSources(repositoryRoot);
    }

    private static void VerifyCommandUris()
    {
        var site = AppCommandProtocol.ParseUri("herdme://site/blog");
        Check(
            site is { Command: "site", Interactive: true } && site.Arguments.SequenceEqual(["blog"]),
            "herdme://site/<name> shows a site"
        );
        var page = AppCommandProtocol.ParseUri("herdme://show/Sites");
        Check(page is { Command: "show" } && page.Arguments.SequenceEqual(["sites"]), "herdme://show/<page> is case-insensitive");
        Check(AppCommandProtocol.ParseUri("herdme://show") is { Command: "show" }, "herdme://show brings the window forward");
        foreach (var rejected in new[]
        {
            "herdme://start",
            "herdme://stop",
            "herdme://share/blog",
            "herdme://open/blog",
            "herdme://link/C:%5Cwork",
            "herdme://unlink/blog",
            "herdme://site/blog?token=1",
            "herdme://site/blog#top",
            "herdme://site/a/b",
            "herdme://site/..",
            "herdme://user@site/blog",
            "herdme://site:8080/blog",
            "herdme://show/secret",
            "https://site/blog",
            "herdme:",
            "herdme://site/" + new string('a', 600)
        })
        {
            Check(AppCommandProtocol.ParseUri(rejected) is null, $"herdme:// links cannot run {rejected}");
        }
    }

    private static void VerifyLaunchArguments()
    {
        var start = AppCommandProtocol.ParseLaunchArguments(["--command", "START"]);
        Check(start is { Command: "start", Interactive: true }, "Jump List --command start is an interactive request");
        var link = AppCommandProtocol.ParseLaunchArguments(["--link", "\"C:\\Projects\\blog\\\""]);
        Check(
            link is { Command: "link" } && link.Arguments.SequenceEqual([@"C:\Projects\blog"]),
            "Explorer --link trims quotes and the trailing separator"
        );
        var driveRoot = AppCommandProtocol.ParseLaunchArguments(["--link", @"D:\"]);
        Check(driveRoot is not null && driveRoot.Arguments[0] == @"D:\", "Explorer --link keeps a drive root separator");
        Check(AppCommandProtocol.ParseLaunchArguments(["--link", "relative"]) is null, "--link needs a full path");
        Check(AppCommandProtocol.ParseLaunchArguments(["--link", @"C:\work\..\secret"]) is null, "--link rejects parent segments");
        Check(AppCommandProtocol.ParseLaunchArguments(["--command", "show", "secret"]) is null, "--command rejects unknown pages");
        Check(AppCommandProtocol.ParseLaunchArguments(["--command"]) is null, "--command needs a command");
        Check(AppCommandProtocol.ParseLaunchArguments([]) is null, "a plain launch has no command");
        Check(AppCommandProtocol.ParseLaunchArguments(["--background"]) is null, "--background is not a command");
        Check(
            AppCommandProtocol.ParseLaunchArguments(["--background", "herdme://site/blog"]) is { Command: "site" },
            "a herdme:// link is found after other switches"
        );
    }

    private static void VerifyCommandValidation()
    {
        Check(AppCommandProtocol.Validate(null) is not null, "an empty command request is rejected");
        Check(
            AppCommandProtocol.Validate(new AppCommandRequest("status", [], Version: 99)) is not null,
            "a command request from a different protocol version is rejected"
        );
        Check(AppCommandProtocol.Validate(new AppCommandRequest("status", [])) is null, "status needs no arguments");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("status", ["x"])) is not null, "status rejects arguments");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("format", [])) is not null, "unknown commands are rejected");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("open", ["bad name"])) is not null, "site names are validated");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("open", ["blog\n"])) is not null, "control characters are rejected");
        Check(
            AppCommandProtocol.Validate(new AppCommandRequest("link", [new string('a', AppCommandProtocol.MaximumArgumentLength + 1)])) is not null,
            "oversized arguments are rejected"
        );
        Check(AppCommandProtocol.Validate(new AppCommandRequest("unlink", ["blog"])) is null, "unlink accepts a site name");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("unlink", [@"C:\Projects\blog"])) is null, "unlink accepts a full path");
        Check(AppCommandProtocol.Validate(new AppCommandRequest("unlink", [@"..\blog"])) is not null, "unlink rejects relative paths");
        var missingArguments = AppCommandProtocol.DeserializeRequest("{\"command\":\"status\",\"version\":1}");
        Check(
            missingArguments is not null && AppCommandProtocol.Validate(missingArguments) is null,
            "a request without an arguments array is treated as no arguments"
        );

        Check(AppCommandProtocol.IsFullyQualifiedWindowsPath(@"C:\Projects"), "drive paths are full paths");
        Check(AppCommandProtocol.IsFullyQualifiedWindowsPath(@"\\server\share\site"), "UNC paths are full paths");
        Check(!AppCommandProtocol.IsFullyQualifiedWindowsPath(@"\\?\C:\Projects"), "device paths are rejected");
        Check(!AppCommandProtocol.IsFullyQualifiedWindowsPath(@"\\.\pipe\x"), "pipe paths are rejected");
        Check(!AppCommandProtocol.IsFullyQualifiedWindowsPath(@"C:Projects"), "drive-relative paths are rejected");
        Check(!AppCommandProtocol.IsFullyQualifiedWindowsPath(@"Projects\blog"), "relative paths are rejected");
    }

    private static void VerifyCliArguments()
    {
        const string current = @"C:\Projects\blog";
        var help = AppCommandProtocol.ParseCliArguments([], current, "1.2.3");
        Check(help.Request is null && help.ExitCode == 0 && help.LocalOutput!.Contains("herdme <command>"), "herdme without arguments prints help");
        var version = AppCommandProtocol.ParseCliArguments(["--version"], current, "1.2.3");
        Check(version.LocalOutput == "herdme 1.2.3" && version.ExitCode == 0, "herdme --version prints the version locally");
        var link = AppCommandProtocol.ParseCliArguments(["link"], current, "1.2.3");
        Check(link.Request is { Command: "link", Interactive: false } && link.Request.Arguments.SequenceEqual([current]), "herdme link defaults to the current folder");
        var unlink = AppCommandProtocol.ParseCliArguments(["unlink", "blog"], current, "1.2.3");
        Check(unlink.Request is not null && unlink.Request.Arguments.SequenceEqual(["blog"]), "herdme unlink <name> keeps the site name");
        var unknown = AppCommandProtocol.ParseCliArguments(["frobnicate"], current, "1.2.3");
        Check(unknown.Request is null && unknown.ExitCode == AppCommandProtocol.UsageExitCode, "unknown CLI commands exit with the usage code");
        var missing = AppCommandProtocol.ParseCliArguments(["open"], current, "1.2.3");
        Check(missing.Request is null && missing.ExitCode == AppCommandProtocol.UsageExitCode, "herdme open needs a site");
        if (OperatingSystem.IsWindows())
        {
            var relative = AppCommandProtocol.ParseCliArguments(["link", @"..\shop\"], current, "1.2.3");
            Check(
                relative.Request is not null && relative.Request.Arguments.SequenceEqual([@"C:\Projects\shop"]),
                "herdme link resolves relative folders against the current folder"
            );
        }
        foreach (var command in AppCommandProtocol.Commands)
        {
            Check(AppCommandProtocol.HelpText("1").Contains(command, StringComparison.Ordinal), $"herdme help lists {command}");
        }
    }

    private static async Task VerifyCommandFramingAsync()
    {
        var request = new AppCommandRequest("open", ["blog"]);
        var bytes = AppCommandProtocol.Serialize(request);
        Check(bytes[^1] == (byte)'\n' && Array.IndexOf(bytes, (byte)'\n') == bytes.Length - 1, "a command request is one JSON line");
        var decoded = AppCommandProtocol.DeserializeRequest(Encoding.UTF8.GetString(bytes).TrimEnd('\n'));
        Check(decoded is { Command: "open", Version: AppCommandProtocol.ProtocolVersion } && decoded.Arguments.SequenceEqual(["blog"]), "command requests round-trip");
        var response = AppCommandProtocol.DeserializeResponse(
            Encoding.UTF8.GetString(AppCommandProtocol.Serialize(AppCommandResponse.Failure("line one\nline two", 3))).TrimEnd('\n')
        );
        Check(response is { Ok: false, ExitCode: 3, Output: "line one\nline two" }, "multi-line output is escaped inside one response line");
        Check(AppCommandResponse.Failure("x", 0).ExitCode == 1, "a failure never reports exit code 0");
        Check(AppCommandProtocol.DeserializeRequest("not json") is null, "malformed requests are ignored");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond"));
        Check(await AppCommandProtocol.ReadLineAsync(stream, CancellationToken.None) == "first", "the pipe reader returns one line");
        Check(stream.Position == 7, "the pipe reader does not consume past the newline");
        Check(await AppCommandProtocol.ReadLineAsync(stream, CancellationToken.None) is null, "an unterminated line reads as end of stream");
        using var large = new MemoryStream(new byte[64]);
        await ThrowsAsync<InvalidDataException>(
            () => AppCommandProtocol.ReadLineAsync(large, CancellationToken.None, 16),
            "the pipe reader rejects oversized messages"
        );
    }

    private static void VerifyCommandShim(string supportRoot)
    {
        var content = HerdMeCommandShim.BuildContent(@"C:\Apps\100%\herdme.exe");
        Check(content.Contains("\"C:\\Apps\\100%%\\herdme.exe\" %*", StringComparison.Ordinal), "the herdme.cmd shim escapes percent signs and forwards arguments");
        Check(content.Contains("\r\n", StringComparison.Ordinal) && content.EndsWith("\r\n", StringComparison.Ordinal), "the herdme.cmd shim uses CRLF lines");
        Check(content.Contains("exit /b %ERRORLEVEL%", StringComparison.Ordinal), "the herdme.cmd shim keeps the exit code");

        var shimRoot = Path.Combine(supportRoot, "command-shim");
        var applicationDirectory = Path.Combine(shimRoot, "app");
        Directory.CreateDirectory(applicationDirectory);
        Check(!HerdMeCommandShim.Ensure(shimRoot, applicationDirectory), "no shim is written when herdme.exe is missing");
        Check(!File.Exists(HerdMeCommandShim.ShimPath(shimRoot)), "a missing herdme.exe leaves no shim behind");
        File.WriteAllText(Path.Combine(applicationDirectory, HerdMeCommandShim.ExecutableName), "fixture");
        Check(HerdMeCommandShim.Ensure(shimRoot, applicationDirectory), "the shim is written next to the other command shims");
        Check(
            File.ReadAllText(HerdMeCommandShim.ShimPath(shimRoot)).Contains(HerdMeCommandShim.ExecutableName, StringComparison.Ordinal),
            "the shim points at herdme.exe"
        );
        Check(HerdMeCommandShim.Ensure(shimRoot, applicationDirectory), "rewriting an unchanged shim succeeds");
        Check(!File.Exists(HerdMeCommandShim.ShimPath(shimRoot) + ".tmp"), "the shim write leaves no temporary file");
    }

    private static void VerifyJumpListEntries()
    {
        foreach (var task in JumpListManager.BuildTasks())
        {
            var request = AppCommandProtocol.ParseLaunchArguments(task.Arguments.Split(' '));
            Check(request is not null, $"Jump List task '{task.Title}' is a valid command");
        }
        var sites = new List<SiteRecord>();
        for (var index = 0; index < 9; index++)
        {
            sites.Add(new SiteRecord { Name = $"site{index}", Domain = $"site{index}.test", Path = $"/p/{index}" });
        }
        sites.Add(new SiteRecord { Name = "zeta", Domain = "zeta.test", Path = "/p/zeta", IsFavorite = true });
        sites.Add(new SiteRecord { Name = "bad name", Domain = "bad.test", Path = "/p/bad", IsFavorite = true });
        var entries = JumpListManager.BuildSites(sites);
        Check(entries.Count == JumpListManager.MaximumSites, "the Jump List shows at most six sites");
        Check(entries[0].Title == "zeta", "favorite sites come first in the Jump List");
        Check(entries.All(entry => entry.Title != "bad name"), "sites whose names are not valid arguments are left out");
        Check(
            entries.All(entry => AppCommandProtocol.ParseLaunchArguments(entry.Arguments.Split(' ')) is { Command: "site" }),
            "Jump List sites relaunch as --command site <name>"
        );
    }

    private static void VerifyCommandSources(string repositoryRoot)
    {
        var windows = Path.Combine(repositoryRoot, "Windows");
        var server = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "Services", "AppCommandServer.cs"));
        Check(server.Contains("PipeOptions.FirstPipeInstance", StringComparison.Ordinal), "the command pipe refuses an existing pipe name");
        Check(server.Contains("WellKnownSidType.NetworkSid", StringComparison.Ordinal)
            && server.Contains("AccessControlType.Deny", StringComparison.Ordinal), "the command pipe denies network logons");
        var client = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "Services", "AppCommandClient.cs"));
        Check(client.Contains("PipeOptions.CurrentUserOnly", StringComparison.Ordinal), "the command client only talks to the current user's pipe");
        var app = File.ReadAllText(Path.Combine(windows, "HerdMe.Windows", "App.xaml.cs"));
        Check(app.Contains("StopAndLogAsync(\"command pipe\", StopCommandServerAsync)", StringComparison.Ordinal), "exit stops the command pipe");
        Check(app.Contains("new SingleInstanceCoordinator(signalActivation: launchRequest is null)", StringComparison.Ordinal), "a forwarded command does not also send a plain activation");
        var cli = File.ReadAllText(Path.Combine(windows, "HerdMe.Cli", "Program.cs"));
        Check(cli.Contains("CreateNoWindow = true", StringComparison.Ordinal) && cli.Contains("UseShellExecute = false", StringComparison.Ordinal), "herdme starts HerdMe without a console window");
        var installer = File.ReadAllText(Path.Combine(windows, "installer.iss"));
        Check(installer.Contains(@"{localappdata}\HerdMe\bin\herdme.cmd", StringComparison.Ordinal), "uninstall removes the herdme.cmd shim");
        Check(installer.Contains("CloseApplicationsFilter=HerdMe.Windows.exe,herdme.exe", StringComparison.Ordinal), "setup closes a running herdme command");
        var portable = File.ReadAllText(Path.Combine(windows, "package-portable.ps1"));
        Check(portable.Contains("\"herdme.exe\"", StringComparison.Ordinal) && portable.Contains("--version", StringComparison.Ordinal), "the portable package ships and runs herdme.exe");
        Check(portable.Contains("/p:PublishReadyToRun=true", StringComparison.Ordinal), "the portable package is ReadyToRun compiled");
    }
}
