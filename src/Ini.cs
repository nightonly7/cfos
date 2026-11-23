using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CfosWidget
{
    public class IniSection
    {
        public string Name;
        public Dictionary<string, string> Kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string k)
        {
            string v;
            return Kv.TryGetValue(k, out v) ? v : null;
        }

        public string Get(string k, string def)
        {
            string v;
            return Kv.TryGetValue(k, out v) ? v : def;
        }

        public int GetInt(string k, int def)
        {
            string v = Get(k);
            int r;
            if (v != null && int.TryParse(v.Trim(), out r)) return r;
            return def;
        }

        public float GetFloat(string k, float def)
        {
            string v = Get(k);
            float r;
            if (v != null && float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        public bool GetBool(string k, bool def)
        {
            string v = Get(k);
            if (v == null) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }
    }

    public class IniFile
    {
        public List<IniSection> Sections = new List<IniSection>();
        public Dictionary<string, IniSection> Map = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
        public string Path;

        public IniSection this[string name]
        {
            get
            {
                IniSection s;
                return Map.TryGetValue(name, out s) ? s : null;
            }
        }

        public static IniFile Load(string path)
        {
            IniFile ini = new IniFile();
            ini.Path = path;
            Encoding enc = Encoding.GetEncoding(1252);
            string[] lines = File.ReadAllLines(path, enc);
            IniSection cur = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0) continue;
                if (s[0] == ';') continue;
                if (s[0] == '[')
                {
                    int close = s.IndexOf(']');
                    if (close < 0) continue;
                    cur = new IniSection();
                    cur.Name = s.Substring(1, close - 1).Trim();
                    ini.Sections.Add(cur);
                    if (!ini.Map.ContainsKey(cur.Name)) ini.Map[cur.Name] = cur;
                    continue;
                }
                if (cur == null) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string k = s.Substring(0, eq).Trim();
                string v = s.Substring(eq + 1).Trim();
                cur.Kv[k] = v;
            }
            return ini;
        }

        public static List<string> ReadLines(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            return new List<string>(File.ReadAllLines(path, Encoding.UTF8));
        }

        public static void WriteLines(string path, List<string> lines)
        {
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }
    }
}
