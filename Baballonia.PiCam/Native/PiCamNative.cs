using Baballonia.PiCam;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PiCamEmbedded;

public static unsafe class PiCamNative
{
    public const string Lib = "PiCamEmbedded";

    // May take up to 10 seconds to return
    private static delegate* unmanaged[Cdecl]<PiCamNativeResult> _Start;
    public static PiCamNativeResult Start() => _Start();

    // May take up to 10 seconds to return
    // When a frozen camera feed is detected, instruct the user to replug the Droolon before calling, or listen for USB connection events before restarting
    private static delegate* unmanaged[Cdecl]<PiCamNativeResult> _Restart;
    public static PiCamNativeResult Restart() => _Restart();

    // Returns almost instantly
    private static delegate* unmanaged[Cdecl]<PiCamNativeResult> _Stop;
    public static PiCamNativeResult Stop() => _Stop();

    // Returns true if the device is currently busy, otherwise false when a Start/Restart/Stop command can be issued
    private static delegate* unmanaged[Cdecl]<byte> _IsBusy;
    public static bool IsBusy() => _IsBusy() != 0;

    // BGRA buffers containing a monochrome image with unused alpha channel
    private static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte*, int, void>, PiCamNativeResult> _RegisterFrameCallback;
    private static FrameCallback? _FrameCallback;
    private static bool _IsNativeFrameCallbackRegistered;

    // Register callback to send eye frame buffers as they are acquired
    // Frame buffers should be copied before the callback completes for further processing as they are overwritten on the next frame capture
    public delegate void FrameCallback(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);

    public static PiCamNativeResult RegisterFrameCallback(FrameCallback? callback)
    {
        if(!_IsNativeFrameCallbackRegistered)
        {
            var result = _RegisterFrameCallback(&OnNativeFrameCallback);
            if(result != PiCamNativeResult.Success) return result;

            _IsNativeFrameCallbackRegistered = true;
        }

        _FrameCallback = callback;
        return PiCamNativeResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeFrameCallback(byte* left, byte* right, int size) => _FrameCallback?.Invoke(new(left, size), new(right, size));

    // Requests the input framerate and returns the actual framerate it's capable of getting for you, no restart needed
    private static delegate* unmanaged[Cdecl]<int, PiCamNativeResult> _SetFramerate;
    public static PiCamNativeResult SetFramerate(int framerate) => _SetFramerate(framerate);

    // Waits for busy state to clear then changes exposure to percentage (clipped to safe range automatically)
    // Restart is called automatically after and returns its result
    private static delegate* unmanaged[Cdecl]<int, PiCamNativeResult> _SetExposure;
    public static PiCamNativeResult SetExposure(int exposurePercent) => _SetExposure(exposurePercent);

    // Waits for busy state to clear then changes resolution (true: 640x480, false: 320x240)
    private static delegate* unmanaged[Cdecl]<byte, PiCamNativeResult> _SetResolution;
    public static PiCamNativeResult SetResolution(bool highResolutionWanted) => _SetResolution(highResolutionWanted ? (byte)1 : (byte)0);

    // Returns the current exposure setting
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentExposure;
    public static int GetCurrentExposure() => _GetCurrentExposure();

    // Currently hardcoded 50 (%), in future returns the minimum possible exposure for the current video mode
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentMinExposure;
    public static int GetCurrentMinExposure() => _GetCurrentMinExposure();

    // Returns the minimum exposure for a specific video mode
    private static delegate* unmanaged[Cdecl]<byte, int> _GetMinExposure;
    public static int GetMinExposure(bool highResolutionWanted) => _GetMinExposure(highResolutionWanted ? (byte)1 : (byte)0);

    // Currently hardcoded 130 (%), in the future returns the maximum possible exposure for the current video mode
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentMaxExposure;
    public static int GetCurrentMaxExposure() => _GetCurrentMaxExposure();

    // Returns the maximum exposure for a specific video mode
    private static delegate* unmanaged[Cdecl]<byte, int> _GetMaxExposure;
    public static int GetMaxExposure(bool highResolutionWanted) => _GetMaxExposure(highResolutionWanted ? (byte)1 : (byte)0);

    // Returns the current framerate that is being used
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentFramerate;
    public static int GetCurrentFramerate() => _GetCurrentFramerate();

    // Returns the current maximum possible framerate
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentMaxFramerate;
    public static int GetCurrentMaxFramerate() => _GetCurrentMaxFramerate();

    // Returns the maximum possible framerate for a specific video mode
    private static delegate* unmanaged[Cdecl]<byte, int> _GetMaxFramerate;
    public static int GetMaxFramerate(bool highResolutionWanted) => _GetMaxFramerate(highResolutionWanted ? (byte)1 : (byte)0);

    // Returns the current video width
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentVideoWidth;
    public static int GetCurrentVideoWidth() => _GetCurrentVideoWidth();

    // Returns the video width for a specific video mode
    private static delegate* unmanaged[Cdecl]<byte, int> _GetVideoWidth;
    public static int GetVideoWidth(bool highResolutionWanted) => _GetVideoWidth(highResolutionWanted ? (byte)1 : (byte)0);

    // Returns the current video height
    private static delegate* unmanaged[Cdecl]<int> _GetCurrentVideoHeight;
    public static int GetCurrentVideoHeight() => _GetCurrentVideoHeight();

    // Returns the video height for a specific video mode
    private static delegate* unmanaged[Cdecl]<byte, int> _GetVideoHeight;
    public static int GetVideoHeight(bool highResolutionWanted) => _GetVideoHeight(highResolutionWanted ? (byte)1 : (byte)0);

    #region Library loading
    private static readonly string NativeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private static IntPtr PiCamEmbeddedHandle;

    public static bool IsLoaded => PiCamEmbeddedHandle != IntPtr.Zero;

    public static bool Load()
    {
        if(PiCamEmbeddedHandle != IntPtr.Zero) return true;

        var path = Path.Combine(NativeDir, $"{Lib}.dll");

        if(!NativeLibrary.TryLoad(path, out var handle)) return false;

        try
        {
            _Start = (delegate* unmanaged[Cdecl]<PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_Start");
            _Restart = (delegate* unmanaged[Cdecl]<PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_Restart");
            _Stop = (delegate* unmanaged[Cdecl]<PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_Stop");
            _IsBusy = (delegate* unmanaged[Cdecl]<byte>)NativeLibrary.GetExport(handle, "PCLE_CheckBusy");
            _RegisterFrameCallback = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, byte*, int, void>, PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_RegisterFrameCallback");
            _SetFramerate = (delegate* unmanaged[Cdecl]<int, PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_SetFramerate");
            _SetExposure = (delegate* unmanaged[Cdecl]<int, PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_SetExposure");
            _SetResolution = (delegate* unmanaged[Cdecl]<byte, PiCamNativeResult>)NativeLibrary.GetExport(handle, "PCLE_SetResolution");
            _GetCurrentExposure = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurExposure");
            _GetCurrentMinExposure = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurMinExposure");
            _GetMinExposure = (delegate* unmanaged[Cdecl]<byte, int>)NativeLibrary.GetExport(handle, "PCLE_GetMinExposure");
            _GetCurrentMaxExposure = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurMaxExposure");
            _GetMaxExposure = (delegate* unmanaged[Cdecl]<byte, int>)NativeLibrary.GetExport(handle, "PCLE_GetMaxExposure");
            _GetCurrentFramerate = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurFramerate");
            _GetCurrentMaxFramerate = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurMaxFramerate");
            _GetMaxFramerate = (delegate* unmanaged[Cdecl]<byte, int>)NativeLibrary.GetExport(handle, "PCLE_GetMaxFramerate");
            _GetCurrentVideoWidth = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurVideoWidth");
            _GetVideoWidth = (delegate* unmanaged[Cdecl]<byte, int>)NativeLibrary.GetExport(handle, "PCLE_GetVideoWidth");
            _GetCurrentVideoHeight = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "PCLE_GetCurVideoHeight");
            _GetVideoHeight = (delegate* unmanaged[Cdecl]<byte, int>)NativeLibrary.GetExport(handle, "PCLE_GetVideoHeight");

            PiCamEmbeddedHandle = handle;
            return true;
        }
        catch
        {
            ClearFunctionPointers();

            NativeLibrary.Free(handle);
            PiCamEmbeddedHandle = IntPtr.Zero;
            return false;
        }
    }

    private static void ClearFunctionPointers()
    {
        _Start = null;
        _Restart = null;
        _Stop = null;
        _IsBusy = null;
        _RegisterFrameCallback = null;
        _SetFramerate = null;
        _SetExposure = null;
        _SetResolution = null;
        _GetCurrentExposure = null;
        _GetCurrentMinExposure = null;
        _GetMinExposure = null;
        _GetCurrentMaxExposure = null;
        _GetMaxExposure = null;
        _GetCurrentFramerate = null;
        _GetCurrentMaxFramerate = null;
        _GetMaxFramerate = null;
        _GetCurrentVideoWidth = null;
        _GetVideoWidth = null;
        _GetCurrentVideoHeight = null;
        _GetVideoHeight = null;
    }

    public static void Unload()
    {
        _FrameCallback = null;
        _IsNativeFrameCallbackRegistered = false;

        if(PiCamEmbeddedHandle != IntPtr.Zero)
        {
            ClearFunctionPointers();

            NativeLibrary.Free(PiCamEmbeddedHandle);
            PiCamEmbeddedHandle = IntPtr.Zero;
        }
    }
    #endregion
    #region Configuration
    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "WritePrivateProfileStringW")]
    private static extern bool WritePrivateProfileStringW(string section, string key, string value, string file);

    public static void SaveSettings(PiCamSettings settings)
    {
        var ini = Path.Combine(NativeDir, "PiCamEmbedded", "config.ini");
        if(!File.Exists(ini)) return;

        WritePrivateProfileStringW("EMBEDDED", "highres", settings.HighRes ? "1" : "0", ini);
        WritePrivateProfileStringW("EMBEDDED", "framerate", settings.Framerate.ToString(), ini);
        WritePrivateProfileStringW("EMBEDDED", "exposure", settings.Exposure.ToString(), ini);
    }
    #endregion
}
