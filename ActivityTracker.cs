using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clicko;

internal enum InputActivity
{
    Keyboard,
    Mouse
}

internal sealed class ActivityTracker : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int WmMouseMove = 0x0200;
    private const uint LlkhfInjected = 0x0010;
    private const uint LlkhfLowerIntegrityInjected = 0x0002;
    private const uint LlmhfInjected = 0x0001;
    private const uint LlmhfLowerIntegrityInjected = 0x0002;

    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HookNativeMethods.HookProcedure _keyboardProcedure;
    private readonly HookNativeMethods.HookProcedure _mouseProcedure;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public event Action<InputActivity>? ActivityDetected;
    public event Func<int, int, bool>? LeftButtonPressed;
    public event Func<int, int, bool>? PointerMovedWhileDragging;
    public event Func<bool>? LeftButtonReleased;

    public ActivityTracker()
    {
        _keyboardProcedure = KeyboardHookCallback;
        _mouseProcedure = MouseHookCallback;
    }

    public void Start()
    {
        var module = HookNativeMethods.GetModuleHandle(null);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to locate the application module for input tracking.");
        }

        _keyboardHook = HookNativeMethods.SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the keyboard activity hook.");
        }

        _mouseHook = HookNativeMethods.SetWindowsHookEx(WhMouseLl, _mouseProcedure, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            HookNativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw new Win32Exception(error, "Unable to install the mouse activity hook.");
        }
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            HookNativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            HookNativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _pressedKeys.Clear();
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var keyboardData = Marshal.PtrToStructure<KeyboardHookData>(data);
            if ((keyboardData.Flags & (LlkhfInjected | LlkhfLowerIntegrityInjected)) == 0)
            {
                var keyDown = message.ToInt32() is WmKeyDown or WmSysKeyDown;
                var keyUp = message.ToInt32() is WmKeyUp or WmSysKeyUp;
                if (keyDown && _pressedKeys.Add(keyboardData.VirtualKey))
                {
                    ActivityDetected?.Invoke(InputActivity.Keyboard);
                }
                else if (keyUp)
                {
                    _pressedKeys.Remove(keyboardData.VirtualKey);
                }
            }
        }

        return HookNativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var mouseData = Marshal.PtrToStructure<MouseHookData>(data);
            var mouseMessage = message.ToInt32();
            if (mouseMessage == WmMouseMove &&
                (PointerMovedWhileDragging?.Invoke(mouseData.X, mouseData.Y) ?? false))
            {
                return new IntPtr(1);
            }

            var isPhysical = (mouseData.Flags & (LlmhfInjected | LlmhfLowerIntegrityInjected)) == 0;
            if (mouseMessage == WmLeftButtonDown)
            {
                var clickoHandled = LeftButtonPressed?.Invoke(mouseData.X, mouseData.Y) ?? false;
                if (clickoHandled)
                {
                    if (isPhysical)
                    {
                        ActivityDetected?.Invoke(InputActivity.Mouse);
                    }

                    return new IntPtr(1);
                }
            }
            else if (mouseMessage == WmLeftButtonUp)
            {
                if (LeftButtonReleased?.Invoke() ?? false)
                {
                    return new IntPtr(1);
                }
            }

            var isButtonDown = mouseMessage is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmXButtonDown;
            if (isButtonDown && isPhysical)
            {
                ActivityDetected?.Invoke(InputActivity.Mouse);
            }
        }

        return HookNativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

internal static class HookNativeMethods
{
    internal delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookType, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
