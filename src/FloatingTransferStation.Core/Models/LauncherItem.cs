using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace FloatingTransferStation.Models;

/// <summary>启动项类型（#30）：应用（exe / .lnk）、打包应用（AUMID）与网页目标
/// （仅数据模型容纳，v1 UI 无入口；文件中存在时按一等条目显示与启动）。</summary>
public enum LauncherKind
{
    Application = 0,
    PackagedApp = 1,
    Url = 2
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

    public LauncherKind Kind { get; }

    /// <summary>目标：应用类=文件路径（.lnk 存快捷方式本体路径）、打包应用=AUMID、Url=网址。</summary>
    public string TargetPath { get; }

    /// <summary>启动参数（仅 Application 类；null/空=无参数）。</summary>
    public string? Arguments { get; }

    /// <summary>工作目录（null/空 = 目标所在目录）。</summary>
    public string? WorkingDirectory { get; }

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

    /// <summary>持久化改名入口（仅 LauncherCatalog 串行协议调用；构造与导入走构造函数）。</summary>
    public void AdoptPersistedName(string displayName) => DisplayName = displayName;

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
