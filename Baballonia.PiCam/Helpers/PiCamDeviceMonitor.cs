using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Baballonia.PiCam;

// Fires DevicePresenceChanged when device connects or disconnects
internal sealed class PiCamDeviceMonitor : IDisposable
{
    private readonly string DeviceName;
    private readonly ILogger? Logger;
    private readonly ManualResetEventSlim Signal = new(false);
    private readonly CancellationTokenSource Cts = new();
    private CmNotifyCallback? Callback;
    private IntPtr Notification;
    private Thread? Thread;
    private int Present = 1;

    public event Action<bool>? DevicePresenceChanged;
    public bool IsPresent => Volatile.Read(ref Present) != 0;

    public PiCamDeviceMonitor(string? deviceName = null, ILogger? logger = null)
    {
        DeviceName = deviceName ?? TrackerDeviceName;
        Logger = logger;
    }

    public const string TrackerDeviceName = "Droolon Pi1";

    public static bool IsDevicePresent(string? deviceName = null)
    {
        deviceName ??= TrackerDeviceName;
        var names = EnumerateUsbDeviceNames();
        return names.Any(candidate => Normalize(candidate).Contains(Normalize(deviceName), StringComparison.OrdinalIgnoreCase));
    }

    public bool Start()
    {
        var names = EnumerateUsbDeviceNames();
        Present = names.Any(Matches) ? 1 : 0;
        RegisterNotification();
        Thread = new Thread(() => Loop(Cts.Token))
        {
            IsBackground = true,
            Name = nameof(PiCamDeviceMonitor)
        };
        Thread.Start();
        Logger?.LogInformation("Watching USB device '{Name}' for disconnects", DeviceName);
        return Present == 1;
    }

    public void Dispose()
    {
        Cts.Cancel();
        if(Notification != IntPtr.Zero)
        {
            CM_Unregister_Notification(Notification); // waits for in-flight callbacks; never call from within one
            Notification = IntPtr.Zero;
        }
        Thread?.Join(TimeSpan.FromSeconds(2));
        Cts.Dispose();
        Signal.Dispose();
    }

    private bool Matches(string candidate) => Normalize(candidate).Contains(Normalize(DeviceName), StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private void Loop(CancellationToken ct)
    {
        // With notifications the poll is only a safety net; without them it is the detection mechanism.
        var pollMs = Notification != IntPtr.Zero ? 10_000 : 2_000;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                Signal.Wait(pollMs, ct);

                // Keep waiting until no new event arrives for 500 ms.
                while(Signal.IsSet)
                {
                    Signal.Reset();
                    if(ct.WaitHandle.WaitOne(500)) return;
                }

                var now = EnumerateUsbDeviceNames().Any(Matches);
                var was = Interlocked.Exchange(ref Present, now ? 1 : 0) != 0;
                if(now == was) continue;

                Logger?.LogInformation("USB device '{Name}' {State}", DeviceName, now ? "connected" : "disconnected");
                try
                {
                    DevicePresenceChanged?.Invoke(now);
                }
                catch(Exception e)
                {
                    Logger?.LogError(e, "DevicePresenceChanged handler failed");
                }
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception e)
        {
            Logger?.LogError(e, "PiCam device monitor stopped unexpectedly");
        }
    }

    #region Device change notification
    private static readonly Guid GuidDevInterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private delegate uint CmNotifyCallback(IntPtr notify, IntPtr context, int action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Register_Notification(IntPtr filter, IntPtr context, CmNotifyCallback callback, out IntPtr notifyContext);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr notifyContext);

    private void RegisterNotification()
    {
        // CM_NOTIFY_FILTER: DWORD cbSize, Flags, FilterType, Reserved, then a union whose largest member is WCHAR InstanceId[200] (400 bytes), for FilterType DEVICEINTERFACE (0) it starts with the class GUID
        const int size = 16 + 400;
        var p = Marshal.AllocHGlobal(size);
        try
        {
            for(var i = 0; i < size; ++i) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, size); // cbSize; Flags = 0, FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0
            Marshal.Copy(GuidDevInterfaceUsbDevice.ToByteArray(), 0, p + 16, 16);

            Callback = OnNotify;
            var cr = CM_Register_Notification(p, IntPtr.Zero, Callback, out Notification);
            if(cr != 0)
            {
                Notification = IntPtr.Zero;
                Logger?.LogWarning("CM_Register_Notification failed (CONFIGRET 0x{Cr:X}); polling for the device every 2 s instead", cr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    // Arbitrary threadpool thread: just wake the monitor thread.
    private uint OnNotify(IntPtr notify, IntPtr context, int action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            Signal.Set();
        }
        catch(ObjectDisposedException)
        {
        }

        return 0;
    }

    #endregion
    #region Presence check
    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_FRIENDLYNAME = 0xC;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA data, uint property, out uint propertyRegDataType, byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private static List<string> EnumerateUsbDeviceNames()
    {
        var names = new List<string>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if(set == InvalidHandle || set == IntPtr.Zero) return names;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for(uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); ++i)
            {
                var desc = GetStringProperty(set, ref data, SPDRP_DEVICEDESC);
                if(!string.IsNullOrEmpty(desc)) names.Add(desc);

                var friendly = GetStringProperty(set, ref data, SPDRP_FRIENDLYNAME);
                if(!string.IsNullOrEmpty(friendly)) names.Add(friendly);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return names;
    }

    private static string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[1024];
        if(!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var required)) return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).TrimEnd('\0');
    }
    #endregion
}
