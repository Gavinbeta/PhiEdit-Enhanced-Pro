using System.ComponentModel;

namespace PhiEdit.Core.Preview;

/// <summary>实时预览中判定线的配色方案。</summary>
public enum PreviewLineColorMode
{
    /// <summary>当前编辑线绿色，其余线白色。</summary>
    SelectedAccent,

    /// <summary>全部使用资源包 perfectColor（内置皮肤的金色），无资源包时使用默认金色。</summary>
    SkinPerfect,
}

/// <summary>
/// 实时预览的可配置渲染选项。这里只放纯数据，不依赖 WinUI，
/// 便于独立预览窗口和后续嵌入主编辑区共用同一套配置。
/// </summary>
public sealed class PreviewRenderOptions : INotifyPropertyChanged
{
    private bool _showLineNumbers = true;
    private bool _showLineDirection = true;
    private bool _showGameUi = true;
    private bool _showHitEffects = true;
    private double _backgroundBrightness = 1;
    private PreviewLineColorMode _lineColorMode = PreviewLineColorMode.SelectedAccent;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>是否显示判定线编号。</summary>
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set => Set(ref _showLineNumbers, value);
    }

    /// <summary>是否在当前编辑线中心显示方向菱形。</summary>
    public bool ShowLineDirection
    {
        get => _showLineDirection;
        set => Set(ref _showLineDirection, value);
    }

    /// <summary>是否显示暂停、进度、连击、分数、曲名与难度。</summary>
    public bool ShowGameUi
    {
        get => _showGameUi;
        set => Set(ref _showGameUi, value);
    }

    /// <summary>是否显示打击特效。</summary>
    public bool ShowHitEffects
    {
        get => _showHitEffects;
        set => Set(ref _showHitEffects, value);
    }

    /// <summary>背景亮度，0 为纯黑，1 为原图亮度。</summary>
    public double BackgroundBrightness
    {
        get => _backgroundBrightness;
        set => Set(ref _backgroundBrightness, Math.Clamp(value, 0, 1));
    }

    /// <summary>判定线配色方案。</summary>
    public PreviewLineColorMode LineColorMode
    {
        get => _lineColorMode;
        set => Set(ref _lineColorMode, value);
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
