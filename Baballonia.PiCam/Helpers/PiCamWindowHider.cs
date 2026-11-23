using System.Runtime.InteropServices;

namespace Baballonia.PiCam;

// Helper to remove eye camera feed windows that PiCamEmbedded exposes from the taskbar ("PCLE Left" / "PCLE Right")
internal class PiCamWindowHider : IDisposable
{
    #region COM Types & Imports
    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    internal class TaskbarListClass {}

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 1;

    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    #endregion
    private static readonly string[] WindowTitles = [ "PCLE Left", "PCLE Right" ];

    private const int PollMs = 250;
    private const int RefreshMs = 5000;

    private readonly uint ProcessID = (uint)Environment.ProcessId;
    private Thread? Thread;
    private readonly ManualResetEventSlim StopEvent = new(false);
    private ITaskbarList? Taskbar;

    public PiCamWindowHider()
    {
        if (Thread != null) return;
        Thread = new Thread(Run)
        {
            IsBackground = true,
            Name = nameof(PiCamWindowHider)
        };
        Thread.SetApartmentState(ApartmentState.STA);
        Thread.Start();
    }

    private void Run()
    {
        try
        {
            Taskbar = (ITaskbarList)new TaskbarListClass();
            Taskbar?.HrInit();
        }
        catch
        {
            return;
        }

        var trackedWindowHandles = new HashSet<IntPtr>();
        var lastRefresh = Environment.TickCount64;

        try
        {
            while(true)
            {
                trackedWindowHandles.RemoveWhere(h => !IsWindow(h));
                if(StopEvent.Wait(PollMs)) break;

                foreach(var title in WindowTitles)
                {
                    var windowHandle = FindWindow(null, title);
                    if(windowHandle == IntPtr.Zero) continue;

                    // Skip other processes and windows already being tracked
                    var result = GetWindowThreadProcessId(windowHandle, out var pid);
                    if(result == 0 || pid != ProcessID || !trackedWindowHandles.Add(windowHandle)) continue;

                    // Hide window from taskbar
                    Hide(windowHandle);
                }

                // Refresh tracked windows periodically in case the shell adds window back to the taskbar
                if(Environment.TickCount64 - lastRefresh >= RefreshMs)
                {
                    lastRefresh = Environment.TickCount64;
                    foreach(var windowHandle in trackedWindowHandles) Hide(windowHandle);
                }
            }
        }
        finally
        {
            if(Taskbar != null)
            {
                Marshal.FinalReleaseComObject(Taskbar);
                Taskbar = null;
            }
        }
    }

    private void Hide(IntPtr windowHandle)
    {
        try
        {
            Taskbar?.DeleteTab(windowHandle);
        }
        catch
        {
            // Window may have just been destroyed
        }
    }

    public void Dispose() => StopEvent.Set();

    public static void SetWindowVisibility(PiCamEye eye, bool visible)
    {
        var hWnd = FindWindow(null, WindowTitles[(int)eye]);
        if(hWnd != IntPtr.Zero && IsWindowVisible(hWnd) != visible) ShowWindowAsync(hWnd, visible ? SW_SHOW : SW_HIDE);
    }
}