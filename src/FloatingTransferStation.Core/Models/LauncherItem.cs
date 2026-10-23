using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace FloatingTransferStation.Models;

/// <summary>启动项类型（#30）：应用（exe / .lnk）、打包应用（AUMID）、网页目标
/// 与系统位置（回收站/「此电脑」的 CLSID 解析名，唯一引入点见
/// <see cref="FloatingTransferStation.Services.SystemLauncherLocations"/>）。</summary>
public enum LauncherKind
{
    Application = 0,
    PackagedApp = 1,
    Url = 2,
    ShellLocation = 3
}

/// <summary>
/// 启动项（#30，Issue #162）：数组顺序即唯一真相（不存 Order 字段）。
/// 持久化字段经 launchers.json 整集合读写（串行化协议见 LauncherCatalog）；
/// 会话态字段一律 <see cref="JsonIgnore"/>，不参与快照与撤销语义。
/// </summary>
public sealed class LauncherItem : INotifyPropertyChanged, IEquatable<LauncherItem>
{
    /// <summary>存储上限（UTF-16 码元）：导入默认名超长截断，改名编辑器 MaxLength 同源。</summary>
    public const int MaxDisplayNameLength = 64;

    private string _displayName;

    public LauncherItem(
        Guid id,
        LauncherKind kind,
        string targetPath,
        string displayName,
        DateTimeOffset createdAt,
        string? arguments = null,
        string? workingDirectory = null,
        string? iconStamp = null)
    {
        Id = id;
        Kind = kind;
        TargetPath = targetPath;
        _displayName = LimitDisplayName(displayName);
        CreatedAt = createdAt;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        IconStamp = iconStamp;
    }

    public Guid Id { get; }

    public LauncherKind Kind { get; private set; }

    /// <summary>目标：应用类=文件路径（.lnk 存快捷方式本体路径）、打包应用=AUMID、
    /// Url=网址、系统位置=CLSID 解析名（仅经 SystemLauncherLocations 注册表引入）。</summary>
    public string TargetPath { get; private set; }

    /// <summary>启动参数（仅 Application 类；null/空=无参数）。</summary>
    public string? Arguments { get; private set; }

    /// <summary>工作目录（null/空 = 目标所在目录）。</summary>
    public string? WorkingDirectory { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>图标指纹（null=不自动失效：AUMID 条目或尚未提取）。只在图标成功发布后推进。</summary>
    public string? IconStamp { get; private set; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetField(ref _displayName, LimitDisplayName(value)))
            {
                // 持久化形态的改名经 LauncherCatalog 串行协议发布；本属性仍通知 UI 即时回显。
                // ToolTipText 派生自名称与目标（T7 评审修订：改名/目标变更连带刷新提示）。
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>改名编辑草稿（会话态）。空提交=回退原名。</summary>
    [JsonIgnore]
    public string DraftName
    {
        get;
        set => SetField(ref field, LimitDisplayName(value));
    } = string.Empty;

    /// <summary>处于就地改名编辑态（会话态）。</summary>
    [JsonIgnore]
    public bool IsEditingName
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>多选会话态（#30 修订 2026-10-18，Issue #186）：瓦片选择视觉与右上
    /// 勾选钮的绑定源；批量删除按本标记聚合（权威集合仍以目录数组序为准）。</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>键盘焦点指示（会话态）：勾选钮随键盘导航可见（#13 族），由窗口焦点
    /// 视觉维护点统一置位/复位。</summary>
    [JsonIgnore]
    public bool IsKeyboardFocused
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>最近一次启动失败态（会话态）：图标 50% 透明 + 警告角标；成功启动或会话内重新提取图标时清除。</summary>
    [JsonIgnore]
    public bool LaunchFailed
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>应用图标缓存键 = SHA256(规范化目标)（派生值不持久化；由图标服务统一计算）。</summary>
    [JsonIgnore]
    public string IconKey => LauncherIconKey.Compute(TargetPath);

    /// <summary>名称辨认提示（#30 修订 2026-10-18 D10）：「名称＋目标（域名/路径）」——
    /// 失败瓦片的可修复入口配名称与目标同览；派生只读不落盘（JsonIgnore 防序列化）。</summary>
    [JsonIgnore]
    public string ToolTipText => $"{_displayName}\n{TargetPath}";

    /// <summary>持久化改名入口（仅 LauncherCatalog 串行协议调用；构造与导入走构造函数）。</summary>
    public void AdoptPersistedName(string displayName) => DisplayName = displayName;

    /// <summary>
    /// 编辑目标入口（#30 修订 2026-10-18，Issue #186；仅 LauncherCatalog 串行协议
    /// 调用）：目标三元组（Kind/TargetPath/Arguments/WorkingDirectory）就地替换＋
    /// IconStamp 置空（图标管线自动重取——stamp=null 恒走提取，契约 #30 §三.6 既有
    /// 语义）；用户名保留不动。属性通知即上屏（IconKey 派生属性随 TargetPath 失效）。
    /// </summary>
    public void AdoptEditedTarget(
        LauncherKind kind,
        string targetPath,
        string? arguments,
        string? workingDirectory)
    {
        Kind = kind;
        TargetPath = targetPath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        if (IconStamp is not null)
        {
            IconStamp = null;
            OnPropertyChanged(nameof(IconStamp));
        }

        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(ToolTipText));
    }

    /// <summary>持久化图标戳推进（仅图标缓存发布成功后经 LauncherCatalog 调用）。</summary>
    public void AdoptPersistedIconStamp(string? iconStamp)
    {
        if (IconStamp != iconStamp)
        {
            IconStamp = iconStamp;
            OnPropertyChanged(nameof(IconStamp));
        }
    }

    /// <summary>剥除会话态的持久化克隆体（快照与撤销基准用；保序整集合读写）。</summary>
    public LauncherItem CloneForSnapshot() => new(
        Id,
        Kind,
        TargetPath,
        _displayName,
        CreatedAt,
        Arguments,
        WorkingDirectory,
        IconStamp);

    public static string LimitDisplayName(string? name)
    {
        var value = name ?? string.Empty;
        return value.Length <= MaxDisplayNameLength ? value : value[..MaxDisplayNameLength];
    }

    public override string ToString() => $"{Kind}:{TargetPath} ({DisplayName})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    bool IEquatable<LauncherItem>.Equals(LauncherItem? other) =>
        other is not null && other.Id == Id;
}
