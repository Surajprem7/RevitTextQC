using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitTextQC.Core;

namespace RevitTextQC.Updates
{
    /// <summary>Where releases are published. The release workflow uploads RevitTextQC-&lt;RevitVersion&gt;.zip assets.</summary>
    public static class UpdateConfig
    {
        public const string Owner = "Surajprem7";
        public const string Repo = "RevitTextQC";

        public static bool IsConfigured => Owner.Length > 0;
        public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";
        public static string AssetName(string revitVersion) => $"RevitTextQC-{revitVersion}.zip";
    }

    public class UpdateCheck
    {
        public ReleaseInfo Release { get; set; }
        public ReleaseAsset Asset { get; set; }
        public bool IsNewer { get; set; }
    }

    /// <summary>
    /// Checks GitHub releases, downloads the build for the running Revit version and stages it.
    /// Revit locks loaded add-in files, so the staged files are copied into place by a small
    /// script that waits for Revit to exit.
    /// </summary>
    public static class UpdateManager
    {
        private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(12);
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(CreateClient);

        private static string UpdatesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitTextQC", "updates");

        private static string StagedFolder => Path.Combine(UpdatesFolder, "staged-" + App.RevitVersion);
        private static string StagedMarker => Path.Combine(UpdatesFolder, "staged-" + App.RevitVersion + ".txt");
        private static string LastCheckFile => Path.Combine(UpdatesFolder, "last-check.txt");
        private static string LogFile => Path.Combine(UpdatesFolder, "update-" + App.RevitVersion + ".log");

        private static HttpClient CreateClient()
        {
#if NETFRAMEWORK
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RevitTextQC", App.CurrentVersion.ToString(3)));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        // ---- checking ----------------------------------------------------------------------

        public static async Task<UpdateCheck> CheckAsync()
        {
            if (!UpdateConfig.IsConfigured)
                throw new InvalidOperationException("No GitHub repository is configured for updates.");

            using (HttpResponseMessage response = await Http.Value.GetAsync(UpdateConfig.LatestReleaseApi).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("No published release was found on GitHub.");
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                ReleaseInfo release = ReleaseInfo.Parse(json);
                return new UpdateCheck
                {
                    Release = release,
                    Asset = release.FindAsset(UpdateConfig.AssetName(App.RevitVersion)),
                    IsNewer = VersionUtil.IsNewer(release.Version, App.CurrentVersion)
                };
            }
        }

        /// <summary>Version already downloaded and waiting for Revit to close, or null.</summary>
        public static Version StagedVersion
        {
            get
            {
                try
                {
                    if (File.Exists(StagedMarker) && Directory.Exists(StagedFolder))
                        return VersionUtil.Parse(File.ReadAllText(StagedMarker));
                }
                catch (IOException) { }
                return null;
            }
        }

        // ---- downloading -------------------------------------------------------------------

        /// <summary>Downloads the release build for this Revit version, verifies it and unpacks it into the staging folder.</summary>
        public static async Task StageAsync(UpdateCheck check)
        {
            if (check.Asset == null)
                throw new InvalidOperationException(
                    $"Release {check.Release.TagName} has no build for Revit {App.RevitVersion} ({UpdateConfig.AssetName(App.RevitVersion)}).");

            // Only ever download from the project's own repository, whatever the API response says.
            string expectedPrefix = $"https://github.com/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/download/";
            if (!check.Asset.DownloadUrl.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The release points to an unexpected download location.");

            Directory.CreateDirectory(UpdatesFolder);
            string zipPath = Path.Combine(UpdatesFolder, check.Asset.Name + ".download");

            using (HttpResponseMessage response = await Http.Value
                       .GetAsync(check.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (FileStream target = File.Create(zipPath))
                {
                    await source.CopyToAsync(target).ConfigureAwait(false);
                }
            }

            try
            {
                VerifyChecksum(zipPath, check.Asset.Sha256);

                if (File.Exists(StagedMarker)) File.Delete(StagedMarker);
                if (Directory.Exists(StagedFolder)) Directory.Delete(StagedFolder, true);
                ExtractSafely(zipPath, StagedFolder);

                if (!File.Exists(Path.Combine(StagedFolder, "RevitTextQC", "RevitTextQC.dll")))
                    throw new InvalidDataException("The downloaded package does not contain the add-in.");

                File.WriteAllText(StagedMarker, check.Release.TagName);
            }
            finally
            {
                File.Delete(zipPath);
            }
        }

        private static void VerifyChecksum(string file, string expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256)) return; // older GitHub API responses carry no digest

            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded update failed its checksum and was discarded.");
            }
        }

        // Entries are checked one by one so a crafted archive cannot write outside the staging folder.
        private static void ExtractSafely(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The update package contains an invalid path.");

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        // ---- startup -----------------------------------------------------------------------

        /// <summary>Non-blocking check when Revit starts. Never throws and never delays startup.</summary>
        public static void StartBackgroundCheck()
        {
            if (!UpdateConfig.IsConfigured || !App.Settings.AutoCheckUpdates || !DueForCheck()) return;
            bool install = App.Settings.AutoInstallUpdates;

            Task.Run(async () =>
            {
                try
                {
                    UpdateCheck check = await CheckAsync().ConfigureAwait(false);
                    Directory.CreateDirectory(UpdatesFolder);
                    File.WriteAllText(LastCheckFile, DateTime.UtcNow.ToString("o"));

                    if (!check.IsNewer) return;
                    Version staged = StagedVersion;
                    if (staged != null && !VersionUtil.IsNewer(check.Release.Version, staged)) return; // already waiting

                    if (install && check.Asset != null)
                    {
                        await StageAsync(check).ConfigureAwait(false);
                        Notify($"Text QC {check.Release.TagName} has been downloaded.",
                            "It will be installed automatically when you close Revit.");
                    }
                    else
                    {
                        Notify($"Text QC {check.Release.TagName} is available.",
                            "Use Text QC > Check for Updates to install it.");
                    }
                }
                catch (Exception ex)
                {
                    Log("Background update check failed: " + ex.Message);
                }
            });
        }

        private static bool DueForCheck()
        {
            try
            {
                if (!File.Exists(LastCheckFile)) return true;
                DateTime last = DateTime.Parse(File.ReadAllText(LastCheckFile), null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                return DateTime.UtcNow - last > AutoCheckInterval;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void Notify(string instruction, string content)
        {
            RevitDispatcher.Post(app =>
            {
                var dialog = new TaskDialog(App.Title) { MainInstruction = instruction, MainContent = content };
                dialog.Show();
            });
        }

        // ---- manual check ------------------------------------------------------------------

        /// <summary>The "Check for Updates" ribbon button.</summary>
        public static void CheckInteractively()
        {
            UpdateCheck check;
            try
            {
                // Task.Run keeps the continuation off Revit's UI thread, so blocking here cannot deadlock.
                check = Task.Run(CheckAsync).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(App.Title, "Could not check for updates.\n\n" + Describe(ex));
                return;
            }

            string current = App.CurrentVersion.ToString(3);
            if (!check.IsNewer)
            {
                TaskDialog.Show(App.Title, $"Text QC {current} is up to date.");
                return;
            }

            Version staged = StagedVersion;
            if (staged != null && !VersionUtil.IsNewer(check.Release.Version, staged))
            {
                TaskDialog.Show(App.Title,
                    $"Text QC {check.Release.TagName} is already downloaded and will be installed when you close Revit.");
                return;
            }

            var dialog = new TaskDialog(App.Title)
            {
                MainInstruction = $"Text QC {check.Release.TagName} is available (you have {current}).",
                MainContent = Shorten(check.Release.Notes, 600),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Download now and install when Revit closes");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the releases page");

            TaskDialogResult choice = dialog.Show();
            if (choice == TaskDialogResult.CommandLink2)
            {
                Process.Start(new ProcessStartInfo(UpdateConfig.ReleasesPage) { UseShellExecute = true });
            }
            else if (choice == TaskDialogResult.CommandLink1)
            {
                try
                {
                    Task.Run(() => StageAsync(check)).GetAwaiter().GetResult();
                    TaskDialog.Show(App.Title, $"Text QC {check.Release.TagName} is ready and will be installed when you close Revit.");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(App.Title, "The update could not be downloaded.\n\n" + Describe(ex));
                }
            }
        }

        private static string Describe(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            return ex.InnerException != null ? ex.Message + "\n" + ex.InnerException.Message : ex.Message;
        }

        private static string Shorten(string text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "â€¦";
        }

        // ---- installing --------------------------------------------------------------------

        /// <summary>
        /// Called when Revit shuts down. Starts a hidden script that waits for this Revit process to exit
        /// and then copies the staged files over the installed add-in.
        /// </summary>
        public static void ApplyStagedUpdateOnExit()
        {
            try
            {
                if (StagedVersion == null) return;

                // Addins\<version>\RevitTextQC\RevitTextQC.dll -> Addins\<version>
                string installRoot = Path.GetDirectoryName(App.InstallFolder);
                string script = Path.Combine(UpdatesFolder, "apply-" + App.RevitVersion + ".cmd");
                int pid = Process.GetCurrentProcess().Id;

                var cmd = new StringBuilder();
                cmd.AppendLine("@echo off");
                cmd.AppendLine("chcp 65001 >nul");
                cmd.AppendLine("setlocal");
                cmd.AppendLine("set /a tries=0");
                cmd.AppendLine(":wait");
                cmd.AppendLine($"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \" {pid} \" >nul");
                cmd.AppendLine("if errorlevel 1 goto copy");
                cmd.AppendLine("set /a tries+=1");
                cmd.AppendLine("if %tries% GEQ 300 goto end");
                cmd.AppendLine("ping -n 3 127.0.0.1 >nul");
                cmd.AppendLine("goto wait");
                cmd.AppendLine(":copy");
                // Another Revit session of the same version can still hold the files: retry, and if it
                // keeps failing leave the staged files in place for the next shutdown.
                cmd.AppendLine($"robocopy \"{StagedFolder}\" \"{installRoot}\" /E /R:10 /W:3 /NP > \"{LogFile}\" 2>&1");
                cmd.AppendLine("if errorlevel 8 goto end");
                cmd.AppendLine($"rmdir /S /Q \"{StagedFolder}\"");
                cmd.AppendLine($"del /Q \"{StagedMarker}\"");
                cmd.AppendLine(":end");
                File.WriteAllText(script, cmd.ToString(), new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = UpdatesFolder
                });
            }
            catch (Exception ex)
            {
                Log("Could not start the update installer: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(UpdatesFolder);
                File.AppendAllText(Path.Combine(UpdatesFolder, "updater.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}
