using System.Runtime.InteropServices;

namespace WebcamTest;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        DisableMediaFoundationHardwareTransforms();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    // Media Foundation hardware transforms make some laptop webcams deliver all-black frames.
    // OpenCV reads this flag through the C runtime's getenv, which does not see
    // Environment.SetEnvironmentVariable, so it is also set with _putenv_s.
    private static void DisableMediaFoundationHardwareTransforms()
    {
        const string name = "OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS";
        Environment.SetEnvironmentVariable(name, "0");
        try
        {
            _putenv_s(name, "0");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("ucrtbase.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int _putenv_s(string name, string value);
}
