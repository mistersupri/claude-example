using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WebcamTest;

/// <summary>Ways to read a camera; some drivers only deliver frames in one of them.</summary>
internal enum CaptureMode
{
    /// <summary>
    /// The pipeline the Windows Camera app uses: a XAML CaptureElement shows the preview and
    /// photos are taken from the preview frames.
    /// </summary>
    XamlPreview,

    /// <summary>Shared access to the preview stream through a frame reader.</summary>
    SharedPreview,

    /// <summary>Shared access to the video-record stream through a frame reader.</summary>
    SharedRecord,

    /// <summary>Exclusive control of the camera, preview stream.</summary>
    ExclusivePreview,

    /// <summary>Shared access to whichever colour stream comes first (last resort).</summary>
    SharedAnyColor,
}

/// <summary>A picture taken from the camera and whether it is all black.</summary>
internal readonly record struct GrabbedFrame(Bitmap Bitmap, bool Black);

/// <summary>
/// One running camera, read through the Windows MediaCapture API (the same stack as the Camera app).
/// Frame-reader modes deliver pictures on a thread-pool thread through the callback given to
/// <see cref="StartAsync"/>; the XAML preview mode shows the video itself and delivers pictures on request.
/// </summary>
internal sealed class CameraSession : IDisposable
{
    private const double BlackMeanThreshold = 2.0;
    private const int MaxListedFormats = 24;
    private const int DefaultPreviewWidth = 1280;
    private const int DefaultPreviewHeight = 720;

    private readonly MediaCapture capture;
    private readonly MediaFrameReader? reader;
    private readonly Action<Bitmap, bool> onFrame;
    private readonly Windows.UI.Xaml.Controls.CaptureElement? captureElement;
    private readonly int previewWidth;
    private readonly int previewHeight;
    private int converting;
    private bool disposed;
    private int eventCount;
    private int emptyCount;
    private int convertedCount;
    private volatile string? lastError;

    private CameraSession(
        MediaCapture capture, MediaFrameReader? reader, string formatInfo, string supportedFormats,
        Action<Bitmap, bool> onFrame,
        Windows.UI.Xaml.Controls.CaptureElement? captureElement = null, int previewWidth = 0, int previewHeight = 0)
    {
        this.capture = capture;
        this.reader = reader;
        FormatInfo = formatInfo;
        SupportedFormats = supportedFormats;
        this.onFrame = onFrame;
        this.captureElement = captureElement;
        this.previewWidth = previewWidth;
        this.previewHeight = previewHeight;
    }

    /// <summary>Format the camera reports it is using, for example "NV12 1920x1080@30".</summary>
    public string FormatInfo { get; }

    /// <summary>Streams and formats the camera offers, for the log.</summary>
    public string SupportedFormats { get; }

    /// <summary>True when the video is shown by the XAML preview element instead of delivered as frames.</summary>
    public bool IsXamlPreview => captureElement is not null;

    /// <summary>Counters that show where frames get lost, for the log.</summary>
    public string Diagnostics =>
        $"gambar diterima={eventCount}, kosong={emptyCount}, terkonversi={convertedCount}, galat terakhir={lastError ?? "-"}";

    public static string Describe(CaptureMode mode) => mode switch
    {
        CaptureMode.XamlPreview => "pratinjau XAML, seperti aplikasi Camera",
        CaptureMode.SharedPreview => "bagi pakai, aliran pratinjau",
        CaptureMode.SharedRecord => "bagi pakai, aliran rekam",
        CaptureMode.ExclusivePreview => "kendali penuh, aliran pratinjau",
        CaptureMode.SharedAnyColor => "bagi pakai, aliran berwarna pertama",
        _ => mode.ToString(),
    };

    /// <param name="onFrame">Receives a new bitmap (the receiver owns it) and whether it is all black.</param>
    /// <param name="element">The preview element; only used by <see cref="CaptureMode.XamlPreview"/>.</param>
    /// <exception cref="NotSupportedException">This mode does not apply to this camera.</exception>
    public static async Task<CameraSession> StartAsync(
        MediaFrameSourceGroup group, CaptureMode mode, Action<Bitmap, bool> onFrame,
        Windows.UI.Xaml.Controls.CaptureElement? element)
    {
        var capture = new MediaCapture();
        try
        {
            if (mode == CaptureMode.XamlPreview)
            {
                return await StartXamlPreviewAsync(capture, group, element, onFrame);
            }

            var exclusive = mode == CaptureMode.ExclusivePreview;
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            // A camera can expose several colour streams (preview, record, photo). Only the preview
            // and record streams deliver frames on their own, so pick the stream on purpose.
            var colorSources = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .ToList();
            var sources = string.Join("; ", capture.FrameSources.Values
                .Select(s => $"{s.Info.MediaStreamType}/{s.Info.SourceKind}/{s.SupportedFormats.Count} format"));
            var wanted = mode == CaptureMode.SharedRecord ? MediaStreamType.VideoRecord : MediaStreamType.VideoPreview;
            var source = (mode == CaptureMode.SharedAnyColor
                    ? colorSources.FirstOrDefault()
                    : colorSources.FirstOrDefault(s => s.Info.MediaStreamType == wanted))
                ?? throw new NotSupportedException(
                    $"Kamera tidak punya aliran {(mode == CaptureMode.SharedAnyColor ? "berwarna" : wanted.ToString())}. " +
                    $"Aliran yang ada: {sources}.");

            var formats = string.Join(", ", source.SupportedFormats
                .Where(f => f.VideoFormat is not null)
                .Select(f => $"{f.Subtype} {f.VideoFormat!.Width}x{f.VideoFormat.Height}@{FramesPerSecond(f):0}")
                .Distinct()
                .Take(MaxListedFormats));
            var supported = $"aliran yang ada: {sources}; dipakai: {source.Info.MediaStreamType}; format: {formats}";

            var format = source.CurrentFormat;
            var formatInfo = format?.VideoFormat is null
                ? "format tidak diketahui"
                : $"{format.Subtype} {format.VideoFormat.Width}x{format.VideoFormat.Height}@{FramesPerSecond(format):0}";

            var frameReader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            frameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            var session = new CameraSession(capture, frameReader, formatInfo, supported, onFrame);
            frameReader.FrameArrived += session.OnFrameArrived;

            var status = await frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                session.Dispose();
                throw new InvalidOperationException($"Windows menolak memulai kamera ({status}).");
            }
            return session;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static async Task<CameraSession> StartXamlPreviewAsync(
        MediaCapture capture, MediaFrameSourceGroup group,
        Windows.UI.Xaml.Controls.CaptureElement? element, Action<Bitmap, bool> onFrame)
    {
        if (element is null)
        {
            throw new NotSupportedException("Elemen pratinjau XAML belum siap.");
        }
        var device = group.SourceInfos
            .FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color)?.DeviceInformation
            ?? throw new NotSupportedException("Kamera ini tidak punya ID perangkat untuk pratinjau XAML.");

        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            element.Source = capture;
            await capture.StartPreviewAsync();
        }
        catch
        {
            element.Source = null;
            throw;
        }

        var properties = capture.VideoDeviceController
            .GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
        var width = properties is { Width: > 0 } ? (int)properties.Width : DefaultPreviewWidth;
        var height = properties is { Height: > 0 } ? (int)properties.Height : DefaultPreviewHeight;
        var info = properties is null
            ? "pratinjau XAML"
            : $"{properties.Subtype} {width}x{height}, pratinjau XAML";

        var session = new CameraSession(capture, null, info, "", onFrame, element, width, height);
        _ = session.ProbeAsync();
        return session;
    }

    private static double FramesPerSecond(MediaFrameFormat format) =>
        format.FrameRate is { Denominator: > 0 } rate ? (double)rate.Numerator / rate.Denominator : 0;

    /// <summary>
    /// Takes one picture from the running XAML preview. Returns null when Windows hands back no bitmap.
    /// </summary>
    public async Task<GrabbedFrame?> GrabPreviewFrameAsync()
    {
        using var target = new VideoFrame(BitmapPixelFormat.Bgra8, previewWidth, previewHeight);
        using var result = await capture.GetPreviewFrameAsync(target);
        Interlocked.Increment(ref eventCount);

        var software = result.SoftwareBitmap;
        if (software is null)
        {
            Interlocked.Increment(ref emptyCount);
            return null;
        }
        var bitmap = ToBitmap(software, out var black);
        Interlocked.Increment(ref convertedCount);
        return new GrabbedFrame(bitmap, black);
    }

    // The video is drawn by the XAML element, so this one test picture only shows in the log whether
    // Windows can also hand pictures to this app, and lets the UI warn when they are black.
    private async Task ProbeAsync()
    {
        try
        {
            await Task.Delay(1500);
            if (disposed)
            {
                return;
            }
            if (await GrabPreviewFrameAsync() is { } grabbed)
            {
                onFrame(grabbed.Bitmap, grabbed.Black);
            }
        }
        catch (Exception ex)
        {
            // Nothing to propagate to: this runs in the background and only feeds the diagnostics.
            lastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
            }
            if (captureElement is not null)
            {
                await capture.StopPreviewAsync();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The camera was already gone (unplugged or taken by another app); disposing below is enough.
        }
        try
        {
            if (captureElement is not null)
            {
                captureElement.Source = null;
            }
        }
        catch (COMException)
        {
            // The XAML element is already torn down.
        }
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
        }
        capture.Dispose();
    }

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        Interlocked.Increment(ref eventCount);

        // Drop frames while the previous one is still being converted.
        if (Interlocked.Exchange(ref converting, 1) == 1)
        {
            return;
        }
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video is null)
            {
                Interlocked.Increment(ref emptyCount);
                return;
            }

            var software = video.SoftwareBitmap;
            SoftwareBitmap? copied = null;
            if (software is null && video.Direct3DSurface is { } surface)
            {
                copied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
                software = copied;
            }
            if (software is null)
            {
                Interlocked.Increment(ref emptyCount);
                return;
            }

            Bitmap bitmap;
            bool black;
            try
            {
                bitmap = ToBitmap(software, out black);
            }
            finally
            {
                copied?.Dispose();
            }
            Interlocked.Increment(ref convertedCount);
            onFrame(bitmap, black);
        }
        catch (Exception ex)
        {
            // This handler runs on a thread-pool thread and cannot pass the error on, so keep the
            // reason for the diagnostics instead of losing it.
            lastError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref converting, 0);
        }
    }

    private static Bitmap ToBitmap(SoftwareBitmap source, out bool black)
    {
        using var converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var bytes = new byte[width * height * 4];
        converted.CopyToBuffer(bytes.AsBuffer());

        long sum = 0;
        var samples = 0;
        for (var i = 0; i + 2 < bytes.Length; i += 64)
        {
            sum += bytes[i] + bytes[i + 1] + bytes[i + 2];
            samples += 3;
        }
        black = samples == 0 || sum / (double)samples < BlackMeanThreshold;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bytes, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
