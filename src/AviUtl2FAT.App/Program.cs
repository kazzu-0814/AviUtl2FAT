using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using AviUtl2FAT.Core;

namespace AviUtl2FAT.App;

public sealed class CaptionRow(FATCaption caption) : INotifyPropertyChanged
{
    private string _text = caption.Text;
    private bool _isCurrent;
    public string Id { get; } = caption.Id;
    public double StartTime { get; } = caption.StartTime;
    public double EndTime { get; } = caption.EndTime;
    public string OriginalTranscript { get; } = caption.OriginalTranscript;
    public string Text { get => _text; set { if (_text != value) { _text = value; Changed(); } } }
    public string Provider { get; private set; } = caption.Provider;
    public string? Model { get; private set; } = caption.Model;
    public bool IsCurrent { get => _isCurrent; set { if (_isCurrent != value) { _isCurrent = value; Changed(); Changed(nameof(CurrentMarker)); } } }
    public string CurrentMarker => IsCurrent ? "▶" : string.Empty;
    public string Warning => CaptionQuality.Warning(ToCaption()) is { } warning ? "⚠ 要確認: " + warning : string.Empty;
    public FATCaption ToCaption() => new(Id, StartTime, EndTime, OriginalTranscript, Text, Provider, Model, caption.Confidence, caption.Enabled, caption.DetectedLanguage, caption.OutputLanguage);
    public void Apply(FATCaption caption) { _text = caption.Text; Provider = caption.Provider; Model = caption.Model; Changed(nameof(Text)); Changed(nameof(Warning)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); if (name == nameof(Text)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Warning))); }
}

public interface IVideoPreviewBackend
{
    PreviewPlayerStatus Status { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    void Open(string path);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void Close();
    Task RefreshFrameAsync(CancellationToken cancellationToken = default);
    string DisplayMode { get; }
    double AspectRatio { get; }
}

/// <summary>
/// Decoder-compatible preview backed by FAT's bundled FFmpeg instead of Windows Media Foundation.
/// This avoids the common 0xC00D11B1 codec error while keeping all timeline work inside FAT.
/// </summary>
public sealed class FfmpegFramePreviewBackend : IVideoPreviewBackend
{
    private readonly Image _image;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly Stopwatch _clock = new();
    private PreviewPlayerStatus _status = PreviewPlayerStatus.NotLoaded;
    private TimeSpan _position;
    private TimeSpan _duration;
    private string? _input;
    private int _rendering;
    private readonly System.Windows.Media.MediaPlayer _audioPlayer = new();
    private CancellationTokenSource? _audioCancellation;
    private string? _temporaryAudioPath;
    private int _audioGeneration;
    private double _volume = 0.7;

    public FfmpegFramePreviewBackend(Image image, string runtimeRoot)
    {
        _image = image;
        _ffmpegPath = Path.Combine(runtimeRoot, "ffmpeg", "ffmpeg.exe");
        _ffprobePath = Path.Combine(runtimeRoot, "ffmpeg", "ffprobe.exe");
    }

    public PreviewPlayerStatus Status => _status;
    public TimeSpan Position => _status == PreviewPlayerStatus.Playing ? ClampPosition(_position + _clock.Elapsed) : _position;
    public TimeSpan Duration => _duration;
    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); _audioPlayer.Volume = _volume; }
    }
    public string DisplayMode => "FFmpeg互換プレビュー（音声対応）";
    public double AspectRatio { get; private set; } = 16d / 9d;

    public void Open(string path)
    {
        if (!File.Exists(_ffmpegPath) || !File.Exists(_ffprobePath))
            throw new FatException("FAT_FFMPEG_MISSING", "プレビュー用の FFmpeg / ffprobe が見つかりません。FATを再インストールしてください。");
        if (!File.Exists(path)) throw new FileNotFoundException("動画ファイルが見つかりません。", path);

        Close();
        _status = PreviewPlayerStatus.Opening;
        _input = path;
        ReadMetadata(path);
        _position = TimeSpan.Zero;
        _status = PreviewPlayerStatus.Stopped;
    }
    public void Play()
    {
        if (_input is null) return;
        _position = Position;
        _clock.Restart();
        _status = PreviewPlayerStatus.Playing;
        StartAudioFromCurrentPosition();
    }
    public void Pause()
    {
        if (_status == PreviewPlayerStatus.Playing) _position = Position;
        _clock.Reset();
        ++_audioGeneration;
        CancelAudioDecode();
        _audioPlayer.Pause();
        if (_input is not null) _status = PreviewPlayerStatus.Paused;
    }
    public void Stop()
    {
        _clock.Reset();
        _position = TimeSpan.Zero;
        ++_audioGeneration;
        CancelAudioDecode();
        _audioPlayer.Stop();
        _status = _input is null ? PreviewPlayerStatus.NotLoaded : PreviewPlayerStatus.Stopped;
    }
    public void Seek(TimeSpan position)
    {
        _position = ClampPosition(position);
        var resume = _status == PreviewPlayerStatus.Playing;
        ++_audioGeneration;
        CancelAudioDecode();
        _audioPlayer.Stop();
        if (resume)
        {
            _clock.Restart();
            StartAudioFromCurrentPosition();
        }
    }
    public void Close()
    {
        _clock.Reset();
        _position = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _input = null;
        _image.Source = null;
        ++_audioGeneration;
        CancelAudioDecode();
        _audioPlayer.Close();
        DeleteTemporaryAudio();
        _status = PreviewPlayerStatus.NotLoaded;
    }

    public async Task RefreshFrameAsync(CancellationToken cancellationToken = default)
    {
        if (_input is null || _status == PreviewPlayerStatus.NotLoaded || Interlocked.Exchange(ref _rendering, 1) != 0) return;
        try
        {
            var position = Position;
            if (_duration > TimeSpan.Zero && position >= _duration)
            {
                _position = _duration;
                _clock.Reset();
                _status = PreviewPlayerStatus.Stopped;
            }

            var start = new ProcessStartInfo(_ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-loglevel"); start.ArgumentList.Add("error");
            start.ArgumentList.Add("-ss"); start.ArgumentList.Add(Position.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-i"); start.ArgumentList.Add(_input);
            start.ArgumentList.Add("-frames:v"); start.ArgumentList.Add("1");
            start.ArgumentList.Add("-vf"); start.ArgumentList.Add("scale='min(1280,iw)':-2");
            start.ArgumentList.Add("-f"); start.ArgumentList.Add("image2pipe");
            start.ArgumentList.Add("-vcodec"); start.ArgumentList.Add("png"); start.ArgumentList.Add("pipe:1");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpegプレビューを起動できませんでした。");
            await using var bytes = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(bytes, cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outputTask, process.WaitForExitAsync(cancellationToken));
            var error = await errorTask;
            if (process.ExitCode != 0 || bytes.Length == 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpegからプレビュー画像を取得できませんでした。" : error.Trim());

            bytes.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
            _image.Source = bitmap;
        }
        finally { Interlocked.Exchange(ref _rendering, 0); }
    }

    private void ReadMetadata(string path)
    {
        var start = new ProcessStartInfo(_ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-v"); start.ArgumentList.Add("error");
        start.ArgumentList.Add("-select_streams"); start.ArgumentList.Add("v:0");
        start.ArgumentList.Add("-show_entries"); start.ArgumentList.Add("format=duration:stream=width,height");
        start.ArgumentList.Add("-of"); start.ArgumentList.Add("json"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffprobeを起動できませんでした。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "動画情報を読み取れませんでした。" : error.Trim());
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            _duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            if (stream.TryGetProperty("width", out var width) && stream.TryGetProperty("height", out var height) && height.GetInt32() > 0)
                AspectRatio = (double)width.GetInt32() / height.GetInt32();
        }
    }

    private TimeSpan ClampPosition(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : _duration > TimeSpan.Zero && value > _duration ? _duration : value;

    // Video frames are decoded by FFmpeg because Media Foundation can reject
    // common MP4 codecs.  Decode audio to a standard WAV chunk and let WPF play
    // that WAV; this keeps audio working without relying on the source codec.
    private void StartAudioFromCurrentPosition()
    {
        if (_input is null) return;
        var generation = ++_audioGeneration;
        CancelAudioDecode();
        _audioCancellation = new CancellationTokenSource();
        _ = DecodeAndPlayAudioAsync(_input, _position, generation, _audioCancellation.Token);
    }

    private async Task DecodeAndPlayAudioAsync(string input, TimeSpan position, int generation, CancellationToken cancellationToken)
    {
        var audioDirectory = Path.Combine(Path.GetTempPath(), "AviUtl2FAT", "preview-audio");
        Directory.CreateDirectory(audioDirectory);
        var output = Path.Combine(audioDirectory, $"preview-{Guid.NewGuid():N}.wav");
        try
        {
            var start = new ProcessStartInfo(_ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-hide_banner"); start.ArgumentList.Add("-loglevel"); start.ArgumentList.Add("error"); start.ArgumentList.Add("-y");
            start.ArgumentList.Add("-ss"); start.ArgumentList.Add(position.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-i"); start.ArgumentList.Add(input);
            start.ArgumentList.Add("-vn"); start.ArgumentList.Add("-ac"); start.ArgumentList.Add("2"); start.ArgumentList.Add("-ar"); start.ArgumentList.Add("48000");
            start.ArgumentList.Add("-c:a"); start.ArgumentList.Add("pcm_s16le"); start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg音声デコーダーを起動できませんでした。");
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            });
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (cancellationToken.IsCancellationRequested || generation != _audioGeneration) return;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "動画音声を取得できませんでした。" : error.Trim());

            _audioPlayer.Stop();
            DeleteTemporaryAudio();
            _temporaryAudioPath = output;
            _audioPlayer.Open(new Uri(output, UriKind.Absolute));
            _audioPlayer.Volume = _volume;
            _audioPlayer.Play();
        }
        catch (OperationCanceledException) { TryDelete(output); }
        catch (Exception) { TryDelete(output); }
    }

    private void CancelAudioDecode()
    {
        _audioCancellation?.Cancel();
        _audioCancellation?.Dispose();
        _audioCancellation = null;
    }

    private void DeleteTemporaryAudio()
    {
        if (_temporaryAudioPath is not null) TryDelete(_temporaryAudioPath);
        _temporaryAudioPath = null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FatWindow : Window
{
    private static readonly string CurrentVersion = GetCurrentVersion();
    private const string ReleasesApiUrl = "https://api.github.com/repos/kazzu-0814/AviUtl2FAT/releases?per_page=20";
    private const string ReleasesPageUrl = "https://github.com/kazzu-0814/AviUtl2FAT/releases";
    private static readonly HttpClient UpdateClient = CreateUpdateClient();
    private readonly TextBlock _media = new() { Text = "Media: not selected" };
    private readonly TextBlock _status = new() { Text = "Ready" };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 18 };
    private readonly ComboBox _recognitionLanguage = LanguageSelector("auto");
    private readonly ComboBox _outputLanguage = LanguageSelector("ja", includeAuto: false);
    private readonly ComboBox _recognitionProfile = RecognitionProfileSelector();
    private readonly ComboBox _aiProvider = AiSelector();
    private readonly TextBlock _aiState = new() { Text = "今回使用するAI: AIなし（高速）" };
    private readonly ObservableCollection<CaptionRow> _captions = [];
    private readonly ProviderRegistry _providers = ProviderRegistry.CreateDefault();
    private readonly PersistentPythonWorker _pythonEngine = new();
    private readonly RuleBasedProvider _ruleProvider = new();
    private readonly CodexCliBackend _codexCli = new();
    private readonly CodexAppServerBackend _codexAppServer = new();
    private readonly ClaudeCodeCliBackend _claudeCode = new();
    private readonly ComboBox _codexBackend = CodexBackendSelectorBox();
    private readonly StackPanel _codexBackendRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _recognizeButton;
    private readonly Button _naturalizeButton;
    private readonly Button _shortenButton;
    private readonly Image _previewImage = new() { Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _previewOverlay = new() { Text = "", Foreground = System.Windows.Media.Brushes.White, Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, Padding = new Thickness(12), Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly TextBlock _previewStatus = new() { Text = "プレビュー: 未読込", Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _previewPosition = new() { Text = "現在: 00:00.000 / 00:00.000" };
    private readonly TextBlock _currentCaptionLabel = new() { Text = "現在字幕: なし", Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _previewFormat = new() { Text = "表示: 自動", Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly Slider _seekBar = new() { Minimum = 0, Maximum = 1, Value = 0 };
    private readonly CheckBox _autoFollowCaptions = new() { Content = "字幕に追従", IsChecked = true, Margin = new Thickness(8, 0, 8, 0) };
    private readonly CheckBox _showPreviewOverlay = new() { Content = "字幕プレビュー", IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Slider _previewVolume = new() { Minimum = 0, Maximum = 1, Value = 0.7, Width = 90 };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IVideoPreviewBackend _previewBackend;
    private DataGrid? _captionGrid;
    private CaptionRow? _editingCaption;
    private int _textSelectionStart;
    private int _textSelectionLength;
    private IReadOnlyList<FATCaption> _lastGenerated = [];
    private readonly Stack<IReadOnlyList<FATCaption>> _undo = new();
    private AviUtl2ObjectTemplate _objectTemplate = AviUtl2ObjectTemplate.CreateStandard();
    private AviUtl2TextStyle _style = AviUtl2TextStyle.Default;
    private readonly string _aiSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AviUtl2FAT", "ai-settings.json");
    private readonly string _previewSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AviUtl2FAT", "preview-settings.json");
    private string? _input;
    private bool _isRecognizing;
    private CancellationTokenSource? _recognitionCancellation;
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.Ordinal);
    private Expander? _advancedSettings;
    private Border? _homeCard;
    private CaptionTimeline _captionTimeline = new([]);
    private CaptionRow? _currentCaption;
    private bool _isSeekingWithSlider;
    private bool _isUpdatingCurrentSelection;
    private bool _isRefreshingPreviewFrame;
    private DateTimeOffset _suspendAutoFollowUntil = DateTimeOffset.MinValue;

    public FatWindow()
    {
        _previewBackend = new FfmpegFramePreviewBackend(_previewImage, FindRuntime(AppContext.BaseDirectory));
        Title = $"AviUtl2 FAT v{CurrentVersion} - Formation Auto Text"; Width = 1200; Height = 780; MinWidth = 980; MinHeight = 620;
        var root = new Grid { Background = System.Windows.Media.Brushes.White };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;

        var navigation = new StackPanel { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55)), Margin = new Thickness(0) };
        navigation.Children.Add(new TextBlock { Text = "AviUtl2\nFAT", Foreground = System.Windows.Media.Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(20, 24, 8, 28) });
        foreach (var item in new[] { "ホーム", "字幕", "AI", "スタイル", "出力", "設定" })
            navigation.Children.Add(CreateNavigationButton(item));
        Grid.SetColumn(navigation, 0); root.Children.Add(navigation);

        // The home, preview and caption editor are intentionally one vertical
        // workflow.  Keep the whole workflow reachable on small displays while
        // retaining the DataGrid's own scrollbar for long caption lists.
        var centerScroll = new ScrollViewer
        {
            Margin = new Thickness(28, 24, 20, 14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true
        };
        Grid.SetColumn(centerScroll, 1); root.Children.Add(centerScroll);
        var center = new DockPanel { LastChildFill = false };
        centerScroll.Content = center;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(heading, Dock.Top); center.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "動画から字幕を作成", FontSize = 27, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "1. 動画を選ぶ 　→　2. 字幕を作る 　→　3. 必要ならAIで整える 　→　4. 自分で直す 　→　5. AviUtl2へ出力", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 6, 0, 12) });
        var homeCard = new Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12), Focusable = true };
        _homeCard = homeCard;
        var home = new StackPanel(); homeCard.Child = home; heading.Children.Add(homeCard);
        home.Children.Add(new TextBlock { Text = "まず動画を選んで、字幕を作成します", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        home.Children.Add(_media);
        var primaryActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        AddButton(primaryActions, "動画を選択", (_, _) => ChooseMedia());
        _recognizeButton = AddButton(primaryActions, "字幕を作成", async (_, _) => await ToggleRecognitionAsync());
        _recognizeButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)); _recognizeButton.Foreground = System.Windows.Media.Brushes.White;
        home.Children.Add(primaryActions);
        var advanced = new Expander { Header = "詳細設定（通常は自動のままで大丈夫です）", Margin = new Thickness(0, 8, 0, 0) };
        var details = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        details.Children.Add(new TextBlock { Text = "認識言語:", VerticalAlignment = VerticalAlignment.Center }); details.Children.Add(_recognitionLanguage);
        details.Children.Add(new TextBlock { Text = "字幕言語:", Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }); details.Children.Add(_outputLanguage);
        details.Children.Add(new TextBlock { Text = "音声認識:", Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }); details.Children.Add(_recognitionProfile);
        advanced.Content = details; home.Children.Add(advanced); _advancedSettings = advanced;

        var previewPanel = CreatePreviewPanel();
        DockPanel.SetDock(previewPanel, Dock.Top);
        center.Children.Add(previewPanel);

        var progressBox = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(progressBox, Dock.Top); center.Children.Add(progressBox);
        progressBox.Children.Add(new TextBlock { Text = "処理状況", FontWeight = FontWeights.SemiBold }); progressBox.Children.Add(_status); progressBox.Children.Add(_progress);
        progressBox.Children.Add(new TextBlock { Text = "字幕を編集・同期", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        progressBox.Children.Add(new TextBlock { Text = "行をクリックするとプレビュー位置へ移動します。再生中の行は ▶ と淡い青で表示されます。字幕本文は直接編集できます。", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            ItemsSource = _captions,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            RowHeight = 34,
            FontSize = 15,
            AlternatingRowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            MinHeight = 260,
            Height = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, System.Windows.Media.Brushes.White));
        rowStyle.Triggers.Add(new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(CaptionRow.IsCurrent)),
            Value = true,
            Setters = { new Setter(DataGridRow.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(219, 234, 254))) }
        });
        grid.RowStyle = rowStyle;
        grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new System.Windows.Data.Binding(nameof(CaptionRow.CurrentMarker)), IsReadOnly = true, Width = 32 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Start", Binding = new System.Windows.Data.Binding(nameof(CaptionRow.StartTime)) { StringFormat = "0.000" }, IsReadOnly = true, Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "End", Binding = new System.Windows.Data.Binding(nameof(CaptionRow.EndTime)) { StringFormat = "0.000" }, IsReadOnly = true, Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "字幕本文（クリックして直接編集）", Binding = new System.Windows.Data.Binding(nameof(CaptionRow.Text)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "確認", Binding = new System.Windows.Data.Binding(nameof(CaptionRow.Warning)), IsReadOnly = true, Width = 220 });
        _captionGrid = grid; center.Children.Add(grid);
        grid.PreparingCellForEdit += (_, eventArgs) =>
        {
            if (eventArgs.Row.Item is not CaptionRow row || eventArgs.EditingElement is not TextBox editor) return;
            _editingCaption = row;
            void CaptureSelection(object? sender, EventArgs eventArgs) { _textSelectionStart = editor.SelectionStart; _textSelectionLength = editor.SelectionLength; }
            editor.SelectionChanged += CaptureSelection;
            editor.LostKeyboardFocus += CaptureSelection;
            CaptureSelection(null, EventArgs.Empty);
        };
        grid.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingCurrentSelection) return;
            _suspendAutoFollowUntil = DateTimeOffset.Now.AddSeconds(2);
            if (grid.SelectedItem is CaptionRow row && !_isSeekingWithSlider) SeekPreview(row.StartTime, pause: false);
            UpdatePreviewOverlay();
        };
        grid.MouseDoubleClick += (_, _) =>
        {
            if (grid.SelectedItem is CaptionRow row)
            {
                SeekPreview(row.StartTime, pause: false);
                PlayPreview();
            }
        };

        var inspector = new StackPanel { Margin = new Thickness(8, 24, 24, 14) }; Grid.SetColumn(inspector, 2); root.Children.Add(inspector);
        inspector.Children.Add(new TextBlock { Text = "字幕・AI", FontSize = 18, FontWeight = FontWeights.SemiBold });
        inspector.Children.Add(new TextBlock { Text = "使用するAI", Margin = new Thickness(0, 16, 0, 4) }); inspector.Children.Add(_aiProvider);
        _codexBackendRow.Children.Add(new TextBlock { Text = "Codex接続:", Margin = new Thickness(0, 0, 8, 8), VerticalAlignment = VerticalAlignment.Center }); _codexBackendRow.Children.Add(_codexBackend); inspector.Children.Add(_codexBackendRow);
        inspector.Children.Add(_aiState);
        var aiButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        _naturalizeButton = AddButton(aiButtons, "自然にする", async (_, _) => await TransformSelectedAsync(CaptionOperation.Naturalize));
        _shortenButton = AddButton(aiButtons, "短くする", async (_, _) => await TransformSelectedAsync(CaptionOperation.Shorten)); inspector.Children.Add(aiButtons);
        var splitButton = AddButton(inspector, "選択字幕を分解", async (_, _) => await SplitCaptionsAsync());
        splitButton.ToolTip = "編集欄で文字を選択してから押すと、選択文字を独立した字幕として分割します。文字選択がない場合は行全体を自動分割します。";
        AddButton(inspector, "選択字幕を統合", (_, _) => MergeSelectedCaptions());
        AddButton(inspector, "元に戻す", (_, _) => Undo());
        inspector.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        inspector.Children.Add(new TextBlock { Text = "出力", FontSize = 18, FontWeight = FontWeights.SemiBold });
        AddButton(inspector, "AviUtl2へ出力", async (_, _) => await ExportAsync()).FontWeight = FontWeights.SemiBold;
        AddButton(inspector, "テキスト統一", (_, _) => ApplyStandardStyle());
        AddButton(inspector, "AviUtl2からスタイルを読み込む", async (_, _) => await RegisterObjectTemplateAsync());
        AddButton(inspector, "AIモデルを管理 / ダウンロード", async (_, _) => await ShowModelManagerAsync());

        var statusBar = new TextBlock { Text = "Python Engine: 待機中　|　Whisper: 自動設定　|　AI: AIなし　|　AviUtl2: 別プロセスで安全に動作", Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)), Padding = new Thickness(18, 8, 18, 8), Foreground = System.Windows.Media.Brushes.DimGray };
        Grid.SetRow(statusBar, 1); Grid.SetColumnSpan(statusBar, 3); root.Children.Add(statusBar);
        var saved = AiSelectionSettingsStore.LoadAsync(_aiSettingsPath, CancellationToken.None).GetAwaiter().GetResult();
        _codexCli.ConfigureExecutablePath(saved.CodexCliPath);
        _codexAppServer.ConfigureExecutablePath(saved.CodexCliPath);
        _claudeCode.ConfigureExecutablePath(saved.ClaudeCliPath);
        _aiProvider.SelectedItem = _aiProvider.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == saved.PreferredAIProvider) ?? _aiProvider.Items[0];
        _codexBackend.SelectedItem = _codexBackend.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == saved.CodexBackend) ?? _codexBackend.Items[0];
        _aiProvider.SelectionChanged += async (_, _) => { UpdateAiState(); await SaveAiSettingsAsync(); };
        _codexBackend.SelectionChanged += async (_, _) => await SaveAiSettingsAsync();
        UpdateAiState();
        LoadPreviewSettings();
        ConfigurePreviewEvents();
        SelectNavigation("ホーム");
        Closed += (_, _) => { SavePreviewSettings(); _previewTimer.Stop(); _previewBackend.Close(); _pythonEngine.Dispose(); _codexAppServer.Dispose(); };
    }

    private Border CreatePreviewPanel()
    {
        var panel = new Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(10) };
        var root = new DockPanel();
        panel.Child = root;

        var previewHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        previewHeader.Children.Add(new TextBlock { Text = "動画プレビュー", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        previewHeader.Children.Add(new TextBlock { Text = "　FFmpeg互換表示（16:9 / 9:16 を自動維持）", Foreground = System.Windows.Media.Brushes.DimGray, VerticalAlignment = VerticalAlignment.Bottom });
        DockPanel.SetDock(previewHeader, Dock.Top);
        root.Children.Add(previewHeader);

        var videoFrame = new Grid { Height = 280, Background = System.Windows.Media.Brushes.Black, ClipToBounds = true };
        videoFrame.Children.Add(_previewImage);
        videoFrame.Children.Add(new Border { Child = _previewOverlay, VerticalAlignment = VerticalAlignment.Bottom, Background = System.Windows.Media.Brushes.Transparent });
        DockPanel.SetDock(videoFrame, Dock.Top);
        root.Children.Add(videoFrame);

        var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.Add(controls);
        var firstRow = new WrapPanel();
        AddButton(firstRow, "▶ 再生", (_, _) => PlayPreview());
        AddButton(firstRow, "⏸ 一時停止", (_, _) => PausePreview());
        AddButton(firstRow, "■ 停止", (_, _) => StopPreview());
        AddButton(firstRow, "-5秒", (_, _) => SeekRelative(-5));
        AddButton(firstRow, "-1秒", (_, _) => SeekRelative(-1));
        AddButton(firstRow, "+1秒", (_, _) => SeekRelative(1));
        AddButton(firstRow, "+5秒", (_, _) => SeekRelative(5));
        firstRow.Children.Add(_autoFollowCaptions);
        firstRow.Children.Add(_showPreviewOverlay);
        firstRow.Children.Add(new TextBlock { Text = "音量", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        firstRow.Children.Add(_previewVolume);
        controls.Children.Add(firstRow);
        controls.Children.Add(new TextBlock { Text = "タイムライン（ドラッグで移動）", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 6, 0, 2) });
        controls.Children.Add(_seekBar);
        var playbackInfo = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        playbackInfo.Children.Add(_previewPosition);
        playbackInfo.Children.Add(new TextBlock { Text = "　|　" });
        playbackInfo.Children.Add(_currentCaptionLabel);
        playbackInfo.Children.Add(new TextBlock { Text = "　|　" });
        playbackInfo.Children.Add(_previewFormat);
        playbackInfo.Children.Add(new TextBlock { Text = "　|　" });
        playbackInfo.Children.Add(_previewStatus);
        controls.Children.Add(playbackInfo);
        return panel;
    }

    private void ConfigurePreviewEvents()
    {
        _previewTimer.Tick += (_, _) => UpdatePlaybackSync();
        _previewTimer.Start();
        _seekBar.PreviewMouseDown += (_, _) => _isSeekingWithSlider = true;
        _seekBar.PreviewMouseUp += (_, _) => { _isSeekingWithSlider = false; SeekPreview(_seekBar.Value, pause: false); };
        _seekBar.ValueChanged += (_, _) => { if (_isSeekingWithSlider) UpdatePreviewPositionLabels(_seekBar.Value, _previewBackend.Duration.TotalSeconds); };
        _previewVolume.ValueChanged += (_, _) => { _previewBackend.Volume = _previewVolume.Value; SavePreviewSettings(); };
        _autoFollowCaptions.Checked += (_, _) => SavePreviewSettings();
        _autoFollowCaptions.Unchecked += (_, _) => SavePreviewSettings();
        _showPreviewOverlay.Checked += (_, _) => { SavePreviewSettings(); UpdatePreviewOverlay(); };
        _showPreviewOverlay.Unchecked += (_, _) => { SavePreviewSettings(); UpdatePreviewOverlay(); };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Space)
            {
                if (_previewBackend.Status == PreviewPlayerStatus.Playing) PausePreview(); else PlayPreview();
                args.Handled = true;
            }
            else if (args.Key is Key.Left or Key.Right)
            {
                var direction = args.Key == Key.Right ? 1 : -1;
                SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 * direction : direction);
                args.Handled = true;
            }
        };
    }

    private void LoadPreviewSettings()
    {
        try
        {
            if (File.Exists(_previewSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<PreviewSettings>(File.ReadAllText(_previewSettingsPath), JsonOptions) ?? new PreviewSettings();
                _autoFollowCaptions.IsChecked = settings.AutoFollowCaptions;
                _showPreviewOverlay.IsChecked = settings.ShowSubtitleOverlay;
                _previewVolume.Value = Math.Clamp(settings.Volume, 0, 1);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        _previewBackend.Volume = _previewVolume.Value;
    }

    private void SavePreviewSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_previewSettingsPath)!);
            var settings = new PreviewSettings(_autoFollowCaptions.IsChecked == true, _showPreviewOverlay.IsChecked == true, _previewVolume.Value);
            File.WriteAllText(_previewSettingsPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void OpenPreviewMedia(string path)
    {
        try
        {
            _previewBackend.Close();
            _previewBackend.Open(path);
            _previewStatus.Text = "プレビュー: フレームを読み込み中";
            _previewFormat.Text = $"表示: {DescribeAspectRatio(_previewBackend.AspectRatio)}";
            _seekBar.Value = 0;
            UpdatePlaybackSync();
            _ = RefreshPreviewFrameAsync();
        }
        catch (Exception error)
        {
            _previewStatus.Text = "プレビュー: 読み込みエラー";
            MessageBox.Show(this, "動画プレビューを開けませんでした。\n字幕生成は引き続き利用できます。\n\n詳細: " + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayPreview()
    {
        if (string.IsNullOrWhiteSpace(_input)) { _status.Text = "動画を選択するとPreviewを再生できます。"; return; }
        try { _previewBackend.Play(); _previewStatus.Text = "プレビュー: 再生中"; UpdatePlaybackSync(); }
        catch (Exception error) { _previewStatus.Text = "プレビュー: エラー"; MessageBox.Show(this, "動画プレビューを再生できませんでした。\n字幕生成は引き続き利用できます。\n\n詳細: " + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PausePreview()
    {
        try { _previewBackend.Pause(); _previewStatus.Text = "プレビュー: 一時停止"; _ = RefreshPreviewFrameAsync(); }
        catch (Exception error) { _previewStatus.Text = "プレビュー: エラー"; _status.Text = error.Message; }
    }

    private void StopPreview()
    {
        try { _previewBackend.Stop(); _previewStatus.Text = _input is null ? "プレビュー: 未読込" : "プレビュー: 停止"; UpdatePlaybackSync(); _ = RefreshPreviewFrameAsync(); }
        catch (Exception error) { _previewStatus.Text = "プレビュー: エラー"; _status.Text = error.Message; }
    }

    private void SeekRelative(double seconds) => SeekPreview(_previewBackend.Position.TotalSeconds + seconds, pause: false);

    private void SeekPreview(double seconds, bool pause)
    {
        if (_previewBackend.Status == PreviewPlayerStatus.NotLoaded) return;
        var duration = _previewBackend.Duration.TotalSeconds;
        var clamped = duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
        try
        {
            _previewBackend.Seek(TimeSpan.FromSeconds(clamped));
            if (pause) _previewBackend.Pause();
            UpdatePlaybackSync();
            _ = RefreshPreviewFrameAsync();
        }
        catch (Exception error) { _previewStatus.Text = "プレビュー: エラー"; _status.Text = error.Message; }
    }

    private void UpdatePlaybackSync()
    {
        var position = _isSeekingWithSlider ? TimeSpan.FromSeconds(_seekBar.Value) : _previewBackend.Position;
        var duration = _previewBackend.Duration;
        if (!_isSeekingWithSlider)
        {
            _seekBar.Maximum = Math.Max(1, duration.TotalSeconds);
            _seekBar.Value = Math.Clamp(position.TotalSeconds, 0, _seekBar.Maximum);
        }
        UpdatePreviewPositionLabels(position.TotalSeconds, duration.TotalSeconds);
        var caption = _captionTimeline.FindCaptionAtTime(position.TotalSeconds);
        SetCurrentCaption(caption?.Id);
        UpdatePreviewOverlay();
        if (!_isSeekingWithSlider) _ = RefreshPreviewFrameAsync();
    }

    private async Task RefreshPreviewFrameAsync()
    {
        if (_isRefreshingPreviewFrame || _previewBackend.Status == PreviewPlayerStatus.NotLoaded) return;
        _isRefreshingPreviewFrame = true;
        try
        {
            await _previewBackend.RefreshFrameAsync();
            if (_previewBackend.Status is PreviewPlayerStatus.Playing or PreviewPlayerStatus.Paused or PreviewPlayerStatus.Stopped)
                _previewStatus.Text = $"プレビュー: {_previewBackend.DisplayMode}{(_previewBackend.Status == PreviewPlayerStatus.Playing ? "・再生中" : "")}";
        }
        catch (Exception error)
        {
            _previewStatus.Text = "プレビュー: フレーム取得エラー";
            _status.Text = "プレビュー表示を更新できませんでした: " + error.Message;
        }
        finally { _isRefreshingPreviewFrame = false; }
    }

    private static string DescribeAspectRatio(double ratio) => ratio switch
    {
        > 1.70 and < 1.85 => "16:9（横）",
        > 0.50 and < 0.64 => "9:16（縦）",
        _ => $"自動（{ratio:0.00}:1）"
    };

    private void UpdatePreviewPositionLabels(double positionSeconds, double durationSeconds)
    {
        _previewPosition.Text = $"現在: {FormatPlaybackTime(positionSeconds)} / {FormatPlaybackTime(durationSeconds)}";
    }

    private static string FormatPlaybackTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $@"{span:hh\:mm\:ss\.fff}" : $@"{span:mm\:ss\.fff}";
    }

    private void SetCurrentCaption(string? captionId)
    {
        if (string.Equals(_currentCaption?.Id, captionId, StringComparison.Ordinal)) return;
        if (_currentCaption is not null) _currentCaption.IsCurrent = false;
        _currentCaption = captionId is null ? null : _captions.FirstOrDefault(row => string.Equals(row.Id, captionId, StringComparison.Ordinal));
        if (_currentCaption is not null) _currentCaption.IsCurrent = true;
        var index = _currentCaption is null ? -1 : _captions.IndexOf(_currentCaption);
        _currentCaptionLabel.Text = index < 0 ? "現在字幕: なし" : $"現在字幕: {index + 1} / {_captions.Count}";
        if (_currentCaption is not null && _autoFollowCaptions.IsChecked == true && DateTimeOffset.Now >= _suspendAutoFollowUntil && _captionGrid is not null)
        {
            _isUpdatingCurrentSelection = true;
            try
            {
                _captionGrid.SelectedItem = _currentCaption;
                _captionGrid.ScrollIntoView(_currentCaption);
            }
            finally
            {
                _isUpdatingCurrentSelection = false;
            }
        }
    }

    private void UpdatePreviewOverlay()
    {
        if (_showPreviewOverlay.IsChecked != true || _currentCaption is null || string.IsNullOrWhiteSpace(_currentCaption.Text))
        {
            _previewOverlay.Visibility = Visibility.Collapsed;
            _previewOverlay.Text = string.Empty;
            return;
        }

        _previewOverlay.Text = _currentCaption.Text;
        _previewOverlay.FontFamily = new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(_style.Font) ? "Yu Gothic UI" : _style.Font);
        var styleSize = double.TryParse(_style.Size, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 96;
        _previewOverlay.FontSize = Math.Clamp(styleSize / 3.2, 18, 46);
        _previewOverlay.Foreground = new System.Windows.Media.SolidColorBrush(ParseRgb(_style.TextColor, System.Windows.Media.Colors.White));
        _previewOverlay.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = ParseRgb(_style.OutlineColor, System.Windows.Media.Colors.Black),
            ShadowDepth = 0,
            BlurRadius = 3,
            Opacity = 0.9
        };
        _previewOverlay.Visibility = Visibility.Visible;
    }

    private static System.Windows.Media.Color ParseRgb(string value, System.Windows.Media.Color fallback)
    {
        if (value.Length != 6) return fallback;
        return byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? System.Windows.Media.Color.FromRgb(r, g, b)
            : fallback;
    }

    private Button CreateNavigationButton(string destination)
    {
        var button = new Button
        {
            Content = destination,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = System.Windows.Media.Brushes.White,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(20, 11, 8, 11),
            ToolTip = destination switch
            {
                "ホーム" => "動画の選択と字幕作成へ移動します",
                "字幕" => "字幕一覧を編集できる状態にします",
                "AI" => "AIモデル管理を開きます。モデルの取得はここから明示的に行います",
                "スタイル" => "字幕の標準スタイルまたはAviUtl2テンプレートを選びます",
                "出力" => "AviUtl2用.object、SRT、TXTなどを出力します",
                _ => "認識設定とアプリ更新を確認します"
            }
        };
        button.Click += async (_, _) => await NavigateAsync(destination);
        _navigationButtons[destination] = button;
        return button;
    }

    private async Task NavigateAsync(string destination)
    {
        SelectNavigation(destination);
        switch (destination)
        {
            case "ホーム":
                _homeCard?.BringIntoView();
                _homeCard?.Focus();
                _status.Text = "ホーム: 動画を選択して字幕作成を開始できます。";
                break;
            case "字幕":
                _captionGrid?.Focus();
                if (_captionGrid?.CurrentItem is CaptionRow row) _captionGrid.ScrollIntoView(row);
                _status.Text = "字幕: 一覧の本文を直接編集できます。文字を選択して「選択字幕を分解」も利用できます。";
                break;
            case "AI":
                _status.Text = "AI: モデル管理を開いています。モデルのダウンロードは必ず確認後に開始されます。";
                await ShowModelManagerAsync();
                break;
            case "スタイル":
                ShowStyleWindow();
                break;
            case "出力":
                await ExportAsync();
                break;
            case "設定":
                ShowSettingsWindow();
                break;
        }
    }

    private void SelectNavigation(string destination)
    {
        foreach (var pair in _navigationButtons)
        {
            var selected = pair.Key == destination;
            pair.Value.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235))
                : System.Windows.Media.Brushes.Transparent;
            pair.Value.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ShowStyleWindow()
    {
        var window = new Window { Title = "字幕スタイル", Owner = this, Width = 480, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "字幕スタイル", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "FAT標準スタイルを使うか、AviUtl2で保存した通常テキストの .object テンプレートを読み込めます。字幕の本文と時間は変更しません。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var standard = AddButton(actions, "FAT標準スタイルを使う", (_, _) => { ApplyStandardStyle(); window.Close(); });
        standard.Margin = new Thickness(0, 0, 8, 0);
        AddButton(actions, "AviUtl2 .objectを読み込む", async (_, _) => { await RegisterObjectTemplateAsync(); window.Close(); });
        panel.Children.Add(actions);
        window.Content = panel;
        window.ShowDialog();
    }

    private void ShowSettingsWindow()
    {
        var window = new Window
        {
            Title = "設定 / 更新",
            Owner = this,
            Width = 540,
            Height = 560,
            MinWidth = 440,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "認識と更新", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "認識言語・字幕言語・認識プロファイルはホームの「詳細設定」から変更できます。アプリ更新はGitHub Releaseを確認し、ユーザーが選んだ場合だけインストーラーをダウンロードします。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "プレビュー", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) });
        var follow = new CheckBox { Content = "字幕に自動追従", IsChecked = _autoFollowCaptions.IsChecked, Margin = new Thickness(0, 0, 0, 4) };
        follow.Checked += (_, _) => _autoFollowCaptions.IsChecked = true;
        follow.Unchecked += (_, _) => _autoFollowCaptions.IsChecked = false;
        panel.Children.Add(follow);
        var overlay = new CheckBox { Content = "動画プレビュー上に字幕を表示", IsChecked = _showPreviewOverlay.IsChecked, Margin = new Thickness(0, 0, 0, 10) };
        overlay.Checked += (_, _) => _showPreviewOverlay.IsChecked = true;
        overlay.Unchecked += (_, _) => _showPreviewOverlay.IsChecked = false;
        panel.Children.Add(overlay);
        var advanced = AddButton(panel, "認識の詳細設定を開く", (_, _) => { _advancedSettings?.SetCurrentValue(Expander.IsExpandedProperty, true); _recognitionLanguage.Focus(); window.Close(); });
        advanced.Margin = new Thickness(0, 0, 0, 8);
        var update = AddButton(panel, "更新を確認", async (_, _) => await CheckForUpdatesAsync());
        update.Margin = new Thickness(0, 0, 0, 8);
        var releases = AddButton(panel, "GitHub Releaseを開く", (_, _) => Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true }));
        releases.Margin = new Thickness(0, 0, 0, 8);
        var uninstall = AddButton(panel, "AviUtl2 FATをアンインストール...", (_, _) => StartUninstaller());
        uninstall.ToolTip = "FATのみを削除します。AviUtl2本体や他のプラグインには変更を加えません。";
        window.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true
        };
        window.ShowDialog();
    }

    private void StartUninstaller()
    {
        // The installer puts unins000.exe in the AviUtl2FAT plugin root while
        // this executable lives in its FAT child directory.  Never attempt to
        // delete files ourselves: Inno Setup owns removal and leaves AviUtl2
        // plus unrelated plugins outside this directory untouched.
        var pluginRoot = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        var uninstaller = pluginRoot is null ? null : Path.Combine(pluginRoot, "unins000.exe");
        if (string.IsNullOrWhiteSpace(uninstaller) || !File.Exists(uninstaller))
        {
            MessageBox.Show(this, "アンインストーラーはインストール済みのFATにだけ含まれます。\n\n現在は開発版または展開フォルダーから実行されています。Windowsの「インストールされているアプリ」から AviUtl2 FAT を選んで削除するか、FATをインストーラーで導入してください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var decision = MessageBox.Show(this, "AviUtl2 FAT をアンインストールしますか？\n\n削除対象は AviUtl2FAT フォルダー内の FAT 本体・Worker・runtime・ダウンロード済みモデルです。AviUtl2 本体、他のプラグイン、プロジェクトファイルには触れません。\n\nアプリ設定は %LOCALAPPDATA%\\AviUtl2FAT に残るため、再インストール時に引き継げます。", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (decision != MessageBoxResult.Yes) return;

        Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true, WorkingDirectory = pluginRoot! });
        Close();
    }

    private static HttpClient CreateUpdateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AviUtl2FAT/1.0 UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using var response = await UpdateClient.GetAsync(ReleasesApiUrl);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var latest = document.RootElement.EnumerateArray()
                .FirstOrDefault(release => !release.GetProperty("draft").GetBoolean());
            if (latest.ValueKind == JsonValueKind.Undefined)
            {
                MessageBox.Show(this, "公開済みの更新情報はまだありません。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var tag = latest.GetProperty("tag_name").GetString() ?? "";
            var current = ParseVersion(CurrentVersion);
            var available = ParseVersion(tag);
            if (available is null || current is null || available.CompareTo(current) <= 0)
            {
                MessageBox.Show(this, $"AviUtl2 FAT は最新です。\n\n現在: v{CurrentVersion}\n公開版: {tag}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var asset = latest.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
                (item.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var downloadUrl = asset.ValueKind == JsonValueKind.Undefined
                ? latest.GetProperty("html_url").GetString()
                : asset.GetProperty("browser_download_url").GetString();
            var decision = MessageBox.Show(this, $"AviUtl2 FAT {tag} を利用できます。\n\n更新インストーラーを開きますか？\n実行中のAviUtl2 FATを閉じてからインストールしてください。", Title, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (decision == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(downloadUrl))
                Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "更新情報を取得できませんでした。ネットワーク接続を確認するか、GitHub Releaseを開いてください。\n\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Version? ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(FatWindow).Assembly.GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private static ComboBox LanguageSelector(string selected, bool includeAuto = true) { var box = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 0, 8) }; if (includeAuto) box.Items.Add(new ComboBoxItem { Content = "自動判定", Tag = "auto" }); box.Items.Add(new ComboBoxItem { Content = "日本語", Tag = "ja" }); box.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" }); box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == selected); return box; }
    private static ComboBox RecognitionProfileSelector() { var box = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 0, 8) }; foreach (var item in new[] { ("自動", "auto"), ("高速", "low"), ("標準", "standard"), ("高精度", "high") }) box.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 }); box.SelectedIndex = 0; return box; }
    private static ComboBox AiSelector() { var box = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 0, 8) }; foreach (var item in new[] { ("自動", "auto"), ("AIなし（高速）", "rule"), ("Gemma 4 E2B", "gemma:gemma-4-e2b-it"), ("Gemma 4 E4B", "gemma:gemma-4-e4b-it"), ("Gemma 4 12B", "gemma:gemma-4-12b-it"), ("Gemma 4 26B A4B", "gemma:gemma-4-26b-a4b-it"), ("OpenAI Codex", "codex"), ("Anthropic Claude Code", "claude-code") }) box.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 }); box.SelectedIndex = 0; return box; }
    private static ComboBox CodexBackendSelectorBox()
    {
        var box = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 0, 8) };
        box.Items.Add(new ComboBoxItem { Content = "自動（接続済みApp Server優先）", Tag = "auto" });
        box.Items.Add(new ComboBoxItem { Content = "Codex App Server", Tag = "appserver" });
        box.Items.Add(new ComboBoxItem { Content = "Codex CLI", Tag = "cli" });
        box.SelectedIndex = 0;
        return box;
    }
    private static string SelectedLanguage(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;
    private string SelectedAi() => (string)((ComboBoxItem)_aiProvider.SelectedItem).Tag;
    private CodexBackendPreference SelectedCodexBackend() => (string)((ComboBoxItem)_codexBackend.SelectedItem).Tag switch { "appserver" => CodexBackendPreference.AppServer, "cli" => CodexBackendPreference.Cli, _ => CodexBackendPreference.Auto };
    private Task SaveAiSettingsAsync() => AiSelectionSettingsStore.SaveAsync(_aiSettingsPath, new AiSelectionSettings(PreferredAIProvider: SelectedAi(), CodexBackend: (string)((ComboBoxItem)_codexBackend.SelectedItem).Tag, CodexCliPath: _codexCli.ExecutablePath, ClaudeCliPath: _claudeCode.ExecutablePath), CancellationToken.None);
    private void UpdateAiState()
    {
        var selected = SelectedAi();
        _codexBackendRow.Visibility = selected == "codex" ? Visibility.Visible : Visibility.Collapsed;
        _aiState.Text = selected switch
        {
            "codex" => "今回使用するAI: OpenAI Codex（字幕本文を外部AIサービスへ送信します）",
            "claude-code" => "今回使用するAI: Anthropic Claude Code（字幕本文を外部AIサービスへ送信します）",
            var gemma when gemma.StartsWith("gemma:", StringComparison.Ordinal) => $"今回使用するAI: {_aiProvider.Text}（ローカルAI・モデル読み込みが必要です）",
            "auto" => "今回使用するAI: 自動（クラウドAIへは自動送信しません）",
            _ => "今回使用するAI: AIなし（Python ルール処理・外部送信なし）"
        };
    }
    private static Button AddButton(Panel panel, string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6) }.With(handler);
        panel.Children.Add(button);
        return button;
    }
    private void ChooseMedia() { var dialog = new OpenFileDialog { Filter = "動画・音声|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mp3;*.m4a;*.wav;*.flac|すべてのファイル|*.*" }; if (dialog.ShowDialog(this) == true) { _input = dialog.FileName; _media.Text = $"動画素材: {Path.GetFileName(_input)}"; OpenPreviewMedia(_input); } }
    private async Task ToggleRecognitionAsync()
    {
        if (_isRecognizing)
        {
            _recognizeButton.IsEnabled = false;
            _recognizeButton.Content = "停止中...";
            _status.Text = "停止を要求しています...";
            _recognitionCancellation?.Cancel();
            return;
        }

        await RecognizeAsync();
    }

    private async Task RecognizeAsync()
    {
        if (string.IsNullOrWhiteSpace(_input)) { Error("FAT_INPUT_REQUIRED", "Choose a media file first."); return; }
        if (_isRecognizing) return;
        _isRecognizing = true;
        _recognitionCancellation = new CancellationTokenSource();
        _recognizeButton.Content = "停止";
        try
        {
            _progress.Value = 0; _status.Text = "Starting worker...";
            var output = Path.Combine(Path.GetTempPath(), "AviUtl2FAT", $"{Guid.NewGuid():N}.att.json");
            var recognitionLanguage = SelectedLanguage(_recognitionLanguage); var outputLanguage = SelectedLanguage(_outputLanguage);
            await RunWorkerAsync(new RecognitionRequest(_input, output, new FatSettings { Version = CurrentVersion, Language = recognitionLanguage, RecognitionLanguage = recognitionLanguage, CaptionOutputLanguage = outputLanguage, SpeechProfile = (string)((ComboBoxItem)_recognitionProfile.SelectedItem).Tag }), p => { _status.Text = p.Message; if (p.Value is not null) _progress.Value = Math.Clamp(p.Value.Value, 0, 100); }, _recognitionCancellation.Token);
            var transcript = await FatFiles.ReadAttTranscriptAsync(output, CancellationToken.None);
            var response = await _providers.GetRequired("passthrough").GenerateCaptionsAsync(new CaptionGenerationRequest(transcript), CancellationToken.None);
            _lastGenerated = response.Captions; Load(response.Captions); _progress.Value = 100; _status.Text = $"完了: {_captions.Count} 件の字幕を編集できます。";
        }
        catch (OperationCanceledException) { _status.Text = "字幕生成を停止しました。既存の字幕はそのまま保持されています。"; }
        catch (FatException exception) { Error(exception.Code, exception.Message); }
        catch (Exception exception) { Error("FAT_APP_UNEXPECTED", exception.Message); }
        finally
        {
            _recognitionCancellation?.Dispose();
            _recognitionCancellation = null;
            _isRecognizing = false;
            _recognizeButton.Content = "字幕を作成";
            _recognizeButton.IsEnabled = true;
        }
    }
    private async Task RegenerateAsync() { try { var transcript = _captions.Select(x => new TranscriptSegment(x.Id, x.StartTime, x.EndTime, x.OriginalTranscript, x.Text)).ToArray(); var response = await _providers.GetRequired("passthrough").GenerateCaptionsAsync(new CaptionGenerationRequest(transcript), CancellationToken.None); _lastGenerated = response.Captions; Load(response.Captions); _status.Text = "Regenerated with Passthrough."; } catch (FatException exception) { Error(exception.Code, exception.Message); } }
    private async Task TransformSelectedAsync(CaptionOperation operation)
    {
        var selected = _captionGrid?.SelectedItems.Cast<CaptionRow>().Select(row => row.ToCaption()).ToArray() ?? [];
        if (selected.Length == 0) { Error("FAT_AI_SELECTION_REQUIRED", "字幕一覧から処理する字幕を1件以上選択してください。"); return; }
        var providerId = SelectedAi();
        if (providerId == "auto") providerId = "rule"; // Cloud providers are never selected implicitly.
        if (providerId == "codex" && MessageBox.Show(this, "OpenAI Codexを使用すると、選択した字幕本文のみが外部AIサービスへ送信されます。動画ファイルや認証情報は送信しません。\n\n続行しますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (providerId == "claude-code" && MessageBox.Show(this, "Anthropic Claude Codeを使用します。\n\n選択した字幕本文だけがAnthropicのサービスへ送信されます。動画・AviUtl2プロジェクト・認証情報は送信しません。\nAI結果は自動確定されず、確認・編集してから適用します。\n\n使用しますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _naturalizeButton.IsEnabled = _shortenButton.IsEnabled = false;
        try
        {
            _status.Text = providerId == "codex" ? "OpenAI Codexで字幕を処理しています..." : providerId == "claude-code" ? "Anthropic Claude Codeで字幕を処理しています..." : "字幕を処理しています...";
            var request = new CaptionTransformRequest(selected, operation, SelectedLanguage(_outputLanguage));
            CaptionTransformResponse response = providerId switch
            {
                "codex" => await TransformWithCodexAsync(request),
                "claude-code" => await TransformWithClaudeAsync(request),
                var gemma when gemma.StartsWith("gemma:", StringComparison.Ordinal) => await TransformWithGemmaAsync(request, gemma[6..]),
                _ => await _ruleProvider.TransformAsync(request, CancellationToken.None)
            };
            var preview = new AiPreviewWindow(selected, response.Captions, response.Provider, response.Backend, operation) { Owner = this };
            if (preview.ShowDialog() != true) { _status.Text = "AI変更案は適用しませんでした。"; return; }
            var before = _captions.Select(row => row.ToCaption()).ToArray(); _undo.Push(before); _lastGenerated = before;
            foreach (var changed in preview.Result) _captions.FirstOrDefault(row => row.Id == changed.Id)?.Apply(changed);
            _status.Text = $"{response.Provider} の変更案を適用しました。元に戻すことができます。";
        }
        catch (FatException error) { Error(error.Code, error.Message); }
        catch (Exception error) { Error("FAT_AI_FAILED", error.Message); }
        finally { _naturalizeButton.IsEnabled = _shortenButton.IsEnabled = true; }
    }
    private async Task<CaptionTransformResponse> TransformWithCodexAsync(CaptionTransformRequest request)
    {
        var selected = await CodexBackendSelector.SelectAsync(SelectedCodexBackend(), _codexAppServer, _codexCli, CancellationToken.None);
        return await new CodexProvider(selected.Backend).TransformAsync(request, CancellationToken.None);
    }
    private Task<CaptionTransformResponse> TransformWithClaudeAsync(CaptionTransformRequest request) => new ClaudeCodeProvider(_claudeCode).TransformAsync(request, CancellationToken.None);
    private async Task<CaptionTransformResponse> TransformWithGemmaAsync(CaptionTransformRequest request, string modelId)
    {
        var payload = JsonSerializer.Serialize(new { provider = "gemma", model_id = modelId, operation = request.Operation == CaptionOperation.Naturalize ? "naturalize" : "shorten", output_language = request.OutputLanguage, captions = request.Captions.Select(caption => new { id = caption.Id, start_time = caption.StartTime, end_time = caption.EndTime, original_transcript = caption.OriginalTranscript, text = caption.Text, confidence = caption.Confidence }) }, JsonOptions);
        var response = await _pythonEngine.RequestAsync(FindRuntime(AppContext.BaseDirectory), request.Operation == CaptionOperation.Naturalize ? "ai.naturalize" : "ai.shorten", payload);
        using var document = JsonDocument.Parse(response); var root = document.RootElement;
        if (root.GetProperty("type").GetString() == "error") throw new FatException("GEMMA_FAILED", root.GetProperty("error").GetProperty("message").GetString() ?? "Gemma failed.");
        var captions = root.GetProperty("payload").GetProperty("captions").EnumerateArray().Select(item => new FATCaption(item.GetProperty("id").GetString()!, item.GetProperty("start_time").GetDouble(), item.GetProperty("end_time").GetDouble(), item.GetProperty("original_transcript").GetString() ?? "", item.GetProperty("text").GetString() ?? "", item.GetProperty("provider").GetString() ?? "gemma", item.TryGetProperty("model", out var model) && model.ValueKind != JsonValueKind.Null ? model.GetString() : null)).ToArray();
        return new CaptionTransformResponse(captions, "gemma", modelId, "Python");
    }
    private async Task RegisterObjectTemplateAsync()
    {
        var dialog = new OpenFileDialog { Filter = "AviUtl2用オブジェクト|*.object" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _objectTemplate = await AviUtl2ObjectTemplateParser.LoadAsync(dialog.FileName, CancellationToken.None);
            _style = _objectTemplate.ReadKnownStyle();
            _status.Text = $"AviUtl2からスタイルを読み込みました: {Path.GetFileName(dialog.FileName)}";
        }
        catch (FatException exception) { Error(exception.Code, exception.Message); }
    }
    private void ApplyStandardStyle()
    {
        _objectTemplate = AviUtl2ObjectTemplate.CreateStandard(_style);
        _status.Text = "FAT標準スタイルを全字幕の出力へ適用します。字幕本文・時間は変更しません。";
    }
    private Task SplitCaptionsAsync()
    {
        _captionGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
        _captionGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = SelectedCaptionRows();
        if (selected.Count == 0) { Error("FAT_SPLIT_SELECTION_REQUIRED", "分解したい字幕の行をクリックしてから実行してください。"); return Task.CompletedTask; }
        var before = _captions.Select(row => row.ToCaption()).ToArray();
        try
        {
            _status.Text = "字幕を読みやすく分解しています...";
            // Splitting must work while Python/Whisper/Codex are unavailable.
            // It also avoids serializing user-edited quotation marks or newlines
            // through a second JSON Lines boundary.
            var selectedIds = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            var manualRowId = _editingCaption?.Id;
            var useTextSelection = _textSelectionLength > 0 && manualRowId is not null && selectedIds.Contains(manualRowId);
            var values = before.SelectMany(caption =>
            {
                if (useTextSelection && caption.Id == manualRowId)
                    return CaptionSplitter.SplitSelection(caption, _textSelectionStart, _textSelectionLength, minimumSeconds: 0.8);
                return selectedIds.Contains(caption.Id)
                    ? CaptionSplitter.Split(caption, maximumCharacters: 24, maximumLines: 2, minimumSeconds: 0.8)
                    : [caption];
            }).ToArray();
            _undo.Push(before); _lastGenerated = before; Load(values); _status.Text = $"字幕を {_captions.Count} 件へ分解しました。内容はそのまま編集できます。";
        }
        catch (Exception error) { Error("FAT_SPLIT_FAILED", error.Message); }
        return Task.CompletedTask;
    }

    private IReadOnlyList<CaptionRow> SelectedCaptionRows()
    {
        if (_captionGrid is null) return [];
        var selected = _captionGrid.SelectedItems.Cast<CaptionRow>().ToArray();
        // When text is being edited, WPF's CurrentItem is the reliable focus
        // indicator even before the multi-selection collection has updated.
        if (selected.Length == 0 && _captionGrid.CurrentItem is CaptionRow focused) return [focused];
        return selected;
    }

    private void MergeSelectedCaptions()
    {
        var selected = SelectedCaptionRows().OrderBy(row => row.StartTime).ThenBy(row => row.EndTime).ToArray();
        if (selected.Length < 2)
        {
            Error("FAT_MERGE_SELECTION_REQUIRED", "統合したい字幕を2件以上、Ctrlキーを押しながら選択してください。");
            return;
        }

        var before = _captions.Select(row => row.ToCaption()).ToArray();
        var values = selected.Select(row => row.ToCaption()).ToArray();
        var merged = values[0] with
        {
            StartTime = values.Min(caption => caption.StartTime),
            EndTime = values.Max(caption => caption.EndTime),
            OriginalTranscript = string.Join("\n", values.Select(caption => caption.OriginalTranscript).Where(text => !string.IsNullOrWhiteSpace(text))),
            Text = MergeCaptionText(values.Select(caption => caption.Text))
        };
        var selectedIds = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var insertAt = _captions.TakeWhile(row => !selectedIds.Contains(row.Id)).Count();
        var result = before.Where(caption => !selectedIds.Contains(caption.Id)).ToList();
        result.Insert(Math.Min(insertAt, result.Count), merged);
        _undo.Push(before); _lastGenerated = before; Load(result);
        _status.Text = $"{selected.Length} 件の字幕を1件へ統合しました。本文は引き続き直接編集できます。";
    }

    private static string MergeCaptionText(IEnumerable<string> texts)
    {
        var result = string.Empty;
        foreach (var value in texts.Select(text => (text ?? string.Empty).Trim()).Where(text => text.Length > 0))
        {
            var insertSpace = result.Length > 0 && char.IsLetterOrDigit(result[^1]) && char.IsLetterOrDigit(value[0]);
            result += (insertSpace ? " " : string.Empty) + value;
        }
        return result;
    }
    private static string FindRuntime(string appBase)
    {
        for (var current = new DirectoryInfo(appBase); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "runtime");
            if (HasPythonEngine(candidate)) return candidate;
        }
        return Path.Combine(appBase, "runtime");
    }
    private static bool HasPythonEngine(string runtime) =>
        (File.Exists(Path.Combine(runtime, "python-runtime", "python.exe")) || File.Exists(Path.Combine(runtime, "python-env", "Scripts", "python.exe"))) &&
        File.Exists(Path.Combine(runtime, "python", "fat_worker.py"));
    private async Task ExportAsync()
    {
        if (_captions.Count == 0) { Error("FAT_NO_CAPTIONS", "There are no captions to export."); return; }
        // One .object is the practical default.  The legacy one-caption-per-file
        // exporter remains available as an explicitly named compatibility option.
        var dialog = new SaveFileDialog { Filter = "AviUtl2用オブジェクト（一括・Experimental）|*.object|AviUtl2用オブジェクト（個別ファイル・互換用）|*.object|Placement JSON|*.placement.json|SRT字幕|*.srt|TXT|*.txt", FilterIndex = 1, FileName = "fat-captions" };
        if (dialog.ShowDialog(this) != true) return;
        var captions = _captions.Select(x => x.ToCaption()).ToArray();
        try
        {
            switch (dialog.FilterIndex)
            {
                case 1:
                    var optionsDialog = new MultiObjectExportOptionsWindow(captions, _objectTemplate, _style, 60) { Owner = this };
                    if (optionsDialog.ShowDialog() != true) return;
                    _status.Text = "Experimental一括.objectを書き出しています...";
                    var multi = await new AviUtl2MultiObjectExporter(_objectTemplate, _style).ExportAsync(dialog.FileName, captions, 60, optionsDialog.Options, CancellationToken.None);
                    _progress.Value = 100; _status.Text = $"Experimental一括.objectを保存しました: {multi.Exported} 件（Layer {string.Join(", ", multi.UsedLayers)}）";
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(dialog.FileName) ?? ".") { UseShellExecute = true });
                    break;
                case 2:
                    var folder = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? ".", $"FAT_Object_Export_{DateTime.Now:yyyyMMdd_HHmmss}");
                    _progress.Value = 0; _status.Text = "互換用の個別AviUtl2オブジェクトを書き出しています...";
                    var result = await new AviUtl2ObjectExporter(_objectTemplate, _style).ExportAsync(folder, captions, 60, new Progress<(int Current, int Total)>(value => { _progress.Value = value.Total == 0 ? 0 : value.Current * 100d / value.Total; _status.Text = $"互換用の個別オブジェクトを書き出しています: {value.Current} / {value.Total}"; }), CancellationToken.None);
                    _progress.Value = 100; _status.Text = $"完了: {result.Exported} 件を個別出力しました。";
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                    break;
                case 3: await FatFiles.WritePlacementAsync(dialog.FileName, captions, 30, CancellationToken.None); _status.Text = "Placement JSONを保存しました。"; break;
                case 4: await new SrtCaptionExporter().ExportAsync(dialog.FileName, captions, CancellationToken.None); _status.Text = "SRTを保存しました。"; break;
                case 5: await new TxtCaptionExporter().ExportAsync(dialog.FileName, captions, CancellationToken.None); _status.Text = "TXTを保存しました。"; break;
            }
        }
        catch (FatException exception) { Error(exception.Code, exception.Message); }
    }
    private async Task ShowModelManagerAsync()
    {
        var window = new ModelManagerWindow(AppContext.BaseDirectory, _pythonEngine, _codexCli, _codexAppServer, _claudeCode);
        window.Owner = this;
        try { await window.RefreshAsync(); }
        catch (Exception error) { window.ShowRefreshFailure(error); }
        // The manager is always a child of the FAT main window.  It must never
        // become Application.MainWindow or leave the application without a visible owner.
        window.ShowDialog();
        Activate();
    }
    private void Undo()
    {
        if (_undo.TryPop(out var captions)) { Load(captions); _status.Text = "直前の変更を元に戻しました。"; }
        else if (_lastGenerated.Count > 0) { Load(_lastGenerated); _status.Text = "認識直後の字幕へ戻しました。"; }
        else Error("FAT_UNDO_EMPTY", "元に戻せる変更がありません。");
    }
    private void Load(IReadOnlyList<FATCaption> captions)
    {
        _captions.Clear();
        foreach (var caption in captions)
        {
            var row = new CaptionRow(caption);
            row.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(CaptionRow.Text) && ReferenceEquals(row, _currentCaption)) UpdatePreviewOverlay();
            };
            _captions.Add(row);
        }
        _captionTimeline = new CaptionTimeline(_captions.Select(row => row.ToCaption()));
        _currentCaption = null;
        _editingCaption = null;
        _textSelectionStart = 0;
        _textSelectionLength = 0;
        UpdatePlaybackSync();
    }
    private void Error(string code, string message)
    {
        _status.Text = $"Error ({code})";
        var guidance = code switch
        {
            "FAT_INPUT_REQUIRED" => "動画または音声ファイルを選択してから実行してください。",
            "FAT_RUNTIME_MISSING" or "FAT_WORKER_MISSING" => "FATを再インストールし、runtime フォルダが揃っているか確認してください。",
            "FAT_FFMPEG_MISSING" => "FAT runtime 内の ffmpeg と ffprobe を確認してください。",
            "FAT_OUTPUT_WRITE_FAILED" => "保存先の書き込み権限と、同名ファイルが他のアプリで開かれていないか確認してください。",
            "OBJECT_MULTI_OVERLAP_UNSUPPORTED" => "「自動レイヤー」を選ぶか、字幕の時間重なりを編集してください。",
            "CODEX_UNAVAILABLE" or "CODEX_APP_SERVER_DISCONNECTED" => "Codexを使わない場合は「AIなし」を選べます。Codexを使う場合は接続を確認してください。",
            _ => "字幕一覧の内容は保持されています。内容を確認してから、もう一度実行してください。"
        };
        MessageBox.Show(this, $"{message}\n\n対処: {guidance}\n\n詳細コード: {code}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private async Task RunWorkerAsync(RecognitionRequest request, Action<FatProgress> progress, CancellationToken cancellationToken)
    {
        var pipeName = $"AviUtl2FAT-{Guid.NewGuid():N}"; var worker = Path.Combine(AppContext.BaseDirectory, "AviUtl2FAT.Worker.exe");
        if (!File.Exists(worker)) throw new FatException("FAT_WORKER_MISSING", "AviUtl2FAT.Worker.exe was not found.");
        using var process = Process.Start(new ProcessStartInfo(worker, $"--pipe {pipeName}") { UseShellExecute = false, CreateNoWindow = true }) ?? throw new FatException("FAT_WORKER_START_FAILED", "FAT Worker could not be started.");
        using var cancellationRegistration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(10_000, cancellationToken); }
        catch (TimeoutException error) { throw new FatException("FAT_WORKER_CONNECT_TIMEOUT", "音声認識ワーカーの起動がタイムアウトしました。FATを再起動してもう一度実行してください。", error); }
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true); await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new FatIpcMessage("recognize", Guid.NewGuid().ToString("N"), request), JsonOptions));
        while (await reader.ReadLineAsync(cancellationToken) is { } line) { var message = JsonSerializer.Deserialize<FatIpcMessage>(line, JsonOptions); if (message is null) continue; FatProtocols.Validate(message.Protocol, message.Version, FatProtocols.AppProtocol); if (message.Type == "error") throw new FatException(message.Error?.Code ?? "FAT_WORKER_ERROR", message.Error?.Message ?? "Worker failed."); if (message.Type == "completed") return; if (message.Type == "progress" && message.Payload is JsonElement payload) { var value = payload.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : (double?)null; var text = payload.TryGetProperty("message", out var item) ? item.GetString() ?? "Working..." : "Working..."; progress(new FatProgress("recognition", value, text)); } }
        throw new FatException("FAT_WORKER_DISCONNECTED", "Worker disconnected before completion.");
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>Explicit confirmation and a read-only layer preview for the experimental .object exporter.</summary>
public sealed class MultiObjectExportOptionsWindow : Window
{
    private readonly IReadOnlyList<FATCaption> _captions;
    private readonly AviUtl2ObjectTemplate _template;
    private readonly AviUtl2TextStyle _style;
    private readonly double _fps;
    private readonly ComboBox _mode = new() { Width = 220 };
    private readonly TextBox _startLayer = new() { Text = "1", Width = 72 };
    private readonly TextBox _maxLayers = new() { Text = "8", Width = 72 };
    private readonly TextBox _gapFrames = new() { Text = "0", Width = 72 };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
    public AviUtl2MultiObjectExportOptions Options { get; private set; } = new();

    public MultiObjectExportOptionsWindow(IReadOnlyList<FATCaption> captions, AviUtl2ObjectTemplate template, AviUtl2TextStyle style, double fps)
    {
        _captions = captions; _template = template; _style = style; _fps = fps;
        Title = "一括 .object 出力（Experimental）"; Width = 590; Height = 430; MinWidth = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "複数の通常テキストを1つの .object にまとめます", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "実機で確認済みの複数オブジェクト形式を使います。最初は2件で読み込みを確認してください。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        _mode.Items.Add(new ComboBoxItem { Content = "自動レイヤー（重なり時だけ分散）", Tag = AviUtl2LayerPlacementMode.Auto });
        _mode.Items.Add(new ComboBoxItem { Content = "単一レイヤー（重なりはエラー）", Tag = AviUtl2LayerPlacementMode.SingleLayer });
        _mode.Items.Add(new ComboBoxItem { Content = "手動レイヤー（指定レイヤー固定）", Tag = AviUtl2LayerPlacementMode.Manual });
        _mode.SelectedIndex = 0;
        AddRow(panel, "配置方式:", _mode);
        AddRow(panel, "開始レイヤー:", _startLayer);
        AddRow(panel, "自動レイヤー上限:", _maxLayers);
        AddRow(panel, "最小間隔（frame）:", _gapFrames);
        panel.Children.Add(_preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var export = new Button { Content = "一括 .object を保存", Padding = new Thickness(14, 6, 14, 6) };
        export.Click += (_, _) => Confirm(); buttons.Children.Add(cancel); buttons.Children.Add(export); panel.Children.Add(buttons);
        _mode.SelectionChanged += (_, _) => UpdatePreview(); _startLayer.TextChanged += (_, _) => UpdatePreview(); _maxLayers.TextChanged += (_, _) => UpdatePreview(); _gapFrames.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private static void AddRow(Panel panel, string label, FrameworkElement input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(input); panel.Children.Add(row);
    }
    private AviUtl2MultiObjectExportOptions ReadOptions() => new AviUtl2MultiObjectExportOptions((AviUtl2LayerPlacementMode)((ComboBoxItem)_mode.SelectedItem).Tag,
        int.TryParse(_startLayer.Text, out var start) ? start : 0, int.TryParse(_maxLayers.Text, out var max) ? max : 0,
        int.TryParse(_gapFrames.Text, out var gap) ? gap : -1).Validate();
    private void UpdatePreview()
    {
        try
        {
            var plan = new AviUtl2MultiObjectExporter(_template, _style).Plan(_captions, _fps, ReadOptions());
            var layers = plan.Select(item => item.Layer).Distinct().Order().ToArray();
            _preview.Text = $"プレビュー: {plan.Count} 件 / 使用レイヤー {string.Join(", ", layers)} / 範囲 Layer {layers.First()}–{layers.Last()}\n同じ開始時刻は、字幕一覧の順序を維持します。";
        }
        catch (FatException error) { _preview.Text = $"この設定では出力できません: {error.Message}"; }
    }
    private void Confirm()
    {
        try { Options = ReadOptions(); _ = new AviUtl2MultiObjectExporter(_template, _style).Plan(_captions, _fps, Options); DialogResult = true; }
        catch (FatException error) { MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

public sealed class AiPreviewWindow : Window
{
    private readonly IReadOnlyList<FATCaption> _original;
    private IReadOnlyList<FATCaption> _result;
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
    public IReadOnlyList<FATCaption> Result => _result;
    public AiPreviewWindow(IReadOnlyList<FATCaption> original, IReadOnlyList<FATCaption> result, string provider, string backend, CaptionOperation operation)
    {
        _original = original; _result = result; Title = "AI変更案"; Width = 620; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = $"使用AI: {provider}\n接続: {backend}\n操作: {(operation == CaptionOperation.Naturalize ? "自然にする" : "短くする")}", FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "変更前:", Margin = new Thickness(0, 12, 0, 2) });
        panel.Children.Add(new TextBox { Text = string.Join(Environment.NewLine, original.Select(c => c.Text)), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 60 });
        panel.Children.Add(new TextBlock { Text = "変更後（編集して適用できます）:", Margin = new Thickness(0, 12, 0, 2) });
        _editor.Text = string.Join(Environment.NewLine, result.Select(c => c.Text)); panel.Children.Add(_editor);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "適用", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => { var lines = _editor.Text.Replace("\r\n", "\n").Split('\n'); if (lines.Length != _result.Count) { MessageBox.Show(this, "字幕件数と同じ行数で編集してください。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; } _result = _result.Select((caption, index) => caption with { Text = OutputValidator.ValidateText(lines[index]) }).ToArray(); DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(12, 6, 12, 6) }; cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
    }
}

public sealed class ModelManagerWindow : Window
{
    private sealed record SpeechModelCard(string Id, string Name, string Source, string Notes);
    private static readonly SpeechModelCard[] SpeechModels =
    [
        new("base", "Whisper Base", "Systran/faster-whisper-base", "低負荷・初回導入向け"),
        new("small", "Whisper Small", "Systran/faster-whisper-small", "標準精度・通常はこちら")
    ];
    private sealed record GemmaCard(string Id, string Name, string Source, double DiskGb, int RamGb, int VramGb);
    private static readonly GemmaCard[] GemmaModels =
    [
        new("gemma-4-e2b-it", "Gemma 4 E2B", "google/gemma-4-E2B-it", 10.3, 16, 12),
        new("gemma-4-e4b-it", "Gemma 4 E4B", "google/gemma-4-E4B-it", 18, 24, 16),
        new("gemma-4-12b-it", "Gemma 4 12B", "google/gemma-4-12B-it", 28, 40, 24),
        new("gemma-4-26b-a4b-it", "Gemma 4 26B A4B", "google/gemma-4-26B-A4B-it", 55, 72, 48)
    ];
    private readonly string _runtime;
    private readonly PersistentPythonWorker _engine;
    private readonly ComboBox _speechModel = new() { Width = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _speechDescription = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _speechState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ProgressBar _speechProgress = new() { Minimum = 0, Maximum = 100, Height = 18, Visibility = Visibility.Collapsed };
    private readonly Button _speechDownload = new() { Content = "音声モデルをダウンロード", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _speechValidate = new() { Content = "確認 / 更新", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _speechDelete = new() { Content = "削除", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 18, Visibility = Visibility.Collapsed };
    private readonly Button _download = new() { Content = "Download model", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _load = new() { Content = "Load model", Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
    private readonly ComboBox _gemmaModel = new() { Width = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _gemmaDescription = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CodexCliBackend _codex;
    private readonly CodexAppServerBackend _appServer;
    private readonly ClaudeCodeCliBackend _claude;
    private readonly ComboBox _codexBackend = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _codexState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _claudeState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    public ModelManagerWindow(string appBase, PersistentPythonWorker engine, CodexCliBackend codex, CodexAppServerBackend appServer, ClaudeCodeCliBackend claude)
    {
        _runtime = FindRuntime(appBase); _engine = engine; _codex = codex; _appServer = appServer; _claude = claude; Title = "AI / 音声モデル管理"; Width = 640; Height = 730; MinHeight = 520;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(new TextBlock { Text = "音声認識", FontSize = 18 });
        panel.Children.Add(new TextBlock { Text = "OpenAI Whisper をベースにした音声認識\nBackend: faster-whisper\nモデル本体は、ここで明示的にダウンロードした場合だけ取得します。", TextWrapping = TextWrapping.Wrap });
        var speechRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        speechRow.Children.Add(new TextBlock { Text = "音声モデル:", VerticalAlignment = VerticalAlignment.Center });
        foreach (var model in SpeechModels) _speechModel.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
        _speechModel.SelectedIndex = 1;
        _speechModel.SelectionChanged += async (_, _) => { UpdateSpeechDescription(); await RefreshSpeechAsync(); };
        speechRow.Children.Add(_speechModel); panel.Children.Add(speechRow);
        UpdateSpeechDescription();
        panel.Children.Add(_speechDescription); panel.Children.Add(_speechState); panel.Children.Add(_speechProgress);
        var speechActions = new StackPanel { Orientation = Orientation.Horizontal };
        _speechDownload.Click += async (_, _) => await DownloadSpeechAsync();
        _speechValidate.Click += async (_, _) => await RefreshSpeechAsync();
        _speechDelete.Click += async (_, _) => await DeleteSpeechAsync();
        speechActions.Children.Add(_speechDownload); speechActions.Children.Add(_speechValidate); speechActions.Children.Add(_speechDelete); panel.Children.Add(speechActions);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "ローカル生成AI（Gemma 4）", FontSize = 18 });
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal }; modelRow.Children.Add(new TextBlock { Text = "モデル:", VerticalAlignment = VerticalAlignment.Center });
        foreach (var model in GemmaModels) _gemmaModel.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
        _gemmaModel.SelectedIndex = 0; modelRow.Children.Add(_gemmaModel); panel.Children.Add(modelRow);
        panel.Children.Add(_gemmaDescription);
        _gemmaModel.SelectionChanged += async (_, _) => { UpdateGemmaDescription(); await RefreshAsync(); };
        UpdateGemmaDescription();
        panel.Children.Add(_state); panel.Children.Add(_progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _download.Content = "モデルをダウンロード"; _load.Content = "モデルを読み込む";
        _download.Click += async (_, _) => await DownloadAsync(); actions.Children.Add(_download);
        _load.Click += async (_, _) => await LoadAsync(); actions.Children.Add(_load); panel.Children.Add(actions);
        var refresh = new Button { Content = "確認 / 更新", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6) }; refresh.Click += async (_, _) => await ValidateAsync(); panel.Children.Add(refresh);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "OpenAI Codex", FontSize = 18 });
        panel.Children.Add(new TextBlock { Text = "Codex App Server は、Codex CLI の公式JSON-RPC接続です。Codex GUIそのものは自動操作しません。字幕本文だけを送信し、結果は必ず確認・編集してから適用します。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_codexState);
        var backendRow = new StackPanel { Orientation = Orientation.Horizontal }; backendRow.Children.Add(new TextBlock { Text = "接続方式:", VerticalAlignment = VerticalAlignment.Center });
        _codexBackend.Items.Add(new ComboBoxItem { Content = "自動（接続済みApp Server優先）", Tag = "auto" });
        _codexBackend.Items.Add(new ComboBoxItem { Content = "Codex App Server", Tag = "appserver" });
        _codexBackend.Items.Add(new ComboBoxItem { Content = "Codex CLI", Tag = "cli" });
        _codexBackend.SelectedIndex = 0; backendRow.Children.Add(_codexBackend); panel.Children.Add(backendRow);
        var chooseCli = new Button { Content = "Codex CLIを参照...", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 8) };
        chooseCli.Click += async (_, _) => await ChooseCodexCliAsync();
        panel.Children.Add(chooseCli);
        var codexActions = new StackPanel { Orientation = Orientation.Horizontal };
        var connectCodex = new Button { Content = "接続する", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        connectCodex.Click += async (_, _) => await ConnectCodexAsync();
        var checkCodex = new Button { Content = "接続確認", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) }; checkCodex.Click += async (_, _) => await RefreshCodexAsync();
        var openCodex = new Button { Content = "Codexを開く", Padding = new Thickness(12, 6, 12, 6) }; openCodex.Click += async (_, _) => { try { var status = await _codex.GetStatusAsync(CancellationToken.None); CodexCliBackend.OpenGui(status); } catch (FatException error) { MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information); } catch (Exception error) { MessageBox.Show(this, "Codex GUIを起動できませんでした。\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); } };
        var disconnectCodex = new Button { Content = "接続解除", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) }; disconnectCodex.Click += async (_, _) => { await _appServer.DisconnectAsync(CancellationToken.None); await RefreshCodexAsync(); };
        codexActions.Children.Add(connectCodex); codexActions.Children.Add(checkCodex); codexActions.Children.Add(openCodex); codexActions.Children.Add(disconnectCodex); panel.Children.Add(codexActions);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Anthropic Claude Code", FontSize = 18 });
        panel.Children.Add(new TextBlock { Text = "Claude Code CLI を使う外部AIです。字幕本文だけを専用の空作業領域から送信し、結果は必ず確認・編集してから適用します。認証情報はFATに保存しません。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_claudeState);
        var claudeActions = new StackPanel { Orientation = Orientation.Horizontal };
        var checkClaude = new Button { Content = "接続確認", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) }; checkClaude.Click += async (_, _) => await RefreshClaudeAsync();
        var chooseClaude = new Button { Content = "Claude CLIを参照...", Padding = new Thickness(12, 6, 12, 6) }; chooseClaude.Click += async (_, _) => await ChooseClaudeCliAsync();
        claudeActions.Children.Add(checkClaude); claudeActions.Children.Add(chooseClaude); panel.Children.Add(claudeActions);
    }
    public async Task RefreshAsync()
    {
        await RefreshSpeechAsync();
        try
        {
            var response = await RunModelCommandAsync("model.status", $"{{\"model_id\":\"{SelectedGemma.Id}\"}}");
            using var document = JsonDocument.Parse(response); var payload = document.RootElement.GetProperty("payload");
            var valid = payload.GetProperty("valid").GetBoolean(); var state = payload.GetProperty("state").GetString(); var path = payload.GetProperty("local_path").GetString();
            var compatibility = payload.GetProperty("compatibility"); var recommendation = compatibility.GetProperty("recommendation").GetString(); var reason = compatibility.GetProperty("reason").GetString();
            _state.Text = valid ? $"状態: {state}\n保存先: {path}\nこのPC: {recommendation} ({reason})\nPython Engine: 接続済み" : $"状態: {state}\nモデルは未導入または不完全です。\nこのPC: {recommendation} ({reason})\n保存先: {path}\nPython Engine: 接続済み";
            _download.IsEnabled = !valid; _load.IsEnabled = valid;
        }
        catch (Exception error) { ShowRefreshFailure(error); }
        await RefreshCodexAsync();
        await RefreshClaudeAsync();
    }
    public void ShowRefreshFailure(Exception error)
    {
        _state.Text = "状態: Python Engineを確認できませんでした。\nGemma機能は使用できませんが、AIなし・Codexの管理は継続できます。\n詳細: " + error.Message;
        _download.IsEnabled = _load.IsEnabled = false;
    }
    private async Task RefreshCodexAsync()
    {
        try
        {
            var status = await _codex.GetStatusAsync(CancellationToken.None);
            var appServer = await _appServer.GetStatusAsync(CancellationToken.None);
            _codexState.Text = $"Codex GUI: {(status.GuiDetected ? "利用可能（手動利用のみ）" : "未検出")}\nFAT → Codex App Server: {_appServer.ConnectionState}\nFAT → Codex CLI: {(status.CliDetected ? (status.CliRunnable ? "使用可能" : "検出済み（実行不可）") : "未検出")}\n詳細: {appServer.Detail}";
        }
        catch (Exception error) { _codexState.Text = "Codex状態: 確認失敗\n詳細: " + error.Message; }
    }
    private async Task RefreshClaudeAsync()
    {
        try { var status = await _claude.GetStatusAsync(CancellationToken.None); _claudeState.Text = $"Claude Code CLI: {(status.IsAvailable ? "使用可能" : status.Detected ? "検出済み（実行不可・認証またはCLIを確認）" : "未インストール")}" + "\n詳細: " + status.Detail; }
        catch (Exception error) { _claudeState.Text = "Claude Code状態: 確認失敗\n詳細: " + error.Message; }
    }
    private async Task ChooseClaudeCliAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Claude Code CLI|claude.exe;claude.cmd|実行ファイル|*.exe;*.cmd|すべてのファイル|*.*", Title = "Claude Code CLI実行ファイルを選択" };
        if (dialog.ShowDialog(this) != true) return;
        _claude.ConfigureExecutablePath(dialog.FileName);
        var status = await _claude.GetStatusAsync(CancellationToken.None);
        if (!status.IsAvailable) { _claude.ConfigureExecutablePath(null); MessageBox.Show(this, "選択したファイルは実行可能なClaude Code CLIとして確認できませんでした。\n\n" + status.Detail, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AviUtl2FAT", "ai-settings.json");
        var current = await AiSelectionSettingsStore.LoadAsync(path, CancellationToken.None);
        await AiSelectionSettingsStore.SaveAsync(path, current with { ClaudeCliPath = dialog.FileName }, CancellationToken.None);
        await RefreshClaudeAsync();
    }
    private async Task ConnectCodexAsync()
    {
        var confirmation = MessageBox.Show(this, "OpenAI Codexとの連携\n\n選択した字幕テキストだけが、AI処理時にOpenAIサービスへ送信される場合があります。動画・AviUtl2プロジェクト・認証情報は送信しません。\n\nAI結果は自動確定せず、必ず確認・編集してから適用します。\n\n連携しますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        var mode = (string)((ComboBoxItem)_codexBackend.SelectedItem).Tag;
        try
        {
            if (mode is "appserver" or "auto")
            {
                await _appServer.ConnectAsync(CancellationToken.None);
                MessageBox.Show(this, "Codex App Serverへ接続しました。\n\nメイン画面でAIを「OpenAI Codex」、Codex接続を「Codex App Server」または「自動」にして、字幕を選択して実行できます。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var status = await _codex.GetStatusAsync(CancellationToken.None);
                if (!status.IsAvailable) throw new FatException("CODEX_CLI_UNAVAILABLE", status.Detail);
                MessageBox.Show(this, "Codex CLI を利用できます。メイン画面でAIを「OpenAI Codex」にして、字幕を選択して実行してください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (FatException error) { MessageBox.Show(this, $"Codexへ接続できませんでした。\n\n{error.Code}: {error.Message}\n\nCodex GUIの有無だけではApp Serverは実行できません。公式Codex CLIが実行可能で、サインイン済みである必要があります。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception error) { MessageBox.Show(this, "Codexへ接続できませんでした。\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        await RefreshCodexAsync();
    }
    private async Task ChooseCodexCliAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Codex CLI|codex.exe;codex.cmd|実行ファイル|*.exe;*.cmd|すべてのファイル|*.*", Title = "Codex CLI実行ファイルを選択" };
        if (dialog.ShowDialog(this) != true) return;
        _codex.ConfigureExecutablePath(dialog.FileName);
        var status = await _codex.GetStatusAsync(CancellationToken.None);
        if (!status.IsAvailable)
        {
            _codex.ConfigureExecutablePath(null);
            MessageBox.Show(this, "選択したファイルは実行可能なCodex CLIとして確認できませんでした。\n\n" + status.Detail, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AviUtl2FAT", "ai-settings.json");
        var current = await AiSelectionSettingsStore.LoadAsync(path, CancellationToken.None);
        await AiSelectionSettingsStore.SaveAsync(path, current with { CodexCliPath = dialog.FileName }, CancellationToken.None);
        _appServer.ConfigureExecutablePath(dialog.FileName);
        await RefreshCodexAsync();
    }
    private SpeechModelCard SelectedSpeech => SpeechModels.First(item => item.Id == (string)((ComboBoxItem)_speechModel.SelectedItem).Tag);
    private GemmaCard SelectedGemma => GemmaModels.First(item => item.Id == (string)((ComboBoxItem)_gemmaModel.SelectedItem).Tag);
    private void UpdateSpeechDescription()
    {
        var item = SelectedSpeech;
        _speechDescription.Text = $"提供元: {item.Source}\n用途: {item.Notes}\n保存先: runtime\\models\\{item.Id}";
    }
    private void UpdateGemmaDescription() { var item = SelectedGemma; _gemmaDescription.Text = $"提供元: Google / Instruction Tuned\nモデルID: {item.Source}\n概算サイズ: {item.DiskGb:0.#} GB\n推奨RAM: {item.RamGb} GB / 推奨VRAM: {item.VramGb} GB\n利用条件: https://huggingface.co/{item.Source}"; }
    private async Task RefreshSpeechAsync()
    {
        if (!HasSpeechModelRuntime())
        {
            _speechState.Text = "状態: Python音声モデル管理を確認できませんでした。\nインストール済みRuntimeを確認してください。";
            _speechDownload.IsEnabled = _speechValidate.IsEnabled = _speechDelete.IsEnabled = false;
            return;
        }
        try
        {
            var result = await RunSpeechModelCommandAsync("validate", SelectedSpeech.Id);
            var valid = result.TryGetProperty("valid", out var validElement) && validElement.GetBoolean();
            var state = result.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : "unknown";
            var path = result.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : Path.Combine(_runtime, "models", SelectedSpeech.Id);
            var missing = result.TryGetProperty("missing", out var missingElement) && missingElement.ValueKind == JsonValueKind.Array
                ? string.Join(", ", missingElement.EnumerateArray().Select(item => item.GetString()))
                : string.Empty;
            _speechState.Text = valid
                ? $"状態: インストール済み\n保存先: {path}"
                : $"状態: {state}\n不足: {(string.IsNullOrWhiteSpace(missing) ? "未導入または不完全" : missing)}\n保存先: {path}";
            _speechDownload.IsEnabled = !valid;
            _speechDelete.IsEnabled = valid;
            _speechValidate.IsEnabled = true;
        }
        catch (Exception error)
        {
            _speechState.Text = "状態: 確認失敗\n詳細: " + error.Message;
            _speechDownload.IsEnabled = true;
            _speechDelete.IsEnabled = false;
            _speechValidate.IsEnabled = true;
        }
    }
    private async Task DownloadSpeechAsync()
    {
        var item = SelectedSpeech;
        var confirmation = MessageBox.Show(this, $"{item.Name} をHugging Faceからダウンロードします。\n\nSource: {item.Source}\n保存先: runtime\\models\\{item.Id}\n\n音声認識時に必要なモデルです。ダウンロードを開始しますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        _speechDownload.IsEnabled = _speechValidate.IsEnabled = _speechDelete.IsEnabled = false;
        _speechProgress.Visibility = Visibility.Visible; _speechProgress.IsIndeterminate = true;
        _speechState.Text = "状態: ダウンロード中...";
        try
        {
            await RunSpeechModelCommandAsync("download", item.Id, message =>
            {
                _speechState.Text = message;
                if (message.Contains("完了", StringComparison.Ordinal)) { _speechProgress.IsIndeterminate = false; _speechProgress.Value = 100; }
            });
            await RefreshSpeechAsync();
        }
        catch (Exception error) { _speechState.Text = "状態: ダウンロード失敗\n詳細: " + error.Message; }
        finally
        {
            _speechProgress.IsIndeterminate = false; _speechProgress.Visibility = Visibility.Collapsed;
            _speechValidate.IsEnabled = true; _speechDownload.IsEnabled = true;
        }
    }
    private async Task DeleteSpeechAsync()
    {
        var item = SelectedSpeech;
        var confirmation = MessageBox.Show(this, $"{item.Name} を削除します。\n\n保存先: runtime\\models\\{item.Id}\n\nFAT本体や他のモデルは削除しません。", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        _speechDownload.IsEnabled = _speechValidate.IsEnabled = _speechDelete.IsEnabled = false;
        try { await RunSpeechModelCommandAsync("delete", item.Id); }
        catch (Exception error) { MessageBox.Show(this, "音声モデルを削除できませんでした。\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        await RefreshSpeechAsync();
    }
    private bool HasSpeechModelRuntime() =>
        File.Exists(ResolveRuntimePython()) &&
        File.Exists(Path.Combine(_runtime, "python", "model_manager.py")) &&
        File.Exists(Path.Combine(_runtime, "python", "att_engine", "model_service.py"));
    private string ResolveRuntimePython()
    {
        var portable = Path.Combine(_runtime, "python-runtime", "python.exe");
        return File.Exists(portable) ? portable : Path.Combine(_runtime, "python-env", "Scripts", "python.exe");
    }
    private async Task<JsonElement> RunSpeechModelCommandAsync(string command, string model, Action<string>? progress = null)
    {
        var python = ResolveRuntimePython();
        var manager = Path.Combine(_runtime, "python", "model_manager.py");
        if (!File.Exists(python) || !File.Exists(manager)) throw new FatException("FAT_RUNTIME_MISSING", "Python音声モデル管理Runtimeが見つかりません。");
        Directory.CreateDirectory(Path.Combine(_runtime, "models"));
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = _runtime,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.ArgumentList.Add(manager);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(model);
        start.ArgumentList.Add("--model-dir");
        start.ArgumentList.Add(Path.Combine(_runtime, "models"));
        using var process = Process.Start(start) ?? throw new FatException("FAT_PYTHON_START_FAILED", "音声モデル管理を開始できませんでした。");
        var stderrTask = process.StandardError.ReadToEndAsync();
        JsonDocument? last = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                last?.Dispose();
                last = JsonDocument.Parse(line);
                var root = last.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "error")
                {
                    var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "FAT_MODEL_ERROR";
                    var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "モデル操作に失敗しました。";
                    throw new FatException(code ?? "FAT_MODEL_ERROR", message ?? "モデル操作に失敗しました。");
                }
                if (root.TryGetProperty("message", out var messageProperty))
                    progress?.Invoke(messageProperty.GetString() ?? line);
            }
            catch (JsonException) { progress?.Invoke(line); }
        }
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new FatException("FAT_MODEL_COMMAND_FAILED", string.IsNullOrWhiteSpace(stderr) ? "モデル操作に失敗しました。" : stderr.Trim());
        if (last is null) throw new FatException("FAT_MODEL_COMMAND_FAILED", "モデル操作の結果を取得できませんでした。");
        var finalRoot = last.RootElement.Clone();
        last.Dispose();
        if (finalRoot.TryGetProperty("type", out var finalType) && finalType.GetString() == "model_validation")
        {
            using var doc = JsonDocument.Parse(finalRoot.GetRawText());
            return doc.RootElement.Clone();
        }
        return finalRoot;
    }
    private async Task ValidateAsync() { _state.Text = "状態: 確認中..."; _state.Text = "確認結果: " + await RunModelCommandAsync("model.validate", $"{{\"model_id\":\"{SelectedGemma.Id}\"}}"); await RefreshAsync(); }
    private async Task LoadAsync() { _state.Text = "状態: モデルを読み込み中..."; _state.Text = "読み込み結果: " + await RunModelCommandAsync("model.load", $"{{\"model_id\":\"{SelectedGemma.Id}\"}}"); await RefreshAsync(); }
    private Task<string> RunModelCommandAsync(string type, string payload, Action<string>? progress = null) => _engine.RequestAsync(_runtime, type, payload, progress);
    private async Task DownloadAsync()
    {
        var item = SelectedGemma;
        var confirmation = MessageBox.Show(this, $"{item.Name} をGoogle / Hugging Faceからダウンロードします。\n\nSource: {item.Source}\nDownload: approximately {item.DiskGb:0.#} GB\n保存先: runtime\\models\\{item.Id}\n\n利用条件を確認し、Hugging Faceで必要なアクセス承認を済ませてから続行してください。", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        _download.IsEnabled = false; _progress.Visibility = Visibility.Visible; _state.Text = "Status: Downloading...";
        try
        {
            await RunModelCommandAsync("model.download", $"{{\"model_id\":\"{item.Id}\",\"confirmed\":true}}", line =>
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("percent", out var percent))
                {
                    _progress.Value = percent.GetDouble();
                    var downloaded = payload.TryGetProperty("downloaded_bytes", out var bytes) ? bytes.GetInt64() / 1_000_000_000d : 0;
                    var total = payload.TryGetProperty("total_bytes", out var totalBytes) ? totalBytes.GetInt64() / 1_000_000_000d : item.DiskGb;
                    _state.Text = $"Status: Downloading {percent.GetDouble():0.0}%\n{downloaded:0.0} GB / {total:0.0} GB";
                }
                else if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new InvalidOperationException(error.ToString());
            });
            await RefreshAsync();
        }
        catch (Exception error) { _state.Text = "Status: Error\n" + error.Message; }
        finally { _progress.Visibility = Visibility.Collapsed; _download.IsEnabled = true; }
    }
    private static string FindRuntime(string appBase)
    {
        var installed = Path.Combine(appBase, "runtime");
        if (HasPythonEngine(installed)) return installed;
        for (var current = new DirectoryInfo(appBase); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "runtime");
            if (HasPythonEngine(candidate)) return candidate;
        }
        return installed;
    }
    private static bool HasPythonEngine(string runtime) =>
        (File.Exists(Path.Combine(runtime, "python-runtime", "python.exe")) || File.Exists(Path.Combine(runtime, "python-env", "Scripts", "python.exe"))) &&
        File.Exists(Path.Combine(runtime, "python", "fat_worker.py"));
}

public sealed class PersistentPythonWorker : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task<string>? _stderr;

    public async Task<string> RequestAsync(string runtime, string type, string payload, Action<string>? progress = null)
    {
        await _gate.WaitAsync();
        try
        {
            StartIfNeeded(runtime);
            var id = Guid.NewGuid().ToString("N");
            await _writer!.WriteLineAsync($"{{\"protocol\":\"fat-python\",\"version\":1,\"id\":\"{id}\",\"type\":\"{type}\",\"payload\":{payload}}}");
            while (await _reader!.ReadLineAsync() is { } line)
            {
                // fat_worker.py owns stdout as UTF-8 JSON Lines.  A native
                // dependency must never make an incidental diagnostic line
                // crash the caption editor, so only accept complete messages.
                JsonDocument json;
                try { json = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (json)
                {
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetString() != id) continue;
                var responseType = root.GetProperty("type").GetString();
                if (responseType == "progress") { progress?.Invoke(line); continue; }
                if (responseType is "result" or "error") return line;
                }
            }
            var error = _stderr is null ? "" : await _stderr;
            Reset();
            throw new InvalidOperationException("FAT_PYTHON_DISCONNECTED: " + error);
        }
        catch
        {
            if (_process?.HasExited == true) Reset();
            throw;
        }
        finally { _gate.Release(); }
    }

    private void StartIfNeeded(string runtime)
    {
        if (_process is { HasExited: false }) return;
        Reset();
        var portablePython = Path.Combine(runtime, "python-runtime", "python.exe");
        var python = File.Exists(portablePython) ? portablePython : Path.Combine(runtime, "python-env", "Scripts", "python.exe");
        var worker = FindEngine(runtime);
        if (!File.Exists(python) || !File.Exists(worker)) throw new FatException("FAT_RUNTIME_MISSING", "Python FAT Engine runtime was not found.");
        var start = new ProcessStartInfo(python, $"\"{worker}\"")
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        _process = Process.Start(start) ?? throw new FatException("FAT_PYTHON_START_FAILED", "Python FAT Engine could not be started.");
        _writer = _process.StandardInput; _writer.AutoFlush = true; _reader = _process.StandardOutput; _stderr = _process.StandardError.ReadToEndAsync();
    }

    private static string FindEngine(string runtime)
    {
        var installed = Path.Combine(runtime, "python", "fat_worker.py");
        // In a development tree, use the current source before the copied runtime.
        // A packaged installation normally has no sibling python source and falls back below.
        for (var current = new DirectoryInfo(runtime).Parent; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "python", "fat_worker.py");
            if (File.Exists(candidate)) return candidate;
        }
        return installed;
    }

    private void Reset()
    {
        _writer?.Dispose(); _reader?.Dispose();
        if (_process is { HasExited: false }) { try { _process.Kill(entireProcessTree: true); } catch { } }
        _process?.Dispose(); _process = null; _writer = null; _reader = null; _stderr = null;
    }
    public void Dispose() { Reset(); _gate.Dispose(); }
}

public static class ButtonExtensions { public static Button With(this Button button, RoutedEventHandler handler) { button.Click += handler; return button; } }
public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var mainWindow = new FatWindow();
        app.MainWindow = mainWindow;
        app.Run(mainWindow);
    }
}
