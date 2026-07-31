namespace HerdMe.Windows.Services;

internal static class ApplicationDiagnostics
{
    internal static Task<bool> WriteEnvironmentStartupFailureAsync(
        Exception error,
        string? supportRoot = null
    )
    {
        return DiagnosticLog.WriteFailureAsync(
            "environment",
            "automatic-start",
            "The configured local site environment could not start automatically.",
            error.ToString(),
            supportRoot,
            context: new Dictionary<string, string?> { ["phase"] = "startup" }
        );
    }

    internal static Task<bool> WriteEnvironmentRecoveryFailureAsync(
        Exception error,
        string? supportRoot = null
    )
    {
        return DiagnosticLog.WriteFailureAsync(
            "environment",
            "automatic-recovery",
            "The local site environment could not recover automatically.",
            error.ToString(),
            supportRoot,
            context: new Dictionary<string, string?> { ["phase"] = "recovery" }
        );
    }

    internal static Task<bool> WriteBackgroundServiceStartupFailureAsync(
        string component,
        Exception error,
        string? supportRoot = null
    )
    {
        var normalizedComponent = string.IsNullOrWhiteSpace(component)
            ? "background service"
            : component.Trim();
        return DiagnosticLog.WriteFailureAsync(
            "background-services",
            "automatic-start",
            $"The {normalizedComponent} component could not start automatically.",
            error.ToString(),
            supportRoot,
            normalizedComponent,
            new Dictionary<string, string?> { ["component"] = normalizedComponent }
        );
    }

    internal static Task<bool> WriteManagedServiceStartupFailureAsync(
        Guid serviceId,
        string serviceName,
        Exception error,
        string? supportRoot = null
    )
    {
        var normalizedName = string.IsNullOrWhiteSpace(serviceName)
            ? "Managed service"
            : serviceName.Trim();
        return DiagnosticLog.WriteFailureAsync(
            "managed-services",
            "automatic-start",
            $"{normalizedName} could not start automatically.",
            error.ToString(),
            supportRoot,
            serviceId.ToString("D"),
            new Dictionary<string, string?>
            {
                ["serviceId"] = serviceId.ToString("D"),
                ["serviceName"] = normalizedName
            }
        );
    }

    internal static Task<bool> WriteUnhandledExceptionAsync(
        Exception error,
        string? supportRoot = null
    )
    {
        return DiagnosticLog.WriteFailureAsync(
            "application",
            "unhandled-exception",
            "HerdMe encountered a recoverable unhandled exception.",
            error.ToString(),
            supportRoot,
            context: new Dictionary<string, string?>
            {
                ["exceptionType"] = error.GetType().FullName
            }
        );
    }

    internal static Task<bool> WriteAutomaticUpdateCheckFailureAsync(
        Exception error,
        string? supportRoot = null
    )
    {
        return DiagnosticLog.WriteFailureAsync(
            "application-update",
            "automatic-check",
            "HerdMe could not check for application updates automatically.",
            error.ToString(),
            supportRoot,
            context: new Dictionary<string, string?> { ["phase"] = "startup" }
        );
    }

    internal static Task<bool> WriteManagedComponentUpdateCheckFailureAsync(
        IReadOnlyList<ManagedComponentUpdateFailure> failures,
        string? supportRoot = null
    )
    {
        var components = string.Join(", ", failures.Select(failure => failure.Component));
        var error = new AggregateException(failures.Select(failure => failure.Error));
        return DiagnosticLog.WriteFailureAsync(
            "component-updates",
            "automatic-check",
            "HerdMe could not check every managed component for updates automatically.",
            error.ToString(),
            supportRoot,
            context: new Dictionary<string, string?>
            {
                ["phase"] = "startup",
                ["failedComponents"] = components,
                ["failureCount"] = failures.Count.ToString()
            }
        );
    }
}
