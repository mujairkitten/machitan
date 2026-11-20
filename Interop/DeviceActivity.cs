using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>ole32.dll: COM on a worker thread, and making a COM object by class ID.</summary>
internal static unsafe partial class Ole32
{
    internal const uint COINIT_MULTITHREADED = 0x0;

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, nint* instance);
}

/// <summary>
/// Which processes record from a microphone right now, read from Core Audio without opening
/// any device: the sessions on every active capture device (the ones Windows' volume mixer
/// lists). A session in the Active state is a stream that is recording.
/// Raw COM calls through the vtables (audiopolicy.h / mmdeviceapi.h order), released at once.
/// Call on a thread where COM is started (Ole32.CoInitializeEx).
/// </summary>
internal static unsafe class MicActivity
{
    private const int CaptureFlow = 1;
    private const uint DeviceStateActive = 1;
    private const int SessionStateActive = 1;

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IidSessionControl2 = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");

    /// <summary>Process IDs with a recording stream; null when Core Audio cannot be read.</summary>
    internal static List<uint>? RecordingProcesses()
    {
        nint enumerator = 0;
        nint devices = 0;
        try
        {
            Guid classId = ClsidDeviceEnumerator;
            Guid interfaceId = IidDeviceEnumerator;
            if (Ole32.CoCreateInstance(&classId, 0, CoreAudio.ClsctxAll, &interfaceId, &enumerator) < 0 || enumerator == 0)
            {
                return null;
            }

            // IMMDeviceEnumerator::EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &collection)
            if (((delegate* unmanaged<nint, int, uint, nint*, int>)ComCall.Slot(enumerator, 3))(enumerator, CaptureFlow, DeviceStateActive, &devices) < 0 || devices == 0)
            {
                return null;
            }

            uint count = 0;
            if (((delegate* unmanaged<nint, uint*, int>)ComCall.Slot(devices, 3))(devices, &count) < 0)
            {
                return null;
            }

            var processes = new List<uint>();
            for (uint i = 0; i < count; i++)
            {
                nint device = 0;
                if (((delegate* unmanaged<nint, uint, nint*, int>)ComCall.Slot(devices, 4))(devices, i, &device) < 0 || device == 0)
                {
                    continue;
                }

                try
                {
                    AddRecording(device, processes);
                }
                finally
                {
                    ComCall.Release(device);
                }
            }

            return processes;
        }
        finally
        {
            ComCall.Release(devices);
            ComCall.Release(enumerator);
        }
    }

    private static void AddRecording(nint device, List<uint> processes)
    {
        nint manager = 0;
        nint sessions = 0;
        try
        {
            // IMMDevice::Activate(IID_IAudioSessionManager2, CLSCTX_ALL, null, &manager)
            Guid managerId = IidSessionManager2;
            if (((delegate* unmanaged<nint, Guid*, uint, nint, nint*, int>)ComCall.Slot(device, 3))(device, &managerId, CoreAudio.ClsctxAll, 0, &manager) < 0 || manager == 0)
            {
                return;
            }

            // IAudioSessionManager2::GetSessionEnumerator
            if (((delegate* unmanaged<nint, nint*, int>)ComCall.Slot(manager, 5))(manager, &sessions) < 0 || sessions == 0)
            {
                return;
            }

            int count = 0;
            if (((delegate* unmanaged<nint, int*, int>)ComCall.Slot(sessions, 3))(sessions, &count) < 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                nint control = 0;
                nint control2 = 0;
                try
                {
                    if (((delegate* unmanaged<nint, int, nint*, int>)ComCall.Slot(sessions, 4))(sessions, i, &control) < 0 || control == 0)
                    {
                        continue;
                    }

                    // IAudioSessionControl::GetState
                    int state = 0;
                    if (((delegate* unmanaged<nint, int*, int>)ComCall.Slot(control, 3))(control, &state) < 0 || state != SessionStateActive)
                    {
                        continue;
                    }

                    Guid control2Id = IidSessionControl2;
                    if (ComCall.Query(control, &control2Id, &control2) < 0 || control2 == 0)
                    {
                        continue;
                    }

                    // IAudioSessionControl2::GetProcessId
                    uint processId = 0;
                    if (((delegate* unmanaged<nint, uint*, int>)ComCall.Slot(control2, 14))(control2, &processId) >= 0
                        && processId != 0
                        && !processes.Contains(processId))
                    {
                        processes.Add(processId);
                    }
                }
                finally
                {
                    ComCall.Release(control2);
                    ComCall.Release(control);
                }
            }
        }
        finally
        {
            ComCall.Release(sessions);
            ComCall.Release(manager);
        }
    }
}

/// <summary>
/// Which processes stream from a camera right now, from Media Foundation's sensor activity
/// monitor (MFCreateSensorActivityMonitor, Windows 10 1703 and later). Windows calls back on
/// its own thread whenever a camera's users change; Machitan never opens the camera.
/// The callback object is a small hand-made COM object (one vtable, lives for the whole run),
/// so it needs no runtime wrapper. Start and stop it on a thread where COM is started.
/// </summary>
internal sealed unsafe partial class CameraActivity : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const int ENoInterface = unchecked((int)0x80004002);

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IidReportCallback = new("DE5072EE-DBE3-46DC-8A87-B6F631194751");
    private static readonly Guid IidShutdown = new("97EC2EA4-0E42-4937-97AC-9D6D328824E1");

    /// <summary>The one callback object: a pointer to the vtable below. Never freed.</summary>
    private static nint s_callback;

    /// <summary>Where reports go (one monitor at a time).</summary>
    private static CameraActivity? s_owner;

    private readonly object _gate = new();
    private readonly Action _changed;
    private uint[] _streaming = Array.Empty<uint>();
    private nint _monitor;
    private bool _mediaFoundation;

    private CameraActivity(Action changed)
    {
        _changed = changed;
    }

    /// <summary>Process IDs streaming from any camera, as last reported.</summary>
    internal uint[] Streaming
    {
        get
        {
            lock (_gate)
            {
                return _streaming;
            }
        }
    }

    /// <summary>
    /// Starts the monitor; <paramref name="changed"/> runs on Windows' thread after each
    /// report. Null when this Windows has no sensor activity monitor (the reason is in
    /// <paramref name="problem"/>).
    /// </summary>
    internal static CameraActivity? TryStart(Action changed, out string problem)
    {
        problem = string.Empty;
        var watch = new CameraActivity(changed);
        try
        {
            int started = MFStartup(MfVersion, MfStartupLite);
            if (started < 0)
            {
                problem = "Media Foundation did not start (0x" + started.ToString("X8") + ").";
                return null;
            }

            watch._mediaFoundation = true;
            s_owner = watch;
            nint monitor = 0;
            int created = MFCreateSensorActivityMonitor(Callback(), &monitor);
            if (created < 0 || monitor == 0)
            {
                problem = "the camera monitor could not be made (0x" + created.ToString("X8") + ").";
                watch.Dispose();
                return null;
            }

            watch._monitor = monitor;

            // IMFSensorActivityMonitor::Start
            int running = ((delegate* unmanaged<nint, int>)ComCall.Slot(monitor, 3))(monitor);
            if (running < 0)
            {
                problem = "the camera monitor did not start (0x" + running.ToString("X8") + ").";
                watch.Dispose();
                return null;
            }

            return watch;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            problem = "this Windows has no camera monitor (" + ex.GetType().Name + ").";
            watch.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(s_owner, this))
        {
            s_owner = null;
        }

        nint monitor = _monitor;
        _monitor = 0;
        if (monitor != 0)
        {
            // IMFSensorActivityMonitor::Stop, then IMFShutdown::Shutdown, as Windows' sample does.
            ((delegate* unmanaged<nint, int>)ComCall.Slot(monitor, 4))(monitor);
            Guid shutdownId = IidShutdown;
            nint shutdown = 0;
            if (ComCall.Query(monitor, &shutdownId, &shutdown) >= 0 && shutdown != 0)
            {
                ((delegate* unmanaged<nint, int>)ComCall.Slot(shutdown, 3))(shutdown);
                ComCall.Release(shutdown);
            }

            ComCall.Release(monitor);
        }

        if (_mediaFoundation)
        {
            _mediaFoundation = false;
            MFShutdown();
        }
    }

    private void Take(nint report)
    {
        var streaming = new List<uint>();

        // IMFSensorActivitiesReport::GetCount / GetActivityReport
        uint devices = 0;
        if (((delegate* unmanaged<nint, uint*, int>)ComCall.Slot(report, 3))(report, &devices) < 0)
        {
            return;
        }

        for (uint d = 0; d < devices; d++)
        {
            nint activity = 0;
            try
            {
                if (((delegate* unmanaged<nint, uint, nint*, int>)ComCall.Slot(report, 4))(report, d, &activity) < 0 || activity == 0)
                {
                    continue;
                }

                // IMFSensorActivityReport::GetProcessCount / GetProcessActivity
                uint processes = 0;
                if (((delegate* unmanaged<nint, uint*, int>)ComCall.Slot(activity, 5))(activity, &processes) < 0)
                {
                    continue;
                }

                for (uint p = 0; p < processes; p++)
                {
                    nint process = 0;
                    try
                    {
                        if (((delegate* unmanaged<nint, uint, nint*, int>)ComCall.Slot(activity, 6))(activity, p, &process) < 0 || process == 0)
                        {
                            continue;
                        }

                        // IMFSensorProcessActivity::GetProcessId / GetStreamingState
                        uint processId = 0;
                        int isStreaming = 0;
                        if (((delegate* unmanaged<nint, uint*, int>)ComCall.Slot(process, 3))(process, &processId) >= 0
                            && ((delegate* unmanaged<nint, int*, int>)ComCall.Slot(process, 4))(process, &isStreaming) >= 0
                            && isStreaming != 0
                            && processId != 0
                            && !streaming.Contains(processId))
                        {
                            streaming.Add(processId);
                        }
                    }
                    finally
                    {
                        ComCall.Release(process);
                    }
                }
            }
            finally
            {
                ComCall.Release(activity);
            }
        }

        lock (_gate)
        {
            _streaming = streaming.ToArray();
        }

        _changed();
    }

    /// <summary>The callback object, made once: [vtable pointer] -> QueryInterface, AddRef, Release, OnActivitiesReport.</summary>
    private static nint Callback()
    {
        if (s_callback != 0)
        {
            return s_callback;
        }

        nint* vtable = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        vtable[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        vtable[3] = (nint)(delegate* unmanaged<nint, nint, int>)&OnActivitiesReport;

        nint* instance = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        instance[0] = (nint)vtable;
        s_callback = (nint)instance;
        return s_callback;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* interfaceId, nint* result)
    {
        if (*interfaceId == IidUnknown || *interfaceId == IidReportCallback || *interfaceId == IidAgileObject)
        {
            *result = self;
            return 0;
        }

        *result = 0;
        return ENoInterface;
    }

    /// <summary>The object lives for the whole run, so counting references is not needed.</summary>
    [UnmanagedCallersOnly]
    private static uint AddRef(nint self) => 2;

    [UnmanagedCallersOnly]
    private static uint Release(nint self) => 1;

    [UnmanagedCallersOnly]
    private static int OnActivitiesReport(nint self, nint report)
    {
        try
        {
            if (report != 0)
            {
                s_owner?.Take(report);
            }
        }
        catch (Exception)
        {
            // Nothing may escape into Windows' thread; the next report tries again.
        }

        return 0;
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfsensorgroup.dll")]
    private static partial int MFCreateSensorActivityMonitor(nint callback, nint* monitor);
}

/// <summary>
/// Calls into COM objects by vtable slot. Not named "Com": a namespace of that name in the
/// referenced Windows assemblies wins the lookup outside Machitan.Interop.
/// </summary>
internal static unsafe class ComCall
{
    /// <summary>The function in slot <paramref name="index"/> of the object's vtable (0-2 are IUnknown's).</summary>
    internal static nint Slot(nint instance, int index)
    {
        nint** pointer = (nint**)instance;
        nint* vtable = pointer[0];
        return vtable[index];
    }

    internal static int Query(nint instance, Guid* interfaceId, nint* result)
    {
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, interfaceId, result);
    }

    internal static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged<nint, uint>)Slot(instance, 2))(instance);
        }
    }
}
