using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioLink
{
    // Saved in %APPDATA%\AudioLink\settings.ini
    class Settings
    {
        public string Role = "";          // "pc" or "laptop"; empty until first set up
        public string PcAddress = "";
        public string PairCode = "";
        public bool Sound = true, Input = true, Clipboard = true;
        public string Edge = "Right";     // where the laptop sits relative to the PC screen
        public int Volume = 100;
        public bool Corners = true;       // don't switch to the laptop from the screen corners
        public string SwitchMode = "instant";  // instant | delay | doubletap
        public bool Mic;                  // share the PC microphone with the laptop (off until asked for)
        public string MicDevice = "";     // laptop: playback device the microphone is played into (VB-Cable)
        public bool Enabled;              // running (started on launch / at Windows start)

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioLink"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string v;
                if (d.TryGetValue("Role", out v)) s.Role = v;
                if (d.TryGetValue("PcAddress", out v)) s.PcAddress = v;
                if (d.TryGetValue("PairCode", out v)) s.PairCode = v;
                if (d.TryGetValue("Sound", out v)) s.Sound = v == "1";
                if (d.TryGetValue("Input", out v)) s.Input = v == "1";
                if (d.TryGetValue("Clipboard", out v)) s.Clipboard = v == "1";
                if (d.TryGetValue("Edge", out v)) s.Edge = v;
                if (d.TryGetValue("Volume", out v)) int.TryParse(v, out s.Volume);
                if (d.TryGetValue("Corners", out v)) s.Corners = v == "1";
                if (d.TryGetValue("SwitchMode", out v)) s.SwitchMode = v;
                if (d.TryGetValue("Mic", out v)) s.Mic = v == "1";
                if (d.TryGetValue("MicDevice", out v)) s.MicDevice = v;
                if (d.TryGetValue("Enabled", out v)) s.Enabled = v == "1";
            }
            catch (Exception ex) { Net.Log("Could not read settings: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "Role=" + Role, "PcAddress=" + PcAddress, "PairCode=" + PairCode,
                    "Sound=" + (Sound ? 1 : 0), "Input=" + (Input ? 1 : 0), "Clipboard=" + (Clipboard ? 1 : 0),
                    "Edge=" + Edge, "Volume=" + Volume, "Enabled=" + (Enabled ? 1 : 0),
                    "Corners=" + (Corners ? 1 : 0), "SwitchMode=" + SwitchMode, "Mic=" + (Mic ? 1 : 0), "MicDevice=" + MicDevice
                });
            }
            catch (Exception ex) { Net.Log("Could not save settings: " + ex.Message); }
        }
    }

    // "Start with Windows": a copy lives in %LOCALAPPDATA%\Programs\AudioLink and is listed under the user's Run key.
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "AudioLink";

        public static string InstallPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AudioLink", "AudioLink.exe"); }
        }

        public static bool RunningFromInstall
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase); }
        }

        public static bool IsEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(ValueName) != null;
            }
        }

        public static void Install()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstallPath));
            File.Copy(Application.ExecutablePath, InstallPath, true);
        }

        public static void Enable()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(ValueName, "\"" + InstallPath + "\" --autostart");
        }

        public static void Disable()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(ValueName, false);
        }
    }

    static class Program
    {
        public const string ShowMessageName = "AudioLink-Show";
        public const string ExitMessageName = "AudioLink-Exit";

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool first;
            var mutex = new Mutex(true, "AudioLink-single-instance", out first);
            if (!first)
            {
                if (!AutoStart.RunningFromInstall && File.Exists(AutoStart.InstallPath) &&
                    MessageBox.Show("AudioLink is already running on this computer.\n\nReplace it with this version?", "AudioLink",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ReplaceRunningCopy(mutex);
                }
                else
                {
                    // Bring the running copy's window up instead.
                    Native.PostMessage((IntPtr)0xFFFF, Native.RegisterWindowMessage(ShowMessageName), IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }

            bool hidden = Array.Exists(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(hidden, mutex));
            GC.KeepAlive(mutex);
        }

        // Close the running AudioLink, copy this version over the installed one, and start it.
        static void ReplaceRunningCopy(Mutex mutex)
        {
            Native.PostMessage((IntPtr)0xFFFF, Native.RegisterWindowMessage(ExitMessageName), IntPtr.Zero, IntPtr.Zero);
            bool owned = false;
            try { owned = mutex.WaitOne(4000); } catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                // Older versions don't understand the polite request.
                int me = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName("AudioLink"))
                    if (p.Id != me) try { p.Kill(); p.WaitForExit(3000); } catch { }
                try { owned = mutex.WaitOne(4000); } catch (AbandonedMutexException) { owned = true; }
            }
            if (!owned)
            {
                MessageBox.Show("Couldn't close the running AudioLink. Right-click its tray icon, choose Exit, then try again.", "AudioLink");
                return;
            }
            Exception last = null;
            for (int i = 0; i < 15; i++)
            {
                try { AutoStart.Install(); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(300); }   // file still in use for a moment
            }
            mutex.ReleaseMutex();
            if (last != null)
            {
                MessageBox.Show("Couldn't update AudioLink: " + last.Message, "AudioLink");
                return;
            }
            Process.Start(AutoStart.InstallPath);
        }
    }
}
