using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace FloatingTransferStation.Services;

/// <summary>
/// 快捷启动图标与启动链路的 Win32/COM 互操作声明（#30，Issue #162）。
/// 铁则：业务接口在 Core（UI-free），本文件只承载 P/Invoke 与 COM 形状，
/// 不含业务逻辑。InterfaceIsIUnknown 接口必须按真实虚表序完整声明
/// （缺中间方法会使槽位漂移导致崩溃）。GDI 句柄只删一次、保持 x64、
/// 不用 SHGFI_USEFILEATTRIBUTES。
/// </summary>
internal static class ShellInterop
{
    internal const int ShellIconSize = 256;

    // SIIGBF: ICONONLY=只取图标；BIGGERSIZEOK=允许超过请求尺寸；MEMORYONLY=网络路径防挂起。
    internal const uint SiigbfIconOnly = 0x4;
    internal const uint SiigbfBiggerSizeOk = 0x8;
    internal const uint SiigbfMemoryOnly = 0x10;

    internal const uint ShgfiSysiconIndex = 0x4000;
    internal const uint ShgfiLargeIcon = 0x0;

    internal const int ShilLarge = 0;
    internal const int ShilExtraLarge = 2;
    internal const int ShilJumbo = 4;

    internal const int IldTransparent = 0x1;

    // SFGAO_FOLDER（已安装应用枚举过滤文件夹项）。
    internal const uint SfgaoFolder = 0x20000000;

    // GetDisplayNameOf 旗标：NORMAL=显示名；FORPARSING=解析名（AppsFolder 条目的
    // 解析名即 AUMID / 文件系统路径，按是否含路径字符分流）。
    internal const uint ShgdnNormal = 0x0;
    internal const uint ShgdnForParsing = 0x8000;

    // EnumObjects 旗标（全枚举后按 AUMID/文件夹过滤）。
    internal const uint ShcontfFolders = 0x20;
    internal const uint ShcontfNonFolders = 0x40;
    internal const uint ShcontfIncludeHidden = 0x80;

    // STRRET.uType（uType 在 offset 0；WSTR 联合指针在 offset 8，CoTaskMem 释放）。
    internal const int StrretWstr = 0;

    internal static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    internal static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10DF-4D6B-9DB5-40755FE23494");
    internal static readonly Guid BhidEnumItems = new("94f60526-8d9e-4e25-94f1-e7fc8ee2d808");
    // PKEY_AppUserModel_ID / PKEY_ItemNameDisplay（已安装应用枚举与 AUMID 图标回退）。
    internal static readonly PropertyKey AppUserModelId = new(
        new Guid(unchecked((int)0x9F4C2855), unchecked((short)0x9F79), 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3), 5);
    internal static readonly PropertyKey ItemNameDisplay = new(
        new Guid(unchecked((int)0xB725F130), 0x47EF, 0x101A, 0xA5, 0xF1, 0x02, 0x60, 0x8C, 0x9E, 0xEB, 0xAC), 10);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHCreateItemFromParsingName(
        string parsingName,
        IntPtr bindingContext,
        in Guid interfaceId,
        out IntPtr shellItem);

    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IntPtr desktopFolder);

    [DllImport("shell32.dll")]
    internal static extern int SHCreateItemFromIDList(
        IntPtr itemIdList,
        in Guid interfaceId,
        out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        int fileInfoSize,
        uint flags);

    [DllImport("shell32.dll")]
    internal static extern int SHGetImageList(int imageListId, in Guid interfaceId, out IImageList imageList);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        public int Width;
        public int Height;

        public Size(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>AUMID 激活选项（AO_NONE=0）。</summary>
    internal enum ActivateOptions : uint
    {
        None = 0x0
    }

    // IShellItem 虚表序：BindToHandler, GetParent, GetDisplayName, GetAttributes。
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindingContext, in Guid handlerId, in Guid interfaceId, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(int displayKind, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint attributesMask, out uint attributes);
    }

    // IShellItem2 追加虚表序：GetPropertyStore, GetPropertyStoreWithCreateObject,
    // GetPropertyStoreForKeys, GetPropertyDescriptionList, Update, GetPropertyHandler,
    // GetCLSID, GetFileSysPath, GetIDList, GetProperty。
    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem2 : IShellItem
    {
        [PreserveSig]
        int GetPropertyStore(IntPtr flags, in Guid interfaceId, out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyStoreWithCreateObject(IntPtr flags, IntPtr createObject, in Guid interfaceId, out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyStoreForKeys(in PropertyKey keys, int keyCount, IntPtr flags, in Guid interfaceId, out IntPtr propertyStore);

        [PreserveSig]
        int GetPropertyDescriptionList(in PropertyKey keyType, in Guid interfaceId, out IntPtr descriptionList);

        [PreserveSig]
        int Update(IntPtr bindingContext);

        [PreserveSig]
        int GetPropertyHandler(IntPtr reserved, in Guid interfaceId, out IntPtr propertyHandler);

        [PreserveSig]
        int GetCLSID(in PropertyKey key, out Guid classId);

        [PreserveSig]
        int GetFileSysPath(int displayKind, out IntPtr path);

        [PreserveSig]
        int GetIDList(out IntPtr itemIdList);

        [PreserveSig]
        int GetProperty(in PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, uint flags, out IntPtr bitmapHandle);
    }

    // IImageList 虚表序（commctrl.h）：Add, ReplaceIcon, SetOverlayImage, Replace,
    // AddMasked, Draw, Remove, GetIcon, GetImageInfo, Copy, Merge, Clone,
    // GetImageRect, GetIconSize, SetIconSize, GetImageCount, SetImageCount, ...
    [ComImport]
    [Guid("46eb5926-582e-4017-9fdf-e899822aa395")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr bitmap, IntPtr bitmapMask, out int index);

        [PreserveSig]
        int ReplaceIcon(int index, IntPtr icon, out int replacedIndex);

        [PreserveSig]
        int SetOverlayImage(int imageIndex, int overlayIndex);

        [PreserveSig]
        int Replace(int index, IntPtr bitmap, IntPtr bitmapMask);

        [PreserveSig]
        int AddMasked(IntPtr bitmap, uint colorMask, out int index);

        [PreserveSig]
        int Draw(IntPtr drawParameters);

        [PreserveSig]
        int Remove(int index);

        [PreserveSig]
        int GetIcon(int imageIndex, int flags, out IntPtr iconHandle);

        [PreserveSig]
        int GetImageInfo(int imageIndex, IntPtr imageInfo);

        [PreserveSig]
        int Copy(int destinationIndex, IImageList sourceImageList, int sourceIndex, uint flags);

        [PreserveSig]
        int Merge(int firstIndex, IImageList firstImageList, int secondIndex, IImageList secondImageList, int dx, int dy, in Guid interfaceId, out IntPtr merged);

        [PreserveSig]
        int Clone(in Guid interfaceId, out IImageList clone);

        [PreserveSig]
        int GetImageRect(int index, IntPtr rectangle);

        [PreserveSig]
        int GetIconSize(out int width, out int height);

        [PreserveSig]
        int SetIconSize(int width, int height);

        [PreserveSig]
        int GetImageCount(out int count);

        [PreserveSig]
        int SetImageCount(int count);

        [PreserveSig]
        int GetBackgroundColor(out uint color);

        [PreserveSig]
        int SetBackgroundColor(uint color);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumShellItems
    {
        [PreserveSig]
        int Next(int count, out IShellItem items, out int fetched);

        [PreserveSig]
        int Skip(int count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumShellItems enumerator);
    }

    // IShellFolder 虚表序（shobjidl_core.h 10 方法）：ParseDisplayName, EnumObjects,
    // BindToObject, BindToStorage, CompareIDs, CreateViewObject, GetAttributesOf,
    // GetUIObjectOf, GetDisplayNameOf, SetNameOf。已安装应用枚举/AUMID 匹配走此经典
    // 路线（BindToHandler(BHID_EnumItems) 在实际环境返回 MK_E_NOOBJECT 不可依赖）。
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr ownerWindow,
            IntPtr bindingContext,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            out uint charactersEaten,
            out IntPtr itemIdList,
            ref uint attributes);

        [PreserveSig]
        int EnumObjects(IntPtr ownerWindow, uint flags, out IntPtr enumIdList);

        [PreserveSig]
        int BindToObject(IntPtr itemIdList, IntPtr bindingContext, in Guid interfaceId, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr itemIdList, IntPtr bindingContext, in Guid interfaceId, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr parameter, IntPtr itemIdList1, IntPtr itemIdList2);

        [PreserveSig]
        int CreateViewObject(IntPtr ownerWindow, in Guid interfaceId, out IntPtr ppv);

        // PCUITEMID_CHILD_ARRAY＝PIDL 指针数组：调用方手工构造（Marshal.AllocCoTaskMem
        // + WriteIntPtr）；数组参数的 CLR 默认编组在此接口上会崩溃（探针实证）。
        [PreserveSig]
        int GetAttributesOf(uint itemCount, IntPtr itemIdListArray, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(IntPtr ownerWindow, uint itemCount, IntPtr itemIdListArray, in Guid interfaceId, IntPtr reserved, out IntPtr ppv);

        // STRRET 出参（uType 在 offset 0，STRRET_WSTR=0，union 指针在 offset 8）：
        // 调用方传 ≥16 字节缓冲并负责释放 WSTR。
        [PreserveSig]
        int GetDisplayNameOf(IntPtr itemIdList, uint flags, IntPtr strRet);

        [PreserveSig]
        int SetNameOf(IntPtr ownerWindow, IntPtr itemIdList, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newItemIdList);
    }

    [ComImport]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint count, out IntPtr itemIdList, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IntPtr enumIdList);
    }

    // IExtractIconW 虚表序（shobjidl_core.h）：GetIconLocation, Extract（仅此两法）。
    // AppsFolder 条目的图标提取链（条目不实现 IShellItemImageFactory，探针实证 2026-10-14）。
    [ComImport]
    [Guid("000214FA-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExtractIconW
    {
        [PreserveSig]
        int GetIconLocation(
            uint flags,
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconFile,
            int capacity,
            out int iconIndex,
            out uint outputFlags);

        [PreserveSig]
        int Extract(
            [MarshalAs(UnmanagedType.LPWStr)] string file,
            uint iconIndex,
            out IntPtr largeIcon,
            IntPtr smallIcon,
            uint iconSize);
    }

    // IShellLinkW 虚表序（shobjidl_core.h）：GetPath, GetIDList, SetIDList,
    // GetDescription, SetDescription, GetWorkingDirectory, SetWorkingDirectory,
    // GetArguments, SetArguments, GetHotKey, SetHotKey, GetShowCmd, SetShowCmd,
    // GetIconLocation, SetIconLocation, SetRelativePath, Resolve, SetPath。
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder filePath,
            int bufferSize,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr itemIdList);

        [PreserveSig]
        int SetIDList(IntPtr itemIdList);

        [PreserveSig]
        int GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder description, int bufferSize);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        [PreserveSig]
        int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder workingDirectory, int bufferSize);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string workingDirectory);

        [PreserveSig]
        int GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int bufferSize);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotKey(out short hotKey);

        [PreserveSig]
        int SetHotKey(short hotKey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath,
            int bufferSize,
            out int iconIndex);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr windowHandle, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string filePath);
    }

    // IPersistFile 虚表序：GetClassID(IPersist), IsDirty, Load, Save, SaveCompleted, GetCurFile。
    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    /// <summary>打包应用激活（CoCreateInstance CLSID_ApplicationActivationManager）。</summary>
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out int processId);

        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            ActivateOptions options,
            out int processId);

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out int processId);
    }

    /// <summary>PROPVARIANT 最小面（字符串值）：AppsFolder 枚举读 AUMID/显示名。</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        public short VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public const short VtLpwstr = 31;

        public string? AsString()
        {
            if (VariantType != VtLpwstr || PointerValue == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(PointerValue);
        }

        public void Clear()
        {
            if (VariantType == VtLpwstr && PointerValue != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(PointerValue);
                PointerValue = IntPtr.Zero;
            }

            VariantType = 0;
        }
    }
}
