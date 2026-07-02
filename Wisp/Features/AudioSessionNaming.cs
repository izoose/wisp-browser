using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp;

/// <summary>
/// Makes Wisp appear as "Wisp" (with its icon) in the Windows Volume Mixer instead of
/// "Microsoft Edge WebView2". Audio sessions are owned by WebView2's child processes, so we
/// enumerate the render device's sessions and rename any owned by our process tree.
/// </summary>
public static class AudioSessionNaming
{
    private static Guid _ctx = Guid.NewGuid();

    public static void Apply(string displayName, string iconPath)
    {
        try
        {
            var ours = OurProcessTree();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device == null) return;

            var iid = IID_IAudioSessionManager2;
            if (device.Activate(ref iid, ClsctxInprocServer, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr) return;
            if (mgr.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return;
            sessions.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var ctl) != 0 || ctl is not IAudioSessionControl2 ctl2) continue;
                if (ctl2.GetProcessId(out int pid) != 0 || !ours.Contains(pid)) continue;
                var g = _ctx;
                try { ctl2.SetDisplayName(displayName, ref g); } catch { }
                try { ctl2.SetIconPath(iconPath, ref g); } catch { }
            }
        }
        catch { /* audio device unavailable, COM quirk — never fatal */ }
    }

    /// <summary>This process plus every descendant (the WebView2 processes that own the audio).</summary>
    private static HashSet<int> OurProcessTree()
    {
        var byParent = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot != IntPtr.Zero && snapshot != InvalidHandle)
        {
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref pe))
                    do
                    {
                        if (!byParent.TryGetValue((int)pe.th32ParentProcessID, out var list))
                            byParent[(int)pe.th32ParentProcessID] = list = new List<int>();
                        list.Add((int)pe.th32ProcessID);
                    } while (Process32Next(snapshot, ref pe));
            }
            finally { CloseHandle(snapshot); }
        }

        var result = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(Process.GetCurrentProcess().Id);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!result.Add(p)) continue;
            if (byParent.TryGetValue(p, out var kids))
                foreach (var k in kids) queue.Enqueue(k);
        }
        return result;
    }

    // ---- Core Audio COM interop ------------------------------------------------------

    private const int ERender = 0, EMultimedia = 1, ClsctxInprocServer = 1;
    private static Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl_EnumAudioEndpoints();
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl_GetAudioSessionControl();
        int NotImpl_GetSimpleAudioVolume();
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int SessionCount);
        int GetSession(int SessionIndex, out IAudioSessionControl2 Session);
    }

    // IAudioSessionControl2 with the full flattened vtable (base IAudioSessionControl first).
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid Override, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr NewNotifications);
        int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetProcessId(out int retVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    // ---- process snapshot ------------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
