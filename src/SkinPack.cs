using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace CfosWidget
{
    /// <summary>
    /// Skins ship as a zip embedded in the exe so the build is a single green file.
    /// On first use the pack is extracted to %TEMP%\cfoswidget-skins\&lt;hash&gt; and every
    /// skin loader goes through ResolveDir(), which prefers an extracted copy but still
    /// honours a plain "skins" folder next to the exe when one exists (so dropping a new
    /// cFos skin in stays possible without rebuilding).
    /// </summary>
    static class SkinPack
    {
        const string ResName = "skins.zip";
        static string root;
        static bool tried;

        public static string Root
        {
            get
            {
                if (tried) return root;
                tried = true;
                root = Extract();
                return root;
            }
        }

        static string Extract()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                Stream res = asm.GetManifestResourceStream(ResName);
                if (res == null)
                {
                    // no embedded pack: fall back to a plain folder beside the exe
                    string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins");
                    return Directory.Exists(local) ? local : null;
                }

                string stamp;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    stamp = BitConverter.ToString(sha.ComputeHash(StreamToBytes(res))).Replace("-", "").Substring(0, 16);

                string dest = Path.Combine(Path.Combine(Path.GetTempPath(), "cfoswidget-skins"), stamp);
                string marker = Path.Combine(dest, ".ok");
                if (!File.Exists(marker))
                {
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.CreateDirectory(dest);
                    res.Position = 0;
                    using (ZipArchive zip = new ZipArchive(res, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(e.Name)) continue;   // directory entry
                            string target = Path.Combine(dest, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            e.ExtractToFile(target, true);
                        }
                    }
                    File.WriteAllText(marker, "ok");
                }
                return dest;
            }
            catch { return null; }
        }

        static byte[] StreamToBytes(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Available skin names (folder names inside the pack).</summary>
        public static List<string> List()
        {
            List<string> names = new List<string>();
            string r = Root;
            if (r == null || !Directory.Exists(r)) return names;
            foreach (string d in Directory.GetDirectories(r))
                names.Add(Path.GetFileName(d));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        static readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Extracted-or-on-disk path for a skin folder, or null when unknown.</summary>
        public static string ResolveDir(string name)
        {
            string r = Root;
            if (r == null || string.IsNullOrEmpty(name)) return null;
            string d = Path.Combine(r, name);
            if (Directory.Exists(d)) return d;
            // accept a folder dropped beside the exe
            string local = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins"), name);
            return Directory.Exists(local) ? local : null;
        }
    }
}
