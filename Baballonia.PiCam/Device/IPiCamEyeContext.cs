using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Baballonia.PiCam;

public interface IPiCamEyeContext
{
    IPiCamDevice Device { get; }
    PiCamEye Eye { get; }
    ILogger Logger { get; }

    Task Destroy();

    Mat? AcquireEyeFrame();
}