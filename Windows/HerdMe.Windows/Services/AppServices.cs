namespace HerdMe.Windows.Services;

public static class AppServices
{
    public static WindowsLocalEnvironment Environment { get; } = new();

    public static MailCaptureService Mail { get; } = new();

    public static DumpCaptureService Dumps { get; } = new();

    public static WindowsServiceManager Services { get; } = new();

    public static SiteConfigurationStore SiteSettings { get; } = new();
}
