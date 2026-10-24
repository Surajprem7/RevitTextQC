using System;
using System.IO;

namespace RevitTextQC
{
    /// <summary>
    /// A small log of what went wrong, so "it shows nothing" from a colleague can be answered. Errors are
    /// otherwise handled quietly (a status line) so they never interrupt work in Revit.
    /// </summary>
    internal static class Support
    {
        private const long MaxBytes = 1000000;

        public static string LogFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitTextQC");

        public static string LogPath => Path.Combine(LogFolder, "textqc.log");

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);

                // Keep the last two megabytes at most.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    string older = LogPath + ".1";
                    if (File.Exists(older)) File.Delete(older);
                    File.Move(LogPath, older);
                }

                string version = App.CurrentVersion?.ToString(3) ?? "?";
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{version} Revit {App.RevitVersion}  {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Logging must never cause a problem of its own.
            }
        }

        public static void Log(string what, Exception ex)
        {
            Log(what + ": " + ex);
        }
    }
}
