using System.Runtime.InteropServices;
using System.Text;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Platform.Windows;

public static class WindowsRecentGames
{
    public static string AppId(NativeProfileStore profile) => profile.Manifest.ProfileKind == NativeProfileKind.Production ? "dev.local.kungalmanager" : "dev.local.kungalmanager.test." + profile.Manifest.ProfileId.ToString("N");
    public static void SetAppId(NativeProfileStore profile) => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId(profile)));
    public static string[] Split(string value)
    {
        if (value.Length > 32768 || value.Contains('\0')) return [];
        var memory = CommandLineToArgvW("app " + value, out var count); if (memory == IntPtr.Zero) return [];
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(memory); }
    }
    public static void Update(NativeProfileStore profile, IEnumerable<LibraryEntry> games)
    {
        profile.ValidateAccess(); var appId = AppId(profile);
        var list = (IDestinationList)Activator.CreateInstance(Type.GetTypeFromCLSID(new("77f10cf0-3db5-4966-b520-b7c54fd35ed6"))!)!;
        object? collection = null, removed = null; var links = new List<object>(); bool begun = false;
        try
        {
            list.SetAppID(appId); var iid = typeof(IObjectArray).GUID; list.BeginList(out _, ref iid, out removed); begun = true;
            var blocked = new HashSet<string>(); var old = (IObjectArray)removed; old.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                var linkIid = typeof(IShellLink).GUID;
                try { old.GetAt(i, ref linkIid, out var item); try { var args = new StringBuilder(32768); ((IShellLink)item).GetArguments(args, args.Capacity); blocked.Add(args.ToString()); } finally { Marshal.ReleaseComObject(item); } } catch (COMException) { }
            }
            collection = Activator.CreateInstance(Type.GetTypeFromCLSID(new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"))!)!;
            var objects = (IObjectCollection)collection;
            foreach (var game in RecentGameActivation.Recent(games))
            {
                var activation = new RecentGameActivation(profile.Manifest.ProfileId, profile.Manifest.StoreId, game.Id, profile.Manifest.ProfileKind.ToString(), profile.Root);
                var args = string.Join(' ', activation.Arguments.Select(WindowsProcessLifetimeScope.Quote)); if (blocked.Contains(args)) continue;
                var shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new("00021401-0000-0000-c000-000000000046"))!)!; links.Add(shell);
                var link = (IShellLink)shell; link.SetPath(Environment.ProcessPath!); link.SetArguments(args); link.SetWorkingDirectory(AppContext.BaseDirectory);
                link.SetDescription("继续游玩 " + game.Name); link.SetIconLocation(Path.Combine(AppContext.BaseDirectory, "Assets", WindowsBrandIcons.SystemLight() ? "Brand.Light.ico" : "Brand.Dark.ico"), 0);
                var key = new PropertyKey { Format = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), Id = 2 }; var name = game.Name.Length > 200 ? game.Name[..200] : game.Name;
                using var title = new Variant(name); ((IPropertyStore)shell).SetValue(ref key, ref title.Value); ((IPropertyStore)shell).Commit(); objects.AddObject(shell);
            }
            objects.GetCount(out var added); if (added > 0) list.AppendCategory("最近游玩", (IObjectArray)collection);
            list.CommitList(); begun = false;
        }
        finally { if (begun) { try { list.AbortList(); } catch { } } foreach (var link in links) Marshal.ReleaseComObject(link); if (collection is not null) Marshal.ReleaseComObject(collection); if (removed is not null) Marshal.ReleaseComObject(removed); Marshal.ReleaseComObject(list); }
    }
    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string id); void BeginList(out uint slots, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string name, IObjectArray items); void AppendKnownCategory(int type); void AddUserTasks(IObjectArray tasks); void CommitList();
        void GetRemovedDestinations(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object items); void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string id); void AbortList();
    }
    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IObjectArray
    { void GetCount(out uint count); void GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item); }
    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IObjectCollection
    { void GetCount(out uint count); void GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item); void AddObject([MarshalAs(UnmanagedType.IUnknown)] object item); void AddFromArray(IObjectArray array); void RemoveObjectAt(uint index); void Clear(); }
    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IShellLink
    {
        void GetPath(StringBuilder path, int size, IntPtr data, uint flags); void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription(StringBuilder value, int size); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory(StringBuilder value, int size); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short key); void SetHotkey(short key); void GetShowCmd(out int cmd); void SetShowCmd(int cmd);
        void GetIconLocation(StringBuilder value, int size, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved); void Resolve(IntPtr window, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    private sealed class Variant : IDisposable { public PropVariant Value; public Variant(string text) { Value.Type = 31; Value.Pointer = Marshal.StringToCoTaskMemUni(text); } public void Dispose() => Marshal.FreeCoTaskMem(Value.Pointer); }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPropertyStore
    { void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit(); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
