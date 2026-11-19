using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace PhiEdit.App.Controls;

/// <summary>
/// 轻量分割条。WinUI 3 未提供 GridSplitter，这里自行实现拖动调整相邻两栏宽度的能力。
/// </summary>
public sealed class ThinSplitter : UserControl
{
    private readonly Border _bar;

    private double _lastPosition;

    public ThinSplitter()
    {
        _bar = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Content = _bar;
        Width = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _bar.PointerPressed += OnPointerPressed;
        _bar.PointerMoved += OnPointerMoved;
        _bar.PointerReleased += OnPointerReleased;
        _bar.PointerCanceled += OnPointerReleased;
        _bar.PointerCaptureLost += (_, _) => _dragging = false;
    }

    /// <summary>拖动时给出横向位移（像素）。</summary>
    public event EventHandler<double>? Dragged;

    /// <summary>指针悬停时高亮。</summary>
    public bool Highlight
    {
        get => _bar.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush &&
               brush.Color != Microsoft.UI.Colors.Transparent;
        set => _bar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            value ? Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Microsoft.UI.Colors.Transparent);
    }

    private bool _dragging;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastPosition = e.GetCurrentPoint(_bar).Position.X;
        _dragging = true;
        _bar.CapturePointer(e.Pointer);
        Highlight = true;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetCurrentPoint(_bar).Position.X;
        var delta = position - _lastPosition;
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        _lastPosition = position;
        Dragged?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _bar.ReleasePointerCapture(e.Pointer);
        Highlight = false;
        e.Handled = true;
    }
}
