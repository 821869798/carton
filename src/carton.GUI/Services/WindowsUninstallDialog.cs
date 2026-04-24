using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using carton.Core.Utilities;

namespace carton.GUI.Services;

internal static class WindowsUninstallDialog
{
    private static readonly WndProc DialogWndProc = UninstallDialogProc;
    private static UninstallDialogState? _dialogState;
    private static IntPtr _dialogClassAtom = IntPtr.Zero;
    private static readonly IntPtr WhiteBrush = (IntPtr)(WindowColorBrush.COLOR_WINDOW + 1);

    private const string UninstallDialogClassName = "CartonUninstallDialogClass";
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int DialogWidth = 460;
    private const int DialogHeight = 196;
    private const int ID_UNINSTALL_BUTTON = 1001;
    private const int ID_CANCEL_BUTTON = 1002;
    private const int ID_DELETE_DATA_CHECKBOX = 1003;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(25);

    public static void HandleBeforeUninstall()
    {
        var result = Show();

        if (!result.Confirmed)
        {
            Environment.Exit(1223);
            return;
        }

        if (result.DeleteData)
        {
            TryDeleteCartonData();
        }
    }

    private static UninstallDialogResult Show()
    {
        try
        {
            RegisterWindowClass();

            _dialogState = new UninstallDialogState();

            var hInstance = GetModuleHandleW(null);
            var dialogHandle = CreateWindowExW(
                0,
                UninstallDialogClassName,
                "Carton Uninstall",
                WindowStyles.WS_OVERLAPPED | WindowStyles.WS_CAPTION | WindowStyles.WS_SYSMENU,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                DialogWidth,
                DialogHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (dialogHandle == IntPtr.Zero)
            {
                return new UninstallDialogResult(false, false);
            }

            CenterWindow(dialogHandle, DialogWidth, DialogHeight);
            ShowWindow(dialogHandle, ShowWindowCommand.SW_SHOW);
            UpdateWindow(dialogHandle);

            var start = Environment.TickCount64;
            while (_dialogState?.Completed != true)
            {
                if (Environment.TickCount64 - start >= DialogTimeout.TotalMilliseconds)
                {
                    _dialogState ??= new UninstallDialogState();
                    _dialogState.Confirmed = false;
                    _dialogState.Completed = true;
                    DestroyWindow(dialogHandle);
                    break;
                }

                if (GetMessageW(out var message, IntPtr.Zero, 0, 0) <= 0)
                {
                    break;
                }

                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            var state = _dialogState ?? new UninstallDialogState();
            _dialogState = null;
            return new UninstallDialogResult(state.Confirmed, state.DeleteData);
        }
        catch
        {
            _dialogState = null;
        }

        MessageBoxW(
            IntPtr.Zero,
            "Unable to show the uninstall confirmation dialog. Uninstall will be cancelled.",
            "Carton Uninstall",
            MessageBoxType.Ok | MessageBoxType.IconWarning | MessageBoxType.TopMost);
        return new UninstallDialogResult(false, false);
    }

    private static void TryDeleteCartonData()
    {
        try
        {
            var appDataPath = PathHelper.GetAppDataPath();
            var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var portableMarkerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PathHelper.PortableMarkerFileName);

            if (!string.Equals(appDataPath, exeDirectory, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(appDataPath))
                {
                    Directory.Delete(appDataPath, recursive: true);
                }
            }
            else
            {
                var dataDirectory = Path.Combine(appDataPath, "data");
                var lockDirectory = Path.Combine(appDataPath, ".locks");

                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }

                if (Directory.Exists(lockDirectory))
                {
                    Directory.Delete(lockDirectory, recursive: true);
                }

                if (File.Exists(portableMarkerPath))
                {
                    File.Delete(portableMarkerPath);
                }
            }
        }
        catch
        {
            // Keep uninstall progressing even if some data files are locked.
        }
    }

    private static void RegisterWindowClass()
    {
        if (_dialogClassAtom != IntPtr.Zero)
        {
            return;
        }

        var hInstance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DialogWndProc),
            hInstance = hInstance,
            lpszClassName = UninstallDialogClassName,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            hbrBackground = WhiteBrush
        };

        _dialogClassAtom = RegisterClassW(ref windowClass);
        if (_dialogClassAtom == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to register uninstall dialog window class.");
        }
    }

    private static IntPtr UninstallDialogProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WindowMessage.WM_CREATE:
                CreateDialogControls(hwnd);
                return IntPtr.Zero;

            case WindowMessage.WM_COMMAND:
                HandleDialogCommand(hwnd, wParam);
                return IntPtr.Zero;

            case WindowMessage.WM_CTLCOLORSTATIC:
                SetBkMode(wParam, 1);
                return WhiteBrush;

            case WindowMessage.WM_CLOSE:
                _dialogState ??= new UninstallDialogState();
                _dialogState.Confirmed = false;
                _dialogState.Completed = true;
                if (_dialogState.TitleFont != IntPtr.Zero)
                {
                    DeleteObject(_dialogState.TitleFont);
                    _dialogState.TitleFont = IntPtr.Zero;
                }
                DestroyWindow(hwnd);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void CreateDialogControls(IntPtr hwnd)
    {
        var hInstance = GetModuleHandleW(null);
        var guiFont = GetStockObject(17);
        var titleFont = CreateFontW(
            -20,
            0,
            0,
            0,
            600,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            "Segoe UI");
        _dialogState ??= new UninstallDialogState();
        _dialogState.TitleFont = titleFont;

        var message = CreateWindowExW(
            0,
            "STATIC",
            "Do you want to uninstall Carton?",
            WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE,
            20,
            18,
            400,
            34,
            hwnd,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
        SendMessageW(message, WindowMessage.WM_SETFONT, titleFont, (IntPtr)1);

        var checkbox = CreateWindowExW(
            0,
            "BUTTON",
            "Also remove configuration data (AppData\\Roaming\\Carton)",
            WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE | WindowStyles.WS_TABSTOP | ButtonStyles.BS_AUTOCHECKBOX,
            20,
            74,
            360,
            24,
            hwnd,
            (IntPtr)ID_DELETE_DATA_CHECKBOX,
            hInstance,
            IntPtr.Zero);
        SendMessageW(checkbox, WindowMessage.WM_SETFONT, guiFont, (IntPtr)1);
        _dialogState.CheckboxHandle = checkbox;

        var uninstallButton = CreateWindowExW(
            0,
            "BUTTON",
            "Uninstall",
            WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE | WindowStyles.WS_TABSTOP | ButtonStyles.BS_DEFPUSHBUTTON,
            260,
            124,
            82,
            30,
            hwnd,
            (IntPtr)ID_UNINSTALL_BUTTON,
            hInstance,
            IntPtr.Zero);
        SendMessageW(uninstallButton, WindowMessage.WM_SETFONT, guiFont, (IntPtr)1);

        var cancelButton = CreateWindowExW(
            0,
            "BUTTON",
            "Cancel",
            WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE | WindowStyles.WS_TABSTOP,
            350,
            124,
            82,
            30,
            hwnd,
            (IntPtr)ID_CANCEL_BUTTON,
            hInstance,
            IntPtr.Zero);
        SendMessageW(cancelButton, WindowMessage.WM_SETFONT, guiFont, (IntPtr)1);
    }

    private static void HandleDialogCommand(IntPtr hwnd, IntPtr wParam)
    {
        var controlId = LowWord(wParam);
        _dialogState ??= new UninstallDialogState();

        if (controlId == ID_UNINSTALL_BUTTON)
        {
            _dialogState.DeleteData = _dialogState.CheckboxHandle != IntPtr.Zero &&
                                      SendMessageW(_dialogState.CheckboxHandle, ButtonMessage.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) ==
                                      (IntPtr)ButtonCheckState.BST_CHECKED;
            _dialogState.Confirmed = true;
            _dialogState.Completed = true;
            DestroyWindow(hwnd);
            return;
        }

        if (controlId == ID_CANCEL_BUTTON)
        {
            _dialogState.Confirmed = false;
            _dialogState.Completed = true;
            DestroyWindow(hwnd);
        }
    }

    private static void CenterWindow(IntPtr hwnd, int width, int height)
    {
        var screenWidth = GetSystemMetrics(SystemMetric.SM_CXSCREEN);
        var screenHeight = GetSystemMetrics(SystemMetric.SM_CYSCREEN);
        var x = Math.Max(0, (screenWidth - width) / 2);
        var y = Math.Max(0, (screenHeight - height) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_SHOWWINDOW);
    }

    private static int LowWord(IntPtr value) => unchecked((ushort)value.ToInt64());

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        MessageBoxType type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        SetWindowPosFlags uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int i);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int nHeight,
        int nWidth,
        int nEscapement,
        int nOrientation,
        int fnWeight,
        uint fdwItalic,
        uint fdwUnderline,
        uint fdwStrikeOut,
        uint fdwCharSet,
        uint fdwOutputPrecision,
        uint fdwClipPrecision,
        uint fdwQuality,
        uint fdwPitchAndFamily,
        string lpszFace);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private sealed class UninstallDialogState
    {
        public IntPtr CheckboxHandle { get; set; }
        public IntPtr TitleFont { get; set; }
        public bool DeleteData { get; set; }
        public bool Confirmed { get; set; }
        public bool Completed { get; set; }
    }

    private readonly record struct UninstallDialogResult(bool Confirmed, bool DeleteData);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static class WindowMessage
    {
        public const uint WM_CREATE = 0x0001;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_COMMAND = 0x0111;
        public const uint WM_CTLCOLORSTATIC = 0x0138;
        public const uint WM_SETFONT = 0x0030;
    }

    private static class ButtonMessage
    {
        public const uint BM_GETCHECK = 0x00F0;
    }

    private static class ButtonCheckState
    {
        public const int BST_CHECKED = 1;
    }

    private static class WindowStyles
    {
        public const int WS_OVERLAPPED = 0x00000000;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_TABSTOP = 0x00010000;
    }

    private static class ButtonStyles
    {
        public const int BS_AUTOCHECKBOX = 0x00000003;
        public const int BS_DEFPUSHBUTTON = 0x00000001;
    }

    private enum ShowWindowCommand
    {
        SW_SHOW = 5
    }

    private enum WindowColorBrush
    {
        COLOR_WINDOW = 5
    }

    private enum SystemMetric
    {
        SM_CXSCREEN = 0,
        SM_CYSCREEN = 1
    }

    [Flags]
    private enum SetWindowPosFlags : uint
    {
        SWP_NOZORDER = 0x0004,
        SWP_SHOWWINDOW = 0x0040
    }

    [Flags]
    private enum MessageBoxType : uint
    {
        Ok = 0x00000000,
        IconWarning = 0x00000030,
        TopMost = 0x00040000
    }
}
