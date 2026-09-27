using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

namespace HerdMe.Windows.Pages;

// Recently run commands per site (Config\command-history.json) and the cached Artisan
// command list (Cache\artisan-commands.json) used by the command dialogs' autocomplete.
public sealed partial class SitesPage
{
    private SiteCommandFavoritesStore? commandHistoryStore;
    private ArtisanCommandCache? artisanCommandCacheStore;

    private SiteCommandFavoritesStore CommandHistory =>
        commandHistoryStore ??= SiteCommandFavoritesStore.History(settingsStore.SupportRoot);

    private ArtisanCommandCache ArtisanCache =>
        artisanCommandCacheStore ??= new ArtisanCommandCache(settingsStore.SupportRoot);

    private IReadOnlyList<string> LoadRecentCommands(SiteRecord site, string tool)
    {
        try
        {
            return CommandHistory.Load(site.Path, tool).Select(item => item.Command).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    // Returns the updated list so the open dialog can show the new entry straight away.
    private IReadOnlyList<string> RememberCommand(SiteRecord site, string tool, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return LoadRecentCommands(site, tool);
        try
        {
            CommandHistory.Add(site.Path, tool, command);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // History is a convenience; a failed write never blocks running the command.
        }
        return LoadRecentCommands(site, tool);
    }
}
