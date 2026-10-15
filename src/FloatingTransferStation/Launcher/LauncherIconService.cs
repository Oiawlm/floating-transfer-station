using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>.lnk 解析结果（图标戳指纹与导入默认名共用；参数原样不 split）。</summary>
internal sealed record ShellLinkInfo(
    string? ResolvedTargetPath,
    string? Arguments,
    string? WorkingDirectory,
    string? IconLocation,
    int IconIndex,
    string? Description);

/// <summary>
/// Shell 快捷方式解析（#30）：IShellLinkW + IPersistFile 仅用于展示解析与指纹
/// （GetPath 32768 缓冲、GetArguments/GetWorkingDirectory/GetIconLocation/GetDescription）；
/// RPC_E_CHANGED_MODE 显式失败（调用方必须处于 STA）。启动 .lnk 一律走本体
/// UseShellExecute（覆盖 MSI 广告快捷方式），不经本解析。
/// </summary>
internal static class ShellLinkHelper
{
    private const int MaxPathBuffer = 32768;
    private const uint StgmRead = 0x0;

    internal static ShellLinkInfo? TryRead(string shortcutPath)
    {
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(ShellInterop.ClsidShellLink);
            if (shellLinkType is null)
            {
                return null;
            }

            if (Activator.CreateInstance(shellLinkType) is not ShellInterop.IShellLinkW shellLink ||
                shellLink is not ShellInterop.IPersistFile persistFile ||
                persistFile.Load(shortcutPath, StgmRead) != 0)
            {
                return null;
            }

            var target = new StringBuilder(MaxPathBuffer);
            var arguments = new StringBuilder(MaxPathBuffer);
            var workingDirectory = new StringBuilder(MaxPathBuffer);
            var iconLocation = new StringBuilder(MaxPathBuffer);
            var description = new StringBuilder(1024);
            _ = shellLink.GetPath(target, MaxPathBuffer, IntPtr.Zero, 0);
            _ = shellLink.GetArguments(arguments, MaxPathBuffer);
            _ = shellLink.GetWorkingDirectory(workingDirectory, MaxPathBuffer);
            _ = shellLink.GetIconLocation(iconLocation, MaxPathBuffer, out var iconIndex);
            _ = shellLink.GetDescription(description, 1024);
            return new ShellLinkInfo(
                target.Length > 0 ? target.ToString() : null,
                arguments.Length > 0 ? arguments.ToString() : null,
                workingDirectory.Length > 0 ? workingDirectory.ToString() : null,
                iconLocation.Length > 0 ? iconLocation.ToString() : null,
                iconIndex,
                description.Length > 0 ? description.ToString() : null);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>导入默认名：.lnk 取描述（缺失回退文件名去扩展），其余取文件名去扩展（≤64 截断）。</summary>
    internal static string DefaultDisplayName(string filePath)
    {
        string name;
        if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var description = TryRead(filePath)?.Description;
            name = string.IsNullOrWhiteSpace(description)
                ? Path.GetFileNameWithoutExtension(filePath)
                : description;
        }
        else
        {
            name = Path.GetFileNameWithoutExtension(filePath);
        }

        return LauncherItem.LimitDisplayName(name);
    }
}

/// <summary>
/// 快捷启动图标服务（#30，Issue #162）：专用 STA WPF Dispatcher 线程承载全部 Shell COM
/// （自带消息泵，满足 STA 重入）。主路径 SHCreateItemFromParsingName →
/// IShellItemImageFactory.GetImage(256, ICONONLY|BIGGERSIZEOK)（网络路径加 MEMORYONLY）；
/// 回退链 SHGetFileInfo(SHGFI_SYSICONINDEX) → SHGetImageList(JUMBO→EXTRALARGE→LARGE)
/// → IImageList.GetIcon（校验 index &lt; count）。缓存发布协议：提取 PNG 字节 → tmp →
/// File.Move(overwrite) → 内存字典 → 最后经目录协议更新 IconStamp——stamp 只在图标
/// 成功发布后推进，任何前置失败=旧图继续有效（陈旧可接受）。目标删除→保留最后成功
/// PNG 进失效灰态。孤儿清扫仅在启动装载成功后运行一次（此后新增 PNG 到下次启动才
/// 可能被清，杜绝与在途提取竞态）。排队等待方放弃（瓦片保持占位）≠ 执行超时：
/// 卡住的 Shell COM 调用阻塞图标管线属可接受降级（启动/添加/主 UI 不受影响）。
/// </summary>
internal sealed class LauncherIconService : IDisposable
{
    private readonly LauncherCatalog _catalog;
    private readonly string _iconsDirectory;
    private readonly Thread _iconThread;
    private readonly Dispatcher _iconDispatcher;
    private readonly Dispatcher _uiDispatcher;
    private readonly Dictionary<string, BitmapSource> _memory = [];
    private readonly Dictionary<string, List<Action<BitmapSource?>>> _pending = [];

    private LauncherIconService(
        LauncherCatalog catalog,
        string iconsDirectory,
        Dispatcher uiDispatcher,
        Thread iconThread,
        Dispatcher iconDispatcher)
    {
        _catalog = catalog;
        _iconsDirectory = iconsDirectory;
        _uiDispatcher = uiDispatcher;
        _iconThread = iconThread;
        _iconDispatcher = iconDispatcher;
    }

    internal static LauncherIconService Start(LauncherCatalog catalog, string iconsDirectory, Dispatcher uiDispatcher)
    {
        var dispatcherReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? iconDispatcher = null;
        var thread = new Thread(() =>
        {
            iconDispatcher = Dispatcher.CurrentDispatcher;
            // 图标管线任何未处理异常不得崩溃进程：提取失败=保持占位（可接受降级），
            // 派发器层兜底吞掉并记录诊断日志。
            iconDispatcher.UnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "fts-launcher-icon-failure.log"),
                        eventArgs.Exception.ToString());
                }
                catch (IOException)
                {
                }
            };
            dispatcherReady.TrySetResult();
            try
            {
                Dispatcher.Run();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dispatcherReady.TrySetException(exception);
            }
        })
        {
            Name = "FTS 启动项图标 STA",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        dispatcherReady.Task.Wait(TimeSpan.FromSeconds(5));
        return new LauncherIconService(
            catalog,
            iconsDirectory,
            uiDispatcher,
            thread,
            iconDispatcher ?? uiDispatcher);
    }

    /// <summary>UI 线程同步读内存缓存（命中为纯内存查询，零磁盘 I/O）。</summary>
    internal BitmapSource? TryGetCached(string iconKey)
    {
        lock (_memory)
        {
            return _memory.GetValueOrDefault(iconKey);
        }
    }

    /// <summary>
    /// 请求图标：内存未命中派发到 STA 线程（stamp 未变优先读缓存文件）；结果经 UI
    /// Dispatcher 回调。等待方放弃＝瓦片保持占位（回调引用失效即 no-op）。
    /// </summary>
    internal void RequestIcon(LauncherItem item, Action<BitmapSource?> onCompleted)
    {
        var iconKey = item.IconKey;
        if (iconKey.Length == 0)
        {
            return;
        }

        lock (_memory)
        {
            if (_memory.TryGetValue(iconKey, out var cached))
            {
                _uiDispatcher.BeginInvoke(() => onCompleted(cached));
                return;
            }
        }

        lock (_pending)
        {
            if (_pending.TryGetValue(iconKey, out var waiters))
            {
                waiters.Add(onCompleted);
                return;
            }

            _pending[iconKey] = [onCompleted];
        }

        _iconDispatcher.BeginInvoke(() =>
        {
            BitmapSource? icon = null;
            try
            {
                icon = LoadOrCreate(item, iconKey);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
            {
                // 提取失败＝保持占位；任何前置失败不动 stamp（旧图继续有效）。
            }

            List<Action<BitmapSource?>> completions;
            lock (_pending)
            {
                completions = _pending.GetValueOrDefault(iconKey) ?? [];
                _pending.Remove(iconKey);
            }

            foreach (var completion in completions)
            {
                _uiDispatcher.BeginInvoke(() => completion(icon));
            }
        });
    }

    /// <summary>孤儿清扫：仅启动装载成功后运行一次。</summary>
    internal void SweepOrphansOnce()
    {
        var referenced = new HashSet<string>(
            _catalog.Items.Select(item => item.IconKey).Where(key => key.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_iconsDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_iconsDirectory, "*.png"))
            {
                if (!referenced.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 下次启动再清。
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private BitmapSource? LoadOrCreate(LauncherItem item, string iconKey)
    {
        var pngPath = Path.Combine(_iconsDirectory, iconKey + ".png");
        var currentStamp = ComputeStamp(item);
        if (File.Exists(pngPath) && currentStamp is not null && item.IconStamp == currentStamp)
        {
            var cached = DecodePng(File.ReadAllBytes(pngPath));
            if (cached is not null)
            {
                Remember(iconKey, cached);
                return cached;
            }
        }

        var extracted = ExtractPng(item);
        if (extracted is null)
        {
            // 目标已删除/提取失败：保留最后成功 PNG 进失效灰态（UI 层灰态显示）。
            if (File.Exists(pngPath))
            {
                var stale = DecodePng(File.ReadAllBytes(pngPath));
                if (stale is not null)
                {
                    Remember(iconKey, stale);
                    return stale;
                }
            }

            return null;
        }

        Directory.CreateDirectory(_iconsDirectory);
        var temporaryPath = pngPath + ".tmp";
        File.WriteAllBytes(temporaryPath, extracted);
        File.Move(temporaryPath, pngPath, overwrite: true);
        var published = DecodePng(extracted);
        if (published is null)
        {
            return null;
        }

        Remember(iconKey, published);

        // stamp 只在图标成功发布后推进（经目录串行协议持久化；失败静默＝陈旧可接受）。
        if (currentStamp is not null && item.IconStamp != currentStamp)
        {
            _ = _catalog.UpdateIconStampAsync(item.Id, currentStamp);
        }

        return published;
    }

    private void Remember(string iconKey, BitmapSource icon)
    {
        lock (_memory)
        {
            _memory[iconKey] = icon;
        }
    }

    /// <summary>主路径 + 回退链，产出 PNG 字节（GDI 句柄按类型只删一次）。</summary>
    private static byte[]? ExtractPng(LauncherItem item)
    {
        var (handle, isIcon) = ExtractIconHandle(item);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = isIcon
                ? System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions())
                : System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (isIcon)
            {
                _ = ShellInterop.DestroyIcon(handle);
            }
            else
            {
                _ = ShellInterop.DeleteObject(handle);
            }
        }
    }

    /// <summary>主路径：IShellItemImageFactory（PackagedApp 解析名 shell:AppsFolder\{AUMID}，
    /// 解析失败时枚举 AppsFolder 按 AUMID 匹配后对项 RCW 直接 QI 图标工厂）。</summary>
    private static (IntPtr Handle, bool IsIcon) ExtractIconHandle(LauncherItem item)
    {
        var parsingName = item.Kind switch
        {
            LauncherKind.Application => item.TargetPath,
            LauncherKind.PackagedApp => $"shell:AppsFolder\\{item.TargetPath}",
            _ => null
        };
        if (parsingName is null)
        {
            return (IntPtr.Zero, false);
        }

        var flags = ShellInterop.SiigbfIconOnly | ShellInterop.SiigbfBiggerSizeOk;
        if (item.Kind == LauncherKind.Application &&
            (item.TargetPath.StartsWith(@"\\", StringComparison.Ordinal) ||
             item.TargetPath.StartsWith(@"//", StringComparison.Ordinal)))
        {
            // UNC 死路径：MEMORYONLY 防 shell 层挂起等待。
            flags |= ShellInterop.SiigbfMemoryOnly;
        }

        var factoryId = typeof(ShellInterop.IShellItemImageFactory).GUID;
        if (ShellInterop.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, factoryId, out var primaryPointer) == 0)
        {
            try
            {
                var factory = (ShellInterop.IShellItemImageFactory)Marshal.GetObjectForIUnknown(primaryPointer);
                if (factory.GetImage(
                        new ShellInterop.Size(ShellInterop.ShellIconSize, ShellInterop.ShellIconSize),
                        flags,
                        out var bitmapHandle) == 0 &&
                    bitmapHandle != IntPtr.Zero)
                {
                    return (bitmapHandle, false);
                }
            }
            catch (InvalidCastException)
            {
            }
            finally
            {
                Marshal.Release(primaryPointer);
            }
        }

        if (item.Kind == LauncherKind.PackagedApp &&
            TryGetAppsFolderItemByAumid(item.TargetPath) is { } enumerated)
        {
            // shell:AppsFolder\{AUMID} 解析对个别打包应用不可行（非契约）：
            // 枚举命中后对项本身 QI 图标工厂（RCW 强制转换即 QI）。
            if (enumerated is ShellInterop.IShellItemImageFactory directFactory &&
                directFactory.GetImage(
                    new ShellInterop.Size(ShellInterop.ShellIconSize, ShellInterop.ShellIconSize),
                    flags,
                    out var enumeratedHandle) == 0 &&
                enumeratedHandle != IntPtr.Zero)
            {
                return (enumeratedHandle, false);
            }
        }

        return (ExtractViaSystemImageList(item), true);
    }

    /// <summary>回退链：SHGetFileInfo(SHGFI_SYSICONINDEX) → SHGetImageList(JUMBO→EXTRALARGE→LARGE)
    /// → IImageList.GetIcon（校验 index &lt; count；不用 SHGFI_USEFILEATTRIBUTES）。</summary>
    private static IntPtr ExtractViaSystemImageList(LauncherItem item)
    {
        if (item.Kind != LauncherKind.Application)
        {
            return IntPtr.Zero;
        }

        var info = new ShellInterop.ShFileInfo();
        var result = ShellInterop.SHGetFileInfoW(
            item.TargetPath,
            0,
            ref info,
            Marshal.SizeOf<ShellInterop.ShFileInfo>(),
            ShellInterop.ShgfiSysiconIndex | ShellInterop.ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.IconIndex < 0)
        {
            return IntPtr.Zero;
        }

        var imageListId = typeof(ShellInterop.IImageList).GUID;
        foreach (var listSize in new[]
                 {
                     ShellInterop.ShilJumbo, ShellInterop.ShilExtraLarge, ShellInterop.ShilLarge
                 })
        {
            if (ShellInterop.SHGetImageList(listSize, imageListId, out var imageList) != 0)
            {
                continue;
            }

            try
            {
                if (imageList.GetImageCount(out var count) != 0 || info.IconIndex >= count)
                {
                    continue;
                }

                if (imageList.GetIcon(info.IconIndex, ShellInterop.IldTransparent, out var iconHandle) == 0 &&
                    iconHandle != IntPtr.Zero)
                {
                    return iconHandle;
                }
            }
            catch (InvalidComObjectException)
            {
                // 试下一档尺寸。
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>AppsFolder 枚举按 AUMID 匹配取 IShellItem2（shell:AppsFolder 解析失败的回退；
    /// PR-B 已安装应用选择器复用同一枚举）。</summary>
    private static object? TryGetAppsFolderItemByAumid(string appUserModelId)
    {
        var itemId = typeof(ShellInterop.IShellItem).GUID;
        if (ShellInterop.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, itemId, out var appsFolderPointer) != 0)
        {
            return null;
        }

        try
        {
            var folderItem = (ShellInterop.IShellItem)Marshal.GetObjectForIUnknown(appsFolderPointer);
            var enumItemsId = typeof(ShellInterop.IEnumShellItems).GUID;
            if (folderItem.BindToHandler(IntPtr.Zero, ShellInterop.BhidEnumItems, enumItemsId, out var enumeratorPointer) != 0)
            {
                return null;
            }

            try
            {
                var enumerator = (ShellInterop.IEnumShellItems)Marshal.GetObjectForIUnknown(enumeratorPointer);
                while (enumerator.Next(1, out var item, out _) == 0)
                {
                    try
                    {
                        if (item is ShellInterop.IShellItem2 { } item2 &&
                            item2.GetProperty(ShellInterop.AppUserModelId, out var variant) == 0)
                        {
                            var aumid = variant.AsString();
                            variant.Clear();
                            if (string.Equals(aumid, appUserModelId, StringComparison.OrdinalIgnoreCase))
                            {
                                return item2;
                            }
                        }
                    }
                    finally
                    {
                        if (item is not ShellInterop.IShellItem2)
                        {
                            Marshal.ReleaseComObject(item);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException)
            {
                return null;
            }
            finally
            {
                Marshal.Release(enumeratorPointer);
            }

            return null;
        }
        finally
        {
            Marshal.Release(appsFolderPointer);
        }
    }

    /// <summary>
    /// 图标戳（#30）：shortHash(lnk.mtime|size ‖ 解析目标.mtime|size ‖ GetIconLocation
    /// 解析出的图标源文件.mtime|size)——.lnk 独立图标源纳入指纹；AUMID/Url 条目 null
    /// （不自动失效；刷新触发=激活失败重枚举或重新添加覆盖）。
    /// </summary>
    internal static string? ComputeStamp(LauncherItem item)
    {
        if (item.Kind != LauncherKind.Application)
        {
            return null;
        }

        var fingerprint = new StringBuilder();
        AppendFileFingerprint(fingerprint, item.TargetPath);
        if (item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            ShellLinkHelper.TryRead(item.TargetPath) is { } link)
        {
            if (link.ResolvedTargetPath is { Length: > 0 } resolved)
            {
                AppendFileFingerprint(fingerprint, resolved);
            }

            if (link.IconLocation is { Length: > 0 } iconSource &&
                File.Exists(iconSource))
            {
                AppendFileFingerprint(fingerprint, iconSource);
            }
        }

        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(fingerprint.ToString()));
        return Convert.ToHexStringLower(hash)[..12];

        static void AppendFileFingerprint(StringBuilder builder, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    builder.Append(path).Append("|missing|0\u0001");
                    return;
                }

                var info = new FileInfo(path);
                builder.Append(path)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('|')
                    .Append(info.Length)
                    .Append('\u0001');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                builder.Append(path).Append("|unavailable|0\u0001");
            }
        }
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _iconDispatcher.InvokeShutdown();
        _ = _iconThread.Join(TimeSpan.FromSeconds(2));
    }
}
