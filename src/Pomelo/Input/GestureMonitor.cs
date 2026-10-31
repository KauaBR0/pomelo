using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Pomelo.Wheel;
using Pomelo.Core.Settings;
using static Pomelo.Interop.NativeMethods;

namespace Pomelo.Input;

/// <summary>
/// Detects "mouse button held + moved past the drag threshold, then the trigger key pressed"
/// anywhere on the desktop. Hooks live on a dedicated thread so a busy UI thread never delays
/// system input (Windows silently removes low-level hooks that respond slowly). Input is
/// observed, never consumed or recorded. Events are raised on the UI dispatcher.
/// </summary>
internal sealed class GestureMonitor : IDisposable
{
    /// <summary>
    /// A wheel key was pressed during a drag: open that wheel under the cursor, or switch to it
    /// if a wheel is already open.
    /// </summary>
    public event Action<WheelMode>? WheelRequested;
    /// <summary>The mouse button was released after a wheel request.</summary>
    public event Action? GestureEnded;
    /// <summary>Escape was pressed after a wheel request.</summary>
    public event Action? Cancelled;

    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>Key per wheel, indexed by <see cref="WheelMode"/>.</summary>
    public void SetKeys(WheelKey convert, WheelKey tools, WheelKey actions) => _keys = [convert, tools, actions];

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _startError;
    private uint _threadId;
    private volatile bool _enabled = true;
    private volatile WheelKey[] _keys = [WheelKey.Shift, WheelKey.Ctrl, WheelKey.Alt]; // Replaced, never mutated.

    // Hook-thread state only.
    private LowLevelHookProc? _mouseProc, _keyboardProc; // Rooted: native code holds these.
    private IntPtr _mouseHook, _keyboardHook;
    private int _dragDx, _dragDy;
    private bool _buttonDown, _dragging, _wheelRequested;
    private readonly bool[] _keyDown = new bool[3]; // Per WheelMode; filters key repeat.
    private POINT _downPoint;

    public GestureMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pomelo input hooks" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
            throw _startError;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _dragDx = GetSystemMetrics(SM_CXDRAG);
        _dragDy = GetSystemMetrics(SM_CYDRAG);
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
        IntPtr module = GetModuleHandleW(null);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook != IntPtr.Zero)
            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível instalar os hooks de entrada.");
            Unhook();
            _ready.Set();
            return;
        }

        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // Create the queue before Dispose can post WM_QUIT.
        _ready.Set();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Unhook();
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN or WM_RBUTTONDOWN:
                    _buttonDown = true;
                    _dragging = false;
                    _downPoint = info.pt;
                    break;
                case WM_MOUSEMOVE when _buttonDown && !_dragging:
                    _dragging = Math.Abs(info.pt.X - _downPoint.X) > _dragDx
                                || Math.Abs(info.pt.Y - _downPoint.Y) > _dragDy;
                    break;
                case WM_LBUTTONUP or WM_RBUTTONUP:
                    bool ended = _wheelRequested;
                    _buttonDown = _dragging = _wheelRequested = false;
                    if (ended)
                        Raise(GestureEnded);
                    break;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            int wheel = Array.FindIndex(_keys, key => IsKey(key, info.vkCode));
            if (wheel >= 0)
            {
                var requested = (WheelMode)wheel;
                // Key repeat sends repeated key-downs; react to the first one only.
                if (down && !_keyDown[wheel] && _dragging && _enabled)
                {
                    _wheelRequested = true;
                    _dispatcher.BeginInvoke(() => WheelRequested?.Invoke(requested));
                }
                _keyDown[wheel] = down;
            }
            else if (down && info.vkCode == VK_ESCAPE && _wheelRequested)
            {
                _wheelRequested = false;
                Raise(Cancelled);
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsKey(WheelKey key, uint vk) => key switch
    {
        WheelKey.Ctrl => vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL,
        WheelKey.Alt => vk is VK_MENU or VK_LMENU or VK_RMENU,
        _ => vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT,
    };

    private void Raise(Action? handler)
    {
        if (handler is not null)
            _dispatcher.BeginInvoke(handler);
    }

    private void Unhook()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }
}
