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
        /// One file per saved face. Under ProgramData\JustShowMe (writable without
        /// elevation, unlike Program Files), beside settings.ini.
        public static readonly string FaceListDir = Path.Combine(Dir, "facelist");

        public int CameraIndex;
        public FilterMode Mode;
        public PersonMode PersonMode;
        public double BodyScale;
        public double SmartFillSeconds;
        public double GhostSustainSeconds;
        public int BlurStrength;
        public double MatchThreshold;
        public int SnapshotCount;
        public int Width, Height, Fps;

        private const string DefaultFilterName = "justshowme_filter.dll";

        /// The bundled filter, beside the *currently running* exe.
        public static string DefaultFilterDll =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultFilterName);

        public static Config Load()
        {
            var c = new Config
            {
                CameraIndex = GetInt("Camera", "Index", 0),
                Mode = GetString("Filter", "Mode", "BlurNotAllowed") == "BlurAll"
                    ? FilterMode.BlurAll : FilterMode.BlurNotAllowed,
                PersonMode = ParsePersonMode(GetString("Filter", "PersonMode", "BlurFace")),
                BodyScale = GetDouble("Filter", "BodyScale", 3.2),
                SmartFillSeconds = GetDouble("Filter", "SmartFillSeconds", 1.0),
                GhostSustainSeconds = GetDouble("Filter", "GhostSustainSeconds", 3.0),
                BlurStrength = GetInt("Filter", "BlurStrength", 51),
                MatchThreshold = GetDouble("Filter", "MatchThreshold", 0.40),
                SnapshotCount = GetInt("Filter", "SnapshotCount", 5),
                Width = GetInt("VirtualCam", "Width", 640),
                Height = GetInt("VirtualCam", "Height", 480),
                Fps = GetInt("VirtualCam", "Fps", 30),
            };
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            Set("Camera", "Index", CameraIndex.ToString());
            Set("Filter", "Mode", Mode.ToString());
            Set("Filter", "PersonMode", PersonMode.ToString());
            Set("Filter", "BodyScale", BodyScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Filter", "SmartFillSeconds", SmartFillSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Filter", "GhostSustainSeconds", GhostSustainSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Filter", "BlurStrength", BlurStrength.ToString());
            Set("Filter", "MatchThreshold", MatchThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Filter", "SnapshotCount", SnapshotCount.ToString());
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

        private static PersonMode ParsePersonMode(string s) =>
            Enum.TryParse(s, out PersonMode m) ? m : PersonMode.BlurFace;

        private static int GetInt(string s, string k, int def) =>
            int.TryParse(GetString(s, k, def.ToString()), out int v) ? v : def;

        private static double GetDouble(string s, string k, double def) =>
            double.TryParse(GetString(s, k, def.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

        private static void Set(string s, string k, string v) =>
            WritePrivateProfileString(s, k, v, Path_);
    }
}
