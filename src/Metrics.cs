using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CfosWidget
{
    public struct Snapshot
    {
        public double Rx;          // bytes per second (instant)
        public double Tx;
        public double RxAvg;       // smoothed
        public double TxAvg;
        public int PingMs;         // -1 = unknown
        public int TcpCount;
        public int UdpCount;
        public bool LinkUp;
    }

    public class NicInfo
    {
        public string Id;
        public string Name;
        public string Desc;
        public bool Up;
        public override string ToString()
        {
            if (string.IsNullOrEmpty(Name) || Name == Desc) return Desc;
            return Name + " - " + Desc;
        }
    }

    public class Sampler
    {
        // --- iphlpapi ---
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int UDP_TABLE_OWNER_PID = 1;
        const int MIB_TCP_STATE_ESTAB = 5;

        string nicId;
        long lastRx, lastTx;
        Stopwatch clock = new Stopwatch();
        long lastTicks;
        bool primed;

        public double RxSmooth, TxSmooth;
        public int PingMs = -1;
        public static string PingTarget = "223.5.5.5";

        public Sampler(string id)
        {
            nicId = id;
            clock.Start();
            lastTicks = 0;
        }

        public string NicId
        {
            get { return nicId; }
            set { nicId = value; primed = false; }
        }

        // Fires whenever the adapter set changes (USB dongle in, WiFi toggled, VPN up).
        // Without this the menu kept showing the list captured at startup.
        public static List<NicInfo> ListNics()
        {
            List<NicInfo> list = new List<NicInfo>();
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < all.Length; i++)
            {
                NetworkInterface ni = all[i];
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                NicInfo n = new NicInfo();
                n.Id = ni.Id;
                n.Name = ni.Name;
                n.Desc = ni.Description;
                n.Up = ni.OperationalStatus == OperationalStatus.Up;
                list.Add(n);
            }
            return list;
        }

        // Same list, but never blocks. GetAdaptersAddresses costs 100-300 ms right after a
        // VPN dials or a dongle is plugged in, and RebuildMenu runs on the UI thread while
        // the tray menu is trying to paint. The menu reuses the last snapshot; a worker
        // refreshes it in the background.
        static List<NicInfo> nicSnapshot = new List<NicInfo>();
        static volatile bool nicRefreshing;
        static long nicSnapshotAt;

        public static List<NicInfo> ListNicsCached()
        {
            long now = Environment.TickCount;
            if (!nicRefreshing && now - nicSnapshotAt >= 2000)
            {
                nicRefreshing = true;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { nicSnapshot = ListNics(); nicSnapshotAt = Environment.TickCount; }
                    catch { }
                    finally { nicRefreshing = false; }
                });
            }
            return nicSnapshot;
        }

        // Warm the snapshot before the first menu open so it is never empty on screen.
        public static void WarmNics()
        {
            nicRefreshing = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { nicSnapshot = ListNics(); nicSnapshotAt = Environment.TickCount; }
                catch { }
                finally { nicRefreshing = false; }
            });
        }

        public static NetworkInterface Find(string id)
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Id == id) return all[i];
            }
            return null;
        }

        long ReadRx(NetworkInterface ni)
        {
            try { return ni.GetIPv4Statistics().BytesReceived; }
            catch { return 0; }
        }

        long ReadTx(NetworkInterface ni)
        {
            try { return ni.GetIPv4Statistics().BytesSent; }
            catch { return 0; }
        }

        // Perf counters on Windows are batched by the driver: bytes arrive in bursts, so a
        // 60 ms delta swings between 0 and tens of MB/s. Keep a short ring of (time, bytes)
        // and divide the whole window instead.
        const int WinSlots = 9;          // ~540 ms at 60 ms ticks (clamped by WinMaxMs)

        // Each direction needs its OWN ring: sharing one meant the Tx call advanced the
        // cursor the Rx call had just written, so timestamps and byte counts desynchronised
        // and the rate collapsed toward zero every other tick.
        class RateRing
        {
            public readonly long[] At = new long[WinSlots];
            public readonly long[] Bytes = new long[WinSlots];
            public int Pos;
            public int Count;

            public void Reset() { Pos = 0; Count = 0; }

            public double Push(long nowBytes, long nowMs)
            {
                At[Pos] = nowMs;
                Bytes[Pos] = nowBytes;
                if (Count < WinSlots) Count++;
                Pos = (Pos + 1) % WinSlots;
                if (Count < 2) return 0;
                int oldest = (Pos - Count + WinSlots * 2) % WinSlots;
                double dt = (nowMs - At[oldest]) / 1000.0;
                if (dt <= 0.05) return 0;
                // Cap the effective window: an unbounded ring at a slow tick rate keeps
                // averaging in stale traffic, so a finished download lingered for over a
                // second and looked like a slow decay rather than a stop.
                if (dt > 0.70) dt = 0.70;
                long db = nowBytes - Bytes[oldest];
                if (db < 0) return 0;
                return db / dt;
            }
        }

        readonly RateRing ringRx = new RateRing();
        readonly RateRing ringTx = new RateRing();

        public string CurrentNicId { get { return nicId; } }
        public long RawRx { get { return lastRx; } }
        public long RawTx { get { return lastTx; } }

        public Snapshot Sample()
        {
            Snapshot s = new Snapshot();
            NetworkInterface ni = string.IsNullOrEmpty(nicId) ? null : Find(nicId);
            if (ni == null)
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].OperationalStatus == OperationalStatus.Up && all[i].NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        ni = all[i];
                        nicId = all[i].Id;
                        break;
                    }
                }
            }
            if (ni != null)
            {
                s.LinkUp = ni.OperationalStatus == OperationalStatus.Up;
                long rx = ReadRx(ni);
                long tx = ReadTx(ni);
                long now = clock.ElapsedMilliseconds;
                // A counter that goes backwards means the adapter re-enumerated. Skipping the
                // sample is enough; wiping the ring made the readout collapse to zero every
                // time that happened, which read as wild fluctuation.
                if (rx >= lastRx) s.Rx = ringRx.Push(rx, now);
                else { s.Rx = RxSmooth; ringRx.Reset(); }
                if (tx >= lastTx) s.Tx = ringTx.Push(tx, now);
                else { s.Tx = TxSmooth; ringTx.Reset(); }
                lastRx = rx;
                lastTx = tx;
                lastTicks = now;
            }
            // smoothing: fast attack, slow release
            RxSmooth = Smooth(RxSmooth, s.Rx);
            TxSmooth = Smooth(TxSmooth, s.Tx);
            s.RxAvg = RxSmooth;
            s.TxAvg = TxSmooth;
            s.PingMs = PingMs;
            int viaCfos = CountViaCfos();
            if (viaCfos >= 0)
            {
                // cFosSpeed already folded tcp+udp into its list
                s.TcpCount = viaCfos;
                s.UdpCount = 0;
            }
            else
            {
                s.TcpCount = CountTcp();
                s.UdpCount = CountUdp();
            }
            return s;
        }

        static double Smooth(double oldv, double nv)
        {
            // Measured: the original's bar holds ONE width across 150 consecutive samples
            // while mine stepped 6 <-> 8 constantly. Its time constant is far longer, so damp
            // much harder here -- the sliding window already removed driver batching.
            if (nv > oldv) return oldv + (nv - oldv) * 0.16;
            return oldv + (nv - oldv) * 0.10;
        }

        public static string Gateway()
        {
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].OperationalStatus != OperationalStatus.Up) continue;
                    if (all[i].NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    GatewayIPAddressInformationCollection gw = all[i].GetIPProperties().GatewayAddresses;
                    for (int j = 0; j < gw.Count; j++)
                    {
                        IPAddress a = gw[j].Address;
                        if (a != null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                            return a.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        // A single dropped ICMP reply used to wipe the reading to -1, which greyed the
        // icon and blanked the digits until the next tick happened to succeed. Keep the
        // last good value and only declare "no signal" after several consecutive misses.
        public int PingFails;
        public int PingSeq;      // increments on every fresh successful measurement
        const int PingGiveUp = 3;

        public void PingOnce()
        {
            string target = PingTarget;
            if (string.IsNullOrEmpty(target)) target = "223.5.5.5";
            int got = -1;
            for (int attempt = 0; attempt < 2 && got < 0; attempt++)
            {
                try
                {
                    using (Ping p = new Ping())
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        PingReply r = p.Send(target, 1200);
                        sw.Stop();
                        if (r != null && r.Status == IPStatus.Success && r.RoundtripTime >= 0)
                        {
                            // Ping() only reports whole milliseconds, so on a fast link every
                            // reading collapses onto the same integer. Average the library's
                            // value with a high-resolution stopwatch to recover real jitter.
                            double ms = r.RoundtripTime;
                            double wall = sw.Elapsed.TotalMilliseconds;
                            if (wall > 0 && wall < ms + 160) ms = (ms + wall) / 2.0;
                            got = (int)Math.Max(1, Math.Round(ms));
                        }
                    }
                }
                catch { }
                if (got < 0) System.Threading.Thread.Sleep(120);
            }
            if (got >= 0)
            {
                // light smoothing: keep the trend, drop single-sample noise
                if (PingMs > 0) PingMs = (int)Math.Round(PingMs * 0.35 + got * 0.65);
                else PingMs = got;
                PingFails = 0;
                PingSeq++;
            }
            else
            {
                PingFails++;
                if (PingFails >= PingGiveUp) PingMs = -1;   // keep the old value for 1-2 misses
            }
        }

        // Ask cFosSpeed itself what it is shaping. spd cons costs ~20 ms when it answers but
        // can block for the whole 1.2 s timeout while the shaper is busy, and Sample() runs
        // on the UI thread -- that stall is what froze the tray right-click. The shell-out
        // now runs on a worker and Sample() only ever reads the last cached answer.
        static string consPath;
        static bool consProbed;
        static int consCached = -1;
        static long consAt;
        static bool consUnavailable;
        static bool consBusy;
        static readonly object consLock = new object();

        static int CountViaCfos()
        {
            lock (consLock)
            {
                if (consUnavailable) return -1;
                long now = Environment.TickCount;
                if (!consBusy && (consCached < 0 || now - consAt >= 700)) KickConsProbe();
                return consCached;
            }
        }

        // Caller holds consLock.
        static void KickConsProbe()
        {
            consBusy = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int v = -1;
                try { v = CountViaCfosBlocking(); }
                catch { v = -1; }
                lock (consLock)
                {
                    if (v >= 0) { consCached = v; consAt = Environment.TickCount; }
                    else if (consPath == null) consUnavailable = true;   // no spd.exe: stop trying
                    else consAt = Environment.TickCount;                 // transient miss, retry later
                    consBusy = false;
                }
            });
        }

        static int CountViaCfosBlocking()
        {
            if (!consProbed)
            {
                consProbed = true;
                string[] cands = new string[] {
                    @"C:\Program Files\cFosSpeed\spd.exe",
                    @"C:\Program Files (x86)\cFosSpeed\spd.exe" };
                for (int i = 0; i < cands.Length; i++)
                    if (System.IO.File.Exists(cands[i])) { consPath = cands[i]; break; }
            }
            if (consPath == null) return -1;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = consPath;
                psi.Arguments = "cons";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process pp = System.Diagnostics.Process.Start(psi))
                {
                    string outp = pp.StandardOutput.ReadToEnd();
                    pp.WaitForExit(1200);
                    int n = 0;
                    string[] lines = outp.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string s = lines[i].Trim();
                        if (s.StartsWith("tcp ") || s.StartsWith("udp ")) n++;
                    }
                    return n;
                }
            }
            catch { consPath = null; return -1; }
        }

        static int CountTcp()
        {
            int size = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return 0;
                int n = Marshal.ReadInt32(buf);
                int count = 0;
                int rowSize = 24;
                for (int i = 0; i < n; i++)
                {
                    int baseIdx = 4 + i * rowSize;
                    int state = Marshal.ReadInt32(buf, baseIdx);
                    if (state != MIB_TCP_STATE_ESTAB) continue;
                    // cFosSpeed's own `cons` listing never includes loopback sockets, and those
                    // dominate the raw count (70 of 96 here). Skip 127.0.0.0/8 so the number
                    // tracks real off-box traffic the way the original widget does.
                    int localAddr = Marshal.ReadInt32(buf, baseIdx + 4);
                    if ((localAddr & 0xFF) == 127) continue;
                    count++;
                }
                return count;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // UDP has no connections to speak of: every bound socket shows up and the count
        // balloons into the hundreds, which is why the number looked like noise.
        // cFosSpeed's counter is effectively the live TCP session count, so mirror that.
        static int CountUdp()
        {
            return 0;
        }
    }
}
