using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ViiCord.Models;

namespace ViiCord.Services;

/// <summary>Lance ViiCord avec Windows (clé Run de l'utilisateur, sans droits administrateur).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ViiCord";
    public const string BackgroundArg = "--background";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue(ValueName, "\"" + exe + "\" " + BackgroundArg);
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}

/// <summary>Icône ViiCord près de l'horloge (zone de notification) : ouvrir, réparer, options, quitter.</summary>
public sealed class TrayService : IDisposable
{
    private const int WM_TRAY = 0x8001;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 1;
    private const uint MF_STRING = 0, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;

    private const int CmdOpen = 1, CmdRepair = 2, CmdAutoRepair = 3, CmdStartup = 4, CmdKeepTray = 5, CmdQuit = 6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIconW(IntPtr hInst, string file, uint index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _added;

    public event Action? OpenRequested;
    public event Action? RepairRequested;
    public event Action? QuitRequested;

    public TrayService()
    {
        var p = new HwndSourceParameters("ViiCordTray") { WindowStyle = 0, ParentWindow = new IntPtr(-3) };   // fenêtre de messages seule
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        try { _icon = ExtractIconW(IntPtr.Zero, Environment.ProcessPath ?? "", 0); } catch { }
        AddIcon();
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = "ViiCord — byRedemptiion",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddIcon()
    {
        try
        {
            NOTIFYICONDATA d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = Shell_NotifyIconW(NIM_ADD, ref d);
        }
        catch { }
    }

    /// <summary>Petite bulle près de l'horloge.</summary>
    public void Balloon(string title, string text)
    {
        if (!_added) return;
        try
        {
            NOTIFYICONDATA d = Data(NIF_INFO);
            d.szInfoTitle = title.Length > 60 ? title[..60] : title;
            d.szInfo = text.Length > 250 ? text[..250] : text;
            d.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIconW(NIM_MODIFY, ref d);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated) { AddIcon(); return IntPtr.Zero; }
        if (msg != WM_TRAY) return IntPtr.Zero;

        int ev = (int)(lParam.ToInt64() & 0xFFFF);
        if (ev == WM_LBUTTONUP || ev == NIN_BALLOONUSERCLICK) { OpenRequested?.Invoke(); handled = true; }
        else if (ev == WM_RBUTTONUP) { ShowMenu(); handled = true; }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        AppSettings s = ConfigService.ReadSettings();
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdOpen, "Ouvrir ViiCord");
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdRepair, "Réparer maintenant (relance Discord)");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING | (s.AutoRepair ? MF_CHECKED : 0), (UIntPtr)CmdAutoRepair, "Réparation automatique après une mise à jour de Discord");
            AppendMenuW(menu, MF_STRING | (StartupService.IsEnabled() ? MF_CHECKED : 0), (UIntPtr)CmdStartup, "Démarrer avec Windows");
            AppendMenuW(menu, MF_STRING | (s.KeepInTray ? MF_CHECKED : 0), (UIntPtr)CmdKeepTray, "Rester près de l'horloge quand je ferme la fenêtre");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdQuit, "Quitter ViiCord");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_source.Handle);
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _source.Handle, IntPtr.Zero);
            PostMessageW(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdOpen: OpenRequested?.Invoke(); break;
                case CmdRepair: RepairRequested?.Invoke(); break;
                case CmdAutoRepair: s.AutoRepair = !s.AutoRepair; ConfigService.SaveSettings(s); break;
                case CmdStartup: StartupService.Set(!StartupService.IsEnabled()); break;
                case CmdKeepTray: s.KeepInTray = !s.KeepInTray; ConfigService.SaveSettings(s); break;
                case CmdQuit: QuitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        try
        {
            if (_added)
            {
                NOTIFYICONDATA d = Data(0);
                Shell_NotifyIconW(NIM_DELETE, ref d);
                _added = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        }
        catch { }
    }
}
