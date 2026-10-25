using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace Clicko;

public partial class MainWindow : Window
{
    private const int SpriteSize = 160;
    private const int QuitHotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMouseMove = 0x0200;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly string[] KeyboardAnimation =
        ["cat-keyboard-1.png", "cat-keyboard-2.png", "cat-keyboard-3.png", "cat-keyboard-2.png"];
    private static readonly string[] MouseAnimation =
        ["cat-mouse-1.png", "cat-mouse-2.png", "cat-mouse-3.png", "cat-mouse-2.png"];
    private static readonly string[] BlinkAnimation = ["cat-blink.png", "cat-idle.png"];
    private static readonly string[] SleepAnimation = ["cat-sleep.png", "cat-blink.png"];

    private readonly ActivityTracker _activityTracker = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _inactivityTimer;
    private readonly Dictionary<string, BitmapImage> _sprites = new();
    private readonly Dictionary<string, byte[]> _spriteAlphaMasks = new();
    private HwndSource? _windowSource;
    private string _currentSpriteName = "cat-idle.png";
    private bool _quitHotkeyRegistered;
    private long _keyboardCount;
    private long _mouseCount;
    private string[] _animationFrames = Array.Empty<string>();
    private int _animationFrame;
    private bool _actionAnimation;
    private bool _sleeping;
    private bool _loopAnimation;
    private bool _isDragging;
    private bool _leftPressIntercepted;
    private NativeMethods.NativePoint _dragStartCursor;
    private NativeMethods.NativeRect _dragStartWindow;
    private DateTime _actionActiveUntil;

    public MainWindow()
    {
        InitializeComponent();
        _activityTracker.ActivityDetected += ActivityTracker_ActivityDetected;
        _activityTracker.LeftButtonPressed += ActivityTracker_LeftButtonPressed;
        _activityTracker.PointerMovedWhileDragging += ActivityTracker_PointerMovedWhileDragging;
        _activityTracker.LeftButtonReleased += ActivityTracker_LeftButtonReleased;
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(115) };
        _animationTimer.Tick += AnimationTimer_Tick;
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _blinkTimer.Tick += BlinkTimer_Tick;
        _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _inactivityTimer.Tick += InactivityTimer_Tick;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _windowSource.AddHook(WindowMessageHook);

        try
        {
            _activityTracker.Start();
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(
                $"Clicko could not start input tracking.\n\n{exception.Message}",
                "Clicko",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        _quitHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowSource.Handle, QuitHotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 'Q');
        var quitHotkeyError = _quitHotkeyRegistered ? 0 : Marshal.GetLastWin32Error();

        try
        {
            SetClickThrough(true);
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(
                $"Clicko could not enable click-through mode.\n\n{exception.Message}",
                "Clicko",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        if (!_quitHotkeyRegistered)
        {
            MessageBox.Show(
                $"Could not register Ctrl+Alt+Q (Windows error {quitHotkeyError}). You may need to close Clicko from Task Manager.",
                "Clicko",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSprites();
            BuildSpriteAlphaMasks();
            SetSprite("cat-idle.png");
            _blinkTimer.Start();
            _inactivityTimer.Start();
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is FormatException)
        {
            MessageBox.Show(
                $"Could not load the cat sprites from the Sprites folder beside the app.\n\n{exception.Message}",
                "Clicko",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _animationTimer.Stop();
        _blinkTimer.Stop();
        _inactivityTimer.Stop();
        _activityTracker.ActivityDetected -= ActivityTracker_ActivityDetected;
        _activityTracker.LeftButtonPressed -= ActivityTracker_LeftButtonPressed;
        _activityTracker.PointerMovedWhileDragging -= ActivityTracker_PointerMovedWhileDragging;
        _activityTracker.LeftButtonReleased -= ActivityTracker_LeftButtonReleased;
        _activityTracker.Dispose();

        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);

            if (_quitHotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, QuitHotkeyId);
            }
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest && !_isDragging)
        {
            if (IsCursorOverVisibleCatPixel())
            {
                handled = true;
                return new IntPtr(HtClient);
            }

            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        if (wParam.ToInt32() == QuitHotkeyId)
        {
            Close();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLong(handle, GwlExStyle);
        var updatedStyle = enabled
            ? extendedStyle | WsExTransparent
            : extendedStyle & ~WsExTransparent;
        if (updatedStyle == extendedStyle)
        {
            return;
        }

        Marshal.SetLastPInvokeError(0);
        var previousStyle = NativeMethods.SetWindowLong(handle, GwlExStyle, updatedStyle);
        var error = Marshal.GetLastWin32Error();
        if (previousStyle == 0 && error != 0)
        {
            throw new Win32Exception(error, "Unable to update Clicko's mouse input style.");
        }

        if (!NativeMethods.SetWindowPos(
            handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to update Clicko window styles.");
        }

    }

    private bool ActivityTracker_LeftButtonPressed(int screenX, int screenY)
    {
        if (_isDragging ||
            _spriteAlphaMasks.Count == 0 ||
            !IsVisibleCatPixel(CatSprite.PointFromScreen(new Point(screenX, screenY))))
        {
            return false;
        }

        SetClickThrough(false);
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetCapture(handle);
        if (NativeMethods.GetCapture() != handle)
        {
            SetClickThrough(true);
            throw new InvalidOperationException("Unable to capture the initial mouse press on Clicko.");
        }

        _leftPressIntercepted = true;
        ForwardMouseMessage(WmLeftButtonDown, 1, screenX, screenY);
        if (!_isDragging)
        {
            NativeMethods.ReleaseCapture();
            SetClickThrough(true);
        }

        return true;
    }

    private bool ActivityTracker_PointerMovedWhileDragging(int screenX, int screenY)
    {
        if (!_isDragging)
        {
            return false;
        }

        ForwardMouseMessage(WmMouseMove, 1, screenX, screenY);
        return true;
    }

    private bool ActivityTracker_LeftButtonReleased()
    {
        if (_isDragging)
        {
            ForwardMouseMessage(WmLeftButtonUp, 0, 0, 0);
            return true;
        }

        if (_leftPressIntercepted)
        {
            _leftPressIntercepted = false;
            NativeMethods.ReleaseCapture();
            SetClickThrough(true);
            return true;
        }

        return false;
    }

    private void ForwardMouseMessage(int message, int buttonState, int screenX, int screenY)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var clientPoint = new NativeMethods.NativePoint { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(handle, ref clientPoint))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to convert the cursor position for Clicko.");
        }

        var coordinates = unchecked((int)((ushort)clientPoint.X | ((uint)(ushort)clientPoint.Y << 16)));
        NativeMethods.SendMessage(handle, message, new IntPtr(buttonState), new IntPtr(coordinates));
    }

    private bool IsCursorOverVisibleCatPixel()
    {
        if (_spriteAlphaMasks.Count == 0)
        {
            return false;
        }

        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            return false;
        }

        var localPosition = CatSprite.PointFromScreen(new Point(cursorPosition.X, cursorPosition.Y));
        return IsVisibleCatPixel(localPosition);
    }

    private bool IsVisibleCatPixel(Point position)
    {
        if (!_spriteAlphaMasks.TryGetValue(_currentSpriteName, out var alphaMask) ||
            position.X < 0 ||
            position.Y < 0 ||
            position.X >= CatSprite.ActualWidth ||
            position.Y >= CatSprite.ActualHeight)
        {
            return false;
        }

        var x = Math.Min(SpriteSize - 1, (int)(position.X / CatSprite.ActualWidth * SpriteSize));
        var y = Math.Min(SpriteSize - 1, (int)(position.Y / CatSprite.ActualHeight * SpriteSize));
        return alphaMask[y * SpriteSize + x] > 12;
    }

    private void BuildSpriteAlphaMasks()
    {
        foreach (var sprite in _sprites)
        {
            if (sprite.Value.PixelWidth != SpriteSize || sprite.Value.PixelHeight != SpriteSize)
            {
                throw new InvalidDataException(
                    $"Sprite '{sprite.Key}' must be {SpriteSize} by {SpriteSize} pixels.");
            }

            var pixels = new byte[SpriteSize * SpriteSize * 4];
            new FormatConvertedBitmap(sprite.Value, PixelFormats.Bgra32, null, 0)
                .CopyPixels(pixels, SpriteSize * 4, 0);
            var alphaMask = new byte[SpriteSize * SpriteSize];
            for (var pixelIndex = 0; pixelIndex < alphaMask.Length; pixelIndex++)
            {
                alphaMask[pixelIndex] = pixels[pixelIndex * 4 + 3];
            }

            _spriteAlphaMasks.Add(sprite.Key, alphaMask);
        }
    }

    private void CatSprite_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CatSprite);
        var visiblePixel = IsVisibleCatPixel(position);
        if (e.ChangedButton != MouseButton.Left || !visiblePixel)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out _dragStartCursor))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the cursor position.");
        }

        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out _dragStartWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read Clicko's window position.");
        }

        _isDragging = true;
        if (!Mouse.Capture(CatSprite, CaptureMode.SubTree))
        {
            _isDragging = false;
            SetClickThrough(true);
            return;
        }

        e.Handled = true;
    }

    private void CatSprite_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursorPosition))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the cursor position.");
        }

        var x = _dragStartWindow.Left + cursorPosition.X - _dragStartCursor.X;
        var y = _dragStartWindow.Top + cursorPosition.Y - _dragStartCursor.Y;
        if (!NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to move Clicko while dragging.");
        }

        e.Handled = true;
    }

    private void CatSprite_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            Mouse.Capture(null);
            _isDragging = false;
            _leftPressIntercepted = false;
            SetClickThrough(true);
        }
    }

    private void CatSprite_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _leftPressIntercepted = false;
            SetClickThrough(true);
        }
    }

    private void ActivityTracker_ActivityDetected(InputActivity activity)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
            _blinkTimer.Stop();
            _blinkTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(4, 8));
            _blinkTimer.Start();
            _sleeping = false;
            SleepGlyph.Visibility = Visibility.Collapsed;

            if (activity == InputActivity.Keyboard)
            {
                _keyboardCount++;
                if (_sprites.Count > 0)
                {
                    PlayActionAnimation(KeyboardAnimation);
                }
            }
            else
            {
                _mouseCount++;
                if (_sprites.Count > 0)
                {
                    PlayActionAnimation(MouseAnimation);
                }
            }

            KeyboardCountText.Text = _keyboardCount.ToString("N0");
            MouseCountText.Text = _mouseCount.ToString("N0");
        }));
    }

    private void LoadSprites()
    {
        var names = new[]
        {
            "cat-idle.png",
            "cat-blink.png",
            "cat-keyboard-1.png",
            "cat-keyboard-2.png",
            "cat-keyboard-3.png",
            "cat-mouse-1.png",
            "cat-mouse-2.png",
            "cat-mouse-3.png",
            "cat-sleep.png"
        };

        foreach (var name in names)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Sprites", name);
            var sprite = new BitmapImage();
            sprite.BeginInit();
            sprite.CacheOption = BitmapCacheOption.OnLoad;
            sprite.UriSource = new Uri(path, UriKind.Absolute);
            sprite.EndInit();
            if (sprite.PixelWidth != SpriteSize || sprite.PixelHeight != SpriteSize)
            {
                throw new InvalidDataException(
                    $"Sprite '{name}' must be {SpriteSize} by {SpriteSize} pixels.");
            }

            sprite.Freeze();
            _sprites.Add(name, sprite);
        }
    }

    private void PlayActionAnimation(string[] frames)
    {
        _actionActiveUntil = DateTime.UtcNow.AddMilliseconds(450);
        if (_actionAnimation && _animationTimer.IsEnabled && ReferenceEquals(_animationFrames, frames))
        {
            return;
        }

        SleepGlyph.Visibility = Visibility.Collapsed;
        PlayAnimation(frames, TimeSpan.FromMilliseconds(105), true, false);
    }

    private void PlayAnimation(string[] frames, TimeSpan interval, bool isAction, bool loop)
    {
        _animationTimer.Stop();
        _animationFrames = frames;
        _animationFrame = 0;
        _actionAnimation = isAction;
        _loopAnimation = loop;
        _animationTimer.Interval = interval;
        SetSprite(_animationFrames[_animationFrame++], true);
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_animationFrame >= _animationFrames.Length)
        {
            if (_actionAnimation && DateTime.UtcNow < _actionActiveUntil)
            {
                _animationFrame = 0;
                SetSprite(_animationFrames[_animationFrame++]);
                return;
            }

            if (_sleeping && _loopAnimation)
            {
                _animationFrame = 0;
                _animationTimer.Interval = TimeSpan.FromMilliseconds(850);
                SetSprite(_animationFrames[_animationFrame++]);
                return;
            }

            _animationTimer.Stop();
            _actionAnimation = false;
            SetSprite("cat-idle.png", true);
            return;
        }

        var frame = _animationFrames[_animationFrame++];
        SetSprite(frame, frame == "cat-idle.png");
    }

    private void BlinkTimer_Tick(object? sender, EventArgs e)
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(4, 8));
        if (!_sleeping && !_actionAnimation && !_animationTimer.IsEnabled)
        {
            PlayAnimation(BlinkAnimation, TimeSpan.FromMilliseconds(110), false, false);
        }
    }

    private void InactivityTimer_Tick(object? sender, EventArgs e)
    {
        _inactivityTimer.Stop();
        if (_actionAnimation)
        {
            _inactivityTimer.Start();
            return;
        }

        _sleeping = true;
        _actionAnimation = false;
        _blinkTimer.Stop();
        SleepGlyph.Visibility = Visibility.Visible;
        PlayAnimation(SleepAnimation, TimeSpan.FromMilliseconds(900), false, true);
    }

    private void SetSprite(string name, bool fadeIn = false)
    {
        CatSprite.BeginAnimation(OpacityProperty, null);
        _currentSpriteName = name;
        CatSprite.Source = _sprites[name];
        if (fadeIn)
        {
            CatSprite.Opacity = 0.65;
            CatSprite.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            CatSprite.Opacity = 1;
        }
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
