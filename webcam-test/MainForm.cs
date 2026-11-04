using System.Diagnostics;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Size = System.Drawing.Size;

namespace WebcamTest;

internal sealed class MainForm : Form
{
    private enum CameraState { Idle, Opening, Running, Error }

    private const int CameraSlots = 4;
    private const int MaxEmptyReads = 50;
    private const int WarmupFrames = 60;
    private const double BlackThreshold = 3.0;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    // DirectShow first: Media Foundation can block forever while opening on some drivers.
    private static readonly (VideoCaptureAPIs Api, string Name)[] Backends =
    {
        (VideoCaptureAPIs.DSHOW, "DirectShow"),
        (VideoCaptureAPIs.MSMF, "Media Foundation"),
    };

    private enum Outcome { Ok, NotOpened, NoFrames, Black, Timeout, Cancelled }

    private readonly ComboBox cameraList = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 140,
        Margin = new Padding(0, 6, 12, 0),
        AccessibleName = "Pilih kamera",
    };

    private readonly Button toggleButton = CreateButton("&Nyalakan kamera");
    private readonly Button captureButton = CreateButton("&Ambil foto");
    private readonly Button openFolderButton = CreateButton("Buka &folder foto");

    private readonly PictureBox preview = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Black,
    };

    private readonly ToolStripStatusLabel statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private readonly string photoFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WebcamTest");

    private readonly Font overlayFont = new("Segoe UI", 12f);
    private readonly object frameLock = new();
    private Mat? lastFrame;
    private CancellationTokenSource? captureCts;
    private Task? captureLoop;
    private CameraState state = CameraState.Idle;
    private string overlayText = "";
    private string? lastSavedPath;

    public MainForm()
    {
        Text = "Webcam Test (draft)";
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        cameraList.Items.Add("Otomatis (cari sendiri)");
        for (var i = 0; i < CameraSlots; i++)
        {
            cameraList.Items.Add($"Kamera {i + 1}");
        }
        cameraList.SelectedIndex = 0;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 10),
            WrapContents = true,
        };
        toolbar.Controls.Add(new Label
        {
            Text = "Kamera:",
            AutoSize = true,
            Margin = new Padding(0, 10, 6, 0),
        });
        toolbar.Controls.Add(cameraList);
        toolbar.Controls.Add(toggleButton);
        toolbar.Controls.Add(captureButton);
        toolbar.Controls.Add(openFolderButton);

        var statusStrip = new StatusStrip { SizingGrip = true };
        statusStrip.Items.Add(statusLabel);

        Controls.Add(preview);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        AcceptButton = captureButton;

        toggleButton.Click += async (_, _) => await ToggleCameraAsync();
        captureButton.Click += (_, _) => CapturePhoto();
        openFolderButton.Click += (_, _) => OpenPhotoFolder();
        preview.Paint += DrawOverlay;
        preview.Resize += (_, _) => preview.Invalidate();

        SetState(CameraState.Idle);
    }

    private static Button CreateButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 34),
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(0, 0, 8, 0),
    };

    private void SetState(CameraState newState, string? message = null)
    {
        state = newState;
        var running = newState == CameraState.Running;
        var busy = newState is CameraState.Opening or CameraState.Running;

        cameraList.Enabled = !busy;
        toggleButton.Text = busy ? "&Matikan kamera" : "&Nyalakan kamera";
        captureButton.Enabled = running;

        overlayText = newState switch
        {
            CameraState.Idle => "Kamera belum menyala.\nPilih kamera, lalu tekan \"Nyalakan kamera\".",
            CameraState.Opening => "Mencari kamera yang menghasilkan gambar...",
            CameraState.Error => message ?? "Kamera tidak bisa dipakai.",
            _ => "",
        };

        statusLabel.Text = newState switch
        {
            CameraState.Idle => $"Foto disimpan ke {photoFolder}",
            CameraState.Opening => "Menunggu kamera. Tekan \"Matikan kamera\" untuk membatalkan.",
            CameraState.Running => $"Kamera aktif ({message}). Tekan Enter atau \"Ambil foto\" untuk menyimpan gambar.",
            CameraState.Error => "Terjadi masalah dengan kamera. Coba kamera lain atau tutup aplikasi yang sedang memakainya.",
            _ => "",
        };

        if (!running)
        {
            ClearPreview();
        }
        preview.Invalidate();
    }

    private async Task ToggleCameraAsync()
    {
        if (state is CameraState.Running or CameraState.Opening)
        {
            await StopCameraAsync();
            SetState(CameraState.Idle);
            return;
        }

        await StopCameraAsync();
        var index = cameraList.SelectedIndex - 1; // -1 = automatic
        SetState(CameraState.Opening);

        captureCts = new CancellationTokenSource();
        var token = captureCts.Token;
        captureLoop = Task.Run(() => RunCapture(index, token), token);
    }

    private async Task StopCameraAsync()
    {
        captureCts?.Cancel();
        if (captureLoop is not null)
        {
            try
            {
                await captureLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        captureCts?.Dispose();
        captureCts = null;
        captureLoop = null;

        lock (frameLock)
        {
            lastFrame?.Dispose();
            lastFrame = null;
        }
    }

    // Runs on a worker thread; every UI change goes through PostToUi.
    private void RunCapture(int selected, CancellationToken token)
    {
        var (capture, cameraIndex, backendName, log) = FindWorkingCamera(selected, token);
        using var ownedCapture = capture;
        if (capture is null)
        {
            if (!token.IsCancellationRequested)
            {
                var message = "Tidak ada kamera yang menghasilkan gambar.\n\n" +
                    string.Join("\n", log) +
                    "\n\nCek penutup lensa atau tombol kamera di keyboard, dan izin kamera di\n" +
                    "Settings > Privacy & security > Camera. Tutup juga aplikasi lain yang memakai kamera.";
                PostToUi(() => SetState(CameraState.Error, message));
            }
            return;
        }

        var label = $"Kamera {cameraIndex + 1}, {backendName}";
        using var frame = new Mat();
        var emptyReads = 0;

        while (!token.IsCancellationRequested)
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                if (++emptyReads >= MaxEmptyReads)
                {
                    PostToUi(() => SetState(CameraState.Error,
                        $"{label} berhenti mengirim gambar.\nCoba nyalakan ulang atau pilih kamera lain."));
                    return;
                }
                Thread.Sleep(20);
                continue;
            }

            emptyReads = 0;
            lock (frameLock)
            {
                lastFrame?.Dispose();
                lastFrame = frame.Clone();
            }

            var bitmap = frame.ToBitmap();
            PostToUi(() =>
            {
                if (token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }
                if (state == CameraState.Opening)
                {
                    SetState(CameraState.Running, label);
                }
                var old = preview.Image;
                preview.Image = bitmap;
                old?.Dispose();
            });
        }
    }

    // Tries every camera index (or only the chosen one) with every backend, and keeps the first
    // one that delivers a non-black frame. Each attempt has a time limit so a driver that hangs
    // cannot freeze the search; a backend that timed out once is skipped from then on.
    private (VideoCapture? Capture, int Index, string Backend, List<string> Log) FindWorkingCamera(
        int selected, CancellationToken token)
    {
        var indices = selected < 0 ? Enumerable.Range(0, CameraSlots).ToArray() : new[] { selected };
        var log = new List<string>();
        var timedOut = new HashSet<VideoCaptureAPIs>();

        foreach (var index in indices)
        {
            foreach (var (api, name) in Backends)
            {
                if (token.IsCancellationRequested)
                {
                    return (null, index, "", log);
                }
                if (timedOut.Contains(api))
                {
                    log.Add($"Kamera {index + 1} / {name}: dilewati karena sebelumnya tidak merespons");
                    continue;
                }

                PostToUi(() =>
                {
                    if (state == CameraState.Opening)
                    {
                        statusLabel.Text = $"Mencoba Kamera {index + 1} lewat {name} (maks. {AttemptTimeout.TotalSeconds:0} detik)...";
                    }
                });

                var (outcome, capture) = TryOpen(index, api, token);
                log.Add($"Kamera {index + 1} / {name}: {Describe(outcome)}");
                if (outcome == Outcome.Ok)
                {
                    return (capture, index, name, log);
                }
                if (outcome == Outcome.Timeout)
                {
                    timedOut.Add(api);
                }
            }
        }
        return (null, -1, "", log);
    }

    private static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Ok => "berhasil",
        Outcome.NotOpened => "tidak bisa dibuka",
        Outcome.NoFrames => "terbuka tetapi tidak mengirim gambar",
        Outcome.Black => "hanya gambar hitam",
        Outcome.Timeout => "tidak merespons",
        _ => "dibatalkan",
    };

    private static (Outcome, VideoCapture?) TryOpen(int index, VideoCaptureAPIs api, CancellationToken token)
    {
        var attempt = Task.Run(() => OpenAndWarmUp(index, api, token));
        try
        {
            if (attempt.Wait(AttemptTimeout, token))
            {
                return attempt.Result;
            }
        }
        catch (OperationCanceledException)
        {
            // Falls through to the cleanup below.
        }

        // The native call is still blocked and cannot be aborted. Release the camera whenever it returns.
        attempt.ContinueWith(
            t => t.Result.Item2?.Dispose(),
            TaskContinuationOptions.OnlyOnRanToCompletion);
        return (token.IsCancellationRequested ? Outcome.Cancelled : Outcome.Timeout, null);
    }

    // Cameras often send a few black frames while the sensor warms up, hence WarmupFrames reads.
    private static (Outcome, VideoCapture?) OpenAndWarmUp(int index, VideoCaptureAPIs api, CancellationToken token)
    {
        var capture = new VideoCapture(index, api);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            return (Outcome.NotOpened, null);
        }

        using var frame = new Mat();
        var gotFrame = false;
        for (var i = 0; i < WarmupFrames; i++)
        {
            if (token.IsCancellationRequested)
            {
                capture.Dispose();
                return (Outcome.Cancelled, null);
            }
            if (!capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(20);
                continue;
            }
            gotFrame = true;
            if (!IsBlack(frame))
            {
                return (Outcome.Ok, capture);
            }
        }

        capture.Dispose();
        return (gotFrame ? Outcome.Black : Outcome.NoFrames, null);
    }

    private static bool IsBlack(Mat frame)
    {
        var mean = Cv2.Mean(frame);
        return mean.Val0 + mean.Val1 + mean.Val2 < BlackThreshold;
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window closed between the check and the call.
        }
    }

    private void CapturePhoto()
    {
        Mat? snapshot;
        lock (frameLock)
        {
            snapshot = lastFrame?.Clone();
        }

        if (snapshot is null)
        {
            statusLabel.Text = "Belum ada gambar dari kamera. Tunggu sebentar lalu coba lagi.";
            return;
        }

        using (snapshot)
        {
            try
            {
                Directory.CreateDirectory(photoFolder);
                var path = Path.Combine(photoFolder, $"foto-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
                // ImEncode + File.WriteAllBytes instead of ImWrite: ImWrite fails on non-ASCII Windows paths.
                Cv2.ImEncode(".jpg", snapshot, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 95));
                File.WriteAllBytes(path, bytes);
                lastSavedPath = path;
                statusLabel.Text = $"Foto tersimpan: {path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
                statusLabel.Text = $"Gagal menyimpan foto: {ex.Message}";
            }
        }
    }

    private void OpenPhotoFolder()
    {
        try
        {
            Directory.CreateDirectory(photoFolder);
            var args = lastSavedPath is not null && File.Exists(lastSavedPath)
                ? $"/select,\"{lastSavedPath}\""
                : $"\"{photoFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            statusLabel.Text = $"Folder foto tidak bisa dibuka: {ex.Message}";
        }
    }

    private void ClearPreview()
    {
        var old = preview.Image;
        preview.Image = null;
        old?.Dispose();
    }

    private void DrawOverlay(object? sender, PaintEventArgs e)
    {
        if (preview.Image is not null || string.IsNullOrEmpty(overlayText))
        {
            return;
        }
        TextRenderer.DrawText(
            e.Graphics,
            overlayText,
            overlayFont,
            preview.ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        captureCts?.Cancel();
        try
        {
            captureLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        lock (frameLock)
        {
            lastFrame?.Dispose();
            lastFrame = null;
        }
        ClearPreview();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            overlayFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
