using System.Runtime.InteropServices;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>已安装应用条目（AppsFolder 枚举产物）：只保留 AUMID，不存 Package Full Name（#30）。</summary>
internal sealed record InstalledApp(string Aumid, string DisplayName);

/// <summary>
/// 已安装应用枚举（#30，设计定稿 §六.6 的实现期落地路线）：经典 IShellFolder 链——
/// SHGetDesktopFolder → ParseDisplayName("shell:AppsFolder") → BindToObject(IShellFolder)
/// → EnumObjects；逐项 GetDisplayNameOf(FORPARSING) 取解析名（含路径分隔符/:: 前缀的
/// 非打包条目丢弃——文件系统桌面应用经「应用文件…」入口添加）、GetAttributesOf
/// 过滤 SFGAO_FOLDER、GetDisplayNameOf(NORMAL) 取显示名。实现期修订：BindToHandler
/// (BHID_EnumItems) 与属性系统字符串读取（IShellItem2.GetProperty / IPropertyStore）
/// 在实际环境不可依赖（探针实证 2026-10-14，见研究稿「实现期修订」）。结果只在打开
/// 选择器时枚举（会话内缓存由调用方持有）；COM 中途失败返回已收集部分。
/// </summary>
internal static class InstalledAppsEnumerator
{
    internal static IReadOnlyList<InstalledApp> Enumerate()
    {
        var results = new List<InstalledApp>();
        var seenAumids = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.CurrentCulture);

        if (ShellInterop.SHGetDesktopFolder(out var desktopPointer) != 0)
        {
            return results;
        }

        try
        {
            var desktop = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(desktopPointer);
            var attributes = 0u;
            if (desktop.ParseDisplayName(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    "shell:AppsFolder",
                    out _,
                    out var appsPidl,
                    ref attributes) != 0)
            {
                return results;
            }

            try
            {
                var folderId = typeof(ShellInterop.IShellFolder).GUID;
                if (desktop.BindToObject(appsPidl, IntPtr.Zero, folderId, out var appsPointer) != 0)
                {
                    return results;
                }

                try
                {
                    var apps = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(appsPointer);
                    if (apps.EnumObjects(
                            IntPtr.Zero,
                            ShellInterop.ShcontfFolders | ShellInterop.ShcontfNonFolders | ShellInterop.ShcontfIncludeHidden,
                            out var enumPointer) != 0)
                    {
                        return results;
                    }

                    try
                    {
                        var items = (ShellInterop.IEnumIDList)Marshal.GetObjectForIUnknown(enumPointer);
                        while (items.Next(1, out var pidl, out _) == 0)
                        {
                            try
                            {
                                CollectEntry(apps, pidl, seenAumids, seenNames, results);
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(pidl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(enumPointer);
                    }
                }
                finally
                {
                    Marshal.Release(appsPointer);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(appsPidl);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException)
        {
            // 枚举中断：返回已收集部分（AppsFolder 项集在系统间差异大，不视为致命）。
        }
        finally
        {
            Marshal.Release(desktopPointer);
        }

        results.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCulture));
        return results;
    }

    /// <summary>单条收集：解析名分流（无路径分隔符且非 :: 前缀 = AUMID）+ 文件夹过滤 +
    /// 重复 AUMID/别名去重（AUMID 去重登记在全部过滤通过之后——先登记会因后续
    /// 被拒条目污染去重集，把同名合法条目误吞）。</summary>
    private static void CollectEntry(
        ShellInterop.IShellFolder apps,
        IntPtr pidl,
        HashSet<string> seenAumids,
        HashSet<string> seenNames,
        List<InstalledApp> results)
    {
        if (ReadDisplayName(apps, pidl, ShellInterop.ShgdnForParsing) is not { } parsing ||
            !LooksLikeAumid(parsing))
        {
            return;
        }

        var folderAttributes = ShellInterop.SfgaoFolder;
        var pidlArray = Marshal.AllocCoTaskMem(IntPtr.Size);
        int attributesResult;
        try
        {
            Marshal.WriteIntPtr(pidlArray, pidl);
            attributesResult = apps.GetAttributesOf(1, pidlArray, ref folderAttributes);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidlArray);
        }

        if (attributesResult == 0 && (folderAttributes & ShellInterop.SfgaoFolder) != 0)
        {
            return;
        }

        if (ReadDisplayName(apps, pidl, ShellInterop.ShgdnNormal) is not { Length: > 0 } displayName ||
            !seenAumids.Add(parsing) ||
            !seenNames.Add(displayName))
        {
            return;
        }

        results.Add(new InstalledApp(parsing, displayName));
    }

    /// <summary>AUMID 判定：解析名不含路径分隔符（:、\、/）且非 :: 命名空间前缀——
    /// 文件系统桌面应用（C:\…）与 CLSID 项（::{…}）由此排除。</summary>
    private static bool LooksLikeAumid(string parsingName) =>
        parsingName.Length > 1 &&
        parsingName.IndexOfAny([':', '\\', '/']) < 0 &&
        !parsingName.StartsWith("::", StringComparison.Ordinal);

    /// <summary>按 AUMID 取 AppsFolder 条目（图标回退路径）：同枚举链按解析名精确匹配，
    /// 命中后 SHCreateItemFromIDList 包 IShellItem（调用方按需 QI 图标工厂）。
    /// 未命中/失败返回 null。</summary>
    internal static object? TryGetItemByAumid(string appUserModelId)
    {
        if (ShellInterop.SHGetDesktopFolder(out var desktopPointer) != 0)
        {
            return null;
        }

        try
        {
            var desktop = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(desktopPointer);
            var attributes = 0u;
            if (desktop.ParseDisplayName(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    "shell:AppsFolder",
                    out _,
                    out var appsPidl,
                    ref attributes) != 0)
            {
                return null;
            }

            try
            {
                var folderId = typeof(ShellInterop.IShellFolder).GUID;
                if (desktop.BindToObject(appsPidl, IntPtr.Zero, folderId, out var appsPointer) != 0)
                {
                    return null;
                }

                try
                {
                    var apps = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(appsPointer);
                    if (apps.EnumObjects(
                            IntPtr.Zero,
                            ShellInterop.ShcontfFolders | ShellInterop.ShcontfNonFolders | ShellInterop.ShcontfIncludeHidden,
                            out var enumPointer) != 0)
                    {
                        return null;
                    }

                    try
                    {
                        var items = (ShellInterop.IEnumIDList)Marshal.GetObjectForIUnknown(enumPointer);
                        while (items.Next(1, out var pidl, out _) == 0)
                        {
                            try
                            {
                                if (ReadDisplayName(apps, pidl, ShellInterop.ShgdnForParsing) is { } parsing &&
                                    string.Equals(parsing, appUserModelId, StringComparison.Ordinal) &&
                                    ShellInterop.SHCreateItemFromIDList(
                                        pidl,
                                        typeof(ShellInterop.IShellItem).GUID,
                                        out var itemPointer) == 0)
                                {
                                    var item = Marshal.GetObjectForIUnknown(itemPointer);
                                    Marshal.Release(itemPointer);
                                    return item;
                                }
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(pidl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(enumPointer);
                    }
                }
                finally
                {
                    Marshal.Release(appsPointer);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(appsPidl);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(desktopPointer);
        }

        return null;
    }

    /// <summary>按解析名提取命名空间项图标（IExtractIconW 路线，#30 扩展系统位置共用
    /// 机制）：桌面文件夹 ParseDisplayName 命中项经 GetUIObjectOf 取 IExtractIconW，
    /// GetIconLocation + Extract 出 HICON；调用方按 HICON 语义释放（DestroyIcon）。
    /// 失败返回 IntPtr.Zero。</summary>
    internal static IntPtr TryExtractIconForParsingName(string parsingName)
    {
        if (ShellInterop.SHGetDesktopFolder(out var desktopPointer) != 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            var desktop = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(desktopPointer);
            var attributes = 0u;
            if (desktop.ParseDisplayName(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    parsingName,
                    out _,
                    out var itemPidl,
                    ref attributes) != 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                return ExtractIconFromItem(desktop, itemPidl);
            }
            finally
            {
                Marshal.FreeCoTaskMem(itemPidl);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException)
        {
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.Release(desktopPointer);
        }
    }

    /// <summary>按 AUMID 提取 AppsFolder 条目图标（IExtractIconW 路线）：枚举命中项经
    /// 文件夹 GetUIObjectOf 取 IExtractIconW，GetIconLocation + Extract 出 HICON；
    /// 调用方按 HICON 语义释放（DestroyIcon）。失败返回 IntPtr.Zero。</summary>
    internal static IntPtr TryExtractIconForAumid(string appUserModelId)
    {
        if (ShellInterop.SHGetDesktopFolder(out var desktopPointer) != 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            var desktop = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(desktopPointer);
            var attributes = 0u;
            if (desktop.ParseDisplayName(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    "shell:AppsFolder",
                    out _,
                    out var appsPidl,
                    ref attributes) != 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                var folderId = typeof(ShellInterop.IShellFolder).GUID;
                if (desktop.BindToObject(appsPidl, IntPtr.Zero, folderId, out var appsPointer) != 0)
                {
                    return IntPtr.Zero;
                }

                try
                {
                    var apps = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(appsPointer);
                    if (apps.EnumObjects(
                            IntPtr.Zero,
                            ShellInterop.ShcontfFolders | ShellInterop.ShcontfNonFolders | ShellInterop.ShcontfIncludeHidden,
                            out var enumPointer) != 0)
                    {
                        return IntPtr.Zero;
                    }

                    try
                    {
                        var items = (ShellInterop.IEnumIDList)Marshal.GetObjectForIUnknown(enumPointer);
                        while (items.Next(1, out var pidl, out _) == 0)
                        {
                            try
                            {
                                if (ReadDisplayName(apps, pidl, ShellInterop.ShgdnForParsing) is { } parsing &&
                                    string.Equals(parsing, appUserModelId, StringComparison.Ordinal))
                                {
                                    return ExtractIconFromItem(apps, pidl);
                                }
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(pidl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(enumPointer);
                    }
                }
                finally
                {
                    Marshal.Release(appsPointer);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(appsPidl);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException)
        {
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.Release(desktopPointer);
        }

        return IntPtr.Zero;
    }

    /// <summary>命中项取图：GetUIObjectOf(IExtractIconW) → GetIconLocation → Extract。</summary>
    private static IntPtr ExtractIconFromItem(ShellInterop.IShellFolder apps, IntPtr pidl)
    {
        var extractId = typeof(ShellInterop.IExtractIconW).GUID;
        var pidlArray = Marshal.AllocCoTaskMem(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(pidlArray, pidl);
            if (apps.GetUIObjectOf(IntPtr.Zero, 1, pidlArray, extractId, IntPtr.Zero, out var extractPointer) != 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                var extract = (ShellInterop.IExtractIconW)Marshal.GetObjectForIUnknown(extractPointer);
                var iconFile = new System.Text.StringBuilder(260);
                if (extract.GetIconLocation(0, iconFile, iconFile.Capacity, out var iconIndex, out _) != 0)
                {
                    return IntPtr.Zero;
                }

                // nIconSize=MAKELONG(低字,高字) 口径传 256（探针实证可出大图标）。
                if (extract.Extract(
                        iconFile.ToString(),
                        unchecked((uint)iconIndex),
                        out var icon,
                        IntPtr.Zero,
                        (256u << 16)) == 0 &&
                    icon != IntPtr.Zero)
                {
                    return icon;
                }

                return IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(extractPointer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidlArray);
        }
    }

    /// <summary>GetDisplayNameOf 出参读出（STRRET：uType@0，WSTR 联合指针@8）；
    /// WSTR 由 Shell 分配、读后释放。缓冲按 sizeof(STRRET)（union 含 char[260]，
    /// x64 ≈272）分配——Shell 若走 CSTR 内联变体最多写 260 字节，小缓冲会溢出。
    /// 仅消费 WSTR 变体，其余（OFFSET/CSTR）返回 null 跳过该条目。</summary>
    private static string? ReadDisplayName(ShellInterop.IShellFolder apps, IntPtr pidl, uint flags)
    {
        var strRet = Marshal.AllocCoTaskMem(280);
        try
        {
            if (apps.GetDisplayNameOf(pidl, flags, strRet) != 0)
            {
                return null;
            }

            if (Marshal.ReadInt32(strRet) != ShellInterop.StrretWstr)
            {
                return null;
            }

            var stringPointer = Marshal.ReadIntPtr(strRet, 8);
            if (stringPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(stringPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(stringPointer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(strRet);
        }
    }
}
