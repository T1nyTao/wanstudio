using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;

using WanStudio.Services;

namespace WanStudio;

public partial class App : Application
{
    private Window? _window;
    public static Window? MainWindowHint { get; private set; }

    /// <summary>--open=home|tasks|long|settings (optional deep link / QA helper).</summary>
    public static string? StartupPage { get; private set; }

    /// <summary>--import=&lt;path&gt; opens a local video on the long-video page.</summary>
    public static string? StartupImportPath { get; private set; }

    public App()
    {
        ParseStartupArgs();

        UnhandledException += (_, e) =>
        {
            try
            {
                AppPaths.AppendCrashLog("[Unhandled] " + e.Exception);
            }
            catch { }
            e.Handled = true;
        };

        InitializeComponent();
    }

    private static void ParseStartupArgs()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--open=", StringComparison.OrdinalIgnoreCase))
            {
                StartupPage = arg["--open=".Length..].Trim().ToLowerInvariant();
            }
            else if (arg.StartsWith("--import=", StringComparison.OrdinalIgnoreCase))
            {
                StartupImportPath = arg["--import=".Length..].Trim('"');
                StartupPage ??= "long";
            }
            else if (string.Equals(arg, "--import", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                StartupImportPath = args[++i].Trim('"');
                StartupPage ??= "long";
            }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            MainWindowHint = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Wan3.0", "wanstudio-crash.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("s") + " [OnLaunched] " + ex + Environment.NewLine);
            }
            catch { }
            throw;
        }
    }
}
