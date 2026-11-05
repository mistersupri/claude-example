using System.Runtime.InteropServices;
using Windows.UI.Xaml.Hosting;
using WinRT;

namespace WebcamTest;

/// <summary>
/// Shows a UWP <c>CaptureElement</c>, the control the Windows Camera app uses for its preview,
/// inside a Windows Forms window through XAML Islands. The island is created on first use so
/// a failure there cannot stop the app from starting.
/// </summary>
internal sealed unsafe class XamlCaptureHost : Control
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    // IID of IDesktopWindowXamlSourceNative (windows.ui.xaml.hosting.desktopwindowxamlsourcenative.h).
    private static readonly Guid DesktopWindowXamlSourceNativeIid = new("3cbcf1bf-2f76-4e9c-96ab-e84b37972554");

    private WindowsXamlManager? manager;
    private DesktopWindowXamlSource? source;
    private IntPtr islandWindow;

    /// <summary>The preview element, or null until <see cref="EnsureCreated"/> has succeeded.</summary>
    public Windows.UI.Xaml.Controls.CaptureElement? Element { get; private set; }

    /// <summary>Creates the XAML island on this window. Must run on the UI thread.</summary>
    public void EnsureCreated()
    {
        if (Element is not null)
        {
            return;
        }

        _ = Handle; // Forces the window handle the island attaches to.

        manager = WindowsXamlManager.InitializeForCurrentThread();
        source = new DesktopWindowXamlSource();
        AttachIsland(source);

        var element = new Windows.UI.Xaml.Controls.CaptureElement
        {
            Stretch = Windows.UI.Xaml.Media.Stretch.Uniform,
        };
        source.Content = element;
        Element = element;
        ResizeIsland();
    }

    private void AttachIsland(DesktopWindowXamlSource xamlSource)
    {
        // The projected class wraps a native object; ask that native object for the interop interface.
        var native = ((IWinRTObject)xamlSource).NativeObject.ThisPtr;
        var iid = DesktopWindowXamlSourceNativeIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, ref iid, out var interop));
        try
        {
            var vtable = *(void***)interop;

            // IUnknown occupies slots 0 to 2; then AttachToWindow, then get_WindowHandle.
            var attachToWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
            Marshal.ThrowExceptionForHR(attachToWindow(interop, Handle));

            var getWindowHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtable[4];
            IntPtr window;
            Marshal.ThrowExceptionForHR(getWindowHandle(interop, &window));
            islandWindow = window;
        }
        finally
        {
            Marshal.Release(interop);
        }
        GC.KeepAlive(xamlSource);
    }

    private void ResizeIsland()
    {
        if (islandWindow != IntPtr.Zero)
        {
            SetWindowPos(islandWindow, IntPtr.Zero, 0, 0, Width, Height, SwpNoZOrder | SwpShowWindow);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeIsland();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source?.Dispose();
            manager?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
