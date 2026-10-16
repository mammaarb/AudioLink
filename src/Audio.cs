// Sound: the laptop captures everything it plays (WASAPI loopback) and streams it to the main PC.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AudioLink
{
    // ---------------------------------------------------------------- WASAPI interop

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    // Lists sound devices and opens a particular one.
    static class Devices
    {
        public class Info
        {
            public string Id, Name;
            public override string ToString() { return Name; }
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        static string FriendlyName(IMMDevice dev)
        {
            IPropertyStore store = null;
            try
            {
                dev.OpenPropertyStore(0 /* STGM_READ */, out store);
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PROPVARIANT v;
                store.GetValue(ref key, out v);
                string name = v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                return name ?? "(unnamed device)";
            }
            catch { return "(unnamed device)"; }
            finally { Wasapi.Release(store); }
        }

        // Active playback devices (dataFlow 0) or microphones (dataFlow 1).
        public static List<Info> List(int dataFlow)
        {
            var list = new List<Info>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                IMMDeviceCollection col;
                en.EnumAudioEndpoints(dataFlow, 1 /* DEVICE_STATE_ACTIVE */, out col);
                uint n;
                col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    col.Item(i, out dev);
                    string id;
                    dev.GetId(out id);
                    list.Add(new Info { Id = id, Name = FriendlyName(dev) });
                    Wasapi.Release(dev);
                }
                Wasapi.Release(col);
            }
            finally { Wasapi.Release(en); }
            return list;
        }

        public static string DefaultName(int dataFlow)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                IMMDevice dev;
                en.GetDefaultAudioEndpoint(dataFlow, 0, out dev);
                string name = FriendlyName(dev);
                Wasapi.Release(dev);
                return name;
            }
            catch { return null; }
            finally { Wasapi.Release(en); }
        }

        // The VB-Cable playback side ("CABLE Input"); apps then record from "CABLE Output".
        public static Info FindVirtualCable(List<Info> outputs)
        {
            foreach (var d in outputs) if (d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            foreach (var d in outputs) if (d.Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }

        // id == null means the default device for that direction.
        public static IAudioClient Client(int dataFlow, string id)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev = null;
            try
            {
                if (string.IsNullOrEmpty(id)) en.GetDefaultAudioEndpoint(dataFlow, 0 /* eConsole */, out dev);
                else en.GetDevice(id, out dev);
                object o;
                Guid iid = Wasapi.IID_IAudioClient;
                dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o);
                return (IAudioClient)o;
            }
            finally
            {
                Wasapi.Release(dev);
                Wasapi.Release(en);
            }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    static class Wasapi
    {
        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public const int SHARED = 0;
        public const int LOOPBACK = 0x00020000;
        public const int AUTOCONVERTPCM = unchecked((int)0x80000000);
        public const int SRC_DEFAULT_QUALITY = 0x08000000;
        public const uint SILENT = 0x2;

        // Client for the default playback device (speakers / headphones).
        public static IAudioClient DefaultPlaybackClient()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out dev);
            object o;
            Guid iid = IID_IAudioClient;
            dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o);
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
            return (IAudioClient)o;
        }

        public static void Release(object o)
        {
            if (o != null) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    class AudioFormat
    {
        public int Channels, Rate, Bits;
        public bool IsFloat;
        public int BlockAlign { get { return Channels * Bits / 8; } }

        public static AudioFormat Parse(IntPtr p)
        {
            var f = new AudioFormat();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.Bits = (ushort)Marshal.ReadInt16(p, 14);
            if (tag == 3) f.IsFloat = true;
            else if (tag == 0xFFFE)
            {
                byte[] g = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), g, 0, 16);
                f.IsFloat = new Guid(g) == Wasapi.FloatSubFormat;
            }
            return f;
        }

        public float Sample(byte[] raw, int offset)
        {
            if (IsFloat) return BitConverter.ToSingle(raw, offset);
            switch (Bits)
            {
                case 16: return BitConverter.ToInt16(raw, offset) / 32768f;
                case 24: return ((raw[offset] << 8 | raw[offset + 1] << 16 | raw[offset + 2] << 24) >> 8) / 8388608f;
                case 32: return BitConverter.ToInt32(raw, offset) / 2147483648f;
            }
            return 0;
        }
    }

    // Thread-safe FIFO of stereo float frames.
    class FrameBuffer
    {
        float[] data = new float[48000 * 2 * 4];
        int head, count; // in samples
        readonly object sync = new object();

        public int Frames { get { lock (sync) return count / 2; } }

        public void Write(float[] src, int frames)
        {
            lock (sync)
            {
                int n = frames * 2;
                if (count + n > data.Length) { int drop = count + n - data.Length; head = (head + drop) % data.Length; count -= drop; }
                int tail = (head + count) % data.Length;
                for (int i = 0; i < n; i++) data[(tail + i) % data.Length] = src[i];
                count += n;
            }
        }

        public int Read(float[] dst, int frames)
        {
            lock (sync)
            {
                int n = Math.Min(frames * 2, count);
                for (int i = 0; i < n; i++) dst[i] = data[(head + i) % data.Length];
                head = (head + n) % data.Length;
                count -= n;
                return n / 2;
            }
        }

        public void DropTo(int keepFrames)
        {
            lock (sync)
            {
                int drop = count - keepFrames * 2;
                if (drop <= 0) return;
                head = (head + drop) % data.Length;
                count -= drop;
            }
        }
    }

    // ---------------------------------------------------------------- capturing what the laptop plays

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Done.Set(); }
    }

    // Records everything this computer plays. Sound drivers differ, so there are three ways to do it,
    // tried in turn until one works:
    //   0. standard loopback of the default output device in its own format
    //   1. the same, but asking Windows to convert to plain 48 kHz stereo
    //   2. Windows 10 2004+/11 "process loopback": all apps' sound, bypassing the output driver entirely
    class LoopbackSource : IDisposable
    {
        public static readonly string[] MethodNames = { "standard", "converted", "all-apps" };
        public static int OnlyMethod = -1;      // for testing
        static int preferred;

        public IAudioClient Client;
        public IAudioCaptureClient Capture;
        public AudioFormat Format;
        public int Method;
        IntPtr fmtPtr;
        bool fmtFromWindows;
        AutoResetEvent bufferEvent;

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

        const int EVENTCALLBACK = 0x00040000;

        public static LoopbackSource Open()
        {
            var errors = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                int m = (preferred + k) % 3;
                if (OnlyMethod >= 0 && m != OnlyMethod) continue;
                var s = new LoopbackSource();
                try
                {
                    s.Init(m);
                    if (preferred != m) Net.Log("Sound capture: using the " + MethodNames[m] + " method");
                    preferred = m;
                    return s;
                }
                catch (Exception ex)
                {
                    errors.Add(MethodNames[m] + ": " + ex.Message.Trim() + " [0x" + Marshal.GetHRForException(ex).ToString("X8") + "]");
                    s.Dispose();
                }
            }
            string all = string.Join("; ", errors);
            Net.Log("Sound capture failed - " + all);
            throw new Exception("couldn't record this laptop's sound - " + all);
        }

        static IntPtr MakeFormat(bool isFloat, int rate)
        {
            int bits = isFloat ? 32 : 16, align = 2 * bits / 8;
            IntPtr p = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(p, 0, (short)(isFloat ? 3 : 1));
            Marshal.WriteInt16(p, 2, 2);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * align);
            Marshal.WriteInt16(p, 12, (short)align);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);
            return p;
        }

        void Init(int method)
        {
            Method = method;
            int flags = Wasapi.LOOPBACK;
            if (method == 0)
            {
                Client = Wasapi.DefaultPlaybackClient();
                Client.GetMixFormat(out fmtPtr);
                fmtFromWindows = true;
            }
            else if (method == 1)
            {
                Client = Wasapi.DefaultPlaybackClient();
                fmtPtr = MakeFormat(true, 48000);
                flags |= Wasapi.AUTOCONVERTPCM | Wasapi.SRC_DEFAULT_QUALITY;
            }
            else
            {
                Client = ActivateProcessLoopback();
                fmtPtr = MakeFormat(false, 48000);
                flags |= Wasapi.AUTOCONVERTPCM | EVENTCALLBACK;
            }
            Format = AudioFormat.Parse(fmtPtr);
            Client.Initialize(Wasapi.SHARED, flags, 2000000, 0, fmtPtr, IntPtr.Zero);
            if ((flags & EVENTCALLBACK) != 0)
            {
                bufferEvent = new AutoResetEvent(false);
                Client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());
            }
            object o;
            Guid iid = Wasapi.IID_IAudioCaptureClient;
            Client.GetService(ref iid, out o);
            Capture = (IAudioCaptureClient)o;
            Client.Start();
        }

        // Capture every app's sound except AudioLink's own.
        static IAudioClient ActivateProcessLoopback()
        {
            IntPtr parms = Marshal.AllocHGlobal(12), prop = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parms, 0, 1);                                   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(parms, 4, System.Diagnostics.Process.GetCurrentProcess().Id);
                Marshal.WriteInt32(parms, 8, 1);                                   // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i++) Marshal.WriteByte(prop, i, 0);
                Marshal.WriteInt16(prop, 0, 65);                                   // PROPVARIANT VT_BLOB
                Marshal.WriteInt32(prop, 8, 12);                                   // blob size
                Marshal.WriteIntPtr(prop, IntPtr.Size == 8 ? 16 : 12, parms);      // blob data

                var handler = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op;
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", Wasapi.IID_IAudioClient, prop, handler, out op);
                if (!handler.Done.WaitOne(5000)) throw new Exception("Windows didn't respond");
                int hr;
                object client;
                op.GetActivateResult(out hr, out client);
                Marshal.ThrowExceptionForHR(hr);
                Wasapi.Release(op);
                return (IAudioClient)client;
            }
            finally
            {
                Marshal.FreeHGlobal(parms);
                Marshal.FreeHGlobal(prop);
            }
        }

        public void Dispose()
        {
            if (Client != null) try { Client.Stop(); } catch { }
            Wasapi.Release(Capture);
            Wasapi.Release(Client);
            Capture = null; Client = null;
            if (fmtPtr != IntPtr.Zero)
            {
                if (fmtFromWindows) Marshal.FreeCoTaskMem(fmtPtr); else Marshal.FreeHGlobal(fmtPtr);
                fmtPtr = IntPtr.Zero;
            }
            if (bufferEvent != null) bufferEvent.Close();
        }
    }

    // ---------------------------------------------------------------- sender (laptop)

    class Sender
    {
        public const int Port = 50505;
        volatile bool running;
        Thread thread;
        string host;
        readonly Action<string> status;
        public volatile float Level;

        public Sender(Action<string> status) { this.status = status; }

        public void Start(string host)
        {
            this.host = host;
            running = true;
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) thread.Join(3000);
            thread = null;
            Level = 0;
        }

        void Run()
        {
            while (running)
            {
                TcpClient tcp = null;
                try
                {
                    status("connecting to " + host + " ...");
                    tcp = new TcpClient();
                    tcp.NoDelay = true;
                    tcp.SendTimeout = 3000;
                    IAsyncResult ar = tcp.BeginConnect(host, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new Exception("no answer - is AudioLink running on the main PC?");
                    tcp.EndConnect(ar);
                    Capture(tcp.GetStream());
                }
                catch (Exception ex)
                {
                    if (running) status("not connected: " + ex.Message + "  (retrying)");
                }
                finally
                {
                    if (tcp != null) tcp.Close();
                    Level = 0;
                }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
            status("stopped");
        }

        void Capture(NetworkStream ns)
        {
            using (var source = LoopbackSource.Open())
            {
                AudioFormat fmt = source.Format;
                IAudioCaptureClient capture = source.Capture;
                status("sending this laptop's sound to " + host);

                // Header: magic, sample rate, computer name
                byte[] name = Encoding.UTF8.GetBytes(Environment.MachineName);
                var header = new MemoryStream();
                header.Write(Net.Magic, 0, 4);
                header.Write(BitConverter.GetBytes(fmt.Rate), 0, 4);
                header.Write(BitConverter.GetBytes(name.Length), 0, 4);
                header.Write(name, 0, name.Length);
                ns.Write(header.ToArray(), 0, (int)header.Length);

                byte[] raw = new byte[0];
                byte[] packet = new byte[0];
                DateTime lastSend = DateTime.UtcNow;

                while (running)
                {
                    Thread.Sleep(5);
                    uint next;
                    capture.GetNextPacketSize(out next);
                    while (next > 0 && running)
                    {
                        IntPtr p; uint frames, flags; ulong dp, qp;
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out p, out frames, out flags, out dp, out qp));
                        int bytes = (int)frames * fmt.BlockAlign;
                        if (raw.Length < bytes) raw = new byte[bytes];
                        bool silent = (flags & Wasapi.SILENT) != 0;
                        if (!silent) Marshal.Copy(p, raw, 0, bytes);
                        capture.ReleaseBuffer(frames);

                        int need = 4 + (int)frames * 4;
                        if (packet.Length < need) packet = new byte[need];
                        Array.Copy(BitConverter.GetBytes((int)frames), 0, packet, 0, 4);
                        int bytesPerSample = fmt.Bits / 8;
                        float peak = 0;
                        for (int f = 0; f < frames; f++)
                        {
                            float l = 0, r = 0;
                            if (!silent)
                            {
                                int off = f * fmt.BlockAlign;
                                l = fmt.Sample(raw, off);
                                r = fmt.Channels > 1 ? fmt.Sample(raw, off + bytesPerSample) : l;
                            }
                            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                            WriteShort(packet, 4 + f * 4, l);
                            WriteShort(packet, 6 + f * 4, r);
                        }
                        Level = peak;
                        ns.Write(packet, 0, need);
                        lastSend = DateTime.UtcNow;
                        capture.GetNextPacketSize(out next);
                    }

                    // Windows sends nothing while the laptop is silent; a tiny keep-alive
                    // lets us notice quickly if the PC goes away.
                    if ((DateTime.UtcNow - lastSend).TotalMilliseconds > 1000)
                    {
                        ns.Write(new byte[4], 0, 4);
                        lastSend = DateTime.UtcNow;
                        Level = 0;
                    }
                }
            }
        }

        static void WriteShort(byte[] buf, int off, float v)
        {
            int s = (int)(v * 32767f);
            if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
            buf[off] = (byte)s;
            buf[off + 1] = (byte)(s >> 8);
        }
    }

    // ---------------------------------------------------------------- receiver (main PC)

    class Receiver
    {
        volatile bool running;
        TcpListener listener;
        Thread acceptThread;
        readonly Action<string> status;
        readonly object sessionLock = new object();
        TcpClient current;
        Thread currentThread;
        public volatile float Level;
        public volatile float Volume = 1f;

        public Receiver(Action<string> status) { this.status = status; }

        public void Start()
        {
            running = true;
            listener = new TcpListener(IPAddress.Any, Sender.Port);
            listener.Start();
            acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            acceptThread.Start();
            status("waiting for the laptop ...");
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            lock (sessionLock) { if (current != null) current.Close(); }
            if (currentThread != null) currentThread.Join(3000);
            Level = 0;
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                // A new connection replaces the old one.
                lock (sessionLock) { if (current != null) current.Close(); }
                if (currentThread != null) currentThread.Join(3000);
                lock (sessionLock) { current = c; }
                currentThread = new Thread(() => Session(c)) { IsBackground = true };
                currentThread.Start();
            }
        }

        void Session(TcpClient c)
        {
            var buffer = new FrameBuffer();
            var renderStop = new ManualResetEvent(false);
            Thread render = null;
            string who = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            try
            {
                c.NoDelay = true;
                NetworkStream ns = c.GetStream();
                byte[] hdr = new byte[12];
                if (!Net.ReadExact(ns, hdr, 12)) return;
                for (int i = 0; i < 4; i++) if (hdr[i] != Net.Magic[i]) return;
                int rate = BitConverter.ToInt32(hdr, 4);
                int nameLen = BitConverter.ToInt32(hdr, 8);
                if (rate < 8000 || rate > 384000 || nameLen < 0 || nameLen > 256) return;
                byte[] name = new byte[nameLen];
                if (!Net.ReadExact(ns, name, nameLen)) return;
                who = Encoding.UTF8.GetString(name) + " (" + who + ")";

                render = new Thread(() => Render(rate, buffer, renderStop)) { IsBackground = true };
                render.Start();
                status("playing sound from " + who);

                byte[] lenBuf = new byte[4];
                byte[] pcm = new byte[0];
                float[] samples = new float[0];
                while (running)
                {
                    if (!Net.ReadExact(ns, lenBuf, 4)) break;
                    int frames = BitConverter.ToInt32(lenBuf, 0);
                    if (frames < 0 || frames > rate) break;
                    if (frames == 0) { Level = 0; continue; } // keep-alive
                    int bytes = frames * 4;
                    if (pcm.Length < bytes) { pcm = new byte[bytes]; samples = new float[frames * 2]; }
                    if (!Net.ReadExact(ns, pcm, bytes)) break;
                    float peak = 0;
                    for (int i = 0; i < frames * 2; i++)
                    {
                        float v = BitConverter.ToInt16(pcm, i * 2) / 32768f;
                        samples[i] = v;
                        if (Math.Abs(v) > peak) peak = Math.Abs(v);
                    }
                    Level = peak;
                    buffer.Write(samples, frames);
                }
            }
            catch (Exception ex) { Net.Log("Sound session ended: " + ex.Message); }
            finally
            {
                renderStop.Set();
                if (render != null) render.Join(3000);
                c.Close();
                Level = 0;
                if (running) status("laptop disconnected, waiting ...");
            }
        }

        void Render(int rate, FrameBuffer buffer, ManualResetEvent stop)
        {
            try { AudioOut.Play(null, rate, buffer, stop, () => Volume); }
            catch (Exception ex) { status("playback error: " + ex.Message); }
        }
    }

    // Plays a stream of stereo frames on a playback device, keeping the delay low.
    static class AudioOut
    {
        // deviceId == null plays on the default device. Runs until stop is set; throws on device errors.
        public static void Play(string deviceId, int rate, FrameBuffer buffer, WaitHandle stop, Func<float> volume)
        {
            IAudioClient client = null;
            IAudioRenderClient renderClient = null;
            IntPtr fmt = Marshal.AllocHGlobal(18);
            try
            {
                // Ask Windows for plain stereo float at the sender's rate; it converts to whatever the device needs.
                Marshal.WriteInt16(fmt, 0, 3);           // WAVE_FORMAT_IEEE_FLOAT
                Marshal.WriteInt16(fmt, 2, 2);           // channels
                Marshal.WriteInt32(fmt, 4, rate);
                Marshal.WriteInt32(fmt, 8, rate * 8);
                Marshal.WriteInt16(fmt, 12, 8);          // block align
                Marshal.WriteInt16(fmt, 14, 32);         // bits
                Marshal.WriteInt16(fmt, 16, 0);

                client = Devices.Client(0, deviceId);
                client.Initialize(Wasapi.SHARED, Wasapi.AUTOCONVERTPCM | Wasapi.SRC_DEFAULT_QUALITY, 1000000, 0, fmt, IntPtr.Zero);
                uint bufFrames;
                client.GetBufferSize(out bufFrames);
                object o;
                Guid iid = Wasapi.IID_IAudioRenderClient;
                client.GetService(ref iid, out o);
                renderClient = (IAudioRenderClient)o;

                int target = Math.Min((int)bufFrames, rate * 40 / 1000);   // keep ~40 ms queued in the device
                int lowWater = rate * 10 / 1000;
                int maxQueue = rate * 250 / 1000, keepQueue = rate * 60 / 1000;
                float[] tmp = new float[bufFrames * 2];
                client.Start();

                while (!stop.WaitOne(5))
                {
                    uint pad;
                    client.GetCurrentPadding(out pad);
                    int want = target - (int)pad;
                    if (want <= 0) continue;

                    // Wi-Fi hiccups can pile up delay; skip ahead if we fall too far behind.
                    if (buffer.Frames > maxQueue) buffer.DropTo(keepQueue);

                    int got = buffer.Read(tmp, want);
                    int toWrite = pad < lowWater ? want : got;  // only pad with silence when about to run dry
                    if (toWrite == 0) continue;
                    for (int i = got * 2; i < toWrite * 2; i++) tmp[i] = 0;
                    float vol = volume();
                    if (vol != 1f) for (int i = 0; i < got * 2; i++) tmp[i] *= vol;

                    IntPtr p;
                    renderClient.GetBuffer((uint)toWrite, out p);
                    Marshal.Copy(tmp, 0, p, toWrite * 2);
                    renderClient.ReleaseBuffer((uint)toWrite, 0);
                }
            }
            finally
            {
                if (client != null) try { client.Stop(); } catch { }
                Wasapi.Release(renderClient);
                Wasapi.Release(client);
                Marshal.FreeHGlobal(fmt);
            }
        }
    }
}
