// Microphone: the main PC's microphone is streamed to the laptop, which plays it into a virtual
// cable (VB-Cable) so apps on the laptop can pick "CABLE Output" as their microphone.
// The stream is encrypted with the pairing code - it's someone's voice, so nobody else should hear it.
// The PC only opens its microphone while a laptop is actually connected.
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AudioLink
{
    static class MicProto
    {
        public const int Port = 50508;
        public const byte Hello = 1, Audio = 2, Ping = 3;
    }

    // Records the default microphone as 48 kHz mono 16-bit (or whatever it offers, converted).
    class MicSource : IDisposable
    {
        public IAudioClient Client;
        public IAudioCaptureClient Capture;
        public AudioFormat Format;
        IntPtr fmtPtr;
        bool fmtFromWindows;

        public static MicSource Open()
        {
            var s = new MicSource();
            try { s.Init(true); return s; }
            catch (Exception ex1)
            {
                s.Dispose();
                Net.Log("Microphone: converted format refused (" + ex1.Message.Trim() + "), using its own format");
                s = new MicSource();
                try { s.Init(false); return s; }
                catch { s.Dispose(); throw; }
            }
        }

        void Init(bool convert)
        {
            Client = Devices.Client(1 /* eCapture */, null);
            int flags = 0;
            if (convert)
            {
                fmtPtr = Marshal.AllocHGlobal(18);
                Marshal.WriteInt16(fmtPtr, 0, 1);          // PCM
                Marshal.WriteInt16(fmtPtr, 2, 1);          // mono
                Marshal.WriteInt32(fmtPtr, 4, 48000);
                Marshal.WriteInt32(fmtPtr, 8, 96000);
                Marshal.WriteInt16(fmtPtr, 12, 2);
                Marshal.WriteInt16(fmtPtr, 14, 16);
                Marshal.WriteInt16(fmtPtr, 16, 0);
                flags = Wasapi.AUTOCONVERTPCM | Wasapi.SRC_DEFAULT_QUALITY;
            }
            else
            {
                Client.GetMixFormat(out fmtPtr);
                fmtFromWindows = true;
            }
            Format = AudioFormat.Parse(fmtPtr);
            Client.Initialize(Wasapi.SHARED, flags, 1000000, 0, fmtPtr, IntPtr.Zero);
            object o;
            Guid iid = Wasapi.IID_IAudioCaptureClient;
            Client.GetService(ref iid, out o);
            Capture = (IAudioCaptureClient)o;
            Client.Start();
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
        }
    }

    // ---------------------------------------------------------------- main PC: share the microphone

    class MicServer
    {
        readonly string code;
        readonly Action<string> status;
        volatile bool running;
        TcpListener listener;
        readonly object sync = new object();
        TcpClient current;
        public volatile float Level;

        public MicServer(string code, Action<string> status) { this.code = code; this.status = status; }

        public void Start()
        {
            running = true;
            listener = new TcpListener(IPAddress.Any, MicProto.Port);
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true }.Start();
            status(Idle());
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            lock (sync) { if (current != null) current.Close(); }
            Level = 0;
        }

        static string Idle()
        {
            string name = Devices.DefaultName(1);
            return name == null ? "no microphone found on this PC" : "waiting for the laptop to use it  (" + name + ")";
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                new Thread(() => Session(c)) { IsBackground = true }.Start();
            }
        }

        void Session(TcpClient c)
        {
            bool active = false;
            try
            {
                c.NoDelay = true;
                c.ReceiveTimeout = 10000;
                c.SendTimeout = 5000;
                var ch = SecureChannel.Handshake(c.GetStream(), code, true);
                byte[] hello = ch.Receive();
                if (hello.Length == 0 || hello[0] != MicProto.Hello) return;

                // Only one laptop listens at a time.
                lock (sync)
                {
                    if (current != null) current.Close();
                    current = c;
                }
                active = true;

                using (var mic = MicSource.Open())
                {
                    var fmt = mic.Format;
                    byte[] header = new byte[6];
                    header[0] = MicProto.Hello;
                    Array.Copy(BitConverter.GetBytes(fmt.Rate), 0, header, 1, 4);
                    header[5] = 1; // mono
                    ch.Send(header);
                    status("in use by the laptop  (" + (Devices.DefaultName(1) ?? "microphone") + ")");

                    byte[] raw = new byte[0];
                    DateTime lastSend = DateTime.UtcNow;
                    int bytesPerSample = fmt.Bits / 8;
                    while (running)
                    {
                        Thread.Sleep(5);
                        uint next;
                        mic.Capture.GetNextPacketSize(out next);
                        while (next > 0 && running)
                        {
                            IntPtr p; uint frames, flags; ulong dp, qp;
                            Marshal.ThrowExceptionForHR(mic.Capture.GetBuffer(out p, out frames, out flags, out dp, out qp));
                            int bytes = (int)frames * fmt.BlockAlign;
                            if (raw.Length < bytes) raw = new byte[bytes];
                            bool silent = (flags & Wasapi.SILENT) != 0;
                            if (!silent) Marshal.Copy(p, raw, 0, bytes);
                            mic.Capture.ReleaseBuffer(frames);

                            byte[] packet = new byte[1 + frames * 2];
                            packet[0] = MicProto.Audio;
                            float peak = 0;
                            for (int f = 0; f < frames; f++)
                            {
                                float v = 0;
                                if (!silent)
                                {
                                    int off = f * fmt.BlockAlign;
                                    for (int chn = 0; chn < fmt.Channels; chn++) v += fmt.Sample(raw, off + chn * bytesPerSample);
                                    v /= fmt.Channels;
                                }
                                if (Math.Abs(v) > peak) peak = Math.Abs(v);
                                int s = (int)(v * 32767f);
                                if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                                packet[1 + f * 2] = (byte)s;
                                packet[2 + f * 2] = (byte)(s >> 8);
                            }
                            Level = peak;
                            ch.Send(packet);
                            lastSend = DateTime.UtcNow;
                            mic.Capture.GetNextPacketSize(out next);
                        }
                        if ((DateTime.UtcNow - lastSend).TotalMilliseconds > 1000)
                        {
                            ch.Send(new[] { MicProto.Ping });
                            lastSend = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (SecureChannel.AuthException) { status("a laptop asked for the microphone with a different pairing code"); }
            catch (Exception ex)
            {
                Net.Log("Microphone session ended: " + ex.Message);
                if (running && active && !(ex is System.IO.IOException) && !(ex is SocketException) && !(ex is ObjectDisposedException))
                    status("couldn't open the microphone: " + ex.Message);
            }
            finally
            {
                c.Close();
                Level = 0;
                bool wasCurrent;
                lock (sync) { wasCurrent = current == c; if (wasCurrent) current = null; }
                if (running && active && wasCurrent) status(Idle());
            }
        }
    }

    // ---------------------------------------------------------------- laptop: receive the microphone

    class MicClient
    {
        readonly string host, code, deviceId, deviceName;
        readonly Action<string> status;
        volatile bool running;
        Thread thread;
        volatile TcpClient tcp;
        public volatile float Level;

        public MicClient(string host, string code, string deviceId, string deviceName, Action<string> status)
        {
            this.host = host; this.code = code; this.deviceId = deviceId; this.deviceName = deviceName; this.status = status;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            var t = tcp;
            if (t != null) try { t.Close(); } catch { }
            if (thread != null) thread.Join(3000);
            Level = 0;
        }

        // What apps should pick as their microphone.
        string AppsShouldPick()
        {
            if (deviceName.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return "\"CABLE Output\"";
            return "the recording side of " + deviceName;
        }

        void Run()
        {
            while (running)
            {
                TcpClient c = null;
                var stop = new ManualResetEvent(false);
                Thread player = null;
                bool connected = false, welcomed = false;
                try
                {
                    status("connecting to the main PC's microphone ...");
                    c = new TcpClient();
                    tcp = c;
                    c.NoDelay = true;
                    IAsyncResult ar = c.BeginConnect(host, MicProto.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new Exception("no answer from the main PC");
                    c.EndConnect(ar);
                    connected = true;
                    c.ReceiveTimeout = 6000;
                    c.SendTimeout = 5000;

                    var ch = SecureChannel.Handshake(c.GetStream(), code, false);
                    ch.Send(new[] { MicProto.Hello });
                    byte[] h = ch.Receive();
                    if (h.Length < 6 || h[0] != MicProto.Hello) throw new Exception("unexpected reply");
                    welcomed = true;
                    int rate = BitConverter.ToInt32(h, 1);
                    if (rate < 8000 || rate > 384000) throw new Exception("bad sample rate");

                    var buffer = new FrameBuffer();
                    player = new Thread(() =>
                    {
                        try { AudioOut.Play(deviceId, rate, buffer, stop, () => 1f); }
                        catch (Exception ex) { status("couldn't play into " + deviceName + ": " + ex.Message); try { c.Close(); } catch { } }
                    }) { IsBackground = true };
                    player.Start();
                    status("receiving the main PC's microphone - in your apps, choose " + AppsShouldPick() + " as the microphone");

                    float[] stereo = new float[0];
                    while (running)
                    {
                        byte[] m = ch.Receive();
                        if (m.Length == 0 || m[0] != MicProto.Audio) continue;
                        int frames = (m.Length - 1) / 2;
                        if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                        float peak = 0;
                        for (int f = 0; f < frames; f++)
                        {
                            float v = (short)(m[1 + f * 2] | m[2 + f * 2] << 8) / 32768f;
                            stereo[f * 2] = stereo[f * 2 + 1] = v;
                            if (Math.Abs(v) > peak) peak = Math.Abs(v);
                        }
                        Level = peak;
                        buffer.Write(stereo, frames);
                    }
                }
                catch (Exception ex)
                {
                    if (running)
                    {
                        var se = ex as SocketException;
                        if (ex is SecureChannel.AuthException || (connected && !welcomed))
                            status("the main PC refused - check the pairing code, and that Microphone is ticked on the PC  (retrying)");
                        else if (se != null && se.SocketErrorCode == SocketError.ConnectionRefused)
                            status("the main PC isn't sharing its microphone - tick Microphone on the PC  (retrying)");
                        else
                            status("not connected: " + ex.Message + "  (retrying)");
                    }
                }
                finally
                {
                    stop.Set();
                    if (player != null) player.Join(2000);
                    if (c != null) c.Close();
                    Level = 0;
                }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
            status("stopped");
        }
    }
}
