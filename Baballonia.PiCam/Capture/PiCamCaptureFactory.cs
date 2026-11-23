using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.PiCam;

public sealed class PiCamCaptureFactory(ILoggerFactory loggerFactory) : ICaptureFactory
{
    public Capture Create(string address) => new PiCamCapture(address, loggerFactory.CreateLogger<PiCamCapture>());
    public bool CanConnect(string address) => address.StartsWith("picam", StringComparison.OrdinalIgnoreCase);
    public string GetProviderName() => "Droolon Pi1 (PiCam)";
}