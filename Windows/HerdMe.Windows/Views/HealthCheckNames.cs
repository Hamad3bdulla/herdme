using HerdMe.Windows.Services;

namespace HerdMe.Windows.Views;

/// <summary>
/// Site Doctor check names stay English inside services (repairs and refreshes match on them);
/// this maps them to the display language only when they are shown.
/// </summary>
internal static class HealthCheckNames
{
    public static string Display(string name)
    {
        var key = name switch
        {
            "Project" => "DoctorProject",
            "Environment" => "DoctorEnvironment",
            "Dependencies" => "DoctorDependencies",
            "PHP extensions" => "DoctorExtensions",
            "Storage directories" => "DoctorStorage",
            "Application key" => "DoctorKey",
            "Database configuration" => "DoctorDatabase",
            "HTTPS" => "DoctorHttps",
            "Laravel" => "DoctorLaravel",
            "Laravel core files" => "DoctorLaravelCoreFiles",
            "PHP" => "DoctorPhp",
            "Composer" => "DoctorComposer",
            "Composer lock" => "DoctorComposerLock",
            "PHP platform requirements" => "DoctorPhpPlatform",
            "Node lock" => "DoctorNodeLock",
            "Node dependencies" => "DoctorNodeDependencies",
            "Node package manager" => "DoctorNodePackageManager",
            "Node version" => "DoctorNodeVersion",
            "Environment configuration" => "DoctorEnvironmentConfiguration",
            "Storage link" => "DoctorStorageLink",
            "Laravel logs" => "DoctorLaravelLogs",
            _ => null
        };
        return key is null ? name : AppLocalization.Get(key);
    }
}
