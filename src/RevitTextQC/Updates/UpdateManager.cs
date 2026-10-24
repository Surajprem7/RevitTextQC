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
        public static string LatestReleasePage => $"https://github.com/{Owner}/{Repo}/releases/latest";
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
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(CreateClient);

        private static string UpdatesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitTextQC", "updates");

        private static string StagedFolder => Path.Combine(UpdatesFolder, "staged-" + App.RevitVersion);
        private static string StagedMarker => Path.Combine(UpdatesFolder, "staged-" + App.RevitVersion + ".txt");
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

            // Short timeout: the ribbon button waits on this, and a dead network must not freeze Revit for minutes.
            using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (HttpResponseMessage response = await Http.Value.GetAsync(UpdateConfig.LatestReleaseApi, timeout.Token).ConfigureAwait(false))
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

        /// <summary>
        /// The updater copies a release over Addins\&lt;year&gt;\RevitTextQC.addin + RevitTextQC\. If the add-in was
        /// installed some other way, copying would scatter files in the wrong place, so it is refused.
        /// </summary>
        public static bool InstallLayoutIsStandard
        {
            get
            {
                try
                {
                    string folder = App.InstallFolder;
                    return string.Equals(Path.GetFileName(folder), "RevitTextQC", StringComparison.OrdinalIgnoreCase)
                           && File.Exists(Path.Combine(Path.GetDirectoryName(folder), "RevitTextQC.addin"));
                }
                catch (Exception)
                {
                    return false;
                }
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

                // Start waiting for Revit to end now, so the install happens however Revit ends.
                LaunchInstaller();
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
            // An update downloaded in an earlier session that was never installed: install it when this one ends.
            LaunchInstaller();

            if (!UpdateConfig.IsConfigured || !App.Settings.AutoCheckUpdates) return;
            bool install = App.Settings.AutoInstallUpdates;

            Task.Run(async () =>
            {
                try
                {
                    // Every start asks GitHub's website for the newest tag, which is not rate limited. The API
                    // (60 calls an hour per internet address) is used only once a newer version really exists,
                    // so a whole office starting Revit at 9 o'clock cannot exhaust it.
                    Version latest = VersionUtil.Parse(await LatestTagAsync().ConfigureAwait(false));
                    if (!VersionUtil.IsNewer(latest, App.CurrentVersion)) return;
                    Version waiting = StagedVersion;
                    if (waiting != null && !VersionUtil.IsNewer(latest, waiting)) return; // already downloaded

                    UpdateCheck check = await CheckAsync().ConfigureAwait(false);

                    if (!check.IsNewer) return;
                    Version staged = StagedVersion;
                    if (staged != null && !VersionUtil.IsNewer(check.Release.Version, staged)) return; // already waiting

                    if (install && check.Asset != null && InstallLayoutIsStandard)
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

        /// <summary>Tag of the newest release, read from the redirect of github.com/.../releases/latest.</summary>
        private static async Task<string> LatestTagAsync()
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RevitTextQC", App.CurrentVersion.ToString(3)));
                using (HttpResponseMessage response = await client.GetAsync(UpdateConfig.LatestReleasePage,
                           HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    Uri location = response.Headers.Location;
                    if (location == null) return null;

                    // /owner/repo/releases/tag/v0.2.4
                    string path = location.AbsolutePath;
                    int marker = path.LastIndexOf("/tag/", StringComparison.Ordinal);
                    return marker < 0 ? null : Uri.UnescapeDataString(path.Substring(marker + "/tag/".Length));
                }
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
            if (InstallLayoutIsStandard)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Download now and install when Revit closes");
            else
                dialog.MainContent = "This copy was not installed in the standard add-ins folder, so it cannot update itself. " +
                                     "Download the new version from the releases page.\n\n" + dialog.MainContent;
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
            if (ex is OperationCanceledException) return "GitHub did not respond in time. Check the internet connection and try again.";
            return ex.InnerException != null ? ex.Message + "\n" + ex.InnerException.Message : ex.Message;
        }

        private static string Shorten(string text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        // ---- installing --------------------------------------------------------------------

        /// <summary>Called when Revit shuts down cleanly. The installer is normally already waiting; this is the backup.</summary>
        public static void ApplyStagedUpdateOnExit()
        {
            LaunchInstaller();
        }

        private static bool _installerLaunched;

        /// <summary>
        /// Starts a hidden script that waits for this Revit process to end, however it ends (closed, crashed or
        /// ended from Task Manager), and then copies the staged files over the installed add-in. It is started
        /// as soon as an update is downloaded, and at every start while one is still waiting, instead of at
        /// shutdown, because a shutdown that never completes cleanly would otherwise leave the update uninstalled.
        /// </summary>
        private static void LaunchInstaller()
        {
            try
            {
                if (_installerLaunched || !InstallLayoutIsStandard) return;
                Version staged = StagedVersion;
                if (staged == null || !VersionUtil.IsNewer(staged, App.CurrentVersion)) return;

                // Addins\<version>\RevitTextQC\RevitTextQC.dll -> Addins\<version>
                string installRoot = Path.GetDirectoryName(App.InstallFolder);
                string mainDll = Path.Combine(App.InstallFolder, "RevitTextQC.dll");
                string coreDll = Path.Combine(App.InstallFolder, "RevitTextQC.Core.dll");
                int pid = Process.GetCurrentProcess().Id;

                // One script per Revit session: cmd reads a script as it runs it, so a script that is still
                // waiting must never be overwritten. Scripts left by sessions long gone are tidied away.
                string script = Path.Combine(UpdatesFolder, "apply-" + App.RevitVersion + "-" + pid + ".cmd");
                foreach (string old in Directory.GetFiles(UpdatesFolder, "apply-" + App.RevitVersion + "*.cmd"))
                {
                    try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2)) File.Delete(old); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                var cmd = new StringBuilder();
                cmd.AppendLine("@echo off");
                cmd.AppendLine("chcp 65001 >nul");
                cmd.AppendLine("setlocal");
                // Waits for as long as this Revit session lives.
                cmd.AppendLine(":wait");
                cmd.AppendLine($"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \" {pid} \" >nul");
                cmd.AppendLine("if errorlevel 1 goto copy");
                cmd.AppendLine("ping -n 11 127.0.0.1 >nul");
                cmd.AppendLine("goto wait");
                cmd.AppendLine(":copy");
                // Another Revit session of the same version can still hold the add-in. Copying then would
                // replace some files and not others, leaving a mixed-version install, so test the two
                // assemblies for a write lock first and leave everything staged for the next shutdown.
                cmd.AppendLine($"if exist \"{mainDll}\" ( ( (call ) >>\"{mainDll}\" ) 2>nul || goto end )");
                cmd.AppendLine($"if exist \"{coreDll}\" ( ( (call ) >>\"{coreDll}\" ) 2>nul || goto end )");
                // /IS /IT: copy even when size and timestamp match, so a staged file is never silently skipped.
                cmd.AppendLine($"robocopy \"{StagedFolder}\" \"{installRoot}\" /E /IS /IT /R:10 /W:3 /NP > \"{LogFile}\" 2>&1");
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
                _installerLaunched = true;
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
