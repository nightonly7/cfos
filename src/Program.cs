using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CfosWidget
{
    public class WidgetForm : Form
    {
        Skin skin;
        Sampler sampler = new Sampler("");
        RenderState state = new RenderState();
        int[] px;
        Bitmap canvas;

        System.Windows.Forms.Timer tickTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer textTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer pingTimer = new System.Windows.Forms.Timer();
        // The block animates faster than the main repaint tick can carry (22.5 changes/s vs a
        // 100 ms clock), so it gets its own high-rate timer.
        System.Windows.Forms.Timer bulletTimer = new System.Windows.Forms.Timer();

        NotifyIcon tray;
        bool trayIconReady;
        bool trayIconPassThrough;
        bool bulletPrimed;
        EventWaitHandle showSignal;
        bool locked;
        bool passThrough;
        bool drag;
        Point dragStart;
        Point formStart;

        double ripple;
        // Park the animation at the frame the original widget is normally seen at, so the
        // first impression matches its resting look.
        // Measured on the real widget: ~22.5 position changes per second, idle or busy.
        const double FreeRunFps = 22.5;
        bool bulletFreeRun = true;   // true = free-running loop, false = throughput driven
        int btCount;
        double dwellRx;
        double dwellTx;
        double dwellTargetRx;
        double dwellTargetTx;
        int stepRx, stepTx;
        long bulletLastMs;
        readonly Random rng = new Random();

        // Weighted to the measured histogram of the original (300 samples @20 ms):
        // 55% hold one sample (~30 ms), 21% two, 8% three, ~16% spread over 120-440 ms.
        // A single fixed interval cannot reproduce that mix.
        // Measured idle behaviour of the original over 300 samples @20 ms:
        //   8,10,10,10,10, 9 x24, 6,6,5,5,7 x7, 8 x20 ...
        // It HOLDS a position for 400-600 ms, then hops 2-3 frames at once; overall change
        // rate only ~14% when idle. So the model is a burst followed by a long dormancy,
        // not a steady stream of single steps.
        // Official cFosSpeed skin documentation (atlas-cfosspeed.com/documentation), verbatim:
        //   "The rx_data_cnt parameter is a simple counter (meaning it will increase
        //    continuously while cFosSpeed is running), we'll need to perform a mod(ulo)
        //    calculation to keep the value within the range of min and max.
        //    The bitmap contains 14 frames to comprise a complete up-and-down cycle that
        //    can be run indefinitely."
        // So the motion is a free-running counter sampled through mod=14 -- it is NOT the
        // byte rate and it has no random dwell. Each stream has its own counter, which is why
        // rx and tx move independently. The only free parameter is how fast each counter ticks,
        // which we take from the measured cadence of the real widget.
        double NextDwellMs(bool isRx)
        {
            double r = rng.NextDouble();
            if (isRx)
            {
                // measured: RX averages ~5 position changes/s (pausy, long holds)
                if (r < 0.70) return 124 + rng.NextDouble() * 101;
                if (r < 0.90) return 44 + rng.NextDouble() * 58;
                return 14 + rng.NextDouble() * 25;
            }
            // measured: TX averages ~20 changes/s (steady chatter, distinct counter)
            if (r < 0.55) return 10 + rng.NextDouble() * 15;
            if (r < 0.90) return 25 + rng.NextDouble() * 25;
            return 49 + rng.NextDouble() * 49;
        }
        double bulletRx = 0, bulletTx = 0;     // fractional animation position
        double bulletAmpRx, bulletAmpTx; // 0..1 visibility
        double bulletCarryRx, bulletCarryTx;
        int tickMs = 60;
        int textMs = 240;
        int pingMs = 2000;
        bool pingBlink = true;      // icon beats with each probe instead of sitting lit
        double pingPulse;           // 1.0 right after a probe, decays to 0
        int lastPingSeq;
        long lastProbeAt;

        string cfgPath;
        double shownRx, shownTx;      // eased bar amplitude
        double textRx, textTx;        // slower values used by the digits
        float zoom = 1.0f;

        double[] rxFifo = new double[9];
        double[] txFifo = new double[9];
        int fifoPos;
        long fifoClock;
        long lastTickAt;
        long lastTextAt;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        // ---- layered window plumbing ----
        // Ctrl+Alt+W toggles passthrough from anywhere. This is the only reliable way back:
        // once WS_EX_TRANSPARENT is on, the window cannot receive the click that would undo it.
        const int HOTKEY_ID = 0xC0F5;
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_NOREPEAT = 0x4000;
        const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int GWL_EXSTYLE = -20;
        const uint ULW_ALPHA = 0x00000002;
        const byte AC_SRC_OVER = 0x00;
        const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        IntPtr memDc;
        IntPtr dib;
        IntPtr oldObj;
        IntPtr dibBits;
        int dibW, dibH;

        public WidgetForm(string dir, bool shotMode)
        {
            cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cfoswidget.ini");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Text = "cFos 网速悬浮窗";
            DoubleBuffered = false;

            skin = Skin.Load(dir, "Liquid Crystal");
            LoadConfig();
            px = new int[skin.W * skin.H];
            canvas = new Bitmap(skin.W, skin.H, PixelFormat.Format32bppArgb);

            if (shotMode) { Size = new Size(skin.W, skin.H); return; }

            Size = ScaledSize();
            RestorePosition();
            ApplyExStyle();
            BuildTray();
            UpdateTrayState();

            tickTimer.Interval = tickMs;
            tickTimer.Tick += delegate { Tick(); };
            tickTimer.Start();

            textTimer.Interval = textMs;
            textTimer.Tick += delegate { TextTick(); };
            textTimer.Start();

            // 10 ms: the dwell distribution needs finer granularity than 30 ms, otherwise
            // every hold collapses to "one tick" and the long pauses never appear.
            bulletTimer.Interval = 10;
            bulletTimer.Tick += delegate { BulletTick(); };
            bulletTimer.Start();

            pingTimer.Interval = pingMs;
            pingTimer.Tick += delegate
            {
                pingCount++;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { sampler.PingOnce(); pingLast = sampler.PingMs; }
                    catch { pingLast = -99; }
                });
            };
            pingTimer.Start();
            ThreadPool.QueueUserWorkItem(delegate { sampler.PingOnce(); });

            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;

            // A second launch raises this event; show the existing widget instead of letting
            // a second one start its own tray icon.
            try
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CfosWidget.Show");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    while (true)
                    {
                        try { showSignal.WaitOne(); }
                        catch { return; }
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (!Visible) ToggleVisible();
                                else { TopMost = TopMost; RenderNow(); }
                            });
                        }
                        catch { return; }
                    }
                });
            }
            catch { }

            Shown += delegate
            {
                RenderNow();
                RegisterHotkeyCandidates();
                if (!hotkeyOk)
                {
                    // another app owns the combo; the tray menu still works
                    try
                    {
                        tray.BalloonTipTitle = "热键注册失败";
                        tray.BalloonTipText = "常用组合都被占用（错误 " + hotkeyErr + "），请用托盘菜单切换穿透";
                        tray.BalloonTipIcon = ToolTipIcon.Warning;
                        tray.ShowBalloonTip(3000);
                    }
                    catch { }
                }
            };
        }

        // ---------- setup ----------
        public string DiagMenu()
        {
            RebuildMenu(tray.ContextMenuStrip);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ToolStripItem it in tray.ContextMenuStrip.Items)
            {
                if (it is ToolStripSeparator) { sb.AppendLine("----"); continue; }
                sb.AppendLine(((ToolStripMenuItem)it).Checked ? "[x] " + it.Text : "[ ] " + it.Text);
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.DropDownItems.Count > 0)
                {
                    foreach (ToolStripItem sub in mi.DropDownItems)
                    {
                        if (sub is ToolStripSeparator) { sb.AppendLine("      ----"); continue; }
                        ToolStripMenuItem sm = sub as ToolStripMenuItem;
                        if (sm == null) continue;
                        sb.AppendLine("      " + (sm.Checked ? "[x] " : "[ ] ") + sm.Text + (sm.Enabled ? "" : "  (disabled)"));
                    }
                }
            }
            return sb.ToString();
        }

        public int tickCount;
        public int textCount;
        public int pingCount;
        public int pingLast;
        public double pulseMin = 2, pulseMax = -1;
        public int pulseSamples;
        public int tickThrows;

        public string DiagProbe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("tickMs=" + tickMs + " textMs=" + textMs);
            sb.AppendLine("ticks=" + tickCount + " texts=" + textCount + " pings=" + pingCount);
            sb.AppendLine("pingLast=" + pingLast + " statePing=" + state.Ping);
            sb.AppendLine("tickThrows=" + tickThrows);
            sb.AppendLine("shownRx=" + shownRx.ToString("0.0") + " shownTx=" + shownTx.ToString("0.0"));
            sb.AppendLine("barRx=" + state.RxBar.ToString("0.000") + " barTx=" + state.TxBar.ToString("0.000"));
            sb.AppendLine("pingTarget=" + Sampler.PingTarget);
            sb.AppendLine("pingBlink=" + pingBlink + " pingMs=" + pingMs);
            sb.AppendLine("pulseMin=" + pulseMin.ToString("0.000") + " pulseMax=" + pulseMax.ToString("0.000") + " samples=" + pulseSamples);
            sb.AppendLine("pingSeq=" + sampler.PingSeq + " lastPingSeq=" + lastPingSeq);
            return sb.ToString();
        }

        public bool hotkeyOk;
        public int hotkeyErr;
        public int hotkeyHits;
        public string hotkeyLabel = "";
        static readonly string[] HotkeyNames = new string[] { "Ctrl+Alt+W", "Ctrl+Alt+P", "Ctrl+Alt+F10", "Ctrl+Shift+F10" };

        // The first combo in the list that the shell lets us have. 1409 means somebody
        // else already owns it, so we just walk down the candidates.
        void RegisterHotkeyCandidates()
        {
            Keys[] vks = new Keys[] { Keys.W, Keys.P, Keys.F10, Keys.F10 };
            uint[] mods = new uint[] {
                MOD_CONTROL | MOD_ALT,
                MOD_CONTROL | MOD_ALT,
                MOD_CONTROL | MOD_ALT,
                MOD_CONTROL | MOD_SHIFT };
            for (int i = 0; i < vks.Length; i++)
            {
                hotkeyErr = 0;
                if (RegisterHotKey(Handle, HOTKEY_ID, mods[i] | MOD_NOREPEAT, (uint)vks[i]))
                {
                    hotkeyOk = true;
                    hotkeyLabel = HotkeyNames[i];
                    return;
                }
                hotkeyErr = Marshal.GetLastWin32Error();
            }
            hotkeyOk = false;
            hotkeyLabel = "";
        }

        // Called from --diagpass: flip passthrough twice and report the exstyle each time.
        public string DiagPassToggle()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("hotkeyRegistered=" + hotkeyOk + " err=" + hotkeyErr + " label=" + hotkeyLabel);
            sb.AppendLine("hotkeyHits=" + hotkeyHits);
            sb.AppendLine("start passThrough=" + passThrough + " ex=0x" + DiagExStyle.ToString("X8"));
            SetPassThrough(!passThrough);
            sb.AppendLine("after toggle passThrough=" + passThrough + " ex=0x" + DiagExStyle.ToString("X8"));
            SetPassThrough(!passThrough);
            sb.AppendLine("after toggle passThrough=" + passThrough + " ex=0x" + DiagExStyle.ToString("X8"));
            return sb.ToString();
        }

        public int DiagExStyle { get { return GetWindowLong(Handle, GWL_EXSTYLE); } }
        public bool DiagPassThrough { get { return passThrough; } }

        void ApplyExStyle()
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (passThrough) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
        }

        void SetPassThrough(bool on)
        {
            passThrough = on;
            ApplyExStyle();
            UpdateTrayState();
            SaveConfig();
        }

        void TogglePassThrough()
        {
            SetPassThrough(!passThrough);
            if (tray == null) return;
            tray.BalloonTipTitle = passThrough ? "鼠标穿透已开启" : "鼠标穿透已关闭";
            tray.BalloonTipText = passThrough
                ? "窗口不再接收点击，用托盘图标" + (hotkeyOk ? " 或 " + hotkeyLabel : "") + " 关闭"
                : "窗口可以正常拖动和右键了";
            tray.BalloonTipIcon = ToolTipIcon.Info;
            try { tray.ShowBalloonTip(2500); } catch { }
        }

        void UpdateTrayState()
        {
            if (tray == null) return;
            string hint = hotkeyOk ? " · " + hotkeyLabel : "";
            try { tray.Text = passThrough ? "网速悬浮窗（穿透中" + hint + "）" : "cFos 网速悬浮窗"; }
            catch { }
            try
            {
                if (!trayIconReady || trayIconPassThrough != passThrough)
                {
                    Icon old = tray.Icon;
                    tray.Icon = MakeIcon();
                    trayIconReady = true;
                    trayIconPassThrough = passThrough;
                    if (old != null && old != appIcon && old != slashIcon && old != SystemIcons.Application)
                        old.Dispose();
                }
            }
            catch { }
        }

        // notifyicon caps Text at 63 chars; keep ours short enough
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                hotkeyHits++;
                TogglePassThrough();
                return;
            }
            base.WndProc(ref m);
        }

        Size ScaledSize()
        {
            return new Size((int)Math.Round(skin.W * zoom), (int)Math.Round(skin.H * zoom));
        }

        void LoadConfig()
        {
            if (!File.Exists(cfgPath)) return;
            IniFile ini = IniFile.Load(cfgPath);
            IniSection g = ini["widget"];
            if (g == null) return;
            string nic = g.Get("nic");
            if (nic != null) sampler.NicId = nic;
            string sk = g.Get("skin");
            if (sk != null)
            {
                string d = SkinPack.ResolveDir(sk);
                if (d != null) skin = Skin.Load(d, sk);
            }
            string pt = g.Get("ping");
            if (!string.IsNullOrEmpty(pt)) Sampler.PingTarget = pt;
            locked = g.GetBool("locked", false);
            passThrough = g.GetBool("passthrough", false);
            TopMost = g.GetBool("ontop", true);
            int iv = g.GetInt("interval", 60);
            if (iv >= 20 && iv <= 500) tickMs = iv;
            int tv = g.GetInt("textinterval", 240);
            if (tv >= 100 && tv <= 3000) textMs = tv;
            int pv = g.GetInt("pinginterval", 2000);
            if (pv >= 500 && pv <= 60000) pingMs = pv;
            pingBlink = g.GetBool("pingblink", true);
            bulletFreeRun = g.GetBool("bulletfreerun", true);
            float z = g.GetFloat("zoom", 1.0f);
            if (z < 1.0f) z = 1.0f;
            if (z > 4.0f) z = 4.0f;
            zoom = z;
        }

        void SaveConfig()
        {
            List<string> lines = new List<string>();
            lines.Add("[widget]");
            lines.Add("nic=" + sampler.NicId);
            lines.Add("skin=" + Path.GetFileName(skin.Dir));
            lines.Add("x=" + Left);
            lines.Add("y=" + Top);
            lines.Add("locked=" + (locked ? "1" : "0"));
            lines.Add("passthrough=" + (passThrough ? "1" : "0"));
            lines.Add("ontop=" + (TopMost ? "1" : "0"));
            lines.Add("interval=" + tickMs);
            lines.Add("textinterval=" + textMs);
            lines.Add("pinginterval=" + pingMs);
            lines.Add("pingblink=" + (pingBlink ? "1" : "0"));
            lines.Add("bulletfreerun=" + (bulletFreeRun ? "1" : "0"));
            lines.Add("zoom=" + zoom.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("ping=" + Sampler.PingTarget);
            try { IniFile.WriteLines(cfgPath, lines); } catch { }
        }

        void RestorePosition()
        {
            int x = -99999, y = -99999;
            if (File.Exists(cfgPath))
            {
                IniFile ini = IniFile.Load(cfgPath);
                IniSection g = ini["widget"];
                if (g != null) { x = g.GetInt("x", -99999); y = g.GetInt("y", -99999); }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size sz = ScaledSize();
            if (x < -5000 || y < -5000 || x > wa.Right - 8 || y > wa.Bottom - 8)
            {
                x = wa.Right - sz.Width - 24;
                y = wa.Bottom - sz.Height - 24;
            }
            Location = new Point(x, y);
        }

        // ---------- tray ----------
        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Text = "cFos 网速悬浮窗";
            tray.Icon = MakeIcon();
            tray.Visible = true;
            tray.DoubleClick += delegate { ToggleVisible(); };

            ContextMenuStrip m = new ContextMenuStrip();
            m.Opening += delegate { RebuildMenu(m); };
            // The 10 ms bullet timer repaints through UpdateLayeredWindow on the same UI
            // thread that has to track the mouse over the open menu. Freezing the animation
            // while the menu is up costs nothing visually (the menu covers the widget) and
            // it is the difference between a menu that snaps open and one that stutters.
            m.Opened += delegate { SuspendAnimation(true); };
            m.Closed += delegate { SuspendAnimation(false); };
            tray.ContextMenuStrip = m;
        }

        void SuspendAnimation(bool on)
        {
            try
            {
                if (on) { if (bulletTimer.Enabled) bulletTimer.Stop(); }
                else if (!bulletTimer.Enabled) { bulletLastMs = 0; bulletTimer.Start(); }
            }
            catch { }
        }

        void ToggleVisible()
        {
            if (Visible) { Hide(); }
            else
            {
                Show();
                ApplyExStyle();
                TopMost = TopMost;
                RenderNow();
            }
        }

        // The exe carries a designed multi-size icon; use it directly so the tray and the
        // taskbar agree, and only overlay the passthrough slash when that mode is on.
        static Icon appIcon;
        static bool appIconTried;

        // Built once and reused. Rebuilding it on every state change leaked one HICON and
        // one GDI bitmap per toggle, and NotifyIcon re-marshals the shell icon each time --
        // that is what made the tray feel sticky right after a right-click toggle.
        static Icon slashIcon;

        Icon MakeIcon()
        {
            if (!appIconTried)
            {
                appIconTried = true;
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { appIcon = null; }
            }
            if (appIcon == null) return SystemIcons.Application;
            if (!passThrough) return appIcon;
            if (slashIcon != null) return slashIcon;

            // overlay a slash on a copy to signal "clicks pass through"
            using (Bitmap b = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.DrawIcon(appIcon, new Rectangle(0, 0, 32, 32));
                    using (SolidBrush back = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                        g.FillEllipse(back, 2, 2, 28, 28);
                    using (Pen p = new Pen(Color.FromArgb(240, 255, 170, 60), 3.2f))
                        g.DrawLine(p, 6, 26, 26, 6);
                }
                IntPtr h = b.GetHicon();
                try { slashIcon = (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
            return slashIcon ?? appIcon;
        }

        void RebuildMenu(ContextMenuStrip m)
        {
            m.Items.Clear();

            // The list is enumerated on every menu open, so a NIC added while the widget is
            // running (WiFi dongle, USB tethering, VPN) shows up without a restart.
            ToolStripMenuItem nicRoot = new ToolStripMenuItem("网卡选择");
            // Cached on purpose: enumerating adapters blocks for 100-300 ms after a VPN or
            // dongle changes state, and this runs on the UI thread while the menu paints.
            List<NicInfo> nics = Sampler.ListNicsCached();
            bool selectedStillPresent = false;
            for (int i = 0; i < nics.Count; i++)
            {
                NicInfo n = nics[i];
                bool isSel = n.Id == sampler.NicId;
                if (isSel) selectedStillPresent = true;
                string label = n.ToString();
                bool up = n.Up;
                ToolStripMenuItem it = new ToolStripMenuItem((up ? "" : "⊘ ") + label);
                it.Checked = isSel;
                it.ToolTipText = up ? "已连接" : "未连接（网线拔出或无线断开）";
                string id = n.Id;
                it.Click += delegate
                {
                    sampler.NicId = id;
                    sampler.RxSmooth = 0; sampler.TxSmooth = 0;
                    shownRx = shownTx = textRx = textTx = 0;
                    SaveConfig();
                    RenderNow();
                };
                nicRoot.DropDownItems.Add(it);
            }
            if (nics.Count == 0)
                nicRoot.DropDownItems.Add(new ToolStripMenuItem("(无可用网卡)") { Enabled = false });
            if (!selectedStillPresent && nics.Count > 0)
            {
                nicRoot.DropDownItems.Add(new ToolStripSeparator());
                ToolStripMenuItem warn = new ToolStripMenuItem("原网卡已移除，请重新选择") { Enabled = false };
                nicRoot.DropDownItems.Add(warn);
            }
            nicRoot.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem refresh = new ToolStripMenuItem("重新扫描网卡");
            refresh.Click += delegate { sampler.NicId = sampler.NicId; shownRx = shownTx = 0; RenderNow(); };
            nicRoot.DropDownItems.Add(refresh);
            m.Items.Add(nicRoot);

            ToolStripMenuItem skinRoot = new ToolStripMenuItem("皮肤切换");
            System.Collections.Generic.List<string> skinNames = SkinPack.List();
            for (int i = 0; i < skinNames.Count; i++)
            {
                string name = skinNames[i];
                ToolStripMenuItem it = new ToolStripMenuItem(name);
                it.Checked = string.Equals(name, Path.GetFileName(skin.Dir ?? ""), StringComparison.OrdinalIgnoreCase);
                it.Click += delegate { SwitchSkin(SkinPack.ResolveDir(name) ?? name); };
                skinRoot.DropDownItems.Add(it);
            }
            m.Items.Add(skinRoot);

            ToolStripMenuItem pt = new ToolStripMenuItem("鼠标穿透");
            pt.ShortcutKeyDisplayString = hotkeyOk ? hotkeyLabel : "";
            pt.Checked = passThrough;
            pt.Click += delegate { SetPassThrough(!passThrough); };
            m.Items.Add(pt);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem zm = new ToolStripMenuItem("缩放");
            float[] zs = new float[] { 1.0f, 2.0f, 3.0f };
            for (int i = 0; i < zs.Length; i++)
            {
                float z = zs[i];
                ToolStripMenuItem it = new ToolStripMenuItem((int)(z * 100) + "%");
                it.Checked = Math.Abs(zoom - z) < 0.01f;
                it.Click += delegate
                {
                    Point keep = Location;
                    zoom = z;
                    Size = ScaledSize();
                    Location = keep;
                    SaveConfig();
                    RenderNow();
                };
                zm.DropDownItems.Add(it);
            }
            m.Items.Add(zm);

            ToolStripMenuItem iv = new ToolStripMenuItem("动画刷新");
            int[] vals = new int[] { 40, 60, 100, 200 };
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v + " ms");
                it.Checked = tickMs == v;
                it.Click += delegate
                {
                    tickMs = v;
                    tickTimer.Interval = v;
                    lastTickAt = clock.ElapsedMilliseconds;
                    SaveConfig();
                };
                iv.DropDownItems.Add(it);
            }
            m.Items.Add(iv);

            ToolStripMenuItem tv = new ToolStripMenuItem("数字刷新");
            int[] tvals = new int[] { 150, 240, 400, 800, 1000 };
            for (int i = 0; i < tvals.Length; i++)
            {
                int v = tvals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v + " ms");
                it.Checked = textMs == v;
                it.Click += delegate
                {
                    textMs = v;
                    textTimer.Interval = v;
                    lastTextAt = clock.ElapsedMilliseconds;
                    textRx = shownRx;      // snap so the digits do not glide for a second
                    textTx = shownTx;
                    SaveConfig();
                    RenderNow();
                };
                tv.DropDownItems.Add(it);
            }
            m.Items.Add(tv);

            ToolStripMenuItem pv = new ToolStripMenuItem("Ping 间隔");
            int[] pvals = new int[] { 500, 1000, 2000, 3000, 5000, 10000, 30000 };
            for (int i = 0; i < pvals.Length; i++)
            {
                int v = pvals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v >= 1000 ? (v / 1000) + " 秒" : v + " ms");
                it.Checked = pingMs == v;
                it.Click += delegate
                {
                    pingMs = v;
                    pingTimer.Interval = v;
                    SaveConfig();
                    // ping once right away so the dot does not sit stale for the new interval
                    ThreadPool.QueueUserWorkItem(delegate { sampler.PingOnce(); });
                };
                pv.DropDownItems.Add(it);
            }
            m.Items.Add(pv);

            ToolStripMenuItem tgt = new ToolStripMenuItem("Ping 目标");
            string[][] targets = new string[][] {
                new string[] { "baidu.com（最快）", "www.baidu.com" },
                new string[] { "腾讯 DNS 119.29.29.29", "119.29.29.29" },
                new string[] { "阿里 DNS 223.5.5.5", "223.5.5.5" },
                new string[] { "Cloudflare 1.1.1.1", "1.1.1.1" },
                new string[] { "Google 8.8.8.8", "8.8.8.8" } };
            for (int i = 0; i < targets.Length; i++)
            {
                string label = targets[i][0];
                string host = targets[i][1];
                ToolStripMenuItem it = new ToolStripMenuItem(label);
                it.Checked = string.Equals(Sampler.PingTarget, host, StringComparison.OrdinalIgnoreCase);
                it.Click += delegate
                {
                    Sampler.PingTarget = host;
                    SaveConfig();
                    ThreadPool.QueueUserWorkItem(delegate { sampler.PingOnce(); });
                };
                tgt.DropDownItems.Add(it);
            }
            m.Items.Add(tgt);

            ToolStripMenuItem bmode = new ToolStripMenuItem("数据流方块驱动");
            ToolStripMenuItem mFree = new ToolStripMenuItem("自由循环（原版行为）");
            mFree.Checked = bulletFreeRun;
            mFree.ToolTipText = "实测原版空闲时也每秒跳动 22.5 次，与流量无关";
            mFree.Click += delegate { bulletFreeRun = true; SaveConfig(); };
            ToolStripMenuItem mTraffic = new ToolStripMenuItem("按流量驱动");
            mTraffic.Checked = !bulletFreeRun;
            mTraffic.ToolTipText = "有流量才走，静止时停住";
            mTraffic.Click += delegate { bulletFreeRun = false; SaveConfig(); };
            bmode.DropDownItems.Add(mFree);
            bmode.DropDownItems.Add(mTraffic);
            m.Items.Add(bmode);

            ToolStripMenuItem blink = new ToolStripMenuItem("Ping 图标随探测闪烁");
            blink.Checked = pingBlink;
            blink.ToolTipText = "关掉则只要有读数就常亮";
            blink.Click += delegate { pingBlink = !pingBlink; SaveConfig(); RenderNow(); };
            m.Items.Add(blink);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem top = new ToolStripMenuItem("总在最前");
            top.Checked = TopMost;
            top.Click += delegate { TopMost = !TopMost; SaveConfig(); };
            m.Items.Add(top);

            ToolStripMenuItem lk = new ToolStripMenuItem("锁定位置");
            lk.Checked = locked;
            lk.Click += delegate { locked = !locked; SaveConfig(); };
            m.Items.Add(lk);

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自启动");
            auto.Checked = StartupEnabled();
            auto.Click += delegate { SetStartup(!StartupEnabled()); };
            m.Items.Add(auto);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hide = new ToolStripMenuItem("隐藏到托盘");
            hide.Click += delegate { Hide(); };
            m.Items.Add(hide);
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { SaveConfig(); tray.Visible = false; Application.Exit(); };
            m.Items.Add(exit);
        }

        void SwitchSkin(string d)
        {
            Point keep = Location;
            skin = Skin.Load(d, Path.GetFileName(d));
            px = new int[skin.W * skin.H];
            canvas = new Bitmap(skin.W, skin.H, PixelFormat.Format32bppArgb);
            Size = ScaledSize();
            Location = keep;
            if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
            SaveConfig();
            RenderNow();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "CfosWidget";

        static bool StartupEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    return k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        // ---------- interaction ----------
        void OnDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !locked && !passThrough)
            {
                drag = true;
                dragStart = Cursor.Position;
                formStart = Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Show() raises Opening, which rebuilds the menu; doing it here too meant the
                // whole item tree was constructed twice on every right-click.
                tray.ContextMenuStrip.Show(Cursor.Position);
            }
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (drag)
            {
                Point p = Cursor.Position;
                Location = new Point(formStart.X + (p.X - dragStart.X), formStart.Y + (p.Y - dragStart.Y));
            }
        }

        void OnUp(object sender, MouseEventArgs e)
        {
            if (drag) { drag = false; SaveConfig(); }
        }

        // ---------- sampling ----------
        void Tick()
        {
            tickCount++;
            long now = clock.ElapsedMilliseconds;
            double dtMs = tickCount <= 1 ? tickMs : (now - lastTickAt);
            if (dtMs <= 0.5 || dtMs > 2000) dtMs = tickMs;
            lastTickAt = now;

            Snapshot s = sampler.Sample();
            // 90 ms attack / 260 ms release: lively but never jittery
            // Long half-lives (~0.9 s attack / 2.6 s release): the original's bar barely moves,
            // so a twitchy 90/260 ms response read as "too much stretch" next to it.
            shownRx = Ease(shownRx, s.RxAvg, 900, 2600, dtMs);
            shownTx = Ease(shownTx, s.TxAvg, 900, 2600, dtMs);
            state.RxBar = Scale(shownRx);
            state.TxBar = Scale(shownTx);
            state.Ping = s.PingMs;

            // The icon means "just measured", not "has ever measured". Keep the digits on
            // screen but let the lamp breathe with the probe cadence, otherwise a 5 s interval
            // looks identical to a 500 ms one.
            if (sampler.PingSeq != lastPingSeq)
            {
                lastPingSeq = sampler.PingSeq;
                pingPulse = 1.0;
                lastProbeAt = now;
            }
            if (pingPulse > 0)
            {
                // Long enough to read as a fade rather than a blink.
                double window = pingMs * 0.40;
                if (window < 900) window = 900;
                if (window > 2600) window = 2600;
                pingPulse -= dtMs / window;
                if (pingPulse < 0) pingPulse = 0;
            }
            state.PingPulse = pingBlink ? pingPulse : (s.PingMs >= 0 ? 1.0 : 0.0);
            if (state.PingPulse < pulseMin) pulseMin = state.PingPulse;
            if (state.PingPulse > pulseMax) pulseMax = state.PingPulse;
            pulseSamples++;
            state.Conn = s.TcpCount + s.UdpCount;

            double busy = Math.Max(shownRx, shownTx);
            state.Flow = Math.Min(1.0, Math.Log10(1 + busy / 1024.0) / 3.0);
            if (state.Flow < 0) state.Flow = 0;

            ripple += tickMs / 1000.0;
            state.RipplePhase = ripple;
            // cFosSpeed feeds disp2/disp3 from rx_data_cnt / tx_data_cnt with mod=14, i.e. the
            // pointer advances with the amount of data moved, not with a fixed per-frame step.
            // Advance by bytes and wrap at the skin's frame count so a fast transfer visibly
            // spins faster while an idle link holds near standstill.
            double dtSec = dtMs / 1000.0;
            double bytesRx = shownRx * dtSec;
            double bytesTx = shownTx * dtSec;
            int framesRx = 14, framesTx = 14;
            FrameImg rbi = skin.Img("rx_bullet.png", skin.Layout.BulletRxRect.Height);
            FrameImg tbi = skin.Img("tx_bullet.png", skin.Layout.BulletTxRect.Height);
            if (rbi != null && rbi.Count > 0) framesRx = rbi.Count;
            if (tbi != null && tbi.Count > 0) framesTx = tbi.Count;

            // Measured behaviour of the real widget:
            //   idle  -> block parks (92% of samples sat on one row)
            //   load  -> block walks continuously through the frames
            // That is a data-count counter (net.ini binds disp2 to rx_data_cnt, mod=14), NOT a
            // free-running animation. A constant per-second rate was what made it feel wrong:
            // it kept creeping while idle. Advance by bytes moved instead.
            // Tuned from the real widget: at ~50 KB/s it completes roughly one 14-frame cycle
            // per second, i.e. about 3.5 KB per frame. 256 KB (the first guess) made a step take
            // 700 frames, so the block sat still even under load.


            // The data-flow block is advanced by BulletTick() on its own 30 ms timer -- it runs
            // faster than this repaint clock, so it cannot be driven from here.

            // 9-slot history ring for the lc_history skin
            long hNow = clock.ElapsedMilliseconds;
            if (fifoClock == 0 || hNow - fifoClock >= 120)
            {
                fifoClock = hNow;
                fifoPos = (fifoPos + 1) % rxFifo.Length;
                rxFifo[fifoPos] = state.RxBar;
                txFifo[fifoPos] = state.TxBar;
            }
            double[] rh = new double[rxFifo.Length];
            double[] th = new double[txFifo.Length];
            for (int i = 0; i < rxFifo.Length; i++)
            {
                int k = (fifoPos + 1 + i) % rxFifo.Length;
                rh[i] = rxFifo[k];
                th[i] = txFifo[k];
            }
            state.RxHist = rh;
            state.TxHist = th;

            state.RxCps = shownRx;
            state.TxCps = shownTx;
            RenderNow();
        }

        // Advances only the data-flow block. Kept separate from the repaint tick because the
        // original changes position ~22.5 times per second, faster than a 100 ms clock allows.
        void BulletTick()
        {
            // Use real elapsed time: a WinForms timer fires late under load, and a fixed 30 ms
            // assumption made the dwell count wrong.
            long nowMs = clock.ElapsedMilliseconds;
            double dt = bulletLastMs == 0 ? 0.030 : (nowMs - bulletLastMs) / 1000.0;
            bulletLastMs = nowMs;
            if (dt <= 0 || dt > 0.5) dt = 0.030;
            FrameImg rbi = skin.Img("rx_bullet.png", skin.Layout.BulletRxRect.Height);
            FrameImg tbi = skin.Img("tx_bullet.png", skin.Layout.BulletTxRect.Height);
            int frx = rbi != null && rbi.Count > 0 ? rbi.Count : 14;
            int ftx = tbi != null && tbi.Count > 0 ? tbi.Count : 14;

            // Measured rhythm of the original: it STEP-AND-DWELLS. In synchronized recordings
            // mine changed position on 76% of samples (a smooth crawl) while the original
            // changed on only 31%, holding each position for ~79 ms on average. Reproduce the
            // stair-step cadence, not a continuous slide.
            // Tuned iteratively against synchronized recordings of both widgets:
            //   79 ms -> mine 65% vs original 50% change rate; 110 ms lands on ~50%.
            // The two streams run INDEPENDENTLY in the original. Measured over the same frames:
            //   RX (download): 18% change rate, avg hold 5.0 frames, longest 11
            //   TX (upload)  : 73% change rate, avg hold 1.3 frames, longest 3
            // Sharing one dwell timer (my previous version) made both beat in lockstep.
            if (dwellTargetRx <= 0) dwellTargetRx = NextDwellMs(true);
            if (dwellTargetTx <= 0) dwellTargetTx = NextDwellMs(false);
            dwellRx += dt * 1000.0;
            dwellTx += dt * 1000.0;

            // stepRx/stepTx are fields; without this reset they stay at 1 and advance every
            // tick, which flattened the dwell behaviour into a continuous crawl.
            stepRx = 0;
            stepTx = 0;
            if (dwellRx >= dwellTargetRx)
            {
                dwellRx = 0;
                dwellTargetRx = NextDwellMs(true);
                if (bulletFreeRun) stepRx = 1;
                else
                {
                    const double floor = 48.0 * 1024.0;
                    double erx = shownRx > floor ? shownRx - floor : 0;
                    stepRx = Math.Log10(1 + erx / 1024.0) >= 0.30 ? 1 : 0;
                }
            }
            if (dwellTx >= dwellTargetTx)
            {
                dwellTx = 0;
                dwellTargetTx = NextDwellMs(false);
                if (bulletFreeRun) stepTx = 1;
                else
                {
                    const double floorT = 48.0 * 1024.0;
                    double etx = shownTx > floorT ? shownTx - floorT : 0;
                    stepTx = Math.Log10(1 + etx / 1024.0) >= 0.30 ? 1 : 0;
                }
            }

            bulletRx += stepRx;
            bulletTx += stepTx;
            if (bulletRx > frx * 1000) bulletRx -= frx * 1000;
            if (bulletTx > ftx * 1000) bulletTx -= ftx * 1000;

            // A 10 ms timer that repaints unconditionally means ~100 UpdateLayeredWindow calls
            // per second on the UI thread; the tray menu then has to wait for a paint slot and
            // looks frozen on right-click. Nothing moves while neither counter advances, so
            // skip the paint entirely and let the message pump breathe.
            if (stepRx == 0 && stepTx == 0 && bulletPrimed) return;

            // Visibility must be stamped here: BlendCycle skips drawing entirely when amp is
            // 0, and the flag was lost when this logic moved out of Tick() -- that is why the
            // block disappeared instead of animating.
            bulletPrimed = true;
            state.RxBullet = bulletRx;
            state.TxBullet = bulletTx;
            state.RxBulletAmp = 1.0;
            state.TxBulletAmp = 1.0;
            RenderNow();
        }

        void TextTick()
        {
            // Digits ride a slower clock with a dead-band: a value only replaces the shown one
            // when it moves more than 6%, so the last digit stops vibrating.
            textCount++;
            long now = clock.ElapsedMilliseconds;
            double dtMs = textCount <= 1 ? textMs : (now - lastTextAt);
            if (dtMs <= 0.5 || dtMs > 5000) dtMs = textMs;
            lastTextAt = now;
            double k = 1.0 - Math.Pow(0.5, dtMs / 320.0);
            textRx = Deadband(textRx, shownRx, k);
            textTx = Deadband(textTx, shownTx, k);
            state.RxCps = textRx;
            state.TxCps = textTx;
            RenderNow();
        }

        static double Deadband(double shown, double now, double k)
        {
            double diff = Math.Abs(now - shown);
            // 1.5% of the current value (min 64 B/s) is below what a digit change means,
            // so ignore it; above that glide instead of snapping.
            double floor = Math.Max(64.0, shown * 0.015);
            if (diff < floor) return shown;
            return shown + (now - shown) * k;
        }

        // Frame-rate independent smoothing: the per-tick factor is derived from elapsed
        // time, so 40 ms and 500 ms refresh produce the same visual glide instead of
        // 200 ms looking choppy and 40 ms looking twitchy.
        static double Ease(double cur, double target, double halfLifeUpMs, double halfLifeDownMs, double dtMs)
        {
            double half = target > cur ? halfLifeUpMs : halfLifeDownMs;
            if (half < 1) half = 1;
            double k = 1.0 - Math.Pow(0.5, dtMs / half);
            if (k < 0) k = 0;
            if (k > 1) k = 1;
            return cur + (target - cur) * k;
        }

        // Calibrated against the real widget on this machine, where the bar stays SHORT even
        // at high speed:
        //   16 KB/s  -> 0 px     25 KB/s -> 1 px
        //   7.9 MB/s -> 6 px     35 MB/s -> 10 px      (full width = 51 px)
        // So the original's scale is logarithmic across a very wide range, roughly full at
        // 1 GB/s. A linear ramp (my previous attempt) made 750 KB/s look like a third of the
        // bar, which is why mine read as a fat block next to the original's sliver.
        static double Scale(double bps)
        {
            double kb = bps / 1024.0;
            if (kb < 0) kb = 0;
            // K = 30 fits the measured point 7.9 MB/s -> 6 px exactly, and the 35 MB/s -> 10 px
            // point to within one pixel. (No single K satisfies all three samples bit-exactly,
            // so the original likely uses a piecewise or sqrt-ish curve; this is the best fit.)
            // K = 14 keeps the original's "short bar" character (7.9 MB/s -> 4 px, 35 MB/s -> 5 px
            // against its measured 6 and 10) while still showing movement in the 1-10 MB/s range
            // that actually occurs on this link.
            double v = Math.Log10(1 + kb) / 14.0;
            if (v > 1) v = 1;
            return v;
        }

        // ---------- rendering ----------
        void RenderNow()
        {
            skin.Render(px, state);
            PushLayered();
        }

        void PushLayered()
        {
            if (!IsHandleCreated) return;
            if (dib != IntPtr.Zero && (dibW != Width || dibH != Height))
            {
                if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                dib = IntPtr.Zero;
            }
            if (dib == IntPtr.Zero) AllocDib(Width, Height);
            if (dib == IntPtr.Zero) return;

            CopyToDib(skin.W, skin.H, Width, Height);

            POINT dst = new POINT();
            dst.X = Left; dst.Y = Top;
            POINT src = new POINT();
            SIZE sz = new SIZE();
            sz.cx = Width; sz.cy = Height;
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = AC_SRC_OVER;
            bf.BlendFlags = 0;
            bf.SourceConstantAlpha = 255;
            bf.AlphaFormat = AC_SRC_ALPHA;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref bf, ULW_ALPHA);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        void AllocDib(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;            // top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            dib = CreateDIBSection(memDc, ref bi, 0, out dibBits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            oldObj = SelectObject(memDc, dib);
            dibW = w; dibH = h;
        }

        void CopyToDib(int sw, int sh, int dw, int dh)
        {
            bool identity = (sw == dw && sh == dh);
            unsafe
            {
                byte* dstBase = (byte*)dibBits;
                for (int y = 0; y < dh; y++)
                {
                    uint* row = (uint*)(dstBase + y * dw * 4);
                    for (int x = 0; x < dw; x++)
                    {
                        int argb;
                        if (identity)
                        {
                            argb = px[y * sw + x];
                        }
                        else
                        {
                            int sx = (int)((long)x * sw / dw);
                            int sy = (int)((long)y * sh / dh);
                            if (sx >= sw) sx = sw - 1;
                            if (sy >= sh) sy = sh - 1;
                            argb = px[sy * sw + sx];
                        }
                        int a = (argb >> 24) & 0xFF;
                        int r = (argb >> 16) & 0xFF;
                        int g = (argb >> 8) & 0xFF;
                        int b = argb & 0xFF;
                        // UpdateLayeredWindow with AC_SRC_ALPHA wants premultiplied BGRA
                        row[x] = (uint)((a << 24) | ((r * a / 255) << 16) | ((g * a / 255) << 8) | (b * a / 255));
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) RenderNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (IsHandleCreated) { try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { } }
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
            }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } } }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // Skin may come from the embedded pack; fall back to a loose folder if present.
            string dir = SkinPack.ResolveDir("liquid_crystal") ?? SkinPack.ResolveDir("lc_history");

            if (args.Length >= 2 && args[0] == "--shot")
            {
                string name = args.Length > 2 ? args[2] : "liquid_crystal";
                string shotDir = SkinPack.ResolveDir(name) ?? dir;
                double flow = 0;
                if (args.Length > 3) double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out flow);
                Shot(shotDir, args[1], name, flow);
                return;
            }

            Application.EnableVisualStyles();
            // diag handled after form creation below
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs te) { Log(te.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ue) { Log(ue.ExceptionObject as Exception); };
            if (args.Length >= 3 && args[0] == "--units")
            {
                // Render one frame per supplied speed (B/s) so the unit suffixes can be eyeballed.
                Skin sk = Skin.Load(dir, "liquid_crystal");
                int[] buf = new int[sk.W * sk.H];
                RenderState st = new RenderState();
                st.RxHist = new double[9]; st.TxHist = new double[9];
                st.Ping = 30; st.Conn = 2; st.RxBar = 0.5; st.TxBar = 0.3;
                System.Text.StringBuilder ub = new System.Text.StringBuilder();
                for (int i = 2; i < args.Length; i++)
                {
                    double v = 0;
                    double.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
                    st.RxCps = v; st.TxCps = v;
                    ub.AppendLine(v.ToString("0") + " -> " + Skin.FormatSpeed(v));
                }
                File.WriteAllText(Path.Combine(baseDir, "units.txt"), ub.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--accuracy")
            {
                // Sample our own pipeline and the adapter's raw counter side by side over
                // a controlled transfer, then report both series.
                System.Text.StringBuilder ab = new System.Text.StringBuilder();
                Sampler sp = new Sampler("");
                sp.Sample();
                System.Threading.Thread thr = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        System.Net.HttpWebRequest rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://speed.cloudflare.com/__down?bytes=30000000");
                        rq.Timeout = 30000;
                        using (System.Net.WebResponse rs = rq.GetResponse())
                        using (System.IO.Stream st = rs.GetResponseStream())
                        {
                            byte[] buf = new byte[65536];
                            while (st.Read(buf, 0, buf.Length) > 0) { }
                        }
                    }
                    catch { }
                });
                thr.IsBackground = true;
                thr.Start();
                System.Collections.Generic.List<string> rows = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 40; i++)
                {
                    Snapshot sn = sp.Sample();
                    rows.Add((sn.Rx / 1024).ToString("0"));
                    System.Threading.Thread.Sleep(120);
                }
                ab.AppendLine("our rx KB/s: " + string.Join(",", rows.ToArray()));
                System.Text.StringBuilder eb = new System.Text.StringBuilder();
                eb.AppendLine("raw bytes at each step (delta per 120ms):");
                long prevB = -1;
                for (int i = 0; i < rows.Count && i < 40; i++)
                {
                    double kb = 0;
                    if (i > 0 && double.TryParse(rows[i], out kb)) { }
                    eb.Append((i == 0 ? "" : rows[i] + "|"));
                }
                ab.AppendLine(eb.ToString());
                if (args.Length > 1 && args[1] == "idle")
                {
                    // Idle jitter: absolute swings on a tiny baseline is what reads as
                    // "fluctuating wildly" even though the measurement is correct.
                    System.Text.StringBuilder ib = new System.Text.StringBuilder();
                    Sampler ip = new Sampler("");
                    ip.Sample();
                    System.Collections.Generic.List<double> vals = new System.Collections.Generic.List<double>();
                    for (int i = 0; i < 60; i++)
                    {
                        Snapshot sn = ip.Sample();
                        vals.Add(sn.Rx / 1024.0);
                        System.Threading.Thread.Sleep(120);
                    }
                    double mn = 1e18, mx = 0, sum = 0;
                    for (int i = 8; i < vals.Count; i++) { if (vals[i] < mn) mn = vals[i]; if (vals[i] > mx) mx = vals[i]; sum += vals[i]; }
                    double avg = sum / (vals.Count - 8);
                    ib.AppendLine("idle avg=" + avg.ToString("0.0") + " KB/s  min=" + mn.ToString("0.0") + "  max=" + mx.ToString("0.0") + "  swing=" + (mx - mn).ToString("0.0") + " KB/s");
                    ib.AppendLine("absolute swing is what the eye sees; relative would be " + (mx / Math.Max(0.1, mn)).ToString("0.0") + "x");
                    File.AppendAllText(Path.Combine(baseDir, "accuracy.txt"), ib.ToString());
                }
                File.WriteAllText(Path.Combine(baseDir, "accuracy.txt"), ab.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--bulletwalk")
            {
                // Prove the pointer visits all 14 frames rather than sticking near one.
                Skin sk = Skin.Load(dir, "liquid_crystal");
                int[] buf = new int[sk.W * sk.H];
                RenderState st = new RenderState();
                st.RxHist = new double[9]; st.TxHist = new double[9];
                st.Ping = 30; st.Conn = 2; st.RxBar = 0.4; st.TxBar = 0.2;
                st.RxBulletAmp = 1; st.TxBulletAmp = 1;
                System.Text.StringBuilder wb = new System.Text.StringBuilder();
                Rectangle r = sk.Layout.BulletRxRect;
                for (int f = 0; f < 14; f++)
                {
                    st.RxBullet = f; st.TxBullet = f;
                    sk.Render(buf, st);
                    // top row of the block inside the cell
                    int top = -1;
                    for (int y = r.Top; y < r.Bottom && top < 0; y++)
                    {
                        int cnt = 0;
                        for (int x = r.Left + 1; x < r.Right - 1; x++)
                        {
                            int p = buf[y * sk.W + x];
                            if (((p >> 24) & 0xFF) > 150) cnt++;
                        }
                        if (cnt >= 7) top = y - r.Top;
                    }
                    wb.AppendLine("frame " + f + " blockTop=" + top);
                }
                File.WriteAllText(Path.Combine(baseDir, "bulletwalk.txt"), wb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--ratevar2")
            {
                System.Text.StringBuilder rb = new System.Text.StringBuilder();
                Sampler sp = new Sampler("");
                sp.Sample();
                System.Collections.Generic.List<double> rxs = new System.Collections.Generic.List<double>();
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 150; i++)
                {
                    Snapshot sn = sp.Sample();
                    rxs.Add(sn.Rx);
                    System.Threading.Thread.Sleep(60);
                }
                double mn = 1e18, mx = 0, sum = 0; int n = 0;
                for (int i = 12; i < rxs.Count; i++)
                {
                    double v = rxs[i];
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                    sum += v; n++;
                }
                double avg = n > 0 ? sum / n : 0;
                rb.AppendLine("samples=" + n);
                rb.AppendLine("avg=" + (avg / 1024).ToString("0.0") + " KB/s");
                rb.AppendLine("min=" + (mn / 1024).ToString("0.0") + " KB/s");
                rb.AppendLine("max=" + (mx / 1024).ToString("0.0") + " KB/s");
                System.Text.StringBuilder ser = new System.Text.StringBuilder();
                for (int i = 0; i < rxs.Count; i++)
                    ser.Append((rxs[i] / 1024).ToString("0")).Append(i == rxs.Count - 1 ? "" : ",");
                System.Text.StringBuilder raw = new System.Text.StringBuilder();
                long pv = -1;
                for (int i = 0; i < rxs.Count; i++)
                {
                    long v = (long)rxs[i];
                    if (pv >= 0) raw.Append(v == pv ? "." : "X");
                    pv = v;
                    if (i > 40) break;
                }
                rb.AppendLine("nicId=" + sp.CurrentNicId);
                rb.AppendLine("change(pattern, . = identical): " + raw.ToString());
                rb.AppendLine("series=" + ser.ToString());
                rb.AppendLine("max/avg=" + (mx / Math.Max(1, avg)).ToString("0.00"));
                rb.AppendLine("min/avg=" + (mn / Math.Max(1, avg)).ToString("0.00"));
                File.WriteAllText(Path.Combine(baseDir, "ratevar2.txt"), rb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--ratevar")
            {
                // Compare instantaneous per-tick deltas with a fixed sliding window.
                // This quantifies the "fluctuation" complaint with numbers.
                System.Text.StringBuilder rb = new System.Text.StringBuilder();
                Sampler sp = new Sampler("");
                System.Collections.Generic.List<double> inst = new System.Collections.Generic.List<double>();
                System.Collections.Generic.List<double> win = new System.Collections.Generic.List<double>();
                long prevBytes = -1;
                long prevAt = 0;
                long winStartAt = 0;
                long winStartBytes = 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 120; i++)
                {
                    Snapshot sn = sp.Sample();
                    long snapBytes = (long)sn.Rx;
                    long now = sw.ElapsedMilliseconds;
                    if (now - winStartAt >= 400)
                    {
                        if (winStartBytes > 0)
                            win.Add((snapBytes - winStartBytes) * 1000.0 / (now - winStartAt));
                        winStartBytes = snapBytes;
                        winStartAt = now;
                    }
                    if (prevBytes > 0 && now > prevAt)
                        inst.Add((snapBytes - prevBytes) * 1000.0 / (now - prevAt));
                    prevBytes = snapBytes;
                    prevAt = now;
                    System.Threading.Thread.Sleep(60);
                }
                rb.AppendLine("instant samples=" + inst.Count);
                rb.AppendLine("window samples=" + win.Count);
                if (inst.Count > 4 && win.Count > 2)
                {
                    double imn = 1e18, imx = 0, isum = 0;
                    for (int i = 2; i < inst.Count; i++) { double v = inst[i]; if (v < imn) imn = v; if (v > imx) imx = v; isum += v; }
                    double iavg = isum / (inst.Count - 2);
                    double wmn = 1e18, wmx = 0, wsum = 0;
                    for (int i = 1; i < win.Count; i++) { double v = win[i]; if (v < wmn) wmn = v; if (v > wmx) wmx = v; wsum += v; }
                    double wavg = wsum / (win.Count - 1);
                    rb.AppendLine("instant: avg=" + (iavg / 1024).ToString("0") + "KB/s min=" + (imn / 1024).ToString("0") + " max=" + (imx / 1024).ToString("0") + " spread=" + (imx / Math.Max(1, imn)).ToString("0.0") + "x");
                    rb.AppendLine("window : avg=" + (wavg / 1024).ToString("0") + "KB/s min=" + (wmn / 1024).ToString("0") + " max=" + (wmx / 1024).ToString("0") + " spread=" + (wmx / Math.Max(1, wmn)).ToString("0.0") + "x");
                }
                File.WriteAllText(Path.Combine(baseDir, "ratevar.txt"), rb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--barlevel")
            {
                Skin sk = Skin.Load(dir, "liquid_crystal");
                int[] buf = new int[sk.W * sk.H];
                RenderState st = new RenderState();
                st.RxHist = new double[9]; st.TxHist = new double[9];
                st.Ping = 30; st.Conn = 2; st.TxBar = 0;
                st.RxBulletAmp = 0; st.TxBulletAmp = 0;
                st.RipplePhase = 0;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                double[] kbs = new double[] { 0, 16, 64, 256, 512, 1024, 1536, 4096 };
                Rectangle r = sk.Layout.RxBarRect;
                foreach (double kb in kbs)
                {
                    st.RxBar = Math.Min(1.0, Math.Log10(1 + kb) / 14.0);
                    sk.Render(buf, st);
                    int w = 0;
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        for (int y = r.Top; y < r.Bottom; y++)
                        {
                            int p = buf[y * sk.W + x];
                            int a = (p >> 24) & 0xFF;
                            int rr = (p >> 16) & 0xFF, gg = (p >> 8) & 0xFF, bb = p & 0xFF;
                            if (a > 200 && rr > 140 && gg > 60 && gg < 200 && bb < 140) { w = x - r.Left + 1; break; }
                        }
                    }
                    sb.AppendLine(kb.ToString("0") + " KB/s -> bar " + w + "px of " + r.Width);
                }
                File.WriteAllText(Path.Combine(baseDir, "barlevel.txt"), sb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--barstat")
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                Sampler sp = new Sampler("");
                sp.Sample();
                for (int i = 0; i < 10; i++)
                {
                    Snapshot sn = sp.Sample();
                    double bps = sn.RxAvg;
                    double kb = bps / 1024.0;
                    double lvl = Math.Min(1.0, kb / 1536.0);
                    int frame = (int)Math.Floor(lvl * 25.0);
                    sb.AppendLine("rx=" + kb.ToString("0.0") + "KB/s level=" + lvl.ToString("0.000") + " frame=" + frame + " width=" + (frame*2+1) + "px");
                    System.Threading.Thread.Sleep(120);
                }
                File.WriteAllText(Path.Combine(baseDir, "barstat.txt"), sb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--connstat")
            {
                Sampler sp = new Sampler("");
                System.Text.StringBuilder cb = new System.Text.StringBuilder();
                for (int i = 0; i < 6; i++)
                {
                    Snapshot sn = sp.Sample();
                    cb.AppendLine("sample " + i + " conn=" + (sn.TcpCount + sn.UdpCount) + " tcp=" + sn.TcpCount + " udp=" + sn.UdpCount);
                    System.Threading.Thread.Sleep(400);
                }
                File.WriteAllText(Path.Combine(baseDir, "connstat.txt"), cb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--pingstat")
            {
                if (args.Length > 1) Sampler.PingTarget = args[1];
                Sampler sp = new Sampler("");
                System.Text.StringBuilder pb = new System.Text.StringBuilder();
                pb.AppendLine("target=" + Sampler.PingTarget);
                for (int i = 0; i < 12; i++)
                {
                    sp.PingOnce();
                    pb.AppendLine("probe " + i + " ping=" + sp.PingMs + " fails=" + sp.PingFails);
                    System.Threading.Thread.Sleep(220);
                }
                File.WriteAllText(Path.Combine(baseDir, "pingstat.txt"), pb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--pulse")
            {
                // Simulate a probe cadence and report how the icon brightness decays.
                // Proves the lamp breathes with the interval instead of staying lit.
                Skin sk = Skin.Load(dir, "liquid_crystal");
                int[] buf = new int[sk.W * sk.H];
                RenderState st = new RenderState();
                st.RxHist = new double[9]; st.TxHist = new double[9];
                st.Ping = 40; st.Conn = 2; st.RxBar = 0.4; st.TxBar = 0.2;
                System.Text.StringBuilder pb = new System.Text.StringBuilder();
                int interval = 2000;
                if (args.Length > 1) int.TryParse(args[1], out interval);
                double window = interval * 0.40;
                if (window < 900) window = 900;
                if (window > 2600) window = 2600;
                pb.AppendLine("interval=" + interval + " window=" + window.ToString("0"));
                double pulse = 1.0;
                for (int t = 0; t <= interval; t += 200)
                {
                    st.PingPulse = pulse > 0 ? pulse : 0;
                    sk.Render(buf, st);
                    Rectangle ir = sk.Layout.PingIconRect;
                    long sum = 0; int n = 0;
                    for (int y = ir.Top; y < ir.Bottom; y++)
                        for (int x = ir.Left; x < ir.Right; x++)
                        {
                            int p = buf[y * sk.W + x];
                            int a = (p >> 24) & 0xFF;
                            int rr = (p >> 16) & 0xFF, gg = (p >> 8) & 0xFF, bb = p & 0xFF;
                            sum += (int)(a * (rr * 0.3 + gg * 0.7 + bb * 0.1) / 255);
                            n++;
                        }
                    pb.AppendLine("t=" + t + "ms pulse=" + pulse.ToString("0.00") + " icon=" + (n > 0 ? (sum / (double)n).ToString("0.0") : "0"));
                    pulse -= 200.0 / window;
                    if (pulse < 0) pulse = 0;
                }
                File.WriteAllText(Path.Combine(baseDir, "pulse.txt"), pb.ToString());
                return;
            }
            if (args.Length >= 1 && args[0] == "--nics")
            {
                System.Text.StringBuilder nb = new System.Text.StringBuilder();
                System.Collections.Generic.List<NicInfo> live = Sampler.ListNics();
                nb.AppendLine("count=" + live.Count);
                foreach (NicInfo n in live)
                    nb.AppendLine((n.Up ? "[up]   " : "[down] ") + n.Name + " | " + n.Desc);
                File.WriteAllText(Path.Combine(baseDir, "nics.txt"), nb.ToString());
                return;
            }
            if (args.Length >= 2 && args[0] == "--levels")
            {
                // Render the bar at many levels and report the bar region's mean luminance.
                // A hard frame-quantised renderer produces ~26 flat steps; a cross-faded one
                // produces a smooth monotonic ramp. This is the regression check for "not smooth".
                Skin sk = Skin.Load(dir, "liquid_crystal");
                int[] buf = new int[sk.W * sk.H];
                RenderState st = new RenderState();
                st.RxHist = new double[9];
                st.TxHist = new double[9];
                st.Ping = 30;
                st.Conn = 2;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i <= 20; i++)
                {
                    double lvl = i / 20.0;
                    st.RxBar = lvl;
                    st.TxBar = lvl;
                    sk.Render(buf, st);
                    Rectangle r = sk.Layout.RxBarRect;
                    long sum = 0; int n = 0;
                    for (int y = r.Top; y < r.Bottom; y++)
                        for (int x = r.Left; x < r.Right; x++)
                        {
                            int p = buf[y * sk.W + x];
                            int a = (p >> 24) & 0xFF;
                            int rr = (p >> 16) & 0xFF, gg = (p >> 8) & 0xFF, bb = p & 0xFF;
                            sum += (int)(a * (rr * 0.3 + gg * 0.6 + bb * 0.1) / 255);
                            n++;
                        }
                    sb.AppendLine(lvl.ToString("0.00") + " " + (n > 0 ? (sum / (double)n).ToString("0.000") : "0"));
                }
                File.WriteAllText(Path.Combine(baseDir, "levels.txt"), sb.ToString());
                return;
            }
            if (args.Length >= 2 && args[0] == "--probe")
            {
                WidgetForm f = new WidgetForm(dir, false);
                int span = 9000;
                int.TryParse(args[1], out span);
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = Math.Max(2000, span);
                t.Tick += delegate
                {
                    t.Stop();
                    try { File.WriteAllText(Path.Combine(baseDir, "probe.txt"), f.DiagProbe()); }
                    catch (Exception ex) { Log(ex); }
                    Application.Exit();
                };
                t.Start();
                Application.Run(f);
                return;
            }
            if (args.Length >= 1 && args[0] == "--diagpass")
            {
                WidgetForm f = new WidgetForm(dir, false);
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1200;
                t.Tick += delegate
                {
                    t.Stop();
                    try { File.WriteAllText(Path.Combine(baseDir, "diagpass.txt"), f.DiagPassToggle()); }
                    catch (Exception ex) { Log(ex); }
                    Application.Exit();
                };
                t.Start();
                Application.Run(f);
                return;
            }
            if (args.Length >= 1 && args[0] == "--diag")
            {
                WidgetForm f = new WidgetForm(dir, false);
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1200;
                t.Tick += delegate
                {
                    t.Stop();
                    try
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.AppendLine("exstyle=0x" + f.DiagExStyle.ToString("X8"));
                        sb.AppendLine("size=" + f.Width + "x" + f.Height);
                        sb.AppendLine("loc=" + f.Left + "," + f.Top);
                        sb.AppendLine("passthrough=" + f.DiagPassThrough);
                        sb.AppendLine("hotkeyRegistered=" + f.hotkeyOk);
                        sb.AppendLine("dpi=" + f.DeviceDpi);
                        sb.AppendLine("--- menu ---");
                        sb.Append(f.DiagMenu());
                        File.WriteAllText(Path.Combine(baseDir, "diag.txt"), sb.ToString());
                    }
                    catch { }
                    Application.Exit();
                };
                t.Start();
                Application.Run(f);
                return;
            }
            // Only one widget may run. A second launch (double-click on the exe, autostart
            // racing a manual start) pokes the running copy and exits instead of stacking a
            // second tray icon and a second layered window on top.
            if (!ClaimSingleInstance())
            {
                SignalExistingInstance();
                return;
            }
            try { Application.Run(new WidgetForm(dir, false)); }
            catch (Exception ex) { Log(ex); throw; }
        }

        const string InstanceMutexName = "Local\\CfosWidget.SingleInstance";
        const string InstanceShowEvent = "Local\\CfosWidget.Show";

        static Mutex instanceMutex;

        static bool ClaimSingleInstance()
        {
            try
            {
                bool created;
                instanceMutex = new Mutex(true, InstanceMutexName, out created);
                if (created) return true;
                instanceMutex.Dispose();
                instanceMutex = null;
                return false;
            }
            catch { return true; }   // the guard must never be the reason a start fails
        }

        static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(InstanceShowEvent))
                    ev.Set();
            }
            catch { }
        }

        static void Log(Exception ex)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(p, DateTime.Now.ToString("s") + "\r\n" + (ex == null ? "(null)" : ex.ToString()) + "\r\n\r\n");
            }
            catch { }
        }

        static void Shot(string dir, string outPath, string skinName, double flow)
        {
            Skin skin = Skin.Load(dir, skinName);
            int[] px = new int[skin.W * skin.H];
            RenderState st = new RenderState();
            if (flow > 0) { st.Flow = flow; st.RipplePhase = 0.28; }
            st.RxCps = 161.0 * 1024; st.TxCps = 121.0 * 1024;
            st.RxBar = 0.62; st.TxBar = 0.38;
            st.Ping = 30; st.Conn = 2;
            st.RxBullet = 4; st.TxBullet = 7;
            st.RxBulletAmp = 1.0; st.TxBulletAmp = 1.0;
            st.RxHist = new double[] { 0.15, 0.30, 0.46, 0.34, 0.62, 0.55, 0.72, 0.84, 0.62 };
            st.TxHist = new double[] { 0.10, 0.22, 0.18, 0.35, 0.28, 0.44, 0.36, 0.30, 0.38 };
            skin.Render(px, st);
            Bitmap b = new Bitmap(skin.W, skin.H, PixelFormat.Format32bppArgb);
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* p0 = (byte*)bd.Scan0;
                    for (int y = 0; y < b.Height; y++)
                    {
                        int* row = (int*)(p0 + y * bd.Stride);
                        for (int x = 0; x < b.Width; x++) row[x] = px[y * b.Width + x];
                    }
                }
            }
            finally { b.UnlockBits(bd); }
            b.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "raw_" + skinName + ".png"), ImageFormat.Png);
            using (Bitmap bg = new Bitmap(skin.W, skin.H))
            {
                using (Graphics g = Graphics.FromImage(bg))
                {
                    using (System.Drawing.Drawing2D.LinearGradientBrush br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, skin.W, skin.H), Color.FromArgb(22, 34, 52), Color.FromArgb(34, 26, 44), 45f))
                        g.FillRectangle(br, 0, 0, skin.W, skin.H);
                    g.DrawImage(b, new Rectangle(0, 0, skin.W, skin.H));
                }
                using (Bitmap big = new Bitmap(skin.W * 4, skin.H * 4))
                {
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(bg, new Rectangle(0, 0, big.Width, big.Height));
                    }
                    big.Save(outPath, ImageFormat.Png);
                }
            }
        }
    }
}
