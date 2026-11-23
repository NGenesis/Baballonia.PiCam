using Baballonia.SDK;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Baballonia.PiCam;

public sealed class PiCamCapture : Capture
{
    public PiCamEye Eye { get; private set; }
    private IPiCamEyeContext? EyeContext;
    private readonly PiCamSettings Settings;
    private CancellationTokenSource? StopCaptureCts;
    private Task<bool>? CreateContextTask;
    private Task? UpdateFrameTask;

    public PiCamCapture(string source, ILogger<PiCamCapture> logger) : base(source, logger)
    {
        (Eye, Settings) = PiCamSettings.FromAddress(source);
    }

    public override async Task<bool> StartCapture()
    {
        if(!PiCamDeviceMonitor.IsDevicePresent())
        {
            Logger?.LogError("Failed to acquire eye state for {Eye}, device is not connected", Eye);
            return false;
        }

        await PiCamDevice.Instance.ChangeSettings(Settings);

        // Tracker can take longer to start up than Baballonia allows before stopping the capture so force blank eye frame material immediately
        IsReady = true;
        SetRawMat(new Mat(2, 2, MatType.CV_8UC4, new Scalar(0, 0, 0, 1)));
        
        StopCaptureCts = new CancellationTokenSource();
        var stopCaptureToken = StopCaptureCts.Token;

        try
        {
            CreateContextTask = Task.Run(() => CreateEyeContext(stopCaptureToken), stopCaptureToken);
            if(!await CreateContextTask)
            {
                Logger?.LogError("Failed to acquire eye state for {Eye}", Eye);
                await DestroyEyeContext();
                IsReady = false;
                return false;
            }
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "Failed to acquire eye state for {Eye} due to exception {Message}", Eye, e.Message);
            await DestroyEyeContext();
            IsReady = false;
            return false;
        }

        if(stopCaptureToken.IsCancellationRequested || EyeContext == null)
        {
            await DestroyEyeContext();
            IsReady = false;
            return false;
        }

        UpdateFrameTask = Task.Run(() => UpdateFrame(stopCaptureToken), stopCaptureToken).ContinueWith(_ => {}, TaskScheduler.Default);
        return true;
    }

    private async Task<bool> CreateEyeContext(CancellationToken cancellationToken)
    {
        var eyeContext = await PiCamDevice.Instance.CreateContext(Eye, Logger, cancellationToken);

        if(cancellationToken.IsCancellationRequested || eyeContext == null)
        {
            if(eyeContext != null) eyeContext?.Destroy();
            return false;
        }

        EyeContext = eyeContext;
        return true;
    }

    private async Task UpdateFrame(CancellationToken cancellationToken)
    {
        while(!cancellationToken.IsCancellationRequested)
        {
            var mat = EyeContext?.AcquireEyeFrame();
            if(mat == null)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                continue;
            }

            SetRawMat(mat);
        }
    }

    public override async Task<bool> StopCapture()
    {
        StopCaptureCts?.Cancel();

        if(CreateContextTask != null)
        {
            await CreateContextTask;
            CreateContextTask = null;
        }

        if(UpdateFrameTask != null)
        {
            await UpdateFrameTask;
            UpdateFrameTask = null;
        }

        await DestroyEyeContext();
        IsReady = false;

        return true;
    }

    private async Task DestroyEyeContext()
    {
        if(EyeContext != null)
        {
            await EyeContext.Destroy();
            EyeContext = null;
        }
    }

    public override void Dispose()
    {
        StopCaptureCts?.Dispose();
        base.Dispose();
    }
}