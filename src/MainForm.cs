using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AudioLink
{
    class MainForm : Form
    {
        static readonly string[] SwitchValues = { "instant", "delay", "doubletap" };
        static readonly string[] SwitchLabels = { "as soon as the pointer reaches the edge", "after a short pause at the edge", "when you tap the edge twice" };
        const int W = 520; // content width

        readonly Settings settings = Settings.Load();
        readonly Mutex mutex;
        readonly uint showMsg = Native.RegisterWindowMessage(Program.ShowMessageName);
        readonly uint exitMsg = Native.RegisterWindowMessage(Program.ExitMessageName);
        bool startHidden, exiting, running, updatingUi, trayHintShown, startedOnce, optionsOpen;
        string role;

        Receiver audioRx;
        Sender audioTx;
        ControlServer ctlServer;
        ControlClient ctlClient;
        DiscoveryResponder discovery;
        MicServer micServer;
        MicClient micClient;
        string soundText = "not running", linkText = "not running", micText = "not running";

        string lastClipHash;
        DateTime ignoreClipUntil = DateTime.MinValue;
        readonly System.Windows.Forms.Timer clipTimer = new System.Windows.Forms.Timer { Interval = 150 };
        readonly System.Windows.Forms.Timer applyTimer = new System.Windows.Forms.Timer { Interval = 800 };
        float levelShown;

        StatusPill pill;
        ModeCard pcCard, laptopCard;
        Label hdrLayout, hdrConn, hdrShare, myAddr, volLabel, hint, firewallText;
        Panel layoutSec, connSec, shareSec, optionsSec, firewallSec;
        LayoutCanvas canvas;
        TextBox pcCodeBox, lapCodeBox;
        ComboBox hostBox, switchBox;
        Button findBtn, newCodeBtn, startBtn, firewallBtn;
        CheckBox cbSound, cbInput, cbClip, cbMic, cbAuto, cbCorners;
        TrackBar volume;
        LinkLabel optionsLink, cableLink;
        Label micInfo, micDeviceLabel;
        ComboBox micDeviceBox;
        StatusRow soundRow, linkRow, micRow;
        LevelBar level;
        NotifyIcon tray;
        ToolStripMenuItem trayStartStop;
        readonly ToolTip tips = new ToolTip();
        readonly Dictionary<Color, Icon> icons = new Dictionary<Color, Icon>();
        LogForm logForm;

        public MainForm(bool hidden, Mutex mutex)
        {
            this.mutex = mutex;
            startHidden = hidden;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "AudioLink";
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = TrayIcon(Theme.Accent, false);

            BuildUi();
            BuildTray();
            LoadSettingsIntoUi();
            UpdateRoleUi();
            UpdateStatusUi();

            clipTimer.Tick += delegate { clipTimer.Stop(); ReadLocalClipboard(); };
            applyTimer.Tick += delegate { applyTimer.Stop(); ApplyChanges(); };
            var meter = new System.Windows.Forms.Timer { Interval = 60 };
            meter.Tick += delegate
            {
                float l = audioRx != null ? audioRx.Level : audioTx != null ? audioTx.Level : 0;
                levelShown = Math.Max(l, levelShown * 0.8f);
                level.Value = levelShown;
                if (ctlServer != null) canvas.LaptopName = ctlServer.LaptopName;
            };
            meter.Start();
        }

        // ------------------------------------------------------------ layout

        static Panel Section(int height)
        {
            return new Panel { Size = new Size(W, height), Margin = new Padding(0, 0, 0, 12) };
        }

        static Label Header(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 8f), ForeColor = Theme.Muted, Margin = new Padding(0, 2, 0, 6) };
        }

        static Label Muted(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = Theme.Muted };
        }

        void BuildUi()
        {
            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20, 16, 20, 14), Margin = new Padding(0), BackColor = Color.White
            };
            Controls.Add(flow);

            // Title + overall status
            var header = Section(58);
            header.Controls.Add(new Label { Text = "AudioLink", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(0, 0) });
            header.Controls.Add(Muted("One keyboard, mouse, clipboard and pair of headphones for two computers", 2, 36));
            pill = new StatusPill { Location = new Point(W - 200, 4), Size = new Size(200, 28) };
            header.Controls.Add(pill);
            flow.Controls.Add(header);

            // What this computer is
            flow.Controls.Add(Header("THIS COMPUTER"));
            var roles = Section(78);
            pcCard = new ModeCard { Title = "Main PC", Description = "Shares its keyboard, mouse and headphones", Location = new Point(0, 0), Size = new Size(254, 78) };
            laptopCard = new ModeCard { Title = "Laptop", Description = "Uses the main PC's keyboard, mouse and headphones", LaptopIcon = true, Location = new Point(266, 0), Size = new Size(254, 78) };
            pcCard.Picked += delegate { SetRole("pc"); };
            laptopCard.Picked += delegate { SetRole("laptop"); };
            roles.Controls.AddRange(new Control[] { pcCard, laptopCard });
            flow.Controls.Add(roles);

            // Main PC: screen layout + pairing code
            hdrLayout = Header("SCREEN LAYOUT   ·   drag the laptop to where it sits on your desk");
            flow.Controls.Add(hdrLayout);
            layoutSec = Section(190);
            canvas = new LayoutCanvas { Location = new Point(0, 0), Size = new Size(W, 150) };
            canvas.EdgeChanged += delegate { UpdateHint(); SettingChanged(); };
            myAddr = Muted("", 0, 164);
            pcCodeBox = new TextBox { Location = new Point(338, 160), Size = new Size(104, 24), CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 10.5f), TextAlign = HorizontalAlignment.Center };
            pcCodeBox.TextChanged += delegate { SettingChanged(); };
            newCodeBtn = Theme.SecondaryButton("New");
            newCodeBtn.SetBounds(448, 158, 72, 28);
            newCodeBtn.Click += delegate { pcCodeBox.Text = PairingCode.New(); };
            tips.SetToolTip(newCodeBtn, "Make a new pairing code (the laptop will need the new one)");
            layoutSec.Controls.AddRange(new Control[] { canvas, myAddr, Muted("Pairing code", 252, 164), pcCodeBox, newCodeBtn });
            flow.Controls.Add(layoutSec);

            // Laptop: where is the main PC
            hdrConn = Header("CONNECT TO THE MAIN PC");
            flow.Controls.Add(hdrConn);
            connSec = Section(70);
            hostBox = new ComboBox { Location = new Point(100, 2), Size = new Size(300, 24) };
            hostBox.TextChanged += delegate { SettingChanged(); };
            findBtn = Theme.SecondaryButton("Find PC");
            findBtn.SetBounds(410, 0, 110, 28);
            findBtn.Click += delegate { FindPc(); };
            lapCodeBox = new TextBox { Location = new Point(100, 38), Size = new Size(120, 24), CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 10.5f), TextAlign = HorizontalAlignment.Center };
            lapCodeBox.TextChanged += delegate { SettingChanged(); };
            connSec.Controls.AddRange(new Control[] { Muted("Address", 0, 6), hostBox, findBtn, Muted("Pairing code", 0, 42), lapCodeBox, Muted("shown on the main PC", 228, 42) });
            flow.Controls.Add(connSec);

            // What to share
            hdrShare = Header("SHARE");
            flow.Controls.Add(hdrShare);
            shareSec = Section(112);
            cbSound = new CheckBox { Location = new Point(0, 0), AutoSize = true };
            cbInput = new CheckBox { Location = new Point(0, 26), AutoSize = true };
            cbClip = new CheckBox { Text = "Clipboard  -  copy on one computer, paste on the other (text and pictures)", Location = new Point(0, 52), AutoSize = true };
            cbMic = new CheckBox { Location = new Point(0, 78), AutoSize = true };
            foreach (var cb in new[] { cbSound, cbInput, cbClip, cbMic }) cb.CheckedChanged += delegate { SettingChanged(); };

            // Main PC: which microphone is shared
            micInfo = Muted("", 18, 102);

            // Laptop: where the microphone is played so apps can record it
            micDeviceLabel = Muted("Play it into", 18, 108);
            micDeviceBox = new ComboBox { Location = new Point(110, 104), Size = new Size(260, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            micDeviceBox.SelectedIndexChanged += delegate { SettingChanged(); };
            cableLink = new LinkLabel { Text = "Get VB-Cable (free)", AutoSize = true, Location = new Point(380, 108), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline };
            cableLink.LinkClicked += delegate { try { Process.Start("https://vb-audio.com/Cable/"); } catch { } };
            tips.SetToolTip(cableLink, "A free virtual microphone for Windows. Once it's installed, AudioLink picks it automatically.");
            micDeviceBox.DropDown += delegate { RefreshMicDevices(); };
            Activated += delegate { if (role == "laptop") RefreshMicDevices(); };

            volLabel = Muted("Laptop volume", 18, 132);
            volume = new TrackBar { Minimum = 0, Maximum = 150, TickStyle = TickStyle.None, Location = new Point(120, 126), Size = new Size(260, 30) };
            volume.ValueChanged += delegate
            {
                if (updatingUi) return;
                settings.Volume = volume.Value;
                if (audioRx != null) audioRx.Volume = volume.Value / 100f;
                settings.Save();
            };
            shareSec.Controls.AddRange(new Control[] { cbSound, cbInput, cbClip, cbMic, micInfo, micDeviceLabel, micDeviceBox, cableLink, volLabel, volume });
            flow.Controls.Add(shareSec);

            // Switching options (main PC)
            optionsLink = new LinkLabel { AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 0, 8) };
            optionsLink.LinkClicked += delegate { optionsOpen = !optionsOpen; UpdateRoleUi(); };
            flow.Controls.Add(optionsLink);
            optionsSec = Section(62);
            switchBox = new ComboBox { Location = new Point(160, 2), Size = new Size(300, 24), DropDownStyle = ComboBoxStyle.DropDownList };
            switchBox.Items.AddRange(SwitchLabels);
            switchBox.SelectedIndexChanged += delegate { UpdateHint(); SettingChanged(); };
            cbCorners = new CheckBox { Text = "Ignore the screen corners (keeps Close buttons and the Start menu easy to hit)", Location = new Point(0, 36), AutoSize = true };
            cbCorners.CheckedChanged += delegate { SettingChanged(); };
            optionsSec.Controls.AddRange(new Control[] { Muted("Go to the laptop", 0, 6), switchBox, cbCorners });
            flow.Controls.Add(optionsSec);

            // Firewall warning (main PC, only when needed)
            firewallSec = new Card { Size = new Size(W, 44), Margin = new Padding(0, 0, 0, 12), Fill = Theme.WarnBack };
            firewallText = new Label { Text = "Windows Firewall is stopping the laptop from connecting.", AutoSize = true, Location = new Point(12, 14), ForeColor = Theme.Text, BackColor = Theme.WarnBack };
            firewallBtn = Theme.PrimaryButton("Allow");
            firewallBtn.SetBounds(W - 98, 8, 86, 28);
            firewallBtn.Click += delegate { AllowThroughFirewall(); };
            firewallSec.Controls.AddRange(new Control[] { firewallText, firewallBtn });
            flow.Controls.Add(firewallSec);

            // Start / autostart / log
            var actions = Section(40);
            startBtn = Theme.PrimaryButton("Start");
            startBtn.SetBounds(0, 0, 140, 40);
            startBtn.Click += delegate { StartStopClicked(); };
            cbAuto = new CheckBox { Text = "Start with Windows", AutoSize = true, Location = new Point(158, 11) };
            cbAuto.CheckedChanged += delegate { if (!updatingUi) ToggleAutoStart(cbAuto.Checked); };
            var logLink = new LinkLabel { Text = "View log", AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline };
            logLink.Location = new Point(W - 60, 12);
            logLink.LinkClicked += delegate { ShowLog(); };
            actions.Controls.AddRange(new Control[] { startBtn, cbAuto, logLink });
            flow.Controls.Add(actions);

            // Live status
            var status = new Card { Size = new Size(W, 108), Margin = new Padding(0, 0, 0, 10) };
            soundRow = new StatusRow { Caption = "Sound", Location = new Point(14, 10), Size = new Size(W - 28, 22) };
            linkRow = new StatusRow { Caption = "Keyboard & mouse", Location = new Point(14, 34), Size = new Size(W - 28, 22) };
            micRow = new StatusRow { Caption = "Microphone", Location = new Point(14, 58), Size = new Size(W - 28, 22) };
            level = new LevelBar { Location = new Point(14, 90), Size = new Size(W - 28, 6) };
            tips.SetToolTip(level, "Sound level");
            status.Controls.AddRange(new Control[] { soundRow, linkRow, micRow, level });
            flow.Controls.Add(status);

            hint = new Label { Size = new Size(W, 34), ForeColor = Theme.Muted, Margin = new Padding(0) };
            flow.Controls.Add(hint);
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open AudioLink", null, delegate { ShowFromTray(); });
            trayStartStop = new ToolStripMenuItem("Stop", null, delegate { StartStopClicked(); });
            menu.Items.Add(trayStartStop);
            menu.Items.Add("View log", null, delegate { ShowLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = TrayIcon(Theme.Grey, true), Text = "AudioLink", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        // App icon, with a coloured status dot for the tray.
        Icon TrayIcon(Color dot, bool withDot)
        {
            Icon ic;
            if (withDot && icons.TryGetValue(dot, out ic)) return ic;
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(Color.White, 2.5f))
                    {
                        g.DrawRectangle(p, 6, 9, 11, 8);
                        g.DrawRectangle(p, 15, 15, 11, 8);
                    }
                    if (withDot)
                    {
                        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 18, 18, 14, 14);
                        using (var b = new SolidBrush(dot)) g.FillEllipse(b, 20, 20, 10, 10);
                    }
                }
                ic = Icon.FromHandle(bmp.GetHicon());
            }
            if (withDot) icons[dot] = ic;
            return ic;
        }

        // ------------------------------------------------------------ settings <-> UI

        void LoadSettingsIntoUi()
        {
            updatingUi = true;
            role = settings.Role;
            pcCard.Selected = role == "pc";
            laptopCard.Selected = role == "laptop";
            hostBox.Text = settings.PcAddress;
            pcCodeBox.Text = lapCodeBox.Text = settings.PairCode;
            cbSound.Checked = settings.Sound;
            cbInput.Checked = settings.Input;
            cbClip.Checked = settings.Clipboard;
            cbMic.Checked = settings.Mic;
            cbCorners.Checked = settings.Corners;
            canvas.Edge = settings.Edge;
            switchBox.SelectedIndex = Math.Max(0, Array.IndexOf(SwitchValues, settings.SwitchMode));
            volume.Value = Math.Max(0, Math.Min(150, settings.Volume));
            cbAuto.Checked = AutoStart.IsEnabled;
            updatingUi = false;
            RefreshMicDevices();
        }

        // Laptop: list playback devices for the microphone, preferring the saved one, then VB-Cable.
        void RefreshMicDevices()
        {
            List<Devices.Info> outs;
            try { outs = Devices.List(0); } catch { return; }
            var current = micDeviceBox.SelectedItem as Devices.Info;
            string want = current != null ? current.Id : settings.MicDevice;
            bool same = outs.Count == micDeviceBox.Items.Count;
            for (int i = 0; same && i < outs.Count; i++) same = ((Devices.Info)micDeviceBox.Items[i]).Id == outs[i].Id;
            if (same && current != null) return;

            bool was = updatingUi;
            updatingUi = true;
            micDeviceBox.Items.Clear();
            foreach (var d in outs) micDeviceBox.Items.Add(d);
            Devices.Info pick = outs.Find(d => d.Id == want) ?? Devices.FindVirtualCable(outs);
            micDeviceBox.SelectedItem = pick;
            updatingUi = was;
            cableLink.Visible = Devices.FindVirtualCable(outs) == null;
            if (pick != null && pick.Id != settings.MicDevice) SettingChanged();
        }

        void ReadUiIntoSettings()
        {
            settings.Role = role ?? "";
            string host = hostBox.Text.Trim();
            int sp = host.IndexOf(' ');
            settings.PcAddress = sp > 0 ? host.Substring(0, sp) : host;   // "192.168.1.5  (DESKTOP)" -> IP
            settings.PairCode = (role == "laptop" ? lapCodeBox : pcCodeBox).Text.Trim();
            settings.Sound = cbSound.Checked;
            settings.Input = cbInput.Checked;
            settings.Clipboard = cbClip.Checked;
            settings.Mic = cbMic.Checked;
            var micDev = micDeviceBox.SelectedItem as Devices.Info;
            if (micDev != null) settings.MicDevice = micDev.Id;
            settings.Edge = canvas.Edge;
            settings.SwitchMode = SwitchValues[Math.Max(0, switchBox.SelectedIndex)];
            settings.Corners = cbCorners.Checked;
            settings.Volume = volume.Value;
        }

        void SetRole(string r)
        {
            if (role == r) return;
            role = r;
            pcCard.Selected = r == "pc";
            laptopCard.Selected = r == "laptop";
            updatingUi = true;
            if (r == "pc") pcCodeBox.Text = lapCodeBox.Text; else lapCodeBox.Text = pcCodeBox.Text;
            updatingUi = false;
            UpdateRoleUi();
            SettingChanged();
        }

        void UpdateRoleUi()
        {
            SuspendLayout();
            bool chosen = role == "pc" || role == "laptop", pc = role == "pc";
            hdrLayout.Visible = layoutSec.Visible = pc;
            hdrConn.Visible = connSec.Visible = role == "laptop";
            hdrShare.Visible = shareSec.Visible = chosen;
            optionsLink.Visible = pc;
            optionsLink.Text = optionsOpen ? "Fewer options ▾" : "More options ▸";
            optionsSec.Visible = pc && optionsOpen;
            startBtn.Enabled = chosen || running;

            myAddr.Text = "This PC   " + string.Join(",  ", Net.LocalAddresses());
            if (pc && pcCodeBox.Text.Trim().Length == 0)
            {
                updatingUi = true;
                pcCodeBox.Text = PairingCode.New();
                updatingUi = false;
            }
            cbSound.Text = pc ? "Sound  -  play the laptop's sound in these headphones" : "Sound  -  send this laptop's sound to the main PC";
            cbInput.Text = pc ? "Keyboard and mouse  -  move off the screen edge to use the laptop" : "Keyboard and mouse  -  let the main PC control this laptop";
            cbMic.Text = pc ? "Microphone  -  let the laptop use this PC's microphone" : "Microphone  -  use the main PC's microphone on this laptop";
            micInfo.Visible = pc;
            micInfo.Text = "Shares " + (Devices.DefaultName(1) ?? "the default microphone") + "  (your default microphone in Windows)";
            micDeviceLabel.Visible = micDeviceBox.Visible = role == "laptop";
            cableLink.Visible = role == "laptop" && Devices.FindVirtualCable(SafeOutputs()) == null;
            volLabel.Visible = volume.Visible = pc;
            shareSec.Height = pc ? 158 : 134;
            UpdateFirewallUi();
            UpdateHint();
            ResumeLayout(true);
        }

        static List<Devices.Info> SafeOutputs()
        {
            try { return Devices.List(0); } catch { return new List<Devices.Info>(); }
        }

        void UpdateHint()
        {
            if (role == "laptop") { hint.Text = "Tip: turn this laptop's own volume down so it doesn't also play out loud."; return; }
            if (role != "pc") { hint.Text = "Choose what this computer is to get started. Run AudioLink on both computers."; return; }
            string e = canvas.Edge == "Top" ? "top" : canvas.Edge == "Bottom" ? "bottom" : canvas.Edge == "Left" ? "left" : "right";
            string how = switchBox.SelectedIndex == 1 ? "Push the pointer against the " + e + " edge for a moment"
                       : switchBox.SelectedIndex == 2 ? "Tap the pointer against the " + e + " edge twice"
                       : "Move the pointer off the " + e + " edge";
            hint.Text = how + " to use the laptop, and back the same way to return. Scroll Lock jumps between the two.";
        }

        // Any setting changed: save it, and if running, restart with the new settings shortly after.
        void SettingChanged()
        {
            if (updatingUi || role == null || role == "") return;
            applyTimer.Stop();
            applyTimer.Start();
        }

        void ApplyChanges()
        {
            ReadUiIntoSettings();
            settings.Save();
            if (!running) return;
            StopServices();
            string problem = Problem();
            if (problem == null) StartServices();
            else { linkRow.Set(problem, Theme.Red); pill.Set("Needs attention", Theme.Red); }
        }

        string Problem()
        {
            if (settings.Role != "pc" && settings.Role != "laptop") return "Choose whether this computer is the main PC or the laptop.";
            if (settings.Role == "laptop" && settings.PcAddress.Length == 0) return "Enter the main PC's address, or press Find PC.";
            if ((settings.Input || settings.Clipboard || settings.Mic) && PairingCode.Normalize(settings.PairCode).Length < 6)
                return settings.Role == "pc" ? "Press New to make a pairing code." : "Enter the pairing code shown on the main PC.";
            return null;
        }

        // ------------------------------------------------------------ status

        void SetSound(string s) { Report(0, s); }
        void SetLink(string s) { Report(1, s); }
        void SetMic(string s) { Report(2, s); }

        void Report(int which, string s)
        {
            Net.Log(new[] { "Sound: ", "Link: ", "Microphone: " }[which] + s);
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!running) return;
                    if (which == 0) soundText = s; else if (which == 1) linkText = s; else micText = s;
                    UpdateStatusUi();
                }));
            }
            catch { }
        }

        void UpdateStatusUi()
        {
            if (!running) { soundText = "not running"; linkText = "not running"; micText = "not running"; }
            Color sc = Theme.Classify(soundText), lc = Theme.Classify(linkText), mc = Theme.Classify(micText);
            soundRow.Set(Capitalise(soundText), sc);
            linkRow.Set(Capitalise(linkText), lc);
            micRow.Set(Capitalise(micText), mc);
            tips.SetToolTip(soundRow, Capitalise(soundText));
            tips.SetToolTip(linkRow, Capitalise(linkText));
            tips.SetToolTip(micRow, Capitalise(micText));

            string other = role == "laptop" ? "main PC" : "laptop";
            string pillText;
            Color pc;
            if (!running) { pillText = "Stopped"; pc = Theme.Grey; }
            else if (linkText.StartsWith("controlling")) { pillText = "Controlling the laptop"; pc = Theme.Accent; }
            else if (sc == Theme.Green || lc == Theme.Green || mc == Theme.Green) { pillText = "Connected"; pc = Theme.Green; }
            else if (sc == Theme.Red || lc == Theme.Red || mc == Theme.Red) { pillText = "Needs attention"; pc = Theme.Red; }
            else { pillText = role == "laptop" ? "Connecting to the main PC" : "Waiting for the " + other; pc = Theme.Amber; }
            pill.Set(pillText, pc);

            startBtn.Text = running ? "Stop" : "Start";
            trayStartStop.Text = running ? "Stop" : "Start";
            string t = "AudioLink - " + pillText;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            tray.Icon = TrayIcon(pc, true);
        }

        static string Capitalise(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        void ShowLog()
        {
            if (logForm == null || logForm.IsDisposed) logForm = new LogForm(Icon);
            if (Visible) logForm.Show(this); else logForm.Show();
            logForm.Activate();
        }

        // ------------------------------------------------------------ start / stop

        void StartStopClicked()
        {
            applyTimer.Stop();
            if (running)
            {
                StopServices();
                settings.Enabled = false;
                settings.Save();
                return;
            }
            ReadUiIntoSettings();
            string problem = Problem();
            if (problem != null)
            {
                ShowFromTray();
                MessageBox.Show(this, problem, "AudioLink");
                return;
            }
            settings.Enabled = true;
            settings.Save();
            StartServices();
        }

        void StartServices()
        {
            if (running) return;
            running = true;
            soundText = linkText = micText = "starting ...";
            var s = settings;
            try
            {
                if (s.Role == "pc")
                {
                    discovery = new DiscoveryResponder();
                    discovery.Start();
                    if (s.Sound)
                    {
                        audioRx = new Receiver(SetSound);
                        audioRx.Volume = s.Volume / 100f;
                        audioRx.Start();
                    }
                    else soundText = "off";
                    if (s.Input || s.Clipboard)
                    {
                        ctlServer = new ControlServer(s.PairCode, s.Input, s.Clipboard, s.Edge, s.Corners, s.SwitchMode, SetLink);
                        ctlServer.ClipboardReceived = ApplyRemoteClipboard;
                        ctlServer.Start();
                    }
                    else linkText = "off";
                    if (s.Mic)
                    {
                        micServer = new MicServer(s.PairCode, SetMic);
                        micServer.Start();
                    }
                    else micText = "off";
                }
                else
                {
                    if (s.Sound)
                    {
                        audioTx = new Sender(SetSound);
                        audioTx.Start(s.PcAddress);
                    }
                    else soundText = "off";
                    if (s.Input || s.Clipboard)
                    {
                        ctlClient = new ControlClient(s.PcAddress, s.PairCode, s.Input, s.Clipboard, SetLink);
                        ctlClient.ClipboardReceived = ApplyRemoteClipboard;
                        ctlClient.Start();
                    }
                    else linkText = "off";
                    if (s.Mic)
                    {
                        var outs = SafeOutputs();
                        var dev = outs.Find(d => d.Id == s.MicDevice);
                        if (dev == null)
                            micText = Devices.FindVirtualCable(outs) == null
                                ? "install VB-Cable (free, link above) so apps on this laptop can use the PC's microphone"
                                : "choose where to play the microphone (CABLE Input)";
                        else
                        {
                            micClient = new MicClient(s.PcAddress, s.PairCode, dev.Id, dev.Name, SetMic);
                            micClient.Start();
                        }
                    }
                    else micText = "off";
                }
            }
            catch (Exception ex)
            {
                StopServices();
                Net.Log("Could not start: " + ex.Message);
                MessageBox.Show(this, "AudioLink could not start: " + ex.Message + "\n\nIs another copy of AudioLink already running?", "AudioLink");
                return;
            }
            UpdateStatusUi();
        }

        void StopServices()
        {
            if (audioRx != null) try { audioRx.Stop(); } catch { }
            if (audioTx != null) try { audioTx.Stop(); } catch { }
            if (ctlServer != null) try { ctlServer.Stop(); } catch { }
            if (ctlClient != null) try { ctlClient.Stop(); } catch { }
            if (discovery != null) try { discovery.Stop(); } catch { }
            if (micServer != null) try { micServer.Stop(); } catch { }
            if (micClient != null) try { micClient.Stop(); } catch { }
            audioRx = null; audioTx = null; ctlServer = null; ctlClient = null; discovery = null; micServer = null; micClient = null;
            running = false;
            canvas.LaptopName = null;
            UpdateStatusUi();
        }

        void FindPc()
        {
            findBtn.Enabled = false;
            findBtn.Text = "Looking ...";
            new Thread(() =>
            {
                List<string> found;
                try { found = Net.FindMainPcs(); } catch { found = new List<string>(); }
                BeginInvoke((Action)(() =>
                {
                    findBtn.Enabled = true;
                    findBtn.Text = "Find PC";
                    hostBox.Items.Clear();
                    foreach (var f in found) hostBox.Items.Add(f);
                    if (found.Count > 0) hostBox.SelectedIndex = 0;
                    else MessageBox.Show(this, "No main PC found.\n\nMake sure AudioLink is running on the main PC, or type its address (shown under the screen layout on the PC).", "AudioLink");
                }));
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------ firewall (main PC only)

        const string FirewallRuleName = "AudioLink Main PC";
        bool? firewallOk;

        static bool FirewallRuleExists()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRuleName + "\" verbose")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0 && output.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return true; } // can't tell; don't nag
        }

        void UpdateFirewallUi()
        {
            if (role != "pc") { firewallSec.Visible = false; return; }
            if (firewallOk == null) firewallOk = FirewallRuleExists();
            firewallSec.Visible = !firewallOk.Value;
        }

        // The laptop has to be able to reach this PC. Clears any old "block" rules for this program and
        // allows it only from devices on the same home network. Windows asks the user to approve (UAC).
        void AllowThroughFirewall()
        {
            string exe = Application.ExecutablePath;
            string cmd = "/c netsh advfirewall firewall delete rule name=all program=\"" + exe + "\" & " +
                         "netsh advfirewall firewall delete rule name=\"" + FirewallRuleName + "\" & " +
                         "netsh advfirewall firewall add rule name=\"" + FirewallRuleName + "\" dir=in action=allow " +
                         "program=\"" + exe + "\" enable=yes profile=any remoteip=localsubnet";
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(15000);
            }
            catch (System.ComponentModel.Win32Exception) { return; } // user said no to the Windows prompt
            firewallOk = FirewallRuleExists();
            UpdateRoleUi();
            if (!firewallOk.Value) MessageBox.Show(this, "The firewall rule could not be added.", "AudioLink");
        }

        // ------------------------------------------------------------ start with Windows

        void ToggleAutoStart(bool on)
        {
            try
            {
                if (!on) { AutoStart.Disable(); return; }
                AutoStart.Enable();
                if (AutoStart.RunningFromInstall) return;

                AutoStart.Install();
                if (role == "pc" || role == "laptop") ReadUiIntoSettings();
                settings.Enabled = running;
                settings.Save();
                MessageBox.Show(this,
                    "AudioLink is now installed on this computer and will start with Windows.\n\n" +
                    "It will now reopen from its installed location. If Windows asks about the firewall, tick Private networks and click Allow.",
                    "AudioLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RelaunchInstalled();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not set up automatic start: " + ex.Message, "AudioLink");
                updatingUi = true;
                cbAuto.Checked = AutoStart.IsEnabled;
                updatingUi = false;
            }
        }

        void RelaunchInstalled()
        {
            StopServices();
            exiting = true;
            tray.Visible = false;
            mutex.ReleaseMutex();
            Process.Start(AutoStart.InstallPath);
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Opened a newer download while an installed copy exists: offer to update it.
            if (AutoStart.IsEnabled && !AutoStart.RunningFromInstall && File.Exists(AutoStart.InstallPath))
            {
                var r = MessageBox.Show(this, "AudioLink is already installed on this computer.\n\nReplace the installed copy with this one?",
                    "AudioLink", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                {
                    try { AutoStart.Install(); RelaunchInstalled(); }
                    catch (Exception ex) { MessageBox.Show(this, "Could not update: " + ex.Message, "AudioLink"); }
                }
            }
        }

        // ------------------------------------------------------------ clipboard

        static string ClipHash(byte type, byte[] data)
        {
            using (var sha = SHA1.Create()) return type + ":" + Convert.ToBase64String(sha.ComputeHash(data));
        }

        void ReadLocalClipboard()
        {
            if (!running || !settings.Clipboard || DateTime.UtcNow < ignoreClipUntil) return;
            if (ctlServer == null && ctlClient == null) return;
            byte type;
            byte[] data;
            try
            {
                if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    if (string.IsNullOrEmpty(t)) return;
                    type = Proto.ClipText;
                    data = Encoding.UTF8.GetBytes(t);
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    {
                        if (img == null) return;
                        using (var ms = new MemoryStream()) { img.Save(ms, ImageFormat.Png); data = ms.ToArray(); }
                    }
                    type = Proto.ClipImage;
                }
                else return;
            }
            catch (Exception ex) { Net.Log("Clipboard read failed: " + ex.Message); return; }

            if (data.Length > 32 * 1024 * 1024) return;
            string h = ClipHash(type, data);
            if (h == lastClipHash) return;
            lastClipHash = h;
            if (ctlServer != null) ctlServer.SendClipboard(type, data);
            else if (ctlClient != null) ctlClient.SendClipboard(type, data);
        }

        // Called from a network thread.
        void ApplyRemoteClipboard(byte type, byte[] data)
        {
            BeginInvoke((Action)(() =>
            {
                lastClipHash = ClipHash(type, data);
                ignoreClipUntil = DateTime.UtcNow.AddMilliseconds(800); // don't bounce it straight back
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        if (type == Proto.ClipText)
                        {
                            string t = Encoding.UTF8.GetString(data);
                            if (t.Length > 0) Clipboard.SetText(t);
                        }
                        else
                        {
                            using (var ms = new MemoryStream(data))
                            using (var img = Image.FromStream(ms)) Clipboard.SetImage(img);
                        }
                        break;
                    }
                    catch (ExternalException) { Thread.Sleep(60); }   // clipboard briefly busy
                    catch (Exception ex) { Net.Log("Clipboard write failed: " + ex.Message); break; }
                }
            }));
        }

        // ------------------------------------------------------------ window / tray behaviour

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                // Started with Windows: stay in the tray, but still create the window so everything works.
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.AddClipboardFormatListener(Handle);
            if (!startedOnce)
            {
                startedOnce = true;
                if (settings.Enabled && Problem() == null) BeginInvoke((Action)StartServices);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.RemoveClipboardFormatListener(Handle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE) { clipTimer.Stop(); clipTimer.Start(); }
            else if (showMsg != 0 && m.Msg == (int)showMsg) ShowFromTray();
            else if (exitMsg != 0 && m.Msg == (int)exitMsg) { exiting = true; BeginInvoke((Action)Close); }  // a newer version is taking over
            base.WndProc(ref m);
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && running && e.CloseReason == CloseReason.UserClosing)
            {
                // Keep working in the background.
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "AudioLink is still running", "It keeps working from the system tray. Right-click the tray icon to quit.", ToolTipIcon.Info);
                }
                return;
            }
            applyTimer.Stop();
            StopServices();
            tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
