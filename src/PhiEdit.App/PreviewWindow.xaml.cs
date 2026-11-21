using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.App.Controls;
using PhiEdit.App.Services.Audio;
using PhiEdit.Core.Editing;
using PhiEdit.Core.Preview;
using Windows.Graphics;

namespace PhiEdit.App;

/// <summary>
/// 独立实时预览窗口。背景音与音符音效由 PreviewAudioEngine 分层播放，
/// 未加载音频时退回高精度占位时钟。
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private readonly PreviewClock _clock = new();
    private readonly PreviewAudioEngine _audio = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly EditorSession _session;
    private bool _updatingSeek;
    private bool _audioLoaded;
    private float _musicVolume = 0.75f;
    private float _hitVolume = 0.3f;

    public PreviewWindow(EditorSession session)
    {
        _session = session;
        InitializeComponent();
        PreviewSurface.BackgroundBrightness = Math.Clamp(BackgroundBrightnessSlider.Value / 100.0, 0, 1);

        AppWindow.Resize(new SizeInt32(1200, 800));
        PreviewSurface.Session = session;
        PreviewSurface.Clock = _clock;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTimerTick;
        _timer.Start();

        Closed += OnClosed;
        UpdateTimeReadout();
        _ = LoadAudioAsync();
    }

    private IPreviewTimeSource ActiveClock => _audioLoaded ? _audio : _clock;

    private async Task LoadAudioAsync()
    {
        try
        {
            await _audio.LoadAsync(_session, _session.Skins.Active);
            DispatcherQueue.TryEnqueue(() =>
            {
                _audioLoaded = _audio.IsLoaded;
                if (!_audioLoaded)
                {
                    AudioStatus.Text = _audio.Status;
                    return;
                }

                _audio.SetVolumes(_musicVolume, _hitVolume);
                _audio.Rate = _clock.Rate;
                _audio.Seek(_clock.CurrentSeconds);
                PreviewSurface.Clock = _audio;
                AudioStatus.Text = _audio.Status;
                UpdateTimeReadout();
            });
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() => AudioStatus.Text = $"音频失败：{exception.Message}");
        }
    }

    /// <summary>主窗口时间变化时单向同步；预览自身播放时不回写编辑器。</summary>
    public void SyncFromEditor(double beat)
    {
        if (FollowEditorCheck.IsChecked != true || ActiveClock.IsPlaying)
        {
            return;
        }

        ActiveClock.Seek(_session.Document.BuildTempoMap().SecondsAt(beat));
        PreviewSurface.Invalidate();
        UpdateTimeReadout();
    }

    /// <summary>谱面结构变化后强制重建预览运行时。</summary>
    public void MarkSceneDirty() => PreviewSurface.Invalidate();

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        var clock = ActiveClock;
        if (clock.IsPlaying)
        {
            clock.Pause();
            PlayButton.Content = "播放";
        }
        else
        {
            clock.Play();
            PlayButton.Content = "暂停";
        }

        UpdateTimeReadout();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        ActiveClock.Pause();
        ActiveClock.Seek(0);
        PlayButton.Content = "播放";
        UpdateTimeReadout();
        PreviewSurface.Invalidate();
    }

    private void OnTimerTick(object sender, object e)
    {
        if (_audioLoaded && _audio.IsPlaying && _audio.DurationSeconds > 0 &&
            _audio.CurrentSeconds >= _audio.DurationSeconds)
        {
            _audio.Pause();
            PlayButton.Content = "播放";
        }

        UpdateTimeReadout();
        PreviewSurface.Invalidate();
    }

    private void UpdateTimeReadout()
    {
        var tempo = _session.Document.BuildTempoMap();
        var seconds = ActiveClock.CurrentSeconds;
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

        ActiveClock.Seek(e.NewValue);
        PreviewSurface.Invalidate();
        TimeReadout.Text = $"{e.NewValue:0.000} s";
    }

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        var rate = RateBox.SelectedIndex switch
        {
            0 => 0.5,
            2 => 2.0,
            _ => 1.0,
        };
        _clock.Rate = rate;
        _audio.Rate = rate;
    }

    private void OnBackgroundBrightnessChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // XAML 初始化时 Value 赋值会先触发 ValueChanged，此时 PreviewSurface 尚未赋值。
        if (PreviewSurface is null)
        {
            return;
        }

        PreviewSurface.BackgroundBrightness = Math.Clamp(e.NewValue / 100.0, 0, 1);
    }

    private void OnAudioFlyoutOpening(object sender, object e)
    {
        AudioPanel.Children.Clear();
        AudioPanel.Children.Add(new TextBlock
        {
            Text = _audio.Status,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            FontSize = 13,
        });

        AudioPanel.Children.Add(CreateSlider("背景音乐", _musicVolume, value =>
        {
            _musicVolume = (float)value;
            _audio.SetVolumes(_musicVolume, _hitVolume);
        }));
        AudioPanel.Children.Add(CreateSlider("音符音效", _hitVolume, value =>
        {
            _hitVolume = (float)value;
            _audio.SetVolumes(_musicVolume, _hitVolume);
        }));

        AudioPanel.Children.Add(new TextBlock
        {
            Text = "判定线音效总线",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 211, 211, 211)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
        });

        for (var index = 0; index < _session.Document.Lines.Count; index++)
        {
            var lineIndex = index;
            var bus = _audio.Buses.Count > index ? _audio.Buses[index] : null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{index}",
                Width = 28,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 211, 211, 211)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(CreateSlider(string.Empty, bus?.Volume ?? 1, value =>
                _audio.SetBus(lineIndex, (float)value,
                    bus?.Muted ?? false, bus?.Solo ?? false), 150));
            var mute = new CheckBox { Content = "静音", IsChecked = bus?.Muted ?? false };
            mute.Checked += (_, _) => ApplyBus(lineIndex, row, mute, null);
            mute.Unchecked += (_, _) => ApplyBus(lineIndex, row, mute, null);
            row.Children.Add(mute);
            var solo = new CheckBox { Content = "独奏", IsChecked = bus?.Solo ?? false };
            solo.Checked += (_, _) => ApplyBus(lineIndex, row, null, solo);
            solo.Unchecked += (_, _) => ApplyBus(lineIndex, row, null, solo);
            row.Children.Add(solo);
            AudioPanel.Children.Add(row);
        }
    }

    private void ApplyBus(int lineIndex, StackPanel row, CheckBox? mute, CheckBox? solo)
    {
        var slider = row.Children.OfType<Slider>().FirstOrDefault();
        var muteBox = mute ?? row.Children.OfType<CheckBox>().FirstOrDefault();
        var soloBox = solo ?? row.Children.OfType<CheckBox>().Skip(1).FirstOrDefault();
        _audio.SetBus(lineIndex, (float)(slider?.Value ?? 1),
            muteBox?.IsChecked == true, soloBox?.IsChecked == true);
    }

    private static Slider CreateSlider(string label, double value, Action<double> onChanged, double width = 280)
    {
        var slider = new Slider
        {
            Header = label,
            Width = width,
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            Value = value,
        };
        slider.ValueChanged += (_, args) => onChanged(args.NewValue);
        return slider;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _audio.Pause();
        _audio.Dispose();
        PreviewSurface.ReleaseResources();
        PreviewSurface.Session = null;
        PreviewSurface.Clock = null;
    }
}
