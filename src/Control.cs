// Keyboard, mouse and clipboard sharing.
// The main PC watches its own keyboard/mouse (low-level hooks). When the pointer leaves the screen
// edge facing the laptop, input stops going to the PC and is sent to the laptop instead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AudioLink
{
    static class Proto
    {
        public const int Port = 50507;
        public const byte Move = 1, Button = 2, Wheel = 3, Key = 4, ClipText = 5, ClipImage = 6, Ping = 9, Hello = 10, Welcome = 11;
        public const byte FlagInput = 1, FlagClipboard = 2;

        public static byte[] MoveMsg(int x, int y)
        {
            byte[] m = new byte[9];
            m[0] = Move;
            Array.Copy(BitConverter.GetBytes(x), 0, m, 1, 4);
            Array.Copy(BitConverter.GetBytes(y), 0, m, 5, 4);
            return m;
        }

        public static byte[] ButtonMsg(int button, bool down) { return new byte[] { Button, (byte)button, (byte)(down ? 1 : 0) }; }

        public static byte[] WheelMsg(bool horizontal, int delta)
        {
            byte[] m = new byte[6];
            m[0] = Wheel;
            m[1] = (byte)(horizontal ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(delta), 0, m, 2, 4);
            return m;
        }

        public static byte[] KeyMsg(int vk, int scan, bool extended, bool up)
        {
            return new byte[] { Key, (byte)vk, (byte)(vk >> 8), (byte)scan, (byte)(scan >> 8), (byte)((extended ? 1 : 0) | (up ? 2 : 0)) };
        }

        public static byte[] ClipMsg(byte type, byte[] data)
        {
            byte[] m = new byte[1 + data.Length];
            m[0] = type;
            Array.Copy(data, 0, m, 1, data.Length);
            return m;
        }

        // Hello (laptop -> PC): screen size, what the laptop allows, its name.
        public static byte[] HelloMsg(int w, int h, byte flags)
        {
            byte[] name = Encoding.UTF8.GetBytes(Environment.MachineName);
            byte[] m = new byte[10 + name.Length];
            m[0] = Hello;
            Array.Copy(BitConverter.GetBytes(w), 0, m, 1, 4);
            Array.Copy(BitConverter.GetBytes(h), 0, m, 5, 4);
            m[9] = flags;
            Array.Copy(name, 0, m, 10, name.Length);
            return m;
        }

        // Welcome (PC -> laptop): what the PC allows, its name.
        public static byte[] WelcomeMsg(byte flags)
        {
            byte[] name = Encoding.UTF8.GetBytes(Environment.MachineName);
            byte[] m = new byte[2 + name.Length];
            m[0] = Welcome;
            m[1] = flags;
            Array.Copy(name, 0, m, 2, name.Length);
            return m;
        }

        public static byte[] Tail(byte[] m, int from)
        {
            byte[] r = new byte[m.Length - from];
            Array.Copy(m, from, r, 0, r.Length);
            return r;
        }
    }

    // ---------------------------------------------------------------- main PC side

    class ControlServer
    {
        const int WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
        const int VK_SCROLL = 0x91;

        readonly string code, edge;
        readonly bool inputEnabled, clipboardEnabled;
        readonly Action<string> status;
        public Action<byte, byte[]> ClipboardReceived;

        volatile bool running;
        TcpListener listener;
        volatile Link link;

        // hook thread state (only touched on the hook thread)
        Thread hookThread;
        uint hookThreadId;
        readonly ManualResetEvent hookReady = new ManualResetEvent(false);
        Native.HookProc mouseProc, keyProc;
        volatile bool remote;
        int rx, ry, localButtons, remoteButtons;
        POINT park, returnPos;
        readonly HashSet<int> localKeys = new HashSet<int>();
        readonly Dictionary<int, int[]> remoteKeys = new Dictionary<int, int[]>();

        class Link
        {
            public TcpClient Tcp;
            public SecureChannel Ch;
            public string Name = "laptop";
            public volatile int W = 1920, H = 1080;
            public volatile bool AcceptsInput, AcceptsClipboard, Alive = true;
            readonly object qLock = new object();
            readonly Queue<byte[]> queue = new Queue<byte[]>();
            bool movePending;
            int moveX, moveY;
            public readonly AutoResetEvent Signal = new AutoResetEvent(false);

            // Mouse moves are merged (only the latest position matters) but never reordered past clicks/keys.
            public void Enqueue(byte[] m) { lock (qLock) { FlushMove(); queue.Enqueue(m); } Signal.Set(); }
            public void QueueMove(int x, int y) { lock (qLock) { movePending = true; moveX = x; moveY = y; } Signal.Set(); }
            void FlushMove() { if (movePending) { queue.Enqueue(Proto.MoveMsg(moveX, moveY)); movePending = false; } }
            public List<byte[]> Take() { lock (qLock) { FlushMove(); var l = new List<byte[]>(queue); queue.Clear(); return l; } }
            public void Close() { Alive = false; try { Tcp.Close(); } catch { } Signal.Set(); }
        }

        // Edge switching options (like Deskflow's): ignore corners, and switch instantly / after a pause / on a double tap.
        readonly bool deadCorners;
        readonly string switchMode;
        bool atEdge;
        int edgeSince, lastTap;
        const int PauseMs = 250, DoubleTapMs = 500, CornerPx = 24;

        public ControlServer(string code, bool input, bool clipboard, string edge, bool deadCorners, string switchMode, Action<string> status)
        {
            this.code = code; inputEnabled = input; clipboardEnabled = clipboard; this.edge = edge; this.status = status;
            this.deadCorners = deadCorners; this.switchMode = switchMode;
        }

        public bool Remote { get { return remote; } }

        public string LaptopName { get { var lk = link; return lk != null && lk.Alive ? lk.Name : null; } }

        public void Start()
        {
            running = true;
            listener = new TcpListener(IPAddress.Any, Proto.Port);
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true }.Start();
            if (inputEnabled)
            {
                hookThread = new Thread(HookLoop) { IsBackground = true };
                hookThread.Start();
                hookReady.WaitOne(3000);
            }
            status("waiting for the laptop ...");
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            var lk = link;
            if (lk != null) lk.Close();
            if (hookThread != null)
            {
                Native.PostThreadMessage(hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                hookThread.Join(2000);
                if (remote) { remote = false; Native.SetCursorPos(returnPos.X, returnPos.Y); }
            }
        }

        public void SendClipboard(byte type, byte[] data)
        {
            var lk = link;
            if (clipboardEnabled && lk != null && lk.Alive && lk.AcceptsClipboard) lk.Enqueue(Proto.ClipMsg(type, data));
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { return; }
                new Thread(() => HandleLink(c)) { IsBackground = true }.Start();
            }
        }

        void HandleLink(TcpClient tcp)
        {
            Link lk = null;
            try
            {
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = 10000;
                tcp.SendTimeout = 5000;
                var ch = SecureChannel.Handshake(tcp.GetStream(), code, true);
                byte[] hello = ch.Receive();
                if (hello[0] != Proto.Hello) return;
                lk = new Link { Tcp = tcp, Ch = ch };
                ApplyHello(lk, hello);
                ch.Send(Proto.WelcomeMsg((byte)((inputEnabled ? Proto.FlagInput : 0) | (clipboardEnabled ? Proto.FlagClipboard : 0))));

                var old = link;
                link = lk;
                if (old != null) old.Close();
                ReportConnected(lk);

                new Thread(() => SendLoop(lk)) { IsBackground = true }.Start();
                tcp.ReceiveTimeout = 8000;
                while (running && lk.Alive)
                {
                    byte[] m = ch.Receive();
                    if (m.Length == 0) continue;
                    if (m[0] == Proto.Hello) { ApplyHello(lk, m); ReportConnected(lk); }
                    else if ((m[0] == Proto.ClipText || m[0] == Proto.ClipImage) && clipboardEnabled && ClipboardReceived != null)
                        ClipboardReceived(m[0], Proto.Tail(m, 1));
                }
            }
            catch (SecureChannel.AuthException)
            {
                status("a laptop tried to connect with a different pairing code");
            }
            catch (Exception ex) { Net.Log("Control link ended: " + ex.Message); }
            finally
            {
                if (lk != null)
                {
                    lk.Close();
                    if (link == lk) { link = null; if (running) status("laptop disconnected, waiting ..."); }
                }
                else tcp.Close();
            }
        }

        void ApplyHello(Link lk, byte[] m)
        {
            lk.W = Math.Max(1, BitConverter.ToInt32(m, 1));
            lk.H = Math.Max(1, BitConverter.ToInt32(m, 5));
            lk.AcceptsInput = (m[9] & Proto.FlagInput) != 0;
            lk.AcceptsClipboard = (m[9] & Proto.FlagClipboard) != 0;
            lk.Name = Encoding.UTF8.GetString(m, 10, m.Length - 10);
        }

        void ReportConnected(Link lk)
        {
            if (inputEnabled && lk.AcceptsInput)
                status("connected to " + lk.Name + " - move the mouse off the " + EdgeWord() + " edge, or press Scroll Lock");
            else
                status("connected to " + lk.Name + (inputEnabled ? " (the laptop has keyboard/mouse sharing turned off)" : ""));
        }

        string EdgeWord()
        {
            switch (edge) { case "Left": return "left"; case "Top": return "top"; case "Bottom": return "bottom"; default: return "right"; }
        }

        void SendLoop(Link lk)
        {
            DateTime lastSend = DateTime.UtcNow;
            try
            {
                while (lk.Alive)
                {
                    lk.Signal.WaitOne(1000);
                    var items = lk.Take();
                    foreach (var m in items) lk.Ch.Send(m);
                    if (items.Count > 0) lastSend = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - lastSend).TotalSeconds >= 2) { lk.Ch.Send(new[] { Proto.Ping }); lastSend = DateTime.UtcNow; }
                }
            }
            catch (Exception ex) { Net.Log("Control send failed: " + ex.Message); }
            lk.Close();
        }

        void Notify(string s)
        {
            // Never do slow work inside a hook callback.
            ThreadPool.QueueUserWorkItem(_ => status(s));
        }

        // ------------------------------------------------ hooks

        void HookLoop()
        {
            hookThreadId = Native.GetCurrentThreadId();
            mouseProc = MouseProc;
            keyProc = KeyProc;
            IntPtr mod = Native.GetModuleHandle(null);
            IntPtr mh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            IntPtr kh = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            hookReady.Set();
            if (mh == IntPtr.Zero || kh == IntPtr.Zero) Notify("could not watch the keyboard/mouse (error " + Marshal.GetLastWin32Error() + ")");
            MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
            if (mh != IntPtr.Zero) Native.UnhookWindowsHookEx(mh);
            if (kh != IntPtr.Zero) Native.UnhookWindowsHookEx(kh);
        }

        static int VsLeft { get { return Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN); } }
        static int VsTop { get { return Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN); } }
        static int VsWidth { get { return Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN); } }
        static int VsHeight { get { return Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN); } }

        bool AtEdge(int x, int y)
        {
            switch (edge)
            {
                case "Left": return x <= VsLeft;
                case "Top": return y <= VsTop;
                case "Bottom": return y >= VsTop + VsHeight - 1;
                default: return x >= VsLeft + VsWidth - 1;
            }
        }

        bool InCorner(int x, int y)
        {
            if (edge == "Left" || edge == "Right") return y < VsTop + CornerPx || y >= VsTop + VsHeight - CornerPx;
            return x < VsLeft + CornerPx || x >= VsLeft + VsWidth - CornerPx;
        }

        // Called for each move while the pointer is pressed against the laptop edge.
        bool EdgeTriggered()
        {
            int now = Environment.TickCount;
            bool firstTouch = !atEdge;
            atEdge = true;
            if (switchMode == "delay")
            {
                if (firstTouch) edgeSince = now;
                return now - edgeSince >= PauseMs;
            }
            if (switchMode == "doubletap")
            {
                if (!firstTouch) return false;
                if (now - lastTap < DoubleTapMs) { lastTap = 0; return true; }
                lastTap = now;
                return false;
            }
            return true;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        static int ButtonIndex(int msg, int data)
        {
            switch (msg)
            {
                case 0x201: case 0x202: return 0;
                case 0x204: case 0x205: return 1;
                case 0x207: case 0x208: return 2;
                case 0x20B: case 0x20C: return (data >> 16) == 2 ? 4 : 3;
            }
            return -1;
        }

        static bool IsButtonDown(int msg) { return msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20B; }

        void EnterFromEdge(Link lk, int x, int y)
        {
            int vl = VsLeft, vt = VsTop, vw = VsWidth, vh = VsHeight;
            double t;
            switch (edge)
            {
                case "Left":
                    t = (y - vt) / (double)vh; rx = lk.W - 1; ry = (int)(t * lk.H); returnPos = new POINT(vl + 40, y); break;
                case "Top":
                    t = (x - vl) / (double)vw; rx = (int)(t * lk.W); ry = lk.H - 1; returnPos = new POINT(x, vt + 40); break;
                case "Bottom":
                    t = (x - vl) / (double)vw; rx = (int)(t * lk.W); ry = 0; returnPos = new POINT(x, vt + vh - 40); break;
                default:
                    t = (y - vt) / (double)vh; rx = 0; ry = (int)(t * lk.H); returnPos = new POINT(vl + vw - 40, y); break;
            }
            EnterRemote(lk);
        }

        void EnterRemote(Link lk)
        {
            remote = true;
            remoteButtons = 0;
            remoteKeys.Clear();
            // Park the PC pointer in the middle of the main screen; every movement away from it is a delta for the laptop.
            park = new POINT(Native.GetSystemMetrics(Native.SM_CXSCREEN) / 2, Native.GetSystemMetrics(Native.SM_CYSCREEN) / 2);
            Native.SetCursorPos(park.X, park.Y);
            lk.QueueMove(rx, ry);
            Notify("controlling " + lk.Name + " - move back across the edge or press Scroll Lock to return");
        }

        // True when the virtual pointer has crossed back over the laptop edge that faces the PC.
        bool PastReturnEdge(Link lk)
        {
            switch (edge)
            {
                case "Left": return rx > lk.W - 1;
                case "Top": return ry > lk.H - 1;
                case "Bottom": return ry < 0;
                default: return rx < 0;
            }
        }

        POINT EdgeReturnPoint(Link lk)
        {
            int vl = VsLeft, vt = VsTop, vw = VsWidth, vh = VsHeight;
            int y = vt + (int)(Clamp(ry, 0, lk.H - 1) / (double)lk.H * vh);
            int x = vl + (int)(Clamp(rx, 0, lk.W - 1) / (double)lk.W * vw);
            switch (edge)
            {
                case "Left": return new POINT(vl + 3, y);
                case "Top": return new POINT(x, vt + 3);
                case "Bottom": return new POINT(x, vt + vh - 4);
                default: return new POINT(vl + vw - 4, y);
            }
        }

        void LeaveRemote(Link lk, POINT target)
        {
            // Let go of anything still held on the laptop so nothing gets stuck down.
            if (lk != null && lk.Alive)
            {
                foreach (var kv in remoteKeys) lk.Enqueue(Proto.KeyMsg(kv.Key, kv.Value[0], kv.Value[1] != 0, true));
                for (int b = 0; b < 5; b++) if ((remoteButtons & (1 << b)) != 0) lk.Enqueue(Proto.ButtonMsg(b, false));
            }
            remoteKeys.Clear();
            remoteButtons = 0;
            remote = false;
            Native.SetCursorPos(target.X, target.Y);
            if (lk != null && lk.Alive) ReportConnectedAsync(lk);
            else Notify("laptop disconnected, waiting ...");
        }

        void ReportConnectedAsync(Link lk) { ThreadPool.QueueUserWorkItem(_ => ReportConnected(lk)); }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            int msg = wParam.ToInt32();
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
            int data = Marshal.ReadInt32(lParam, 8), flags = Marshal.ReadInt32(lParam, 12);
            if ((flags & 1) != 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // injected by software

            Link lk = link;
            if (!remote)
            {
                if (msg == WM_MOUSEMOVE)
                {
                    bool pressing = lk != null && lk.Alive && lk.AcceptsInput && localButtons == 0 && AtEdge(x, y) && !(deadCorners && InCorner(x, y));
                    if (!pressing) atEdge = false;
                    else if (EdgeTriggered())
                    {
                        atEdge = false;
                        EnterFromEdge(lk, x, y);
                        return (IntPtr)1;
                    }
                }
                else
                {
                    int b = ButtonIndex(msg, data);
                    if (b >= 0) { if (IsButtonDown(msg)) localButtons |= 1 << b; else localButtons &= ~(1 << b); }
                }
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (lk == null || !lk.Alive)
            {
                LeaveRemote(null, returnPos);
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (msg == WM_MOUSEMOVE)
            {
                int dx = x - park.X, dy = y - park.Y;
                if (dx == 0 && dy == 0) return (IntPtr)1;
                rx += dx; ry += dy;
                if (PastReturnEdge(lk)) { LeaveRemote(lk, EdgeReturnPoint(lk)); return (IntPtr)1; }
                rx = Clamp(rx, 0, lk.W - 1);
                ry = Clamp(ry, 0, lk.H - 1);
                lk.QueueMove(rx, ry);
            }
            else if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                lk.Enqueue(Proto.WheelMsg(msg == WM_MOUSEHWHEEL, (short)(data >> 16)));
            }
            else
            {
                int b = ButtonIndex(msg, data);
                if (b >= 0)
                {
                    bool down = IsButtonDown(msg);
                    if (down) remoteButtons |= 1 << b; else remoteButtons &= ~(1 << b);
                    lk.Enqueue(Proto.ButtonMsg(b, down));
                }
            }
            return (IntPtr)1;
        }

        IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            int vk = Marshal.ReadInt32(lParam, 0), scan = Marshal.ReadInt32(lParam, 4), flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & 0x10) != 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // injected by software
            bool up = (flags & 0x80) != 0, extended = (flags & 1) != 0;
            Link lk = link;

            // Scroll Lock flips between PC and laptop.
            if (vk == VK_SCROLL && lk != null && lk.Alive && lk.AcceptsInput)
            {
                if (!up)
                {
                    if (remote) LeaveRemote(lk, returnPos);
                    else
                    {
                        POINT cur;
                        Native.GetCursorPos(out cur);
                        returnPos = cur;
                        rx = lk.W / 2; ry = lk.H / 2;
                        EnterRemote(lk);
                    }
                }
                return (IntPtr)1;
            }

            if (!remote)
            {
                if (up) localKeys.Remove(vk); else localKeys.Add(vk);
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (lk == null || !lk.Alive)
            {
                LeaveRemote(null, returnPos);
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // A key that was already held on the PC before switching: let its release reach the PC.
            if (up && localKeys.Contains(vk) && !remoteKeys.ContainsKey(vk))
            {
                localKeys.Remove(vk);
                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (up) remoteKeys.Remove(vk); else remoteKeys[vk] = new[] { scan, extended ? 1 : 0 };
            lk.Enqueue(Proto.KeyMsg(vk, scan, extended, up));
            return (IntPtr)1;
        }
    }

    // ---------------------------------------------------------------- laptop side

    class ControlClient
    {
        readonly string host, code;
        readonly bool inputEnabled, clipboardEnabled;
        readonly Action<string> status;
        public Action<byte, byte[]> ClipboardReceived;

        volatile bool running;
        Thread thread;
        volatile TcpClient tcp;
        volatile SecureChannel channel;
        volatile bool pcAcceptsClipboard;

        public ControlClient(string host, string code, bool input, bool clipboard, Action<string> status)
        {
            this.host = host; this.code = code; inputEnabled = input; clipboardEnabled = clipboard; this.status = status;
        }

        public void Start()
        {
            running = true;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            var t = tcp;
            if (t != null) try { t.Close(); } catch { }
            if (thread != null) thread.Join(3000);
        }

        byte[] Hello()
        {
            return Proto.HelloMsg(Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
                (byte)((inputEnabled ? Proto.FlagInput : 0) | (clipboardEnabled ? Proto.FlagClipboard : 0)));
        }

        void OnDisplayChanged(object sender, EventArgs e) { Send(Hello()); }

        void Send(byte[] m)
        {
            var ch = channel;
            if (ch == null) return;
            try { ch.Send(m); }
            catch (Exception ex) { Net.Log("Control send failed: " + ex.Message); var t = tcp; if (t != null) t.Close(); }
        }

        public void SendClipboard(byte type, byte[] data)
        {
            if (!clipboardEnabled || !pcAcceptsClipboard || channel == null) return;
            ThreadPool.QueueUserWorkItem(_ => Send(Proto.ClipMsg(type, data)));
        }

        void Run()
        {
            while (running)
            {
                bool connected = false, welcomed = false;
                TcpClient c = null;
                try
                {
                    status("connecting to " + host + " ...");
                    c = new TcpClient();
                    tcp = c;
                    c.NoDelay = true;
                    IAsyncResult ar = c.BeginConnect(host, Proto.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new Exception("no answer - is AudioLink running on the main PC?");
                    c.EndConnect(ar);
                    connected = true;
                    c.ReceiveTimeout = 8000;
                    c.SendTimeout = 5000;

                    var ch = SecureChannel.Handshake(c.GetStream(), code, false);
                    ch.Send(Hello());
                    byte[] w = ch.Receive();
                    if (w[0] != Proto.Welcome) throw new Exception("unexpected reply");
                    welcomed = true;
                    pcAcceptsClipboard = (w[1] & Proto.FlagClipboard) != 0;
                    bool pcSharesInput = (w[1] & Proto.FlagInput) != 0;
                    string pcName = Encoding.UTF8.GetString(w, 2, w.Length - 2);
                    channel = ch;
                    status("connected to " + pcName + (inputEnabled && pcSharesInput ? " - its keyboard and mouse can control this laptop" : ""));

                    new Thread(() => PingLoop(ch)) { IsBackground = true }.Start();
                    while (running)
                    {
                        byte[] m = ch.Receive();
                        if (m.Length > 0) Handle(m);
                    }
                }
                catch (Exception ex)
                {
                    if (running)
                    {
                        if (ex is SecureChannel.AuthException || (connected && !welcomed))
                            status("the main PC refused the connection - check the pairing code matches  (retrying)");
                        else
                            status("not connected: " + ex.Message + "  (retrying)");
                    }
                }
                finally
                {
                    channel = null;
                    if (c != null) c.Close();
                }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
            status("stopped");
        }

        void PingLoop(SecureChannel ch)
        {
            while (running && channel == ch)
            {
                Thread.Sleep(2000);
                if (channel == ch) Send(new[] { Proto.Ping });
            }
        }

        void Handle(byte[] m)
        {
            switch (m[0])
            {
                case Proto.Move:
                    if (inputEnabled) Inject.MoveTo(BitConverter.ToInt32(m, 1), BitConverter.ToInt32(m, 5));
                    break;
                case Proto.Button:
                    if (inputEnabled) Inject.Button(m[1], m[2] != 0);
                    break;
                case Proto.Wheel:
                    if (inputEnabled) Inject.Wheel(m[1] != 0, BitConverter.ToInt32(m, 2));
                    break;
                case Proto.Key:
                    if (inputEnabled) Inject.Key(m[1] | m[2] << 8, m[3] | m[4] << 8, (m[5] & 1) != 0, (m[5] & 2) != 0);
                    break;
                case Proto.ClipText:
                case Proto.ClipImage:
                    if (clipboardEnabled && ClipboardReceived != null) ClipboardReceived(m[0], Proto.Tail(m, 1));
                    break;
            }
        }
    }
}
