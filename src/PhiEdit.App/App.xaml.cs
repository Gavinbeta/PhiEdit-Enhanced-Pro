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
        if (arguments.Length > 0)
        {
            window.OpenStartupFile(arguments[0]);
        }
        if (arguments.Length > 1 && double.TryParse(arguments[1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var beat))
        {
            window.SetStartupBeat(beat);
        }
        window.Activate();
    }
}
