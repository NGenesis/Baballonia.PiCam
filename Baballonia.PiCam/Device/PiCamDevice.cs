using Microsoft.Extensions.Logging;
using OpenCvSharp;
using PiCamEmbedded;
using System.Diagnostics;

namespace Baballonia.PiCam;

internal class PiCamDevice : IPiCamDevice
{
    public static IPiCamDevice Instance { get; } = new PiCamDevice();
    public PiCamSettings Settings { get; private set; }

    public Task ChangeSettings(PiCamSettings settings)
    {
        lock(DeviceStateLock)
        {
            Settings = settings;
            PiCamNative.SaveSettings(Settings);
        }

        return Task.CompletedTask;
    }

    public Task<IPiCamEyeContext?> CreateContext(PiCamEye eye, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading {eye} eye...", eye);
        lock(DeviceStateLock)
        {
            var eyeContext = new PiCamEyeContext(this, eye, logger);
            EyeContexts.Add(eyeContext);

            if(!StartDevice(logger))
            {
                EyeContexts.Remove(eyeContext);
                return Task.FromResult<IPiCamEyeContext?>(null);
            }

            return Task.FromResult<IPiCamEyeContext?>(eyeContext);
        }
    }

    public Task DestroyContext(IPiCamEyeContext eyeContext)
    {
        if(eyeContext == null) return Task.CompletedTask;
        Logger?.LogInformation("Unloading {eye} eye...", eyeContext.Eye);

        lock(DeviceStateLock)
        {
            if(!EyeContexts.Remove(eyeContext)) return Task.CompletedTask;
            if(EyeContexts.Count == 0) StopDevice();
        }

        return Task.CompletedTask;
    }

    private volatile bool Started;
    private readonly Lock DeviceStateLock = new();
    private readonly HashSet<IPiCamEyeContext> EyeContexts = new();
    private readonly Mat?[] EyeFrameMaterials = new Mat?[2];
    private readonly Lock EyeFrameMaterialLock = new();
    private volatile int FrameWidth, FrameHeight;
    private PiCamWindowHider? WindowHider;
    private ILogger? Logger;

    private const int LeftEyeIndex = (int)PiCamEye.Left;
    private const int RightEyeIndex = (int)PiCamEye.Right;

    private readonly Dictionary<int, (int Width, int Height)> AvailableResolutionsBySize = new()
    {
        { 320 * 240 * 4, (320, 240) },
        { 640 * 480 * 4, (640, 480) }
    };

    #region Device connection monitoring
    private PiCamDeviceMonitor? DeviceMonitor;
    private volatile bool TrackerConnected = true;

    private void OnDevicePresenceChanged(bool present)
    {
        TrackerConnected = present;
        
        lock(DeviceStateLock)
        {
            if(present)
            {
                Logger?.LogInformation("Device connected, attempting to start tracker...");

                if(!LoadNative())
                {
                    Logger?.LogError("Failed to load library during restart");
                    return;
                }

                Logger?.LogInformation("Tracker started");
            }
            else
            {
                Logger?.LogInformation("Device disconnected, stopping tracker...");
                UnloadNative();
            }
        }
    }
    #endregion
    private bool StartDevice(ILogger logger)
    {
        lock(DeviceStateLock)
        {
            if(Started) return true;

            Logger = logger;
            Logger?.LogInformation("Starting tracker");

            WindowHider ??= new PiCamWindowHider();

            if(!LoadNative())
            {
                Logger?.LogError("Failed to start");
                return false;
            }

            Started = true;

            DeviceMonitor ??= new PiCamDeviceMonitor(null, Logger);
            DeviceMonitor.DevicePresenceChanged += OnDevicePresenceChanged;
            TrackerConnected = DeviceMonitor.Start();

            return true;
        }
    }

    private void StopDevice()
    {
        lock(DeviceStateLock)
        {
            if(!Started) return;

            Logger?.LogInformation("Stopping tracker");

            DeviceMonitor?.Dispose();
            DeviceMonitor = null;

            WindowHider?.Dispose();
            WindowHider = null;

            UnloadNative();

            lock(EyeFrameMaterialLock)
            {
                foreach(var m in EyeFrameMaterials) m?.Dispose();
                Array.Clear(EyeFrameMaterials);
            }

            Logger = null;
            Started = false;
        }
    }

    private bool LoadNative()
    {
        try
        {
            PiCamNative.SaveSettings(Settings);

            if(!PiCamNative.Load())
            {
                Logger?.LogError("Failed to load library");
                return false;
            }

            var result = InvokeNativeCommand("Start", PiCamNative.Start);
            if(result != PiCamNativeResult.Success)
            {
                Logger?.LogError("Start failed: {Result}", result);
                PiCamNative.Unload();
                return false;
            }

            WaitNotBusy();
            UpdateResolution();

            WaitNotBusy();
            result = PiCamNative.RegisterFrameCallback(OnEyeFrame);
            if(result != PiCamNativeResult.Success)
            {
                Logger?.LogError("RegisterFrameCallback failed: {Result}", result);

                WaitNotBusy();
                result = InvokeNativeCommand("Stop", PiCamNative.Stop);
                if(result != PiCamNativeResult.Success) Logger?.LogError("Stop failed: {Result}", result);

                PiCamNative.Unload();

                return false;
            }

            // Show camera capture windows
            PiCamWindowHider.SetWindowVisibility(PiCamEye.Left, true);
            PiCamWindowHider.SetWindowVisibility(PiCamEye.Right, true);
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "Failed to start due to exception - {message}", $"{e.Message} {e.StackTrace} {e.InnerException?.Message} {e.InnerException?.StackTrace}");
            return false;
        }

        return true;
    }

    private void UnloadNative()
    {
        try
        {
            // Hide camera capture windows
            PiCamWindowHider.SetWindowVisibility(PiCamEye.Left, false);
            PiCamWindowHider.SetWindowVisibility(PiCamEye.Right, false);

            if(PiCamNative.IsLoaded)
            {
                WaitNotBusy();
                var result = InvokeNativeCommand("Stop", PiCamNative.Stop);
                if(result != PiCamNativeResult.Success) Logger?.LogError("Stop failed: {Result}", result);

                WaitNotBusy();
                PiCamNative.RegisterFrameCallback(null);
            }
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "Stop failed due to exception {Message}", e.Message);
        }
        finally
        {
            PiCamNative.Unload();
        }
    }

    private void WaitNotBusy()
    {
        if(!PiCamNative.IsLoaded) return;

        var sw = Stopwatch.StartNew();
        while(PiCamNative.IsBusy() && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
    }

    private PiCamNativeResult InvokeNativeCommand(string name, Func<PiCamNativeResult> call)
    {
        if(!PiCamNative.IsLoaded)
        {
            Logger?.LogError("{Operation} failed, the native library is not loaded", name);
            return PiCamNativeResult.Error;
        }

        try
        {
            return call();
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "{Operation} failed due to exception {Message}", name, e.Message);
            return PiCamNativeResult.Error;
        }
    }

    private void UpdateResolution()
    {
        FrameWidth = PiCamNative.GetCurrentVideoWidth();
        FrameHeight = PiCamNative.GetCurrentVideoHeight();
    }

    // Callback providing eye frame material buffers, buffers are overwritten on next frame so copy immediately
    private void OnEyeFrame(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if(!Started || !TrackerConnected) return;

        try
        {
            var w = FrameWidth;
            var h = FrameHeight;

            if(left.Length != w * h * 4)
            {
                // Cached resolution may be out of date
                if(!AvailableResolutionsBySize.TryGetValue(left.Length, out var availableResolution)) return;
                w = availableResolution.Width;
                h = availableResolution.Height;
            }

            if(w <= 0 || h <= 0) return;

            // Create current eye frame materials
            var leftEyeMat = CreateEyeFrameMaterial(PiCamEye.Left, left, w, h);
            var rightEyeMat = CreateEyeFrameMaterial(PiCamEye.Right, right, w, h);

            lock(EyeFrameMaterialLock)
            {
                // Swap eye frame materials
                (leftEyeMat, EyeFrameMaterials[LeftEyeIndex]) = (EyeFrameMaterials[LeftEyeIndex], leftEyeMat);
                (rightEyeMat, EyeFrameMaterials[RightEyeIndex]) = (EyeFrameMaterials[RightEyeIndex], rightEyeMat);
            }

            // Release previous eye frame materials
            leftEyeMat?.Dispose();
            rightEyeMat?.Dispose();
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "Frame callback failed due to exception {Message}", e.Message);
        }
    }

    private unsafe Mat? CreateEyeFrameMaterial(PiCamEye eye, ReadOnlySpan<byte> src, int w, int h)
    {
        fixed(byte* srcBuffer = src)
        {
            using var tmp = Mat.FromPixelData(h, w, MatType.CV_8UC4, (IntPtr)srcBuffer);
            using var bgr = tmp.CvtColor(ColorConversionCodes.BGRA2BGR);

            // Crop grey borders
            using var cropped = new Mat(bgr, new Rect(1, 1, w - 2, h - 2));

            // Flip vertically
            var adjusted = cropped.Flip(FlipMode.X);

            return adjusted;
        }
    }

    // Take ownership of the latest eye frame material
    public Mat? AcquireEyeFrame(PiCamEye eye)
    {
        var eyeIndex = (int)eye;
        lock(EyeFrameMaterialLock)
        {
            var m = EyeFrameMaterials[eyeIndex];
            EyeFrameMaterials[eyeIndex] = null;
            return m;
        }
    }
}