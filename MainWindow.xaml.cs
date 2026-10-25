using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace Clicko;

public partial class MainWindow : Window
{
    private const int SpriteSize = 160;
    private const int ToggleHotkeyId = 1;
    private const int QuitHotkeyId = 2;
    private const int WmHotkey = 0x0312;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
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
    private bool _clickThrough = true;
    private bool _toggleHotkeyRegistered;
    private bool _quitHotkeyRegistered;
    private long _keyboardCount;
    private long _mouseCount;
    private string[] _animationFrames = Array.Empty<string>();
    private int _animationFrame;
    private bool _actionAnimation;
    private bool _sleeping;
    private bool _loopAnimation;
    private DateTime _actionActiveUntil;

    public MainWindow()
    {
        InitializeComponent();
        _activityTracker.ActivityDetected += ActivityTracker_ActivityDetected;
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

        _toggleHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowSource.Handle, ToggleHotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 'B');
        var toggleHotkeyError = _toggleHotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
        _quitHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowSource.Handle, QuitHotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 'Q');
        var quitHotkeyError = _quitHotkeyRegistered ? 0 : Marshal.GetLastWin32Error();

        if (!_toggleHotkeyRegistered)
        {
            _clickThrough = false;
            MessageBox.Show(
                $"Could not register Ctrl+Alt+B (Windows error {toggleHotkeyError}). The pet opened in interactive mode; use the buttons to drag or quit.",
                "Clicko",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            SetClickThrough(true);
        }

        UpdateModeControls();

        if (!_quitHotkeyRegistered)
        {
            MessageBox.Show(
                $"Could not register Ctrl+Alt+Q (Windows error {quitHotkeyError}). Quit from the pet's interactive mode instead.",
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
        _activityTracker.Dispose();

        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);

            if (_toggleHotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, ToggleHotkeyId);
            }

            if (_quitHotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_windowSource.Handle, QuitHotkeyId);
            }
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case ToggleHotkeyId:
                SetClickThrough(!_clickThrough);
                handled = true;
                break;
            case QuitHotkeyId:
                Close();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLong(handle, GwlExStyle);
        extendedStyle = enabled
            ? extendedStyle | WsExTransparent
            : extendedStyle & ~WsExTransparent;
        NativeMethods.SetWindowLong(handle, GwlExStyle, extendedStyle);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
        UpdateModeControls();
    }

    private void UpdateModeControls()
    {
        ModeButton.Visibility = _clickThrough ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = _clickThrough ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetClickThrough(!_clickThrough);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CatSprite_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_clickThrough &&
            e.ChangedButton == System.Windows.Input.MouseButton.Left &&
            IsVisibleCatPixel(e.GetPosition(CatSprite)))
        {
            e.Handled = true;
            DragMove();
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
            sprite.Freeze();
            _sprites.Add(name, sprite);
        }
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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
