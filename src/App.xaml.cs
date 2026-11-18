using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ViiCord.Models;
using ViiCord.Services;

namespace ViiCord;

public partial class App : Application
{
    private const string ShowEventName = "ViiCord.ShowWindow.byRedemptiion";
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private TrayService? _tray;
    private DispatcherTimer? _backgroundTimer;
    private bool _backgroundBusy;

    /// <summary>Vrai quand l'utilisateur quitte vraiment ViiCord (sinon fermer la fenêtre le garde près de l'horloge).</summary>
    public static bool IsQuitting { get; private set; }
    public static bool TrayActive => ((App)Current)._tray != null;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Une seule instance : deux fenêtres ViiCord pourraient s'écraser mutuellement la configuration.
        _singleInstance = new Mutex(true, "ViiCord.SingleInstance.byRedemptiion", out bool createdNew);
        if (!createdNew)
        {
            // ViiCord tourne déjà (peut-être près de l'horloge) : on lui demande d'afficher sa fenêtre.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        SessionEnding += (_, _) => IsQuitting = true;   // fermeture de Windows : ne pas retenir la session
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        // Ambiance de couleur choisie la dernière fois (avant la création de la fenêtre).
        string themeKey = ThemeService.DefaultKey;
        try
        {
            AppSettings saved = ConfigService.ReadSettings();
            themeKey = saved.Theme;
            ThemeService.CustomAccent = ThemeService.NormalizeHex(saved.CustomAccent) ?? ThemeService.CustomAccent;
        }
        catch { }
        ThemeService.Apply(themeKey);

        var window = new MainWindow();
        MainWindow = window;

        bool background = e.Args.Any(a => string.Equals(a, StartupService.BackgroundArg, StringComparison.OrdinalIgnoreCase));
        try
        {
            _tray = new TrayService();
            _tray.OpenRequested += () => Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            _tray.RepairRequested += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowMainWindow();
                _ = window.Vm.RepairFromTrayAsync();
            }));
            _tray.QuitRequested += () => Dispatcher.BeginInvoke(new Action(QuitApp));
        }
        catch { _tray = null; }

        // Réveil par un second lancement (raccourci cliqué alors que ViiCord est près de l'horloge).
        var waiter = new Thread(() =>
        {
            while (_showEvent != null)
            {
                try { _showEvent.WaitOne(); } catch { return; }
                if (IsQuitting) return;
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
        }) { IsBackground = true };
        waiter.Start();

        if (background && _tray != null)
        {
            // Démarrage avec Windows : on reste discret, la fenêtre ne s'ouvre pas.
            window.StartHidden();
        }
        else window.Show();

        _backgroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _backgroundTimer.Tick += async (_, _) =>
        {
            if (_backgroundBusy || IsQuitting) return;
            _backgroundBusy = true;
            try { await window.Vm.BackgroundTickAsync((title, text) => _tray?.Balloon(title, text)); }
            catch { }
            finally { _backgroundBusy = false; }
        };
        _backgroundTimer.Start();
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not Window w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false;   // passe devant sans rester au-dessus
        w.Focus();
    }

    public void QuitFromWindow() => QuitApp();
    public void TrayBalloon(string title, string text) => _tray?.Balloon(title, text);

    private void QuitApp()
    {
        IsQuitting = true;
        try { _backgroundTimer?.Stop(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _showEvent?.Set(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        MessageBox.Show(
            "Une erreur inattendue est survenue :\n\n" + e.Exception.Message +
            "\n\nDétails enregistrés dans app-crash.log (dossier %APPDATA%\\ViiCord).",
            "ViiCord", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string path = System.IO.Path.Combine(AppPaths.DataDir, "app-crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:O}] {ex}");
            sb.AppendLine();
            System.IO.File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
