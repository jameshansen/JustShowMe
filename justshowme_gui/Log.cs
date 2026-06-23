using System;
using System.IO;

namespace JustShowMe
{
    /// Dead-simple append log to %ProgramData%\JustShowMe\log.txt. Used to catch
    /// failures on background threads (the pump timer) that never reach the UI.
    public static class Log
    {
        public static readonly string Path_ = System.IO.Path.Combine(Config.Dir, "log.txt");
        private static readonly object _lock = new object();

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                lock (_lock)
                    File.AppendAllText(Path_,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
        }

        public static void Write(string context, Exception ex) => Write($"{context}: {ex}");
    }
}
