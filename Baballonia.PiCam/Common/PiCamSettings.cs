namespace Baballonia.PiCam;

public readonly record struct PiCamSettings(bool HighRes, int Framerate, int Exposure)
{
    public static PiCamSettings Default => new(false, 100, 100);

    // Parse settings URI (picam://left, picam://right, picam://xxxx?fps=100&exposure=100&highres=0)
    public static (PiCamEye eye, PiCamSettings settings) FromAddress(string address)
    {
        var s = address.Trim();
        var q = s.IndexOf('?');
        var query = q >= 0 ? s[q..] : null;

        if(q >= 0) s = s[..q];

        var eye = s.EndsWith("right", StringComparison.OrdinalIgnoreCase) ? PiCamEye.Right : PiCamEye.Left;

        return (eye, FromQuery(query));
    }

    // Parses options from the address query, e.g. picam://left?highres=0&fps=100&exposure=100
    public static PiCamSettings FromQuery(string? query)
    {
        var s = Default;

        if(string.IsNullOrEmpty(query)) return s;

        foreach(var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if(kv.Length != 2) continue;

            switch(kv[0].ToLowerInvariant())
            {
                case "highres":
                {
                    s = s with { HighRes = kv[1] is "1" or "true" };
                    break;
                }

                case "fps":
                {
                    if(int.TryParse(kv[1], out var f)) s = s with { Framerate = Math.Clamp(f, 1, 100) };
                    break;
                }

                case "exposure":
                {
                    if(int.TryParse(kv[1], out var e)) s = s with { Exposure = Math.Clamp(e, 50, 130) };
                    break;
                }
            }
        }

        return s;
    }
}