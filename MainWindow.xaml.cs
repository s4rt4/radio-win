using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NAudio.Dsp;
using NAudio.Wave;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Path = System.IO.Path;

namespace ClassicRadio;

public partial class MainWindow : Window
{
    // Segoe MDL2 Assets / Segoe Fluent Icons glyph codepoints
    private const string GlyphVolume = "";
    private const string GlyphMute   = "";

    private readonly LibVLC _libVLC;
    private readonly MediaPlayer _player;

    private List<Station> _stationsId = new();
    private List<Station> _stationsIntl = new();

    // ====== Visualizer audio path ======
    // We force LibVLC to deliver raw PCM through an audio callback (instead of
    // routing it to the system mixer). The samples are then (a) pushed to a
    // WaveOutEvent so the user can still hear the radio and (b) FFT'd for the
    // visualizer. This isolates the spectrum from any other system audio.
    private WaveOutEvent? _audioOut;
    private BufferedWaveProvider? _audioProvider;
    private MediaPlayer.LibVLCAudioPlayCb? _audioPlayCb; // keep delegate alive (GC root)
    private bool _audioPipelineReady;

    private DispatcherTimer? _renderTimer;
    private DispatcherTimer? _retryTimer;
    private int _retryAttempt;
    private static readonly int[] RetryDelaysSec = { 2, 5, 10 };

    private const int FftLength = 512;     // shorter window = updates twice as fast (~11ms)
    private const int BarCount = 64;
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _sampleBuffer = new float[FftLength];
    private int _sampleBufferPos;
    private readonly object _fftLock = new();
    private float[] _fftBars = new float[BarCount];
    private readonly float[] _smoothBars = new float[BarCount];
    private readonly float[] _velocity   = new float[BarCount]; // spring physics

    // Pre-computed Hamming window — saves ~40K cos() calls per second.
    private static readonly float[] HammingWindow = BuildHammingWindow();
    private static float[] BuildHammingWindow()
    {
        var w = new float[FftLength];
        for (int i = 0; i < FftLength; i++)
            w[i] = (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FftLength - 1)));
        return w;
    }

    // Reusable PCM buffer for audio callback — avoids ~950 KB/sec of garbage.
    private byte[]? _audioByteBuf;

    private Rectangle[]? _barRects; // cached visuals — avoid per-frame allocation

    private bool _isMuted;

    private bool _reallyExiting;

    // ====== Persisted state ======
    private AppState _state = new();
    private bool _restoring;            // suppress ScheduleSave while ApplyState is running
    private DispatcherTimer? _saveTimer;

    // Status line state
    private string _currentStationName = "";
    private DispatcherTimer? _statusAnim;
    private string _statusBase = "";

    private static readonly SolidColorBrush AmberBrush;
    private static readonly SolidColorBrush ErrorBrush;
    private static readonly SolidColorBrush PlayingBrush;

    static MainWindow()
    {
        AmberBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffe082"));
        ErrorBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8a80"));
        PlayingBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffd700"));
        AmberBrush.Freeze();
        ErrorBrush.Freeze();
        PlayingBrush.Freeze();
    }

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();
        _libVLC = new LibVLC("--no-video", "--quiet");
        _player = new MediaPlayer(_libVLC) { Volume = 100 };

        HookPlayerEvents();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ====== Dynamic status line driven by LibVLC events ======

    private void HookPlayerEvents()
    {
        _player.Opening          += (_, _)  => OnUI(SetConnecting);
        _player.Buffering        += (_, e)  => OnUI(() => SetBuffering(e.Cache));
        _player.Playing          += (_, _)  => OnUI(SetPlaying);
        _player.Paused           += (_, _)  => OnUI(() => SetSimple("Paused"));
        _player.Stopped          += (_, _)  => OnUI(() => SetSimple("Stopped"));
        _player.EncounteredError += (_, _)  => OnUI(OnPlayerError);
        _player.EndReached       += (_, _)  => OnUI(OnStreamEnded);
    }

    private void OnPlayerError() => MaybeRetryOrFail();
    private void OnStreamEnded() => MaybeRetryOrFail();

    private void MaybeRetryOrFail()
    {
        if (_retryAttempt >= RetryDelaysSec.Length || string.IsNullOrEmpty(_currentStationName))
        {
            // out of attempts or no station context — show plain error
            SetError();
            _retryAttempt = 0;
            return;
        }
        var delay = RetryDelaysSec[_retryAttempt];
        _retryAttempt++;
        ScheduleRetry(delay);
    }

    private void ScheduleRetry(int seconds)
    {
        CancelRetry();
        StopStatusAnim();
        StopLogoPulse();          // not actually playing anymore
        StartPlayBtnPulse();      // keep the "we're trying" feedback
        StatusText.Foreground = ErrorBrush;
        StatusText.Text = $"⟲  Reconnecting in {seconds}s…";
        SyncPopupStatus($"Reconnecting in {seconds}s…", ErrorBrush);
        PulseInTextBlock(StatusText);
        if (PopupStatusText is not null) PulseInTextBlock(PopupStatusText);

        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _retryTimer.Tick += (_, _) =>
        {
            CancelRetry();
            if (StationsCombo.SelectedItem is not Station st) return;
            try
            {
                using var media = new Media(_libVLC, new Uri(st.Url));
                _player.Play(media); // Opening → Buffering → Playing or another Error
            }
            catch
            {
                MaybeRetryOrFail();
            }
        };
        _retryTimer.Start();
    }

    private void CancelRetry()
    {
        _retryTimer?.Stop();
        _retryTimer = null;
    }

    private void OnUI(Action a) => Dispatcher.BeginInvoke(a);

    private void SetConnecting()
    {
        StopStatusAnim();
        StatusText.Foreground = AmberBrush;
        _statusBase = string.IsNullOrEmpty(_currentStationName)
            ? "Connecting"
            : $"Connecting to {_currentStationName}";
        StatusText.Text = _statusBase;
        SyncPopupStatus("Connecting", AmberBrush);
        PulseInTextBlock(StatusText);
        if (PopupStatusText is not null) PulseInTextBlock(PopupStatusText);
        StartPlayBtnPulse();
        StopLogoPulse();
        StartStatusAnim();
    }

    private void SetBuffering(float pct)
    {
        // Cache==0 fires before any data is in: keep "Connecting…" animating.
        if (pct <= 0f) return;
        StopStatusAnim();
        StatusText.Foreground = AmberBrush;
        if (pct < 99.5f)
        {
            StatusText.Text = $"Buffering…  {pct:F0}%";
            SyncPopupStatus($"Buffering  {pct:F0}%", AmberBrush);
        }
        // else: Playing event will swap to "Now Playing" momentarily
    }

    private void SetPlaying()
    {
        StopStatusAnim();
        StopPlayBtnPulse();
        StartLogoPulse();
        _retryAttempt = 0;
        StatusText.Foreground = PlayingBrush;
        StatusText.Text = string.IsNullOrEmpty(_currentStationName)
            ? "Now Playing"
            : $"Now Playing: {_currentStationName}";
        SyncPopupStatus("Now Playing", PlayingBrush);
        PulseInTextBlock(StatusText);
        if (PopupStatusText is not null) PulseInTextBlock(PopupStatusText);
        if (PopupStationName is not null && !string.IsNullOrEmpty(_currentStationName))
            PopupStationName.Text = _currentStationName;
    }

    private void SetSimple(string msg)
    {
        StopStatusAnim();
        StopPlayBtnPulse();
        StopLogoPulse();
        StatusText.Foreground = AmberBrush;
        StatusText.Text = msg;
        SyncPopupStatus(msg, AmberBrush);
        PulseInTextBlock(StatusText);
        if (PopupStatusText is not null) PulseInTextBlock(PopupStatusText);
    }

    private void SetError()
    {
        StopStatusAnim();
        StopPlayBtnPulse();
        StopLogoPulse();
        StatusText.Foreground = ErrorBrush;
        StatusText.Text = string.IsNullOrEmpty(_currentStationName)
            ? "⚠  Cannot play stream"
            : $"⚠  Cannot play  \"{_currentStationName}\"";
        SyncPopupStatus("⚠  Cannot play", ErrorBrush);
        PulseInTextBlock(StatusText);
        if (PopupStatusText is not null) PulseInTextBlock(PopupStatusText);
    }

    private void SyncPopupStatus(string text, SolidColorBrush color)
    {
        if (PopupStatusText is null) return;
        PopupStatusText.Text = text;
        PopupStatusText.Foreground = color;
        // Tray hover tooltip mirrors current state so user sees it without
        // opening the popup or focusing the window.
        if (TrayIcon is not null)
        {
            var name = string.IsNullOrEmpty(_currentStationName) ? "Classic Radio" : _currentStationName;
            TrayIcon.ToolTipText = text == "Now Playing"
                ? $"♪ {name}"
                : $"Classic Radio — {text}";
        }
    }

    private void StartStatusAnim()
    {
        var n = 0;
        _statusAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _statusAnim.Tick += (_, _) =>
        {
            n = (n + 1) % 4;
            var dots = new string('.', n);
            StatusText.Text = _statusBase + dots;
            if (PopupStatusText is not null)
                PopupStatusText.Text = "Connecting" + dots;
        };
        _statusAnim.Start();
    }

    private void StopStatusAnim()
    {
        _statusAnim?.Stop();
        _statusAnim = null;
    }

    // ====== UI animation polish ======

    private static readonly Duration TextPulseDuration = new(TimeSpan.FromMilliseconds(220));

    private void PulseInTextBlock(TextBlock t)
    {
        var anim = new DoubleAnimation
        {
            From = 0.35, To = 1.0,
            Duration = TextPulseDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        t.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private bool _playBtnPulsing;

    private void StartPlayBtnPulse()
    {
        if (_playBtnPulsing) return;
        _playBtnPulsing = true;
        var anim = new DoubleAnimation
        {
            From = 1.0, To = 0.55,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        PlayBtn.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void StopPlayBtnPulse()
    {
        if (!_playBtnPulsing) return;
        _playBtnPulsing = false;
        PlayBtn.BeginAnimation(UIElement.OpacityProperty, null);
        PlayBtn.Opacity = 1.0;
    }

    private bool _logoPulsing;

    private void StartLogoPulse()
    {
        if (_logoPulsing || HeaderLogo is null) return;
        _logoPulsing = true;
        if (HeaderLogo.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform();
            HeaderLogo.RenderTransform = st;
        }
        var anim = new DoubleAnimation
        {
            From = 1.0, To = 1.10,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StopLogoPulse()
    {
        if (!_logoPulsing) return;
        _logoPulsing = false;
        if (HeaderLogo?.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = 1; st.ScaleY = 1;
        }
    }

    private DispatcherTimer? _firstRunTimer;

    private void StartFirstRunHint()
    {
        StartPlayBtnPulse();
        _firstRunTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _firstRunTimer.Tick += (_, _) =>
        {
            _firstRunTimer?.Stop();
            _firstRunTimer = null;
            // Only stop if we're not in a connecting/buffering state (those keep the pulse).
            var s = _player.State;
            if (s != VLCState.Opening && s != VLCState.Buffering)
                StopPlayBtnPulse();
        };
        _firstRunTimer.Start();
    }

    private void StopFirstRunHint()
    {
        _firstRunTimer?.Stop();
        _firstRunTimer = null;
    }

    // ====== Lifecycle ======

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadStationsFromJson();
        BindStations(_stationsId, grouped: false);
        StartRenderLoop();
        // Audio pipeline is initialized lazily on first Play to avoid claiming
        // the audio device until needed (esp. when launched into the tray).

        LoadState();
        ApplyState();

        // Wire after restore so initial selection assignments don't get saved.
        StationsCombo.SelectionChanged += (_, _) =>
        {
            ScheduleSave();
            _retryAttempt = 0; // station change clears retry chain
        };

        // First-run hint: pulse the Play button briefly so new users see what to click.
        if (!File.Exists(StatePath)) StartFirstRunHint();
    }

    // ====== Taskbar icon (Win32 WM_SETICON) ======
    // WindowStyle=None + AllowsTransparency=True creates a layered window
    // and WPF's managed Icon property frequently fails to commit to the OS,
    // leaving a generic placeholder in the taskbar / Alt-Tab. We send the
    // WM_SETICON message ourselves with HICONs loaded by LoadImageW.

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    private const uint IMAGE_ICON      = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint WM_SETICON      = 0x80;
    private static readonly IntPtr ICON_SMALL = (IntPtr)0;
    private static readonly IntPtr ICON_BIG   = (IntPtr)1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            // Extract embedded ICO to disk so LoadImageW can read it.
            var iconPath = Path.Combine(Path.GetTempPath(), "ClassicRadio.ico");
            var info = Application.GetResourceStream(
                new Uri("pack://application:,,,/radio-win-logo.ico", UriKind.Absolute));
            if (info is null) return;
            using (var src = info.Stream)
            using (var dst = File.Create(iconPath))
                src.CopyTo(dst);

            var hwnd = new WindowInteropHelper(this).Handle;
            var hSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            var hBig   = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_SMALL, hSmall);
            if (hBig   != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, ICON_BIG,   hBig);

            // Also set the WPF property — it drives the in-app window icon
            // surface (which our custom title bar doesn't show, but Alt-Tab
            // peek thumbnails still read it).
            Icon = BitmapFrame.Create(
                new Uri("pack://application:,,,/radio-win-logo.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Icon load failed: {ex}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Final flush: ScheduleSave may have a pending timer that hasn't fired.
        _saveTimer?.Stop();
        SaveState();

        StopStatusAnim();
        StopFirstRunHint();
        CancelRetry();
        _renderTimer?.Stop();
        try { _audioOut?.Stop(); } catch { }
        _audioOut?.Dispose();
        _player.Stop();
        _player.Dispose();
        _libVLC.Dispose();
    }

    // ====== Custom title bar (Settings / Min / Close) ======

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        dlg.ShowDialog();

        // Reload (file may have changed) and re-bind whatever source is active.
        var wasIntl = SourceIntlRadio.IsChecked == true;
        LoadStationsFromJson();
        BindStations(wasIntl ? _stationsIntl : _stationsId, grouped: wasIntl);
    }

    private void Min_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    // X button = hide to tray, NOT exit. The OnClosing override intercepts and
    // cancels the close so the app keeps running with the tray icon visible.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExiting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    private void HideToTray()
    {
        var anim = new DoubleAnimation
        {
            From = Opacity, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            Hide();
            ShowInTaskbar = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1; // reset for next show
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void ShowFromTray()
    {
        Opacity = 0;
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // Topmost flicker forces foreground over other apps reliably.
        Topmost = true;
        Topmost = false;
        Focus();

        var anim = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ====== Keyboard shortcuts ======

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't intercept while typing in any text field
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Space when !ctrl:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Right when ctrl:
                NextBtn_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left when ctrl:
                PrevBtn_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Up when ctrl:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                e.Handled = true;
                break;
            case Key.Down when ctrl:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                e.Handled = true;
                break;
            case Key.M when ctrl:
                MuteBtn_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape:
                Close(); // OnClosing intercepts → hide to tray
                e.Handled = true;
                break;
        }
    }

    private void TogglePlayPause()
    {
        if (_player.State == VLCState.Playing)
            PauseBtn_Click(this, new RoutedEventArgs());
        else
            PlayBtn_Click(this, new RoutedEventArgs());
    }

    // ====== Tray icon: context menu + popup ======

    private void TrayShow_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        // Popup auto-closes on focus loss when the window comes forward.
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _reallyExiting = true;
        Close();
    }

    private void PopupVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeSlider is null) return;
        if (Math.Abs(VolumeSlider.Value - e.NewValue) > 0.5)
            VolumeSlider.Value = e.NewValue; // triggers main slider's ValueChanged → updates _player.Volume
    }

    // ====== Persisted state (state.json) ======

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var json = File.ReadAllText(StatePath);
            var s = JsonSerializer.Deserialize<AppState>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (s is not null) _state = s;
        }
        catch { /* missing or corrupt → use defaults */ }
    }

    private void ApplyState()
    {
        _restoring = true;
        try
        {
            // Source
            if (_state.LastSource == "International")
                SourceIntlRadio.IsChecked = true; // fires SourceChanged → BindStations(intl, grouped)

            // Station: look up by name in the now-active list
            var list = SourceIntlRadio.IsChecked == true ? _stationsIntl : _stationsId;
            if (!string.IsNullOrEmpty(_state.LastStation))
            {
                var found = list.FirstOrDefault(s => s.Name == _state.LastStation);
                if (found is not null)
                {
                    StationsCombo.SelectedItem = found;
                    _currentStationName = found.Name;
                    if (PopupStationName is not null) PopupStationName.Text = found.Name;
                }
            }

            // Volume
            var vol = Math.Max(0, Math.Min(100, _state.Volume));
            VolumeSlider.Value = vol;

            // Mute (toggle Mute flag directly — Volume is preserved on the player)
            if (_state.Muted)
            {
                _player.Mute = true;
                _isMuted = true;
                MuteBtn.Content = GlyphMute;
            }
        }
        finally { _restoring = false; }
    }

    private void ScheduleSave()
    {
        if (_restoring) return;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer?.Stop();
                SaveState();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(UserDataDir);
            _state.LastSource  = SourceIntlRadio.IsChecked == true ? "International" : "Indonesia";
            _state.LastStation = (StationsCombo.SelectedItem as Station)?.Name ?? _state.LastStation;
            _state.Volume      = (int)VolumeSlider.Value;
            _state.Muted       = _isMuted;
            var json = JsonSerializer.Serialize(_state,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StatePath, json);
        }
        catch { /* state save isn't critical — ignore disk errors */ }
    }

    // ====== Stations ======

    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicRadio");
    public static string UserStationsPath => Path.Combine(UserDataDir, "stations.json");
    public static string BundledStationsPath => Path.Combine(AppContext.BaseDirectory, "stations.json");
    public static string StatePath => Path.Combine(UserDataDir, "state.json");

    private void LoadStationsFromJson()
    {
        // Prefer user-edited file; fall back to the bundled copy that ships
        // with the .exe so first launch always has data.
        string? json = null;
        if (File.Exists(UserStationsPath))
        {
            try { json = File.ReadAllText(UserStationsPath); } catch { }
        }
        if (json is null)
        {
            if (!File.Exists(BundledStationsPath))
            {
                StatusText.Text = "stations.json not found";
                return;
            }
            json = File.ReadAllText(BundledStationsPath);
        }

        var doc = JsonSerializer.Deserialize<StationData>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (doc is null) return;
        _stationsId = doc.Indonesia;
        _stationsIntl = doc.International;
    }

    private void BindStations(List<Station> stations, bool grouped)
    {
        var view = new ListCollectionView(stations);
        if (grouped)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Station.Country)));
        StationsCombo.ItemsSource = view;
        if (stations.Count > 0) StationsCombo.SelectedIndex = 0;
    }

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var useId = SourceIdRadio.IsChecked == true;
        BindStations(useId ? _stationsId : _stationsIntl, grouped: !useId);
        // _player.Stop() will fire Stopped event → status reflects it.
        _player.Stop();
        ScheduleSave();
    }

    // ====== Playback ======

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (StationsCombo.SelectedItem is not Station st) return;
        StopFirstRunHint();
        CancelRetry();
        _retryAttempt = 0; // user-initiated play resets the chain
        EnsureAudioPipeline();
        try
        {
            _currentStationName = st.Name;
            // LibVLC handles HLS, Icecast, Shoutcast, raw mp3/aac streams natively.
            using var media = new Media(_libVLC, new Uri(st.Url));
            _player.Play(media);
            // Status updates flow from Opening → Buffering → Playing events.
        }
        catch (Exception ex)
        {
            SetError();
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        // _player.Pause() will fire Paused event → status reflects it.
        _player.Pause();
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e) => Skip(-1);
    private void NextBtn_Click(object sender, RoutedEventArgs e) => Skip(+1);

    private void Skip(int delta)
    {
        var count = StationsCombo.Items.Count;
        if (count == 0) return;
        var idx = StationsCombo.SelectedIndex + delta;
        if (idx < 0) idx = count - 1;
        if (idx >= count) idx = 0;
        StationsCombo.SelectedIndex = idx;
        PlayBtn_Click(this, new RoutedEventArgs());
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        // Use LibVLC's Mute flag (independent of Volume so it doesn't disturb
        // the perceptual filter chain on round-trip). _isMuted is our local
        // source of truth — we don't read libvlc_audio_get_mute() because it
        // can return undefined (-1) before the audio output is initialized.
        _isMuted = !_isMuted;
        _player.Mute = _isMuted;
        MuteBtn.Content = _isMuted ? GlyphMute : GlyphVolume;
        ScheduleSave();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires once during InitializeComponent before _player is constructed.
        if (_player is null) return;
        _player.Volume = (int)e.NewValue;
        // Drag up while muted → auto-unmute (mirrors common media-player behavior).
        if (_isMuted && e.NewValue > 0)
        {
            _player.Mute = false;
            _isMuted = false;
            MuteBtn.Content = GlyphVolume;
        }
        // Sync popup slider (without infinite loop)
        if (PopupVolumeSlider is not null && Math.Abs(PopupVolumeSlider.Value - e.NewValue) > 0.5)
            PopupVolumeSlider.Value = e.NewValue;
        ScheduleSave();
    }

    // ====== Tooltips for Prev/Next (mirror the web behavior) ======

    private void PrevBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => UpdateSkipTooltip(PrevBtn, -1);
    private void NextBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => UpdateSkipTooltip(NextBtn, +1);

    private void UpdateSkipTooltip(Button btn, int delta)
    {
        var count = StationsCombo.Items.Count;
        if (count == 0) return;
        var idx = StationsCombo.SelectedIndex + delta;
        if (idx < 0) idx = count - 1;
        if (idx >= count) idx = 0;
        if (StationsCombo.Items[idx] is Station st)
            btn.ToolTip = st.Name;
    }

    // ====== Visualizer (LibVLC PCM callback + FFT + WPF Canvas) ======

    private const int AudioRate = 44100;
    private const int AudioChannels = 2;

    private void EnsureAudioPipeline()
    {
        if (_audioPipelineReady) return;
        _audioProvider = new BufferedWaveProvider(new WaveFormat(AudioRate, 16, AudioChannels))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
        };
        try
        {
            _audioOut = new WaveOutEvent { DesiredLatency = 120 };
            _audioOut.Init(_audioProvider);
            _audioOut.Play();
        }
        catch
        {
            // No default render device. Audio output silent, visualizer still works
            // because the FFT runs from the same callback.
            try { _audioOut?.Dispose(); } catch { }
            _audioOut = null;
        }

        // Route LibVLC's decoded PCM through our callback. After this, LibVLC
        // no longer outputs to the system mixer; we play it via WaveOutEvent.
        _audioPlayCb = AudioPlayCallback;
        _player.SetAudioFormat("S16N", AudioRate, AudioChannels);
        _player.SetAudioCallbacks(_audioPlayCb, null, null, null, null);
        _audioPipelineReady = true;
    }

    private void StartRenderLoop()
    {
        // 60fps for smooth motion. WPF Render priority schedules with the
        // composition thread so we don't beat layout to death.
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += (_, _) => DrawVisualizer();
        _renderTimer.Start();
        VisualizerCanvas.SizeChanged += (_, _) => RebuildBars();
    }

    private void AudioPlayCallback(IntPtr data, IntPtr samples, uint count, long pts)
    {
        // count = number of audio frames (one frame = AudioChannels samples).
        int byteCount = (int)count * AudioChannels * 2; // S16 = 2 bytes per sample
        if (byteCount <= 0) return;

        // Reuse buffer: LibVLC fires this ~43×/s; allocating fresh produces
        // hundreds of KB/s of garbage which can cause GC-induced audio hitches.
        if (_audioByteBuf is null || _audioByteBuf.Length < byteCount)
            _audioByteBuf = new byte[Math.Max(byteCount, 8192)];
        var bytes = _audioByteBuf;
        Marshal.Copy(samples, bytes, 0, byteCount);

        // (1) feed audio output so the user hears it. AddSamples copies internally
        //     so reusing our buffer is safe.
        _audioProvider?.AddSamples(bytes, 0, byteCount);

        // (2) feed FFT buffer (mono mix of the two channels)
        int frames = (int)count;
        for (int i = 0; i < frames; i++)
        {
            int offset = i * 4;
            short l = (short)((bytes[offset + 1] << 8) | bytes[offset]);
            short r = (short)((bytes[offset + 3] << 8) | bytes[offset + 2]);
            float mono = (l + r) / 2f / 32768f;

            _sampleBuffer[_sampleBufferPos++] = mono;
            if (_sampleBufferPos >= FftLength)
            {
                ComputeFft();
                _sampleBufferPos = 0;
            }
        }
    }

    private void ComputeFft()
    {
        // Apply pre-computed Hamming window
        for (int i = 0; i < FftLength; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * HammingWindow[i];
            _fftBuffer[i].Y = 0f;
        }

        int m = (int)Math.Log2(FftLength);
        FastFourierTransform.FFT(true, m, _fftBuffer);

        var bars = new float[BarCount];
        int halfLen = FftLength / 2;
        for (int b = 0; b < BarCount; b++)
        {
            // Log-spaced bin edges so low frequencies are not crammed into one bar
            double f0 = Math.Pow(halfLen, (double)b / BarCount);
            double f1 = Math.Pow(halfLen, (double)(b + 1) / BarCount);
            int i0 = Math.Max(1, (int)f0);
            int i1 = Math.Min(halfLen, Math.Max(i0 + 1, (int)f1));
            float max = 0f;
            for (int i = i0; i < i1; i++)
            {
                var c = _fftBuffer[i];
                float mag = MathF.Sqrt(c.X * c.X + c.Y * c.Y);
                if (mag > max) max = mag;
            }
            // NAudio's FFT normalizes by N, so bin magnitudes for typical music
            // sit in the 0.001..0.1 range. Heavy log-boost lets quiet bins
            // still register while peaks easily saturate to full bar height.
            float v = (float)(Math.Log10(1 + max * 150) * 0.85);
            bars[b] = v > 1f ? 1f : v;
        }

        lock (_fftLock) _fftBars = bars;
    }

    private LinearGradientBrush? _barBrush;

    private LinearGradientBrush BarBrush =>
        _barBrush ??= CreateBarBrush();

    private static LinearGradientBrush CreateBarBrush()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString("#ffd700"), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString("#8d6e63"), 1),
            }
        };
        b.Freeze();
        return b;
    }

    private void RebuildBars()
    {
        var canvas = VisualizerCanvas;
        double width = canvas.ActualWidth;
        if (width <= 0) return;

        canvas.Children.Clear();
        _barRects = new Rectangle[BarCount];
        double slot = width / BarCount;
        double barWidth = slot * 0.78;
        for (int i = 0; i < BarCount; i++)
        {
            var r = new Rectangle
            {
                Width = barWidth,
                Height = 1,
                Fill = BarBrush,
                RadiusX = 1,
                RadiusY = 1,
            };
            Canvas.SetLeft(r, i * slot + (slot - barWidth) / 2);
            canvas.Children.Add(r);
            _barRects[i] = r;
        }
    }

    private void DrawVisualizer()
    {
        var canvas = VisualizerCanvas;
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (_barRects is null || _barRects.Length != BarCount)
            RebuildBars();
        if (_barRects is null) return;

        float[] bars;
        lock (_fftLock) bars = _fftBars;

        // Spring-damper motion per bar — feels organic, never overshoots into
        // jitter, recovers smoothly. Compared to plain exponential smoothing
        // this carries momentum: bars accelerate towards target, then settle.
        const float Stiffness = 0.55f; // pull toward target
        const float Damping   = 0.35f; // velocity friction

        for (int i = 0; i < BarCount; i++)
        {
            float target = bars[i];
            float pos    = _smoothBars[i];
            float vel    = _velocity[i];

            // Asymmetric: rising uses physics for snappy-but-natural attack,
            // falling uses simple exponential decay so peaks linger pleasantly.
            if (target > pos)
            {
                float force = (target - pos) * Stiffness;
                vel = vel * (1f - Damping) + force;
                pos += vel;
                if (pos > target) pos = target; // clamp overshoot
            }
            else
            {
                vel = 0f;
                pos = pos * 0.86f + target * 0.14f;
            }

            _smoothBars[i] = pos;
            _velocity[i]   = vel;

            double bh = pos * height;
            if (bh < 1) bh = 1;
            var rect = _barRects[i];
            rect.Height = bh;
            Canvas.SetTop(rect, height - bh);
        }
    }
}
