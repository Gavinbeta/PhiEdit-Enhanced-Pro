using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.App.Controls;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using Windows.Graphics;

namespace PhiEdit.App;

/// <summary>
/// 独立实时预览窗口。后续会在编辑画布下层复用同一个 PreviewCanvas，
/// 因此这里不持有任何编辑交互状态，只负责时间控制。
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private readonly PreviewClock _clock = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly EditorSession _session;
    private bool _updatingSeek;

    public PreviewWindow(EditorSession session)
    {
        _session = session;
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(1200, 800));
        PreviewSurface.Session = session;
        PreviewSurface.Clock = _clock;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTimerTick;
        _timer.Start();

        Closed += OnClosed;
        UpdateTimeReadout();
    }

    /// <summary>主窗口时间变化时单向同步；预览自身播放时不回写编辑器。</summary>
    public void SyncFromEditor(double beat)
    {
        if (FollowEditorCheck.IsChecked != true || _clock.IsPlaying)
        {
            return;
        }

        _clock.Seek(_session.Document.BuildTempoMap().SecondsAt(beat));
        PreviewSurface.Invalidate();
        UpdateTimeReadout();
    }

    /// <summary>谱面结构变化后强制重建预览运行时。</summary>
    public void MarkSceneDirty() => PreviewSurface.Invalidate();

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_clock.IsPlaying)
        {
            _clock.Pause();
            PlayButton.Content = "播放";
        }
        else
        {
            _clock.Play();
            PlayButton.Content = "暂停";
        }

        UpdateTimeReadout();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _clock.Pause();
        _clock.Seek(0);
        PlayButton.Content = "播放";
        UpdateTimeReadout();
        PreviewSurface.Invalidate();
    }

    private void OnTimerTick(object sender, object e)
    {
        UpdateTimeReadout();
        PreviewSurface.Invalidate();
    }

    private void UpdateTimeReadout()
    {
        var tempo = _session.Document.BuildTempoMap();
        var seconds = _clock.CurrentSeconds;
        var beat = tempo.BeatAt(seconds);
        TimeReadout.Text = $"{seconds:0.000} s   /   {beat:0.00} 拍";

        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, tempo.SecondsAt(_session.Document.LastBeat + 4));
        SeekSlider.Value = Math.Clamp(seconds, SeekSlider.Minimum, SeekSlider.Maximum);
        _updatingSeek = false;
    }

    private void OnSeekSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek)
        {
            return;
        }

        _clock.Seek(e.NewValue);
        PreviewSurface.Invalidate();
        TimeReadout.Text = $"{e.NewValue:0.000} s";
    }

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        _clock.Rate = RateBox.SelectedIndex switch
        {
            0 => 0.5,
            2 => 2.0,
            _ => 1.0,
        };
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        PreviewSurface.Session = null;
        PreviewSurface.Clock = null;
    }
}
