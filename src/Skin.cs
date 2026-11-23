using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CfosWidget
{
    public class FrameImg
    {
        public string Name;
        public int W;
        public int H;
        public int FrameH;
        public int Count;
        public int[] Px;      // straight ARGB per pixel, rows top->bottom

        public static int TransparentColor = -1;

        public static FrameImg Load(string dir, string file, int frameH)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string p = Path.Combine(dir, file);
            if (!File.Exists(p)) return null;
            using (Bitmap b = new Bitmap(p))
            {
                FrameImg f = new FrameImg();
                f.Name = file;
                f.W = b.Width;
                f.H = b.Height;
                f.FrameH = frameH <= 0 ? b.Height : frameH;
                f.Count = Math.Max(1, b.Height / f.FrameH);
                f.Px = new int[f.W * f.H];
                BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* p0 = (byte*)bd.Scan0;
                        for (int y = 0; y < b.Height; y++)
                        {
                            int* row = (int*)(p0 + y * bd.Stride);
                            for (int x = 0; x < b.Width; x++)
                            {
                                int v = row[x];
                                if (TransparentColor >= 0 && (v & 0x00FFFFFF) == (TransparentColor & 0x00FFFFFF)) v = 0;
                                f.Px[y * f.W + x] = v;
                            }
                        }
                    }
                }
                finally { b.UnlockBits(bd); }
                return f;
            }
        }
    }

    public class RenderState
    {
        public double RxBar;      // 0..1 amplitude for the download meter
        public double TxBar;
        public double RxCps;      // bytes/s
        public double TxCps;
        public int Ping = -1;
        public int Conn;
        public double PingPulse;  // 0..1 secondary animation
        public double RipplePhase; // seconds
        public double RxBullet;   // fractional frame position, wraps 0..count
        public double TxBullet;
        public double RxBulletAmp; // 0..1 how strongly it is currently moving
        public double TxBulletAmp;
        public double Flow;       // 0..1 traffic intensity, drives the ripple animation
        public double[] RxHist;   // 0..1 amplitude history, oldest first
        public double[] TxHist;
    }

    public class Layout
    {
        public Rectangle RxBarRect;
        public Rectangle TxBarRect;
        public Rectangle BulletRxRect;
        public Rectangle BulletTxRect;
        public Rectangle PingIconRect;
        public Rectangle PingTextRect;
        public Rectangle MsRect;
        public Rectangle ConnIconRect;
        public Rectangle ConnTextRect;
        public Rectangle RxTextRect;
        public Rectangle TxTextRect;
        public string Mode;   // "single" or "history"
        public int[] HistoryX;
    }

    public class Skin
    {
        public string Dir;
        public string Title;
        public IniFile Ini;
        public Layout Layout = new Layout();
        public int W = 117;
        public int H = 61;

        Dictionary<string, FrameImg> cache = new Dictionary<string, FrameImg>(StringComparer.OrdinalIgnoreCase);
        public FrameImg Font;

        public int[] CharWidth = new int[128];
        public int RippleAlpha = 96;   // the ripple is a sheen, not a background

        // Smoothstep: gentle at both ends, so the lamp eases down instead of snapping off.
        static double SmoothStep(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3.0 - 2.0 * t);
        }
        public int PanelAlpha = 255;   // strength of the skin's own glass panel

        public static Skin Load(string dirOrName, string title)
        {
            // Accept either a full path or a bare skin name ("liquid_crystal"); the latter is
            // resolved through the embedded pack so a single-file build works with no folders.
            string dir = Directory.Exists(dirOrName) ? dirOrName : SkinPack.ResolveDir(dirOrName);
            if (dir == null) dir = SkinPack.ResolveDir("liquid_crystal");
            Skin s = new Skin();
            s.Dir = dir;
            s.Title = title;
            s.Ini = dir == null ? new IniFile() : IniFile.Load(Path.Combine(dir, "net.ini"));
            IniSection all = s.Ini["all"];
            if (all != null && all.Get("name") != null) s.Title = all.Get("name");
            if (all != null)
            {
                string tc = all.Get("transparentcolor");
                if (!string.IsNullOrEmpty(tc))
                {
                    string[] p3 = tc.Split(',');
                    int r0, g0, b0;
                    if (p3.Length >= 3 && int.TryParse(p3[0].Trim(), out r0) && int.TryParse(p3[1].Trim(), out g0) && int.TryParse(p3[2].Trim(), out b0))
                        FrameImg.TransparentColor = (r0 << 16) | (g0 << 8) | b0;
                }
            }

            string bg = dir == null ? null : Path.GetFullPath(Path.Combine(dir, "back.png"));
            if (bg != null && File.Exists(bg))
            {
                using (Bitmap b = new Bitmap(bg)) { s.W = b.Width; s.H = b.Height; }
            }

            IniSection nc = s.Ini["num_chars"];
            int fh = 13;
            if (nc != null) fh = nc.GetInt("frameheight", 13);
            s.Font = s.Img("num_chars.png", fh);
            for (int i = 0; i < 128; i++) s.CharWidth[i] = 8;
            if (nc != null)
            {
                string cw = nc.Get("char_width");
                if (!string.IsNullOrEmpty(cw))
                {
                    string[] parts = cw.Split(' ');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string p = parts[i];
                        int c = p.IndexOf(':');
                        if (c <= 0) continue;
                        string chs = p.Substring(0, c);
                        int w;
                        if (!int.TryParse(p.Substring(c + 1), out w)) continue;
                        for (int j = 0; j < chs.Length; j++) s.CharWidth[(int)chs[j]] = w;
                    }
                }
                string dw = nc.Get("default_char_width");
                int defw;
                if (dw != null && int.TryParse(dw, out defw))
                    for (int i = 0; i < 128; i++) if (s.CharWidth[i] == 8) s.CharWidth[i] = defw;
            }

            s.BuildLayout();
            return s;
        }

        public FrameImg Img(string file, int frameH)
        {
            FrameImg f;
            if (cache.TryGetValue(file, out f)) return f;
            f = FrameImg.Load(Dir, file, frameH);
            cache[file] = f;
            return f;
        }

        string R(IniSection s, string key, string def) { return s == null ? def : s.Get(key, def); }

        static Rectangle ParseRect(string v)
        {
            if (string.IsNullOrEmpty(v)) return Rectangle.Empty;
            string[] p = v.Split(',');
            if (p.Length < 4) return Rectangle.Empty;
            int x, y, a, b;
            if (!int.TryParse(p[0].Trim(), out x)) return Rectangle.Empty;
            if (!int.TryParse(p[1].Trim(), out y)) return Rectangle.Empty;
            if (!int.TryParse(p[2].Trim(), out a)) return Rectangle.Empty;
            if (!int.TryParse(p[3].Trim(), out b)) return Rectangle.Empty;
            return Rectangle.FromLTRB(x, y, a, b);
        }

        void BuildLayout()
        {
            IniSection s6 = Ini["disp6"];
            string hd = R(s6, "hdisp1", null);
            IniSection bar = hd == null ? null : Ini[hd];
            Layout.RxBarRect = ParseRect(R(bar, "rect", "15,22,69,40"));
            IniSection s7 = Ini["disp7"];
            IniSection bar2 = Ini[s7 == null ? "" : R(s7, "hdisp1", "")];
            Layout.TxBarRect = ParseRect(R(bar2, "rect", "15,41,69,59"));

            Layout.Mode = "single";
            if (s6 != null && s6.GetInt("historysize", 1) > 1)
            {
                Layout.Mode = "history";
                int n = s6.GetInt("historysize", 9);
                int[] xs = new int[n];
                for (int i = 0; i < n; i++)
                {
                    IniSection hs = Ini["rx_speed_disp_t" + (i + 1)];
                    Rectangle r = ParseRect(R(hs, "rect", "15,22,24,40"));
                    xs[i] = r.X;
                }
                Layout.HistoryX = xs;
            }

            Layout.BulletRxRect = ParseRect(R(Ini["disp2"], "rect", "4,22,15,40"));
            Layout.BulletTxRect = ParseRect(R(Ini["disp3"], "rect", "4,41,15,59"));
            Layout.PingIconRect = ParseRect(R(Ini["disp10"], "rect", "4,5,19,21"));
            Layout.PingTextRect = ParseRect(R(Ini["disp17"], "rect", "17,7,56,20"));
            Layout.MsRect = ParseRect(R(Ini["disp18"], "rect", "53,9,73,20"));
            // the ini rects overlap by a few px; the digits must stop where the "ms" plate starts
            if (Layout.MsRect.Width > 0 && Layout.PingTextRect.Right > Layout.MsRect.Left)
                Layout.PingTextRect = Rectangle.FromLTRB(Layout.PingTextRect.Left, Layout.PingTextRect.Top,
                    Layout.MsRect.Left, Layout.PingTextRect.Bottom);
            Layout.ConnIconRect = ParseRect(R(Ini["disp21"], "rect", "73,4,89,21"));
            Layout.ConnTextRect = ParseRect(R(Ini["disp34"], "rect", "73,7,112,20"));
            Layout.RxTextRect = ParseRect(R(Ini["disp8"], "rect", "47,25,112,38"));
            Layout.TxTextRect = ParseRect(R(Ini["disp9"], "rect", "47,44,112,57"));
        }

        // ---------- compositing ----------
        // Composite one skin frame over a subtle dark backing plate. Used for the connection
        // counter, whose artwork is drawn in low-alpha greys that vanish on light wallpapers.
        static void BlendPlate(int[] dst, int dw, int dh, FrameImg img, int frame, int x, int y)
        {
            if (img == null || img.Px == null) return;
            if (frame < 0) frame = 0;
            if (frame >= img.Count) frame = img.Count - 1;
            int off = frame * img.FrameH * img.W;
            for (int yy = 0; yy < img.FrameH; yy++)
            {
                int dy = y + yy;
                if (dy < 0 || dy >= dh) continue;
                for (int xx = 0; xx < img.W; xx++)
                {
                    int dx = x + xx;
                    if (dx < 0 || dx >= dw) continue;
                    int s = img.Px[off + yy * img.W + xx];
                    int sa = (s >> 24) & 0xFF;
                    if (sa == 0) continue;
                    int di = dy * dw + dx;
                    int d = dst[di];
                    int da = (d >> 24) & 0xFF;
                    int sr = (s >> 16) & 0xFF, sg = (s >> 8) & 0xFF, sb = s & 0xFF;
                    // lift the artwork's own luminance so thin edges survive
                    sr = Math.Min(255, sr + 40);
                    sg = Math.Min(255, sg + 42);
                    sb = Math.Min(255, sb + 46);
                    sa = Math.Min(255, (int)(sa * 1.9));
                    int rest = 255 - sa;
                    int outA = sa + da * rest / 255;
                    int dr = (d >> 16) & 0xFF, dg = (d >> 8) & 0xFF, db = d & 0xFF;
                    int orr = (sr * sa + dr * da * rest / 255) / Math.Max(1, outA);
                    int og = (sg * sa + dg * da * rest / 255) / Math.Max(1, outA);
                    int ob = (sb * sa + db * da * rest / 255) / Math.Max(1, outA);
                    if (orr > 255) orr = 255; if (og > 255) og = 255; if (ob > 255) ob = 255;
                    dst[di] = (outA << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        // Only the outer N pixels of the sheet: keeps the glass bevel, leaves the interior clean.
        static void BlendRim(int[] dst, int dw, int dh, FrameImg img, int frame, int rim)
        {
            if (img == null || img.Px == null) return;
            if (frame < 0) frame = 0;
            if (frame >= img.Count) frame = img.Count - 1;
            int off = frame * img.FrameH * img.W;
            for (int yy = 0; yy < img.FrameH; yy++)
            {
                int dy = yy;
                if (dy < 0 || dy >= dh) continue;
                for (int xx = 0; xx < img.W; xx++)
                {
                    bool edge = yy < rim || yy >= img.FrameH - rim || xx < rim || xx >= img.W - rim;
                    if (!edge) continue;
                    int dx = xx;
                    if (dx < 0 || dx >= dw) continue;
                    int s = img.Px[off + yy * img.W + xx];
                    int sa = (s >> 24) & 0xFF;
                    if (sa == 0) continue;
                    int di = dy * dw + dx;
                    int d = dst[di];
                    int da = (d >> 24) & 0xFF;
                    int sr = (s >> 16) & 0xFF, sg = (s >> 8) & 0xFF, sb = s & 0xFF;
                    int rest = 255 - sa;
                    int outA = sa + da * rest / 255;
                    int dr = (d >> 16) & 0xFF, dg = (d >> 8) & 0xFF, db = d & 0xFF;
                    int orr = (sr * sa + dr * da * rest / 255) / Math.Max(1, outA);
                    int og = (sg * sa + dg * da * rest / 255) / Math.Max(1, outA);
                    int ob = (sb * sa + db * da * rest / 255) / Math.Max(1, outA);
                    if (orr > 255) orr = 255; if (og > 255) og = 255; if (ob > 255) ob = 255;
                    dst[di] = (outA << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        static void Blend(int[] dst, int dw, int dh, FrameImg img, int frame, int x, int y, int alphaScale)
        {
            Blend(dst, dw, dh, img, frame, x, y, alphaScale, img == null ? 0 : img.W);
        }

        static void Blend(int[] dst, int dw, int dh, FrameImg img, int frame, int x, int y, int alphaScale, int srcW)
        {
            if (img == null || img.Px == null) return;
            if (frame < 0) frame = 0;
            if (frame >= img.Count) frame = img.Count - 1;
            if (srcW <= 0) srcW = img.W;
            if (srcW > img.W) srcW = img.W;
            int off = frame * img.FrameH * img.W;
            for (int yy = 0; yy < img.FrameH; yy++)
            {
                int dy = y + yy;
                if (dy < 0 || dy >= dh) continue;
                for (int xx = 0; xx < srcW; xx++)
                {
                    int dx = x + xx;
                    if (dx < 0 || dx >= dw) continue;
                    int s = img.Px[off + yy * img.W + xx];
                    int sa = (s >> 24) & 0xFF;
                    if (sa == 0) continue;
                    if (alphaScale < 255) sa = sa * alphaScale / 255;
                    if (sa == 0) continue;
                    int sr = (s >> 16) & 0xFF, sg = (s >> 8) & 0xFF, sb = s & 0xFF;
                    int di = dy * dw + dx;
                    int d = dst[di];
                    int da = (d >> 24) & 0xFF;
                    if (sa >= 255 || da == 0)
                    {
                        dst[di] = (sa << 24) | (sr << 16) | (sg << 8) | sb;
                        continue;
                    }
                    int rest = 255 - sa;
                    int outA = sa + da * rest / 255;
                    int dr = (d >> 16) & 0xFF, dg = (d >> 8) & 0xFF, db = d & 0xFF;
                    int orr = (sr * sa + dr * da * rest / 255) / Math.Max(1, outA);
                    int og = (sg * sa + dg * da * rest / 255) / Math.Max(1, outA);
                    int ob = (sb * sa + db * da * rest / 255) / Math.Max(1, outA);
                    if (orr > 255) orr = 255; if (og > 255) og = 255; if (ob > 255) ob = 255;
                    dst[di] = (outA << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        // Draw a looping animation at a fractional frame position by cross-fading the two
        // neighbouring frames. "amp" fades the whole thing out when there is no traffic.
        static void BlendCycle(int[] dst, int dw, int dh, FrameImg img, double pos, int x, int y, double amp)
        {
            if (img == null || img.Count < 2) return;
            if (amp <= 0.004) return;
            double span = img.Count;
            double p = pos % span;
            if (p < 0) p += span;
            int i0 = (int)Math.Floor(p);
            int i1 = (i0 + 1) % img.Count;
            double t = p - i0;
            double a = amp > 1 ? 1 : amp;
            int baseA = (int)Math.Round(255 * a);
            Blend(dst, dw, dh, img, i0, x, y, baseA);
            int overlay = (int)Math.Round(baseA * t);
            if (overlay > 0) Blend(dst, dw, dh, img, i1, x, y, overlay);
        }

        // A skin only has 26 bar frames, so a hard frame switch looks like stepping.
        // Cross-fade the two frames the level sits between: motion stays continuous
        // while the artwork is still the skin's own.
        static void BlendLevel(int[] dst, int dw, int dh, FrameImg img, double level,
            int x, int y, int alpha, int srcW)
        {
            if (img == null) return;
            if (level < 0) level = 0;
            if (level > 1) level = 1;
            double f = level * (img.Count - 1);
            int i0 = (int)Math.Floor(f);
            int i1 = i0 + 1;
            if (i1 > img.Count - 1) i1 = img.Count - 1;
            // These sheets are a left-to-right growth animation: frame 0 has no bar at all and
            // frame 1 is a 2 px sliver. Drawing BOTH neighbouring frames painted the empty
            // base as a red block under the real bar, which is why the meter looked like a
            // solid square instead of a thin line at low speed. Draw only the taller frame.
            int frame = (f - i0) >= 0.5 ? i1 : i0;
            Blend(dst, dw, dh, img, frame, x, y, alpha, srcW);
        }

        // Snap the level to a frame but bias upward so a slowly rising value does not
        // hover between two frames; that edge flicker is what read as "blurry".
        static int QuantizeFrame(double v, int count)
        {
            if (v <= 0) return 0;
            if (v >= 1) return count - 1;
            double f = v * (count - 1);
            int i = (int)Math.Floor(f);
            if (f - i > 0.72) i++;
            if (i < 0) i = 0;
            if (i > count - 1) i = count - 1;
            return i;
        }

        // fontbitmapchars=0 123456789KMGT.  -> frame 0 is the default '0', then " 123456789KMGT."
        static int GlyphIndex(char c)
        {
            if (c >= '1' && c <= '9') return c - '0' + 1;
            if (c == '0') return 0;
            if (c == ' ') return 1;
            if (c == 'K') return 11;
            if (c == 'M') return 12;
            if (c == 'G') return 13;
            if (c == 'T') return 14;
            if (c == '.') return 15;
            if (c == '-') return 1;
            return 0;
        }

        int TextWidth(string s)
        {
            int w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                w += (c < 128) ? CharWidth[(int)c] : 8;
            }
            return w;
        }

        // Shrink a value to the box while never eating its trailing unit letter:
        // "161.0K" -> "161K" -> "61K", not "161." .
        string FitText(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return text;
            char unit = '\0';
            char last = text[text.Length - 1];
            if (last == 'K' || last == 'M' || last == 'G' || last == 'T')
            {
                unit = last;
                text = text.Substring(0, text.Length - 1);
            }
            string body = text;
            while (true)
            {
                string candidate = body + (unit == '\0' ? "" : unit.ToString());
                if (TextWidth(candidate) <= width) return candidate;
                int dot = body.IndexOf('.');
                if (dot >= 0) body = body.Substring(0, dot);
                else if (body.Length > 1) body = body.Substring(1);
                else return candidate;
            }
        }

        void DrawText(int[] dst, int dw, int dh, string text, Rectangle box, int alphaScale)
        {
            if (Font == null || string.IsNullOrEmpty(text)) return;
            int tw = TextWidth(text);
            if (tw > box.Width) text = FitText(text, box.Width);
            tw = TextWidth(text);
            int x = box.Right - tw;
            int y = box.Top + (box.Height - Font.FrameH) / 2;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int gi = GlyphIndex(c);
                int w = (c < 128) ? CharWidth[(int)c] : 8;
                Blend(dst, dw, dh, Font, gi, x, y, alphaScale, w);
                x += w;
            }
        }

        // The skin font ships K/M/G/T glyphs (fontbitmapchars=0 123456789KMGT.) exactly
        // so a rate can carry its own unit; use them instead of leaving a bare number.
        public static string FormatSpeed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 0) kb = 0;
            // One decimal while there are only two or three significant digits, integer
            // once the digits themselves carry enough resolution. The unit always rides along.
            if (kb < 100.0)
            {
                if (kb < 0.05) kb = 0;
                return kb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "K";
            }
            if (kb < 1000.0) return kb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "K";
            double mb = kb / 1024.0;
            if (mb < 100.0) return mb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
            if (mb < 1000.0) return mb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "M";
            double gb = mb / 1024.0;
            return gb.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "G";
        }

        public void Render(int[] dst, RenderState st)
        {
            int w = W, h = H;
            Array.Clear(dst, 0, w * h);

            // 0. panel base: the skin's own glass sheet is the background, NOT a top sheen.
            //    Drawing it last washed out every glyph and bar edge.
            FrameImg glass0 = Img("glass.png", h);
            if (glass0 != null) Blend(dst, w, h, glass0, 0, 0, 0, PanelAlpha);

            // 1. animated ripple background
            IniSection sd = Ini["shape_disp"];
            if (sd != null)
            {
                FrameImg rip = Img(R(sd, "bitmap", "ripple.png"), h);
                if (rip != null)
                {
                    int idleFr = sd.GetInt("idleframe", 1);
                    int fr;
                    if (st.Flow <= 0.004) fr = idleFr;
                    else fr = idleFr + (int)(st.RipplePhase * (14.0 + 46.0 * st.Flow)) % Math.Max(1, rip.Count - idleFr);
                    if (fr >= rip.Count) fr = rip.Count - 1;
                    Blend(dst, w, h, rip, fr, 0, 0, RippleAlpha);
                }
                else
                {
                    IniSection sw = Ini["shape_disp"];
                    string bmp = R(sw, "bitmap", null);
                    FrameImg o = bmp == null ? null : Img(bmp, 0);
                    if (o != null) Blend(dst, w, h, o, 0, 0, 0, 255);
                }
            }

            // 2. download / upload meters
            if (Layout.Mode == "single")
            {
                FrameImg rb = Img("rx_bar.png", Layout.RxBarRect.Height);
                if (rb != null)
                {
                    BlendLevel(dst, w, h, rb, st.RxBar, Layout.RxBarRect.X, Layout.RxBarRect.Y, 255, Layout.RxBarRect.Width);
                }
                FrameImg tb = Img("tx_bar.png", Layout.TxBarRect.Height);
                if (tb != null)
                {
                    BlendLevel(dst, w, h, tb, st.TxBar, Layout.TxBarRect.X, Layout.TxBarRect.Y, 255, Layout.TxBarRect.Width);
                }
            }
            else
            {
                FrameImg rb = Img("rx_bars.png", Layout.RxBarRect.Height);
                FrameImg tb = Img("tx_bars.png", Layout.TxBarRect.Height);
                int n = Layout.HistoryX == null ? 9 : Layout.HistoryX.Length;
                for (int i = 0; i < n; i++)
                {
                    int alpha = 55 + i * 25;
                    if (alpha > 255) alpha = 255;
                    double rv = (st.RxHist != null && i < st.RxHist.Length) ? st.RxHist[i] : st.RxBar;
                    double tv = (st.TxHist != null && i < st.TxHist.Length) ? st.TxHist[i] : st.TxBar;
                    if (rb != null) Blend(dst, w, h, rb, (int)Math.Round(rv * (rb.Count - 1)), Layout.HistoryX[i], Layout.RxBarRect.Y, alpha, 9);
                    if (tb != null) Blend(dst, w, h, tb, (int)Math.Round(tv * (tb.Count - 1)), Layout.HistoryX[i], Layout.TxBarRect.Y, alpha, 9);
                }
            }

            // 3. data-flow bullets
            // The skin ships rx_bullet.png as a 14-frame position animation (the block slides
            // up and down inside an 11x18 cell), not a static icon. Cross-fade neighbouring
            // frames so motion is continuous instead of stepping.
            FrameImg rbu = Img("rx_bullet.png", Layout.BulletRxRect.Height);
            if (rbu != null && rbu.Count > 1)
                BlendCycle(dst, w, h, rbu, st.RxBullet, Layout.BulletRxRect.X, Layout.BulletRxRect.Y, st.RxBulletAmp);
            FrameImg tbu = Img("tx_bullet.png", Layout.BulletTxRect.Height);
            if (tbu != null && tbu.Count > 1)
                BlendCycle(dst, w, h, tbu, st.TxBullet, Layout.BulletTxRect.X, Layout.BulletTxRect.Y, st.TxBulletAmp);

            // 4. numbers
            DrawText(dst, w, h, FormatSpeed(st.RxCps), Layout.RxTextRect, 255);
            DrawText(dst, w, h, FormatSpeed(st.TxCps), Layout.TxTextRect, 255);
            // The value lives and dies with the lamp: it flashes up on each probe and is
            // completely gone by the next one, so the readout is a pulse rather than a label.
            if (st.Ping >= 0)
            {
                double dp = st.PingPulse;
                if (dp < 0) dp = 0;
                if (dp > 1) dp = 1;
                int da = (int)Math.Round(255 * SmoothStep(dp));
                if (da > 1) DrawText(dst, w, h, st.Ping.ToString(), Layout.PingTextRect, da);
            }
            DrawText(dst, w, h, st.Conn.ToString(), Layout.ConnTextRect, 255);

            // 5. ping icon + "ms"
            FrameImg ping = Img("ping.png", Layout.PingIconRect.Height);
            if (ping != null)
            {
                // frame 0 = grey/idle, frame 1 = coloured/live. Cross-fade on the pulse so
                // the lamp lights up on each probe and settles back instead of staying on.
                double pulse = st.PingPulse;
                if (pulse < 0) pulse = 0;
                if (pulse > 1) pulse = 1;
                if (st.Ping < 0) pulse = 0;          // no reading -> never light up

                // Off means fully off: nothing is stamped when the pulse has decayed, so the
                // lamp visibly blinks instead of leaving a grey ghost behind.
                double eased = SmoothStep(pulse);
                if (eased > 0.004)
                {
                    int a = (int)Math.Round(255 * eased);
                    if (ping.Count > 1) Blend(dst, w, h, ping, 1, Layout.PingIconRect.X, Layout.PingIconRect.Y, a);
                    else Blend(dst, w, h, ping, 0, Layout.PingIconRect.X, Layout.PingIconRect.Y, a);
                }
            }
            FrameImg ms = Img("ms.png", Layout.MsRect.Height);
            if (ms != null && st.Ping >= 0 && st.PingPulse > 0.01)
            {
                double mp = st.PingPulse;
                if (mp < 0) mp = 0;
                if (mp > 1) mp = 1;
                int ma = (int)Math.Round(255 * SmoothStep(mp));   // unit plate dies with the lamp
                Blend(dst, w, h, ms, ms.Count > 1 ? ms.Count - 1 : 0, Layout.MsRect.X, Layout.MsRect.Y, ma);
            }

            // 6. connection counter icon
            // net.ini splits the counter icon into disp21..disp24 with min/max thresholds,
            // each pointing at tcpN.png. Those sheets are 2 frames: frame 0 is blank and
            // frame 1 carries the artwork (that is what "idleframe=1" refers to).
            string tcpIcon = "tcp0.png";
            string tcpSec = "disp21";
            if (st.Conn >= 1 && st.Conn <= 5) { tcpIcon = "tcp1.png"; tcpSec = "disp22"; }
            else if (st.Conn > 5 && st.Conn <= 50) { tcpIcon = "tcp2.png"; tcpSec = "disp23"; }
            else if (st.Conn > 50) { tcpIcon = "tcp3.png"; tcpSec = "disp24"; }
            IniSection cs = Ini[tcpSec];
            int cFrame = cs == null ? 1 : cs.GetInt("idleframe", 1);
            FrameImg ti = Img(tcpIcon, Layout.ConnIconRect.Height);
            if (ti != null)
            {
                if (cFrame >= ti.Count) cFrame = ti.Count - 1;
                // Draw the skin's own pixels untouched. The frame is a solid dark-grey box in
                // the artwork (alpha 255), so brightening it fabricated a look cFos never has.
                Blend(dst, w, h, ti, cFrame, Layout.ConnIconRect.X, Layout.ConnIconRect.Y, 255);
            }

            // 7. edge bevel: re-stamp only the outermost glass rim so the panel keeps its
            //    border without fogging the middle
            FrameImg glassRim = Img("glass.png", h);
            if (glassRim != null) BlendRim(dst, w, h, glassRim, 0, 2);
        }
    }
}
