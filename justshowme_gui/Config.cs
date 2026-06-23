using System;
using System.IO;
using System.Runtime.InteropServices;
using JustShowMe.Filter;

namespace JustShowMe
{
    /// Reads/writes %ProgramData%\JustShowMe\settings.ini so the GUI (and any
    /// other JustShowMe process) share one config. Uses the Win32 INI API rather
    /// than a parser dependency. ponytail: Win32 profile API is the native INI store.
    public sealed class Config
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key, string def,
            System.Text.StringBuilder val, int size, string path);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string section, string key, string val, string path);

        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JustShowMe");
        public static readonly string Path_ = Path.Combine(Dir, "settings.ini");

        public int CameraIndex;
        public FilterMode Mode;
        public int BlurStrength;
        public string FilterDllPath;
        public int Width, Height, Fps;

        public static string DefaultFilterDll =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "justshowme_filter.dll");

        public static Config Load()
        {
            var c = new Config
            {
                CameraIndex = GetInt("Camera", "Index", 0),
                Mode = GetString("Filter", "Mode", "BlurNotAllowed") == "BlurAll"
                    ? FilterMode.BlurAll : FilterMode.BlurNotAllowed,
                BlurStrength = GetInt("Filter", "BlurStrength", 51),
                FilterDllPath = GetString("Filter", "DllPath", DefaultFilterDll),
                Width = GetInt("VirtualCam", "Width", 640),
                Height = GetInt("VirtualCam", "Height", 480),
                Fps = GetInt("VirtualCam", "Fps", 30),
            };
            if (string.IsNullOrWhiteSpace(c.FilterDllPath)) c.FilterDllPath = DefaultFilterDll;
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            Set("Camera", "Index", CameraIndex.ToString());
            Set("Filter", "Mode", Mode.ToString());
            Set("Filter", "BlurStrength", BlurStrength.ToString());
            Set("Filter", "DllPath", FilterDllPath);
            Set("VirtualCam", "Width", Width.ToString());
            Set("VirtualCam", "Height", Height.ToString());
            Set("VirtualCam", "Fps", Fps.ToString());
        }

        private static string GetString(string s, string k, string def)
        {
            var sb = new System.Text.StringBuilder(1024);
            GetPrivateProfileString(s, k, def, sb, sb.Capacity, Path_);
            return sb.ToString();
        }

        private static int GetInt(string s, string k, int def) =>
            int.TryParse(GetString(s, k, def.ToString()), out int v) ? v : def;

        private static void Set(string s, string k, string v) =>
            WritePrivateProfileString(s, k, v, Path_);
    }
}
