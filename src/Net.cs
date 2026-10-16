// Networking helpers: finding the main PC on the network, and the encrypted channel
// that carries keyboard, mouse and clipboard data.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AudioLink
{
    static class Net
    {
        public const int DiscoveryPort = 50506;
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ALNK");

        public static bool ReadExact(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) return false;
                off += r;
            }
            return true;
        }

        static IEnumerable<UnicastIPAddressInformation> LanAddresses()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.Address.ToString().StartsWith("169.254.")) continue;
                    yield return ua;
                }
            }
        }

        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            foreach (var ua in LanAddresses()) list.Add(ua.Address.ToString());
            return list;
        }

        static List<IPAddress> BroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            foreach (var ua in LanAddresses())
            {
                if (ua.IPv4Mask == null) continue;
                byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                list.Add(new IPAddress(ip));
            }
            return list;
        }

        // Ask the network "is there an AudioLink main PC out there?"
        public static List<string> FindMainPcs()
        {
            var found = new List<string>();
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = 300;
                byte[] q = Encoding.ASCII.GetBytes("ALNK?");
                foreach (var b in BroadcastAddresses())
                    try { udp.Send(q, q.Length, new IPEndPoint(b, DiscoveryPort)); } catch { }
                DateTime end = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < end)
                {
                    try
                    {
                        IPEndPoint from = null;
                        byte[] r = udp.Receive(ref from);
                        string s = Encoding.UTF8.GetString(r);
                        if (!s.StartsWith("ALNK!")) continue;
                        string entry = from.Address + "  (" + s.Substring(5) + ")";
                        if (!found.Contains(entry)) found.Add(entry);
                    }
                    catch (SocketException) { }
                }
            }
            return found;
        }

        public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "AudioLink.log");
        public static event Action<string> LineAdded;
        static readonly List<string> recent = new List<string>();

        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss  ") + msg;
            lock (recent)
            {
                recent.Add(line);
                if (recent.Count > 1000) recent.RemoveAt(0);
                try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
            }
            var h = LineAdded;
            if (h != null) h(line);
        }

        public static string[] RecentLines() { lock (recent) return recent.ToArray(); }
    }

    // Answers "Find PC" requests from the laptop.
    class DiscoveryResponder
    {
        UdpClient udp;
        volatile bool running;

        public void Start()
        {
            try { udp = new UdpClient(Net.DiscoveryPort); }
            catch (Exception ex) { Net.Log("Discovery unavailable: " + ex.Message); return; }
            running = true;
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            running = false;
            if (udp != null) try { udp.Close(); } catch { }
        }

        void Loop()
        {
            byte[] reply = Encoding.UTF8.GetBytes("ALNK!" + Environment.MachineName);
            while (running)
            {
                try
                {
                    IPEndPoint from = null;
                    byte[] q = udp.Receive(ref from);
                    if (Encoding.ASCII.GetString(q) == "ALNK?") udp.Send(reply, reply.Length, from);
                }
                catch { if (!running) return; }
            }
        }
    }

    static class PairingCode
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public static string New()
        {
            byte[] r = new byte[8];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(r);
            var sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                if (i == 4) sb.Append('-');
                sb.Append(Alphabet[r[i] % Alphabet.Length]);
            }
            return sb.ToString();
        }

        public static string Normalize(string code)
        {
            var sb = new StringBuilder();
            foreach (char c in (code ?? "").ToUpperInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
    }

    // Encrypted, authenticated message channel. Both sides derive keys from the pairing code,
    // so someone on the same Wi-Fi can neither read keystrokes nor send fake input.
    class SecureChannel
    {
        public class AuthException : Exception
        {
            public AuthException() : base("pairing code doesn't match") { }
        }

        static readonly Dictionary<string, byte[]> masterCache = new Dictionary<string, byte[]>();
        static readonly byte[] Salt = Encoding.ASCII.GetBytes("AudioLink pairing v1");

        readonly Stream stream;
        readonly Aes sendAes, recvAes;
        readonly HMACSHA256 sendMac, recvMac;
        long sendSeq, recvSeq;
        readonly object sendLock = new object();

        SecureChannel(Stream s, byte[] sendEnc, byte[] sendMacKey, byte[] recvEnc, byte[] recvMacKey)
        {
            stream = s;
            sendAes = Aes.Create(); sendAes.Key = sendEnc;
            recvAes = Aes.Create(); recvAes.Key = recvEnc;
            sendMac = new HMACSHA256(sendMacKey);
            recvMac = new HMACSHA256(recvMacKey);
        }

        static byte[] Master(string code)
        {
            string norm = PairingCode.Normalize(code);
            lock (masterCache)
            {
                byte[] k;
                if (masterCache.TryGetValue(norm, out k)) return k;
                using (var kdf = new Rfc2898DeriveBytes(norm, Salt, 100000)) k = kdf.GetBytes(32);
                masterCache[norm] = k;
                return k;
            }
        }

        static byte[] Derive(byte[] master, string label, byte[] serverNonce, byte[] clientNonce)
        {
            using (var h = new HMACSHA256(master))
            {
                var ms = new MemoryStream();
                byte[] l = Encoding.ASCII.GetBytes(label);
                ms.Write(l, 0, l.Length);
                ms.Write(serverNonce, 0, serverNonce.Length);
                ms.Write(clientNonce, 0, clientNonce.Length);
                return h.ComputeHash(ms.ToArray());
            }
        }

        static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return b;
        }

        public static SecureChannel Handshake(Stream s, string code, bool isServer)
        {
            byte[] mine = RandomBytes(16), theirs = new byte[16];
            s.Write(mine, 0, 16);
            if (!Net.ReadExact(s, theirs, 16)) throw new IOException("connection closed during handshake");
            byte[] sn = isServer ? mine : theirs, cn = isServer ? theirs : mine;
            byte[] m = Master(code);
            byte[] s2cEnc = Derive(m, "s2c-enc", sn, cn), s2cMac = Derive(m, "s2c-mac", sn, cn);
            byte[] c2sEnc = Derive(m, "c2s-enc", sn, cn), c2sMac = Derive(m, "c2s-mac", sn, cn);
            return isServer
                ? new SecureChannel(s, s2cEnc, s2cMac, c2sEnc, c2sMac)
                : new SecureChannel(s, c2sEnc, c2sMac, s2cEnc, s2cMac);
        }

        public void Send(byte[] payload)
        {
            lock (sendLock)
            {
                sendSeq++;
                byte[] plain = new byte[8 + payload.Length];
                Array.Copy(BitConverter.GetBytes(sendSeq), plain, 8);
                Array.Copy(payload, 0, plain, 8, payload.Length);
                byte[] iv = RandomBytes(16);
                byte[] ct;
                using (var enc = sendAes.CreateEncryptor(sendAes.Key, iv)) ct = enc.TransformFinalBlock(plain, 0, plain.Length);

                byte[] frame = new byte[4 + 16 + ct.Length + 32];
                Array.Copy(BitConverter.GetBytes(16 + ct.Length + 32), frame, 4);
                Array.Copy(iv, 0, frame, 4, 16);
                Array.Copy(ct, 0, frame, 20, ct.Length);
                byte[] mac = sendMac.ComputeHash(frame, 4, 16 + ct.Length);
                Array.Copy(mac, 0, frame, 20 + ct.Length, 32);
                stream.Write(frame, 0, frame.Length);
            }
        }

        // Called from a single reader thread.
        public byte[] Receive()
        {
            byte[] lenBuf = new byte[4];
            if (!Net.ReadExact(stream, lenBuf, 4)) throw new IOException("connection closed");
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len < 16 + 16 + 32 || len > 64 * 1024 * 1024) throw new AuthException();
            byte[] body = new byte[len];
            if (!Net.ReadExact(stream, body, len)) throw new IOException("connection closed");

            byte[] mac = recvMac.ComputeHash(body, 0, len - 32);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= mac[i] ^ body[len - 32 + i];
            if (diff != 0) throw new AuthException();

            byte[] iv = new byte[16];
            Array.Copy(body, iv, 16);
            byte[] plain;
            using (var dec = recvAes.CreateDecryptor(recvAes.Key, iv)) plain = dec.TransformFinalBlock(body, 16, len - 16 - 32);
            long seq = BitConverter.ToInt64(plain, 0);
            if (seq != recvSeq + 1) throw new AuthException();
            recvSeq = seq;
            byte[] payload = new byte[plain.Length - 8];
            Array.Copy(plain, 8, payload, 0, payload.Length);
            return payload;
        }
    }
}
