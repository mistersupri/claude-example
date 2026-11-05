using System.Diagnostics;
using System.Drawing.Imaging;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Windows.Media.Capture.Frames;

namespace WebcamTest;

internal sealed class MainForm : Form
{
    private enum CameraState { Idle, Opening, Running, Error }

    private sealed record CameraChoice(MediaFrameSourceGroup Group, bool HasColor)
    {
        public override string ToString() =>
            HasColor ? Group.DisplayName : $"{Group.DisplayName} (inframerah)";
    }

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BlackHintDelay = TimeSpan.FromSeconds(6);
    private const int MaxLogLines = 60;

    // Tried in this order when a camera opens but sends no pictures.
    private static readonly CaptureMode[] Modes = Enum.GetValues<CaptureMode>();

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly ComboBox cameraList = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300,
        Margin = new Padding(0, 6, 12, 0),
        AccessibleName = "Pilih kamera",
    };

    private readonly Button toggleButton = CreateButton("&Nyalakan kamera");
    private readonly Button captureButton = CreateButton("&Ambil foto");
    private readonly Button openFolderButton = CreateButton("Buka &folder foto");
    private readonly Button infoButton = CreateButton("&Info perangkat");

    private readonly PictureBox preview = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Black,
    };

    // The Windows Camera app's preview control, hosted through XAML Islands. Hidden until a camera uses it.
    private readonly XamlCaptureHost xamlHost = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.Black,
        Visible = false,
    };

    private readonly ToolStripStatusLabel statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private readonly System.Windows.Forms.Timer watchdog = new() { Interval = 1000 };
    private readonly string photoFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WebcamTest");
    private readonly Font overlayFont = new("Segoe UI", 12f);
    private readonly List<string> sessionLog = new();

    private CameraSession? session;
    private CameraChoice? currentChoice;
    private int currentMode;
    private CancellationTokenSource? startCts;
    private CameraState state = CameraState.Idle;
    private string overlayText = "";
    private string activeCameraName = "";
    private string? lastSavedPath;
    private bool cameraListLoaded;
    private bool watchdogBusy;
    private bool blackHintShown;
    private DateTime sessionStartedAt;
    private DateTime? firstFrameAt;
    private DateTime? lastNonBlackAt;
    private int uiPending;

    public MainForm()
    {
        Text = "Webcam Test (draft)";
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

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
        toolbar.Controls.Add(infoButton);

        var statusStrip = new StatusStrip { SizingGrip = true };
        statusStrip.Items.Add(statusLabel);

        Controls.Add(preview);
        Controls.Add(xamlHost);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        AcceptButton = captureButton;

        toggleButton.Click += async (_, _) => await ToggleCameraAsync();
        captureButton.Click += async (_, _) => await CapturePhotoAsync();
        openFolderButton.Click += (_, _) => OpenPhotoFolder();
        infoButton.Click += async (_, _) => await ShowDeviceInfoAsync();
        watchdog.Tick += async (_, _) => await WatchdogTickAsync();
        preview.Paint += DrawOverlay;
        preview.Resize += (_, _) => preview.Invalidate();
        Shown += async (_, _) => await LoadCamerasAsync();

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

    private void Log(string line)
    {
        sessionLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (sessionLog.Count > MaxLogLines)
        {
            sessionLog.RemoveAt(0);
        }
    }

    private async Task LoadCamerasAsync()
    {
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var choices = groups
                .Select(g => new CameraChoice(g, g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color)))
                .OrderByDescending(c => c.HasColor)
                .ToList();

            cameraList.Items.Clear();
            foreach (var choice in choices)
            {
                cameraList.Items.Add(choice);
            }
            if (choices.Count > 0)
            {
                cameraList.SelectedIndex = 0;
            }
            Log($"Ditemukan {choices.Count} kamera");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log($"Gagal membaca daftar kamera: {ex.Message}");
        }

        cameraListLoaded = true;
        if (state == CameraState.Idle)
        {
            SetState(CameraState.Idle);
        }
    }

    private void SetState(CameraState newState, string? message = null)
    {
        state = newState;
        var running = newState == CameraState.Running;
        var busy = newState is CameraState.Opening or CameraState.Running;
        var hasCameras = cameraList.Items.Count > 0;

        cameraList.Enabled = !busy && hasCameras;
        toggleButton.Enabled = busy || hasCameras;
        toggleButton.Text = busy ? "&Matikan kamera" : "&Nyalakan kamera";
        captureButton.Enabled = running;

        var showXaml = running && session is { IsXamlPreview: true };
        xamlHost.Visible = showXaml;
        preview.Visible = !showXaml;

        if (running)
        {
            activeCameraName = message ?? activeCameraName;
        }

        overlayText = newState switch
        {
            CameraState.Idle when !cameraListLoaded => "Mencari daftar kamera...",
            CameraState.Idle when !hasCameras =>
                "Windows tidak menemukan kamera.\nCek sambungan kamera dan izin kamera di\nSettings > Privacy & security > Camera.",
            CameraState.Idle => "Kamera belum menyala.\nPilih kamera, lalu tekan \"Nyalakan kamera\".",
            CameraState.Opening => "Membuka kamera...",
            CameraState.Error => message ?? "Kamera tidak bisa dipakai.",
            _ => "",
        };

        statusLabel.Text = newState switch
        {
            CameraState.Idle => $"Foto disimpan ke {photoFolder}",
            CameraState.Opening => "Menunggu kamera. Tekan \"Matikan kamera\" untuk membatalkan.",
            CameraState.Running => RunningStatus(),
            CameraState.Error => "Terjadi masalah dengan kamera. Baca pesan di layar, atau tekan Info perangkat.",
            _ => "",
        };

        if (busy)
        {
            watchdog.Start();
        }
        else
        {
            watchdog.Stop();
        }

        if (!running)
        {
            ClearPreview();
        }
        preview.Invalidate();
    }

    private string RunningStatus() =>
        $"Kamera aktif ({activeCameraName}). Tekan Enter atau \"Ambil foto\" untuk menyimpan gambar.";

    private async Task ToggleCameraAsync()
    {
        if (state is CameraState.Running or CameraState.Opening)
        {
            await StopCameraAsync();
            SetState(CameraState.Idle);
            return;
        }

        await StopCameraAsync();
        currentChoice = cameraList.SelectedItem as CameraChoice;
        await StartCameraAsync(0);
    }

    private async Task StartCameraAsync(int modeIndex)
    {
        if (currentChoice is not { } choice)
        {
            return;
        }
        currentMode = modeIndex;
        var mode = Modes[modeIndex];

        SetState(CameraState.Opening);
        firstFrameAt = null;
        lastNonBlackAt = null;
        blackHintShown = false;
        activeCameraName = choice.ToString();

        var cts = new CancellationTokenSource();
        startCts = cts;
        var token = cts.Token;

        Windows.UI.Xaml.Controls.CaptureElement? element = null;
        if (mode == CaptureMode.XamlPreview)
        {
            try
            {
                xamlHost.EnsureCreated();
                element = xamlHost.Element;
            }
            catch (Exception ex)
            {
                // XAML Islands touches several Windows components and fails with different exception
                // types; whatever the reason, this mode is unusable and the next one is tried.
                await FailOrRetryAsync(
                    modeIndex,
                    $"XAML Islands gagal dimulai: {ex.GetType().Name}: {ex.Message}",
                    $"Pratinjau XAML gagal dimulai.\n\n{ex.Message}");
                return;
            }
        }

        Log($"Membuka {choice} ({CameraSession.Describe(mode)})");
        var start = CameraSession.StartAsync(choice.Group, mode, OnFrame, element);
        try
        {
            var finished = await Task.WhenAny(start, Task.Delay(OpenTimeout, token));
            if (finished != start)
            {
                ReleaseWhenStartFinishes(start);
                if (!token.IsCancellationRequested)
                {
                    await FailOrRetryAsync(
                        modeIndex,
                        $"{choice}: tidak merespons dalam {OpenTimeout.TotalSeconds:0} detik",
                        $"{choice} tidak merespons saat dibuka.\n\n" +
                        "Coba restart layanan Windows Camera Frame Server (services.msc),\n" +
                        "tutup aplikasi lain yang memakai kamera, atau pilih kamera lain.");
                }
                return;
            }

            var started = await start;
            if (token.IsCancellationRequested || startCts != cts)
            {
                started.Dispose();
                return;
            }

            session = started;
            sessionStartedAt = DateTime.UtcNow;
            Log($"{choice}: terbuka ({started.FormatInfo}), menunggu gambar");
            if (started.IsXamlPreview)
            {
                SetState(CameraState.Running, activeCameraName);
            }
            if (started.SupportedFormats.Length > 0)
            {
                Log($"Kamera: {started.SupportedFormats}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            Log($"{choice}: akses kamera ditolak");
            SetState(CameraState.Error,
                "Windows menolak akses kamera untuk aplikasi ini.\n\n" +
                "Buka Settings > Privacy & security > Camera, lalu nyalakan\n" +
                "\"Camera access\" dan \"Let desktop apps access your camera\".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                await FailOrRetryAsync(
                    modeIndex,
                    $"{choice} ({CameraSession.Describe(mode)}): gagal, {ex.Message}",
                    $"{choice} tidak bisa dibuka.\n\n{ex.Message}");
            }
        }
    }

    // Tries the next way of reading the camera, or shows the error when none is left.
    private async Task FailOrRetryAsync(int modeIndex, string logLine, string errorMessage)
    {
        Log(logLine);
        if (state == CameraState.Opening && modeIndex + 1 < Modes.Length)
        {
            await StartCameraAsync(modeIndex + 1);
            return;
        }
        SetState(CameraState.Error, errorMessage);
    }

    // A start that was abandoned (timeout or cancel) may still finish later; close it then.
    private static void ReleaseWhenStartFinishes(Task<CameraSession> start) =>
        start.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);

    private async Task StopCameraAsync()
    {
        startCts?.Cancel();
        startCts = null;

        var current = session;
        session = null;
        if (current is not null)
        {
            await current.StopAsync();
        }
        ClearPreview();
    }

    // Runs on a thread-pool thread; the bitmap now belongs to this method.
    private void OnFrame(Bitmap bitmap, bool black)
    {
        // Skip frames while the UI has not yet shown the previous one.
        if (Interlocked.Exchange(ref uiPending, 1) == 1)
        {
            bitmap.Dispose();
            return;
        }

        PostToUi(() =>
        {
            Interlocked.Exchange(ref uiPending, 0);
            if (state is not (CameraState.Opening or CameraState.Running))
            {
                bitmap.Dispose();
                return;
            }

            var now = DateTime.UtcNow;
            firstFrameAt ??= now;
            if (!black)
            {
                lastNonBlackAt = now;
                if (blackHintShown)
                {
                    blackHintShown = false;
                    statusLabel.Text = RunningStatus();
                }
            }
            if (state == CameraState.Opening)
            {
                Log($"{activeCameraName}: gambar pertama diterima{(black ? " (hitam)" : "")}");
                SetState(CameraState.Running, activeCameraName);
            }

            var old = preview.Image;
            preview.Image = bitmap;
            old?.Dispose();
        });
    }

    private async Task WatchdogTickAsync()
    {
        if (watchdogBusy)
        {
            return;
        }
        watchdogBusy = true;
        try
        {
            var now = DateTime.UtcNow;
            if (state == CameraState.Opening && session is not null && now - sessionStartedAt > FirstFrameTimeout)
            {
                var diagnostics = session.Diagnostics;
                Log($"{activeCameraName}: tidak ada gambar dalam {FirstFrameTimeout.TotalSeconds:0} detik " +
                    $"({CameraSession.Describe(Modes[currentMode])}) [{diagnostics}]");
                await StopCameraAsync();
                if (state != CameraState.Opening)
                {
                    return;
                }
                if (currentMode + 1 < Modes.Length)
                {
                    await StartCameraAsync(currentMode + 1);
                    return;
                }
                SetState(CameraState.Error,
                    $"{activeCameraName} terbuka tetapi tidak mengirim gambar,\n" +
                    $"walau sudah dicoba dengan {Modes.Length} cara membaca kamera.\n\n" +
                    $"Rincian: {diagnostics}\n\n" +
                    "Coba restart layanan Windows Camera Frame Server (services.msc),\n" +
                    "atau matikan efek kamera (Settings > Bluetooth & devices > Cameras).");
            }
            else if (state == CameraState.Running && !blackHintShown && firstFrameAt is { } first)
            {
                var lastGood = lastNonBlackAt ?? first;
                if (now - lastGood > BlackHintDelay)
                {
                    blackHintShown = true;
                    Log($"{activeCameraName}: gambar masih hitam");
                    statusLabel.Text =
                        "Gambar masih hitam. Cek penutup lensa, matikan efek kamera (Windows Studio Effects), atau pilih kamera lain.";
                }
            }
        }
        finally
        {
            watchdogBusy = false;
        }
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

    private async Task CapturePhotoAsync()
    {
        Bitmap? grabbed = null;
        try
        {
            if (session is { IsXamlPreview: true } xaml)
            {
                grabbed = (await xaml.GrabPreviewFrameAsync())?.Bitmap;
            }

            var current = grabbed ?? preview.Image as Bitmap;
            if (current is null)
            {
                statusLabel.Text = "Belum ada gambar dari kamera. Tunggu sebentar lalu coba lagi.";
                return;
            }
            SavePhoto(current);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            statusLabel.Text = $"Gagal mengambil gambar dari kamera: {ex.Message}";
        }
        finally
        {
            grabbed?.Dispose();
        }
    }

    private void SavePhoto(Bitmap current)
    {
        try
        {
            Directory.CreateDirectory(photoFolder);
            var path = Path.Combine(photoFolder, $"foto-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            using var copy = new Bitmap(current);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
            copy.Save(path, JpegCodec, parameters);
            lastSavedPath = path;
            statusLabel.Text = $"Foto tersimpan: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            statusLabel.Text = $"Gagal menyimpan foto: {ex.Message}";
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

    private async Task ShowDeviceInfoAsync()
    {
        infoButton.Enabled = false;
        var previousStatus = statusLabel.Text;
        statusLabel.Text = "Membaca daftar perangkat...";

        var cameras = cameraList.Items.Count == 0
            ? "(tidak ada)"
            : string.Join("\n", cameraList.Items.Cast<CameraChoice>().Select(c =>
                $"- {c.Group.DisplayName}: aliran {string.Join(", ", c.Group.SourceInfos.Select(i => i.SourceKind))}"));
        var log = sessionLog.Count == 0 ? "(belum ada)" : string.Join("\n", sessionLog);

        try
        {
            var text = await Task.Run(() => BuildDeviceInfo(cameras, log));
            ShowTextDialog("Info perangkat", text);
        }
        finally
        {
            infoButton.Enabled = true;
            statusLabel.Text = previousStatus;
        }
    }

    private static string BuildDeviceInfo(string cameras, string log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine();
        sb.AppendLine("Kamera menurut Windows MediaCapture (dipakai aplikasi ini):");
        sb.AppendLine(cameras);
        sb.AppendLine();
        sb.AppendLine("Perangkat kamera di Device Manager:");
        AppendQuery(sb,
            "SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'",
            o => $"- {o["Name"]} [{o["PNPClass"]}, status: {o["Status"]}]");
        sb.AppendLine();
        sb.AppendLine("Layanan kamera Windows:");
        AppendQuery(sb,
            "SELECT Name, State, StartMode FROM Win32_Service WHERE Name = 'FrameServer' OR Name = 'FrameServerMonitor'",
            o => $"- {o["Name"]}: {o["State"]}, start {o["StartMode"]}");
        sb.AppendLine();
        AppendCameraPrivacy(sb);
        sb.AppendLine();
        sb.AppendLine("GPU:");
        AppendQuery(sb,
            "SELECT Name, DriverVersion FROM Win32_VideoController",
            o => $"- {o["Name"]} (driver {o["DriverVersion"]})");
        sb.AppendLine();
        sb.AppendLine("Catatan sesi ini:");
        sb.AppendLine(log);
        return sb.ToString();
    }

    private static void AppendCameraPrivacy(StringBuilder sb)
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        sb.AppendLine("Izin kamera Windows (Allow = diizinkan, Deny = ditolak):");
        sb.AppendLine($"- Aplikasi ini berjalan sebagai administrator: {(elevated ? "ya" : "tidak")}");
        sb.AppendLine($"- Akses kamera, pengguna: {ReadConsent(Registry.CurrentUser, root)}");
        sb.AppendLine($"- Akses kamera, seluruh perangkat: {ReadConsent(Registry.LocalMachine, root)}");
        sb.AppendLine($"- Aplikasi desktop, pengguna: {ReadConsent(Registry.CurrentUser, root + @"\NonPackaged")}");
        sb.AppendLine($"- Aplikasi desktop, seluruh perangkat: {ReadConsent(Registry.LocalMachine, root + @"\NonPackaged")}");
        sb.AppendLine("Aplikasi desktop yang tercatat memakai kamera:");
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(root + @"\NonPackaged");
            var names = apps?.GetSubKeyNames() ?? Array.Empty<string>();
            if (names.Length == 0)
            {
                sb.AppendLine("(tidak ada)");
            }
            foreach (var name in names.Take(30))
            {
                using var app = apps!.OpenSubKey(name);
                var value = app?.GetValue("Value") as string ?? "?";
                var started = app?.GetValue("LastUsedTimeStart") as long? ?? 0;
                var stopped = app?.GetValue("LastUsedTimeStop") as long? ?? 0;
                var last = started == 0 ? "belum pernah" : DateTime.FromFileTime(started).ToString("yyyy-MM-dd HH:mm:ss");
                var inUse = started != 0 && stopped == 0 ? ", masih dipakai" : "";
                sb.AppendLine($"- {name.Replace('#', '\\')}: {value}, terakhir {last}{inUse}");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            sb.AppendLine($"(gagal membaca: {ex.Message})");
        }
    }

    private static string ReadConsent(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue("Value") as string ?? "(tidak diatur)";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "(gagal dibaca)";
        }
    }

    private static void AppendQuery(StringBuilder sb, string query, Func<ManagementBaseObject, string> format)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            var any = false;
            foreach (ManagementBaseObject item in results)
            {
                any = true;
                sb.AppendLine(format(item));
                item.Dispose();
            }
            if (!any)
            {
                sb.AppendLine("(tidak ada)");
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            sb.AppendLine($"(gagal membaca: {ex.Message})");
        }
    }

    private void ShowTextDialog(string title, string text)
    {
        using var mono = new Font(FontFamily.GenericMonospace, 10f);
        using var dialog = new Form
        {
            Text = title,
            ClientSize = new Size(700, 460),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = mono,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            AccessibleName = title,
        };
        var copyButton = CreateButton("&Salin ke clipboard");
        var closeButton = CreateButton("&Tutup");
        closeButton.DialogResult = DialogResult.Cancel;
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                copyButton.Text = "Tersalin";
            }
            catch (ExternalException)
            {
                copyButton.Text = "Gagal menyalin, blok teks lalu Ctrl+C";
            }
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(10),
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(copyButton);
        dialog.Controls.Add(box);
        dialog.Controls.Add(buttons);
        dialog.CancelButton = closeButton;
        dialog.ShowDialog(this);
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
        watchdog.Stop();
        startCts?.Cancel();
        session?.Dispose();
        session = null;
        ClearPreview();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            overlayFont.Dispose();
            watchdog.Dispose();
        }
        base.Dispose(disposing);
    }
}
