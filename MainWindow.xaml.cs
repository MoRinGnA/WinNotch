using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinNotch.Models;
using WinNotch.Services;
using MenuItem = System.Windows.Forms.MenuItem;

namespace WinNotch
{
    public partial class MainWindow : Window
    {
        private enum ViewMode { IdleCompact, IdleExpanded, MediaCompact, MediaExpanded, VolumeHud }

        private enum NotchTheme { Dark, LiquidGlass }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        private const double VolumeBarExtraHeight = 54;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_ENABLE_HOSTBACKDROP = 5
        }

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private void EnableAcrylicBlur(bool enable)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var accent = new AccentPolicy
            {
                AccentState = enable ? AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND : AccentState.ACCENT_DISABLED,
                GradientColor = unchecked((int)0x99141212)
            };

            int accentStructSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private readonly AudioService _audioService;
        private readonly MediaService _mediaService;
        private readonly LyricsService _lyricsService;

        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _clockTimer;
        private DispatcherTimer? _volumeHudTimer;
        private Storyboard? _eqStoryboard;
        private Storyboard? _glassLightStoryboard;
        private Storyboard? _ambientBreathStoryboard;

        private ViewMode _currentViewMode = ViewMode.IdleCompact;
        private NotchTheme _currentTheme = NotchTheme.Dark;
        private string _lastMediaKey = string.Empty;
        private bool _isExpanded = false;
        private bool _hasLyrics = false;
        private bool _isVolumeAdjusting = false;
        private bool _volumeBarVisible = false;
        private bool _ambientActive = false;
        private Color _currentAmbientColor = Colors.Transparent;
        private List<LyricLine> _syncedLyrics = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool HasMedia => !string.IsNullOrEmpty(_lastMediaKey);

        public MainWindow()
        {
            InitializeComponent();

            _audioService = new AudioService();
            _mediaService = new MediaService();
            _lyricsService = new LyricsService();

            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _progressTimer.Tick += ProgressTimer_Tick;

            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            ClockTimer_Tick(null, EventArgs.Empty);

            _audioService.VolumeChanged += AudioService_VolumeChanged;
            _mediaService.MediaChanged += MediaService_MediaChanged;
            _mediaService.PlaybackStatusChanged += MediaService_PlaybackStatusChanged;
            _mediaService.TimelineChanged += MediaService_TimelineChanged;

            Closed += MainWindow_Closed;

            InitializeTrayIcon();

            _ = _mediaService.InitializeAsync();
            SwitchViewMode(ViewMode.IdleCompact);
            ApplyTheme(NotchTheme.Dark);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TOOLWINDOW);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            AddClipboardFormatListener(hwnd);

            UpdatePosition();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        Dispatcher.InvokeAsync(() => ShowClipboardToast(text));
                    }
                }
                catch { }
            }
            if (msg == WM_NCHITTEST)
            {
                try
                {
                    int x = unchecked((short)(long)lParam);
                    int y = unchecked((short)((long)lParam >> 16));

                    if (!NotchBorder.IsLoaded)
                    {
                        handled = true;
                        return (IntPtr)HTTRANSPARENT;
                    }

                    Point pt = NotchBorder.PointFromScreen(new Point(x, y));

                    if (pt.X < 0 || pt.Y < 0 || pt.X > NotchBorder.ActualWidth || pt.Y > NotchBorder.ActualHeight)
                    {
                        handled = true;
                        return (IntPtr)HTTRANSPARENT;
                    }
                }
                catch
                {
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowClipboardToast(string text)
        {
            CompactTitleText.Text = "\U0001f4cb 복사됨";
            CompactLyricText.Text = text.Length > 20 ? text.Substring(0, 20) + "..." : text;
            SwitchViewMode(ViewMode.MediaCompact);

            Task.Delay(3000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
                    if (HasMedia)
                    {
                        string[] parts = _lastMediaKey.Split(new[] { ":::" }, StringSplitOptions.None);
                        if (parts.Length > 1) CompactTitleText.Text = parts[1];
                        CompactLyricText.Text = "";
                    }
                });
            });
        }

        private void UpdatePosition()
        {
            this.Left = 0;
            this.Top = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;
        }

        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            IdleTimeText.Text = now.ToString("HH:mm");
            IdleExpandedTimeText.Text = now.ToString("HH:mm");
            IdleExpandedDateText.Text = now.ToString("M월 d일 dddd");
        }

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _isExpanded = true;
            UpdateThemeToggleVisibility();
            if (_volumeHudTimer != null && _volumeHudTimer.IsEnabled) return;
            SwitchViewMode(HasMedia ? ViewMode.MediaExpanded : ViewMode.IdleExpanded);
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            _isExpanded = false;
            _isVolumeAdjusting = false;
            UpdateThemeToggleVisibility();
            HideVolumeBarExpanded();
            if (_volumeHudTimer != null && _volumeHudTimer.IsEnabled) return;
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
        }

        private void Notch_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            float step = 0.02f;
            int newVol = _audioService.StepVolume(e.Delta > 0 ? step : -step, out bool isMuted);
            ShowVolumeHud(newVol, isMuted);
            e.Handled = true;
        }

        private void ApplyTheme(NotchTheme theme)
        {
            _currentTheme = theme;

            string styleKey = theme == NotchTheme.LiquidGlass ? "LiquidGlassNotchStyle" : "DarkNotchStyle";
            NotchBorder.Style = (Style)FindResource(styleKey);

            bool isGlass = theme == NotchTheme.LiquidGlass;
            GlassOverlayLayers.Visibility = isGlass ? Visibility.Visible : Visibility.Collapsed;

            if (isGlass)
            {
                StartGlassLightAnimation();
            }
            else
            {
                StopGlassLightAnimation();
            }

            ThemeToggleIcon.Fill = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(isGlass ? "#FFD60A" : "#8E8E93"));
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(_currentTheme == NotchTheme.Dark ? NotchTheme.LiquidGlass : NotchTheme.Dark);
        }

        private void UpdateThemeToggleVisibility()
        {
            bool show = _isExpanded && !_volumeBarVisible;

            DoubleAnimation fade = new DoubleAnimation
            {
                To = show ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(300)
            };
            ThemeToggleButton.BeginAnimation(UIElement.OpacityProperty, fade);
            ThemeToggleButton.IsHitTestVisible = show;
        }

        private void StartGlassLightAnimation()
        {
            StopGlassLightAnimation();

            var rotateAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(rotateAnim, 30);

            _glassLightStoryboard = new Storyboard();
            _glassLightStoryboard.Children.Add(rotateAnim);
            Storyboard.SetTarget(rotateAnim, GlassLightRotate);
            Storyboard.SetTargetProperty(rotateAnim, new PropertyPath(RotateTransform.AngleProperty));
            _glassLightStoryboard.Begin();
        }

        private void StopGlassLightAnimation()
        {
            _glassLightStoryboard?.Stop();
            _glassLightStoryboard = null;
        }

        private void SetAmbientColor(Color color)
        {
            _currentAmbientColor = color;

            BezelTopBrush.Color = color;
            BezelBottomBrush.Color = color;
            BezelLeftBrush.Color = color;
            BezelRightBrush.Color = color;

            NotchAmbientEffect.Color = color;

            if (!_ambientActive)
            {
                _ambientActive = true;
                StartAmbientBreathAnimation();
            }
        }

        private void ClearAmbientLight()
        {
            _ambientActive = false;
            StopAmbientBreathAnimation();

            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
            BezelTop.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            BezelBottom.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            BezelLeft.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            BezelRight.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void StartAmbientBreathAnimation()
        {
            StopAmbientBreathAnimation();

            _ambientBreathStoryboard = new Storyboard();

            Rectangle[] edges = { BezelTop, BezelBottom, BezelLeft, BezelRight };
            double[] minOpacity = { 0.35, 0.20, 0.25, 0.25 };
            double[] maxOpacity = { 0.75, 0.45, 0.55, 0.55 };
            double[] durations = { 2.0, 2.5, 2.2, 2.3 };

            for (int i = 0; i < edges.Length; i++)
            {
                var anim = new DoubleAnimation(minOpacity[i], maxOpacity[i], TimeSpan.FromSeconds(durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Timeline.SetDesiredFrameRate(anim, 30);
                Storyboard.SetTarget(anim, edges[i]);
                Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
                _ambientBreathStoryboard.Children.Add(anim);
            }

            var notchGlowAnim = new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(notchGlowAnim, 30);
            Storyboard.SetTarget(notchGlowAnim, NotchGlowBorder);
            Storyboard.SetTargetProperty(notchGlowAnim, new PropertyPath(UIElement.OpacityProperty));
            _ambientBreathStoryboard.Children.Add(notchGlowAnim);

            _ambientBreathStoryboard.Begin();
        }

        private void StopAmbientBreathAnimation()
        {
            _ambientBreathStoryboard?.Stop();
            _ambientBreathStoryboard = null;
        }

        private double CalculateCompactWidth()
        {
            string title = CompactTitleText.Text ?? "";
            string lyric = CompactLyricText.Text ?? "";

            double titleWidth = MeasureTextWidth(title, 13.5, FontWeights.SemiBold);
            double lyricWidth = MeasureTextWidth(lyric, 13, FontWeights.Medium);

            double baseWidth = 45;
            double calculated = baseWidth + titleWidth + lyricWidth + 35;
            return Math.Clamp(calculated, 320, 800);
        }

        private double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, -apple-system"), FontStyles.Normal, fontWeight, FontStretches.Normal),
                fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            return formattedText.Width;
        }

        private void SwitchViewMode(ViewMode mode)
        {
            _currentViewMode = mode;

            Duration duration = new Duration(TimeSpan.FromMilliseconds(450));
            ExponentialEase ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

            double targetWidth = 100;
            double targetHeight = 38;
            double targetRadius = 19;
            UIElement activeView = IdleCompactView;

            switch (mode)
            {
                case ViewMode.IdleCompact:
                    targetWidth = 100;
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = IdleCompactView;
                    break;
                case ViewMode.IdleExpanded:
                    targetWidth = 260;
                    targetHeight = 120;
                    targetRadius = 26;
                    activeView = IdleExpandedView;
                    break;
                case ViewMode.MediaCompact:
                    targetWidth = CalculateCompactWidth();
                    targetHeight = 38;
                    targetRadius = 19;
                    activeView = MediaCompactView;
                    break;
                case ViewMode.MediaExpanded:
                    targetWidth = _hasLyrics ? 620 : 276;
                    targetHeight = 190;
                    targetRadius = 36;
                    activeView = MediaExpandedView;
                    break;
                case ViewMode.VolumeHud:
                    targetWidth = _isExpanded ? (_hasLyrics ? 620 : 276) : 240;
                    targetHeight = _isExpanded ? 190 : 38;
                    targetRadius = _isExpanded ? 36 : 19;
                    activeView = _isExpanded ? MediaExpandedView : VolumeHudView;
                    break;
            }

            SetViewActive(activeView, duration, ease);
            NotchBorder.CornerRadius = new CornerRadius(targetRadius);
            NotchGlowBorder.CornerRadius = new CornerRadius(targetRadius);

            DoubleAnimation widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            DoubleAnimation heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            DoubleAnimation glowWidthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = ease };
            DoubleAnimation glowHeightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };

            Timeline.SetDesiredFrameRate(widthAnim, 60);
            Timeline.SetDesiredFrameRate(heightAnim, 60);

            NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(Border.HeightProperty, heightAnim);
            NotchGlowBorder.BeginAnimation(Border.WidthProperty, glowWidthAnim);
            NotchGlowBorder.BeginAnimation(Border.HeightProperty, glowHeightAnim);

            DoubleAnimation mainContainerHeightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(mainContainerHeightAnim, 60);
            MainContainer.BeginAnimation(FrameworkElement.HeightProperty, mainContainerHeightAnim);
        }

        private void SetViewActive(UIElement activeView, Duration duration, IEasingFunction ease)
        {
            UIElement[] views = { VolumeHudView, IdleCompactView, IdleExpandedView, MediaCompactView, MediaExpandedView };
            Duration fadeOutDuration = new Duration(TimeSpan.FromMilliseconds(150));

            foreach (var view in views)
            {
                if (view == activeView)
                {
                    view.IsHitTestVisible = true;
                    DoubleAnimation fadeIn = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
                    view.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                else
                {
                    view.IsHitTestVisible = false;
                    DoubleAnimation fadeOut = new DoubleAnimation { To = 0, Duration = fadeOutDuration };
                    view.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
            }
        }

        private void AudioService_VolumeChanged(int volume, bool isMuted)
        {
            Dispatcher.Invoke(() =>
            {
                ShowVolumeHud(volume, isMuted);
            });
        }

        private void ShowVolumeHud(int volumeVal, bool isMuted)
        {
            _isVolumeAdjusting = true;
            string volStr = isMuted ? "Mute" : $"{volumeVal}%";

            VolumeProgressBar.Value = volumeVal;
            VolumeText.Text = volStr;

            VolumeBarProgressBar.Value = volumeVal;
            VolumeBarText.Text = volStr;

            if (_isExpanded)
            {
                ShowVolumeBarExpanded();
            }
            else
            {
                SwitchViewMode(ViewMode.VolumeHud);
            }
            StartHudTimer();
        }
        private static readonly Duration NotchAnimDuration = new Duration(TimeSpan.FromMilliseconds(450));
        private static readonly ExponentialEase NotchAnimEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        private void ShowVolumeBarExpanded()
        {
            VolumeBarPanel.Visibility = Visibility.Visible;

            double baseHeight = HasMedia ? 190 : 120;
            double targetHeight = baseHeight + VolumeBarExtraHeight;

            NotchBorder.BeginAnimation(Border.HeightProperty,
                new DoubleAnimation { To = targetHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            VolumeBarPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation { To = 1, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            VolumeBarTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            _volumeBarVisible = true;
            UpdateThemeToggleVisibility();
        }

        private void HideVolumeBarExpanded()
        {
            if (!_volumeBarVisible) return;
            _volumeBarVisible = false;
            UpdateThemeToggleVisibility();

            double baseHeight = HasMedia ? 190 : 120;

            NotchBorder.BeginAnimation(Border.HeightProperty,
                new DoubleAnimation { To = baseHeight, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });

            var opacityAnim = new DoubleAnimation { To = 0, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase };
            opacityAnim.Completed += (s, e) =>
            {
                if (!_volumeBarVisible)
                {
                    VolumeBarPanel.Visibility = Visibility.Collapsed;
                }
            };
            VolumeBarPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            VolumeBarTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { To = -10, Duration = NotchAnimDuration, EasingFunction = NotchAnimEase });
        }

        private void StartHudTimer()
        {
            if (_volumeHudTimer == null)
            {
                _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
                _volumeHudTimer.Tick += (s, args) =>
                {
                    _volumeHudTimer.Stop();
                    _isVolumeAdjusting = false;

                    if (_isExpanded)
                    {
                        HideVolumeBarExpanded();
                    }
                    else
                    {
                        SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
                    }
                };
            }
            _volumeHudTimer.Stop();
            _volumeHudTimer.Start();
        }

        private void MediaService_MediaChanged(MediaMetadata? media)
        {
            Dispatcher.Invoke(async () =>
            {
                if (media == null || string.IsNullOrWhiteSpace(media.Title))
                {
                    ResetToEmptyMedia();
                    return;
                }

                string key = $"{media.Artist}:::{media.Title}";
                bool isNewTrack = _lastMediaKey != key;

                if (isNewTrack)
                {
                    _lastMediaKey = key;

                    var parsed = LyricsService.ParseTitleAndArtist(media.Title, media.Artist);
                    string displayTitle = string.IsNullOrEmpty(parsed.Title) ? media.Title : parsed.Title;
                    string displayArtist = string.IsNullOrEmpty(parsed.Artist) ? (string.IsNullOrEmpty(media.Artist) ? "YouTube" : media.Artist) : parsed.Artist;

                    CompactTitleText.Text = displayTitle;
                    if (!_isVolumeAdjusting)
                    {
                        ExpandedTitleText.Text = displayTitle;
                        ExpandedArtistText.Text = displayArtist;
                    }

                    string searchArtist = displayArtist == "YouTube" ? string.Empty : displayArtist;
                    await LoadLyricsAsync(displayTitle, searchArtist);

                    if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
                    {
                        SwitchViewMode(_isExpanded ? ViewMode.MediaExpanded : ViewMode.MediaCompact);
                    }
                }

                bool isValidThumbnail = false;
                BitmapSource? validBmp = null;
                if (media.HasThumbnail && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        isValidThumbnail = true;
                        validBmp = bmp;
                    }
                }

                if (isValidThumbnail && validBmp != null)
                {
                    ExpandedAlbumArtImage.Source = media.Thumbnail;
                    ExpandedDefaultIcon.Visibility = Visibility.Collapsed;

                    var avgColor = GetDominantColor(validBmp);
                    SetAmbientColor(avgColor);
                }
                else
                {
                    ExpandedAlbumArtImage.Source = null;
                    ExpandedDefaultIcon.Visibility = Visibility.Visible;

                    SetAmbientColor(GenerateFallbackColor(media.Title));

                    if (isNewTrack)
                    {
                        _ = RetryThumbnailAsync(key);
                    }
                }

                UpdatePlaybackState(media.IsPlaying);
                UpdateTimelineDisplay(media.CurrentEstimatedPosition, media.Duration);
            });
        }

        private Color GenerateFallbackColor(string title)
        {
            int hash = Math.Abs(title.GetHashCode());
            double hue = (hash % 360);
            double saturation = 0.6 + (hash % 30) / 100.0;
            double lightness = 0.55 + (hash % 20) / 100.0;
            return HslToColor(hue, saturation, lightness);
        }

        private static Color HslToColor(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;

            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }

            return Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255));
        }

        private Color GetDominantColor(BitmapSource bitmap)
        {
            try
            {
                int targetW = Math.Min(bitmap.PixelWidth, 40);
                int targetH = Math.Min(bitmap.PixelHeight, 40);
                var scaled = new TransformedBitmap(bitmap, new ScaleTransform(
                    (double)targetW / bitmap.PixelWidth,
                    (double)targetH / bitmap.PixelHeight));

                var formatConverted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int width = formatConverted.PixelWidth;
                int height = formatConverted.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                formatConverted.CopyPixels(pixels, stride, 0);

                long totalR = 0, totalG = 0, totalB = 0;
                long satR = 0, satG = 0, satB = 0;
                int satCount = 0;
                int pixelCount = width * height;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte pb = pixels[i];
                    byte pg = pixels[i + 1];
                    byte pr = pixels[i + 2];

                    totalB += pb;
                    totalG += pg;
                    totalR += pr;

                    int max = Math.Max(pr, Math.Max(pg, pb));
                    int min = Math.Min(pr, Math.Min(pg, pb));
                    int delta = max - min;

                    if (delta > 30 && max > 50)
                    {
                        satR += pr;
                        satG += pg;
                        satB += pb;
                        satCount++;
                    }
                }

                if (satCount > pixelCount / 10)
                {
                    return Color.FromRgb(
                        (byte)(satR / satCount),
                        (byte)(satG / satCount),
                        (byte)(satB / satCount));
                }

                return Color.FromRgb(
                    (byte)(totalR / pixelCount),
                    (byte)(totalG / pixelCount),
                    (byte)(totalB / pixelCount));
            }
            catch
            {
                return Color.FromRgb(74, 144, 226);
            }
        }

        private async Task RetryThumbnailAsync(string expectedKey)
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500);

                if (_lastMediaKey != expectedKey) return;

                await _mediaService.UpdateMediaPropertiesAsync();

                var media = _mediaService.CurrentMedia;
                if (media?.HasThumbnail == true && media.Thumbnail is BitmapSource bmp)
                {
                    if (bmp.PixelWidth >= 48 && bmp.PixelHeight >= 48 && !IsLikelyFavicon(bmp))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_lastMediaKey == expectedKey)
                            {
                                ExpandedAlbumArtImage.Source = media.Thumbnail;
                                ExpandedDefaultIcon.Visibility = Visibility.Collapsed;

                                var avgColor = GetDominantColor(bmp);
                                SetAmbientColor(avgColor);
                            }
                        });
                        return;
                    }
                }
            }
        }

        private static bool IsLikelyFavicon(BitmapSource bmp)
        {
            try
            {
                if (bmp.PixelWidth < 48 || bmp.PixelHeight < 48) return true;

                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] pixels = new byte[converted.PixelHeight * stride];
                converted.CopyPixels(pixels, stride, 0);

                int w = converted.PixelWidth;
                int h = converted.PixelHeight;
                (int x, int y)[] corners = { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) };

                int transparentCorners = 0;
                foreach (var (x, y) in corners)
                {
                    int idx = (y * stride) + (x * 4);
                    if (idx + 3 < pixels.Length && pixels[idx + 3] < 250)
                    {
                        transparentCorners++;
                    }
                }

                return transparentCorners >= 4;
            }
            catch
            {
                return false;
            }
        }

        private void MediaService_PlaybackStatusChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePlaybackState(isPlaying);
            });
        }

        private void MediaService_TimelineChanged(TimeSpan position, TimeSpan duration)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTimelineDisplay(position, duration);
            });
        }

        private void UpdatePlaybackState(bool isPlaying)
        {
            if (isPlaying)
            {
                _progressTimer.Start();
                StartEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
            }
            else
            {
                _progressTimer.Stop();
                StopEqualizerAnimation();
                PlayPauseIcon.Data = Geometry.Parse("M8 5v14l11-7z");
            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaService.GetExactPosition(out var currentPos, out var duration))
            {
                UpdateTimelineDisplay(currentPos, duration);
                UpdateLyricsDisplay(currentPos);
            }
        }

        private void UpdateTimelineDisplay(TimeSpan currentPos, TimeSpan duration)
        {
            if (duration.TotalSeconds > 0)
            {
                double progress = (currentPos.TotalSeconds / duration.TotalSeconds) * 100;
                progress = Math.Clamp(progress, 0, 100);
                ExpandedProgressBar.Value = progress;

                CurrentTimeText.Text = currentPos.ToString(@"m\:ss");
                TotalTimeText.Text = duration.ToString(@"m\:ss");
            }
            else
            {
                ExpandedProgressBar.Value = 0;
                CurrentTimeText.Text = "0:00";
                TotalTimeText.Text = "0:00";
            }
        }

        private async Task LoadLyricsAsync(string rawTitle, string rawArtist)
        {
            _syncedLyrics.Clear();
            _hasLyrics = false;
            SetLyricsVisibility(false);

            var lyrics = await _lyricsService.GetLyricsAsync(rawTitle, rawArtist);
            if (lyrics != null && lyrics.Count > 0)
            {
                _syncedLyrics = lyrics;
                _hasLyrics = true;
                SetLyricsVisibility(true);
            }
            else
            {
                _syncedLyrics.Clear();
                _hasLyrics = false;
                SetLyricsVisibility(false);
            }

            if (_isExpanded && (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled))
            {
                SwitchViewMode(ViewMode.MediaExpanded);
            }
        }

        private void SetLyricsVisibility(bool visible)
        {
            if (visible)
            {
                LyricsDivider.Visibility = Visibility.Visible;
                LyricsContainer.Visibility = Visibility.Visible;

                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Stretch;
                LyricsDividerCol.Width = GridLength.Auto;
                LyricsCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                LyricsDivider.Visibility = Visibility.Collapsed;
                LyricsContainer.Visibility = Visibility.Collapsed;

                MediaExpandedView.HorizontalAlignment = HorizontalAlignment.Center;
                LyricsDividerCol.Width = new GridLength(0);
                LyricsCol.Width = new GridLength(0);
            }
        }
        private void UpdateLyricsDisplay(TimeSpan currentPos)
        {
            if (_syncedLyrics.Count == 0)
            {
                CompactLyricText.Text = string.Empty;
                return;
            }

            int activeIndex = -1;
            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (_syncedLyrics[i].Time <= currentPos)
                {
                    activeIndex = i;
                }
                else
                {
                    break;
                }
            }

            if (activeIndex >= 0)
            {
                string activeText = _syncedLyrics[activeIndex].Text;
                CompactLyricText.Text = activeText;
                PrevLyricText.Text = activeIndex > 0 ? _syncedLyrics[activeIndex - 1].Text : string.Empty;
                CurrentLyricText.Text = activeText;
                NextLyricText.Text = activeIndex < _syncedLyrics.Count - 1 ? _syncedLyrics[activeIndex + 1].Text : string.Empty;

                if (!_isExpanded && _currentViewMode == ViewMode.MediaCompact)
                {
                    double newWidth = CalculateCompactWidth();
                    if (Math.Abs(NotchBorder.Width - newWidth) > 5)
                    {
                        DoubleAnimation widthAnim = new DoubleAnimation { To = newWidth, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new QuadraticEase() };
                        NotchBorder.BeginAnimation(Border.WidthProperty, widthAnim);
                    }
                }
            }
            else
            {
                CompactLyricText.Text = string.Empty;
                PrevLyricText.Text = string.Empty;
                CurrentLyricText.Text = "...";
                NextLyricText.Text = _syncedLyrics.Count > 0 ? _syncedLyrics[0].Text : string.Empty;
            }
        }

        private void ResetToEmptyMedia()
        {
            _lastMediaKey = string.Empty;
            CompactTitleText.Text = "재생 중인 미디어 없음";
            CompactLyricText.Text = string.Empty;
            ExpandedTitleText.Text = "재생 중인 미디어 없음";
            ExpandedArtistText.Text = string.Empty;
            ExpandedAlbumArtImage.Source = null;
            ExpandedDefaultIcon.Visibility = Visibility.Visible;
            StopEqualizerAnimation();

            _syncedLyrics.Clear();
            _hasLyrics = false;
            SetLyricsVisibility(false);

            ExpandedProgressBar.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            _progressTimer.Stop();

            ClearAmbientLight();

            if (_volumeHudTimer == null || !_volumeHudTimer.IsEnabled)
            {
                SwitchViewMode(_isExpanded ? ViewMode.IdleExpanded : ViewMode.IdleCompact);
            }
        }

        private void StartEqualizerAnimation()
        {
            StopEqualizerAnimation();

            _eqStoryboard = new Storyboard();

            var anim1 = new DoubleAnimation(4, 13, TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim1, EqBar1);
            Storyboard.SetTargetProperty(anim1, new PropertyPath(Border.HeightProperty));

            var anim2 = new DoubleAnimation(14, 5, TimeSpan.FromMilliseconds(460))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim2, EqBar2);
            Storyboard.SetTargetProperty(anim2, new PropertyPath(Border.HeightProperty));

            var anim3 = new DoubleAnimation(6, 12, TimeSpan.FromMilliseconds(330))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim3, EqBar3);
            Storyboard.SetTargetProperty(anim3, new PropertyPath(Border.HeightProperty));

            _eqStoryboard.Children.Add(anim1);
            _eqStoryboard.Children.Add(anim2);
            _eqStoryboard.Children.Add(anim3);
            _eqStoryboard.Begin();
        }

        private void StopEqualizerAnimation()
        {
            if (_eqStoryboard != null)
            {
                _eqStoryboard.Stop();
                _eqStoryboard = null;
            }
            EqBar1.Height = 3;
            EqBar2.Height = 3;
            EqBar3.Height = 3;
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipPreviousAsync();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaService.CurrentMedia != null)
            {
                bool toggled = !_mediaService.CurrentMedia.IsPlaying;
                _mediaService.CurrentMedia.IsPlaying = toggled;
                UpdatePlaybackState(toggled);
            }
            await _mediaService.TryTogglePlayPauseAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TrySkipNextAsync();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "WinNotch"
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("열기/포커스", null, (s, e) =>
            {
                this.Activate();
            });

            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add("다크 모드", null, (s, e) => ApplyTheme(NotchTheme.Dark));
            contextMenu.Items.Add("리퀴드 글래스", null, (s, e) => ApplyTheme(NotchTheme.LiquidGlass));

            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add("종료", null, (s, e) => TrayExit_Click());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => this.Activate();
        }

        private void TrayExit_Click()
        {
            _trayIcon!.Visible = false;
            _trayIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _progressTimer.Stop();
            _clockTimer.Stop();
            _volumeHudTimer?.Stop();
            StopEqualizerAnimation();
            StopGlassLightAnimation();
            StopAmbientBreathAnimation();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) RemoveClipboardFormatListener(hwnd);

            _audioService.Dispose();
            _mediaService.Dispose();
            _lyricsService.Dispose();
        }
    }
}
