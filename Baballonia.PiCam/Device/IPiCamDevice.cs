using Microsoft.Extensions.Logging;

namespace Baballonia.PiCam;

public interface IPiCamDevice
{
    Task<IPiCamEyeContext?> CreateContext(PiCamEye eye, ILogger logger, CancellationToken cancellationToken);
    Task DestroyContext(IPiCamEyeContext eyeContext);

    Task ChangeSettings(PiCamSettings settings);
}