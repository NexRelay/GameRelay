using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace GameRelay.App;

public partial class App : Application
{
    // Held for the app's lifetime; its existence marks the running instance.
    private static Mutex? _singleInstanceMutex;

    public static MainWindow? Window { get; private set; }

    public App()
    {
        // Two instances would fight over the relay's single control slot
        // (each reconnect kicks the other), so enforce a single instance:
        // focus the existing window and quit.
        _singleInstanceMutex = new Mutex(true, @"Local\GameRelay.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            FocusExistingWindow();
            Environment.Exit(0);
        }
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }

    private static void FocusExistingWindow()
    {
        nint hwnd = FindWindowW(null, "GameRelay");
        if (hwnd != 0)
        {
            const int SW_RESTORE = 9;
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
