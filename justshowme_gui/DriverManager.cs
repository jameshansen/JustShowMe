using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace JustShowMe
{
    /// Installs / uninstalls / tracks the JustShowMe Virtual Webcam DirectShow
    /// driver (justshowme_cam.dll). Registration is done with regsvr32 (needs
    /// elevation, so we launch it with the runas verb).
    public static class DriverManager
    {
        // Must match CLSID_DShowSoftcam in the C++ driver (justshowme_cam).
        private const string Clsid = "{7F3B2C10-9A4D-4E6F-B821-3C5D7E9A1B20}";

        public static string DriverPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "justshowme_cam.dll");

        public static bool IsInstalled => File.Exists(DriverPath);

        public static bool IsRegistered =>
            KeyExists(RegistryView.Registry64) || KeyExists(RegistryView.Registry32);

        private static bool KeyExists(RegistryView view)
        {
            using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
            using (var k = hkcr.OpenSubKey($@"CLSID\{Clsid}"))
                return k != null;
        }

        public static bool Register() => RunRegsvr32(false);
        public static bool Unregister() => RunRegsvr32(true);

        private static bool RunRegsvr32(bool unregister)
        {
            if (!IsInstalled)
                throw new FileNotFoundException("Driver not found next to the GUI.", DriverPath);

            var args = (unregister ? "/u " : "") + "/s \"" + DriverPath + "\"";
            var psi = new ProcessStartInfo("regsvr32.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas", // elevate
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // user declined the UAC prompt
            }
        }
    }
}
