using System;
using System.Windows;

namespace JustShowMe
{
    public partial class App : Application
    {
        private void App_Startup(object sender, StartupEventArgs e)
        {
            Log.Write($"==== JustShowMe GUI starting (64-bit={Environment.Is64BitProcess}) ====");

            // UI-thread exceptions: show + log, keep running.
            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Write("DispatcherUnhandledException", ex.Exception);
                MessageBox.Show(ex.Exception.ToString(), "JustShowMe error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            // Background-thread exceptions (e.g. the pump timer) can't be handled,
            // but we can at least log them before the process dies.
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                Log.Write("AppDomain.UnhandledException", ex.ExceptionObject as Exception
                          ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));

            new MainWindow().Show();
        }
    }
}
