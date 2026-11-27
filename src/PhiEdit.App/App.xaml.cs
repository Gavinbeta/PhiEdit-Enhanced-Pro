using Microsoft.UI.Xaml;

namespace PhiEdit.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        var arguments = args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length > 0 && !arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            window.OpenStartupFile(arguments[0]);
        }
        var beatArgument = arguments.FirstOrDefault(item => double.TryParse(item,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        if (beatArgument is not null && double.TryParse(beatArgument,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var beat))
        {
            window.SetStartupBeat(beat);
        }
        // 带启动参数（谱面文件/拍号/预览）时直接进入编辑器，避免自动化与查看流程被导航页挡住。
        if (arguments.Length > 0)
        {
            window.EnterEditor();
        }
        window.Activate();
        if (arguments.Contains("--preview") ||
            Environment.GetEnvironmentVariable("PHIEDIT_OPEN_PREVIEW") == "1")
        {
            window.OpenPreviewForStartup();
        }
    }
}
