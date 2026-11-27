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
        // 第一个既不是开关、也不是纯数字的参数视为谱面路径。
        var pathArgument = arguments.FirstOrDefault(item =>
            !item.StartsWith("--", StringComparison.Ordinal) &&
            !double.TryParse(item, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _));
        if (pathArgument is not null)
        {
            window.OpenStartupFile(pathArgument);
        }
        var beatArgument = arguments.FirstOrDefault(item => double.TryParse(item,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        if (beatArgument is not null && double.TryParse(beatArgument,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var beat))
        {
            window.SetStartupBeat(beat);
        }
        // 程序启动始终显示主界面（谱面库页），编辑器只是其中的子页面。
        // 仅显式 --editor 参数时直接进入编辑器（供自动化/外部调用使用）。
        if (arguments.Contains("--editor", StringComparer.OrdinalIgnoreCase))
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
