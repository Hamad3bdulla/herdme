using HerdMe.Windows.Services;

namespace HerdMe.Windows.Pages;

// Fix-it cards: when sites are down because another program holds 443 or 80, say which
// program it is and offer Retry, instead of a generic "could not start".
public sealed partial class DashboardPage
{
    private static readonly int[] WebPorts = [443, 80];

    // Warning text -> detail line. Empty while the environment runs (it owns the ports).
    private Dictionary<string, string> WebPortConflictWarnings()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment.IsRunning || environment.IsDegraded) return result;
        foreach (var port in WebPorts)
        {
            PortConflictDetails conflict;
            try
            {
                conflict = PortConflictInspector.Inspect(port);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or System.Net.NetworkInformation.NetworkInformationException)
            {
                continue;
            }
            if (!conflict.InUse || conflict.ProcessId == System.Environment.ProcessId) continue;
            var owner = string.IsNullOrWhiteSpace(conflict.ProcessName)
                ? AppLocalization.Format(
                    "Dashboard_Repair_UnknownProcess",
                    conflict.ProcessId?.ToString() ?? AppLocalization.Get("Dashboard_Repair_UnknownProcessId")
                )
                : conflict.ProcessId is { } id
                    ? AppLocalization.Format("DashboardFixPortOwner", conflict.ProcessName, id)
                    : conflict.ProcessName;
            result.TryAdd(
                AppLocalization.Format("DashboardFixPortBusy", port, owner),
                AppLocalization.Format("DashboardFixPortBusyDetail", port)
            );
        }
        return result;
    }
}
