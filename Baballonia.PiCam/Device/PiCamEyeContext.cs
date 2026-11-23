using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Baballonia.PiCam;

internal class PiCamEyeContext : IPiCamEyeContext
{
    public PiCamEyeContext(PiCamDevice device, PiCamEye eye, ILogger logger)
    {
        Eye = eye;
        _Device = device;
        Logger = logger;
    }

    private readonly PiCamDevice _Device;
    public IPiCamDevice Device => _Device;

    public PiCamEye Eye { get; private set; }
    public ILogger Logger { get; private set; }

    public Task Destroy() => _Device.DestroyContext(this);

    public Mat? AcquireEyeFrame() => _Device?.AcquireEyeFrame(Eye);
}