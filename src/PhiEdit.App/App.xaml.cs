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
        window.Activate();
        if (arguments.Contains("--preview") ||
            Environment.GetEnvironmentVariable("PHIEDIT_OPEN_PREVIEW") == "1")
        {
            window.OpenPreviewForStartup();
        }
    }
}
