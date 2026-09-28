using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record JumpListEntry(string Title, string Arguments, string Description);

// Builds the taskbar Jump List: fixed tasks, a "Recent" category (sites opened last) and a
// "Sites" category (favorites first, without the recent ones).
// Every entry relaunches HerdMe.Windows.exe with --command, which the running instance
// receives over the command pipe, so a click never starts a second copy of HerdMe.
public static class JumpListManager
{
    public const int MaximumSites = 6;

    public static IReadOnlyList<JumpListEntry> BuildTasks()
    {
        return
        [
            new JumpListEntry(
                ServiceText.Get("JumpListStartAll", "Start all"),
                "--command start",
                ServiceText.Get("JumpListStartAllDescription", "Start the environment and enabled services")
            ),
            new JumpListEntry(
                ServiceText.Get("JumpListStopAll", "Stop all"),
                "--command stop",
                ServiceText.Get("JumpListStopAllDescription", "Stop the environment and all services")
            ),
            new JumpListEntry(
                ServiceText.Get("JumpListSites", "Sites"),
                "--command show sites",
                ServiceText.Get("JumpListSitesDescription", "Show the Sites page")
            ),
            new JumpListEntry(
                ServiceText.Get("JumpListTinker", "Tinker"),
                "--command show tinker",
                ServiceText.Get("JumpListTinkerDescription", "Open Tinker in the Sites page")
            )
        ];
    }

    public const int MaximumRecent = 4;

    // The sites opened last, newest first (RecentSitesStore). Unknown paths are skipped.
    public static IReadOnlyList<JumpListEntry> BuildRecent(IEnumerable<SiteRecord> sites, IEnumerable<string> recentPaths)
    {
        var known = sites
            .Where(site => AppCommandProtocol.IsSiteName(site.Name))
            .GroupBy(site => site.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return recentPaths
            .Where(known.ContainsKey)
            .Select(path => known[path])
            .DistinctBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecent)
            .Select(site => new JumpListEntry(site.Name, $"--command site {site.Name}", site.Domain))
            .ToList();
    }

    // Sites whose names are not valid command arguments are left out rather than escaped.
    // Sites already in the Recent category are not repeated.
    public static IReadOnlyList<JumpListEntry> BuildSites(
        IEnumerable<SiteRecord> sites,
        IReadOnlyCollection<JumpListEntry>? recent = null
    )
    {
        var shown = (recent ?? Array.Empty<JumpListEntry>()).Select(entry => entry.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sites
            .Where(site => AppCommandProtocol.IsSiteName(site.Name))
            .Where(site => !shown.Contains(site.Name))
            .OrderByDescending(site => site.IsFavorite)
            .ThenBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSites)
            .Select(site => new JumpListEntry(
                site.Name,
                $"--command site {site.Name}",
                site.Domain
            ))
            .ToList();
    }

    public static string SitesCategory => ServiceText.Get("JumpListSitesCategory", "Sites");

    public static string RecentCategory => ServiceText.Get("JumpListRecentCategory", "Recent");

    // Returns false when the shell rejected the list (Explorer restarting, policy disabled).
    [SupportedOSPlatform("windows")]
    public static bool Apply(string executable, IEnumerable<SiteRecord> sites, IEnumerable<string>? recentPaths = null)
    {
        var siteList = sites.ToList();
        object? list = null;
        try
        {
            list = new DestinationList();
            var destinations = (ICustomDestinationList)list;
            var removedGuid = typeof(IObjectArray).GUID;
            destinations.BeginList(out _, ref removedGuid, out var removed);
            ReleaseComObject(removed);

            var recentEntries = BuildRecent(siteList, recentPaths ?? Array.Empty<string>());
            AppendCategory(destinations, executable, RecentCategory, recentEntries);
            AppendCategory(destinations, executable, SitesCategory, BuildSites(siteList, recentEntries));

            var taskCollection = CreateCollection(executable, BuildTasks());
            try
            {
                destinations.AddUserTasks((IObjectArray)taskCollection);
            }
            finally
            {
                ReleaseComObject(taskCollection);
            }
            destinations.CommitList();
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            try
            {
                (list as ICustomDestinationList)?.AbortList();
            }
            catch (COMException)
            {
            }
            return false;
        }
        finally
        {
            ReleaseComObject(list);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AppendCategory(
        ICustomDestinationList destinations,
        string executable,
        string category,
        IReadOnlyList<JumpListEntry> entries
    )
    {
        if (entries.Count == 0) return;
        var collection = CreateCollection(executable, entries);
        try
        {
            destinations.AppendCategory(category, (IObjectArray)collection);
        }
        catch (COMException)
        {
            // E_ACCESSDENIED when the user turned off recent items; tasks still work.
        }
        finally
        {
            ReleaseComObject(collection);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object CreateCollection(string executable, IEnumerable<JumpListEntry> entries)
    {
        object collection = new EnumerableObjectCollection();
        var objects = (IObjectCollection)collection;
        foreach (var entry in entries)
        {
            var link = CreateLink(executable, entry);
            try
            {
                objects.AddObject(link);
            }
            finally
            {
                ReleaseComObject(link);
            }
        }
        return collection;
    }

    [SupportedOSPlatform("windows")]
    private static object CreateLink(string executable, JumpListEntry entry)
    {
        object link = new ShellLink();
        var shellLink = (IShellLinkW)link;
        shellLink.SetPath(executable);
        shellLink.SetArguments(entry.Arguments);
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(executable) ?? string.Empty);
        shellLink.SetIconLocation(executable, 0);
        shellLink.SetDescription(entry.Description);

        var store = (IPropertyStore)link;
        var key = TitleKey;
        var value = new PropVariant { ValueType = StringValueType, Pointer = Marshal.StringToCoTaskMemUni(entry.Title) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }
        return link;
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private const ushort StringValueType = 31;

    // PKEY_Title: the text the shell shows for a Jump List link.
    private static readonly PropertyKey TitleKey = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // PROPVARIANT is 24 bytes on x64; only the type and the string pointer are used here.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort ValueType;

        [FieldOffset(8)]
        public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private sealed class DestinationList;

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private sealed class EnumerableObjectCollection;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minimumSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);
        void AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object value);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCAB9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int maximumPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription(IntPtr description, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(IntPtr directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(IntPtr iconPath, int maximumPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
