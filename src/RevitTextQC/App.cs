using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTextQC.Core;
using RevitTextQC.UI;
using RevitTextQC.Updates;

namespace RevitTextQC
{
    public class App : IExternalApplication
    {
        public const string Title = "Text QC";

        /// <summary>"2024" ... "2027": decides which release asset the updater downloads.</summary>
        public static string RevitVersion { get; private set; }

        public static QcSettings Settings { get; set; }

        public static string InstallFolder => Path.GetDirectoryName(typeof(App).Assembly.Location);

        public static Version CurrentVersion => typeof(App).Assembly.GetName().Version;

        public Result OnStartup(UIControlledApplication application)
        {
            // Must be hooked before the first type from a dependency is touched.
            DependencyResolver.Install();
            return Start(application);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Result Start(UIControlledApplication application)
        {
            RevitVersion = application.ControlledApplication.VersionNumber;
            Settings = QcSettings.Load();
            RevitDispatcher.Initialize();
            CreateRibbon(application);
            try
            {
                UpdateManager.StartBackgroundCheck();
            }
            catch (Exception ex)
            {
                // An updater problem must never stop the add-in itself from loading.
                Support.Log("starting the update check", ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            UpdateManager.ApplyStagedUpdateOnExit();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(Title); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

            RibbonPanel panel = application.CreateRibbonPanel(Title, "Text QC");
            string assembly = typeof(App).Assembly.Location;

            AddButton(panel, assembly, "RunQc", "Run\nText QC", typeof(RunQcCommand), "QC", 0x15, 0x70, 0xEF,
                "Check spelling, text types and sizes, letter case, spacing and naming in the model.");
            AddButton(panel, assembly, "Settings", "QC\nSettings", typeof(SettingsCommand), "S", 0x47, 0x54, 0x67,
                "Choose what is scanned and set the office text standards.");
            AddButton(panel, assembly, "Update", "Check for\nUpdates", typeof(CheckUpdatesCommand), "↑", 0x0E, 0x93, 0x84,
                "Look for a newer version of Text QC on GitHub.");
        }

        private static void AddButton(RibbonPanel panel, string assembly, string name, string text, Type command,
            string glyph, byte r, byte g, byte b, string tooltip)
        {
            var data = new PushButtonData("RevitTextQC." + name, text, assembly, command.FullName)
            {
                ToolTip = tooltip,
                LargeImage = IconFactory.Create(glyph, r, g, b, 32),
                Image = IconFactory.Create(glyph, r, g, b, 16)
            };
            panel.AddItem(data);
        }
    }

    /// <summary>
    /// Resolves the add-in's own dependencies from its folder. Revit ships different versions of common
    /// libraries (System.Memory and friends), so a version-exact bind can fail without this.
    /// </summary>
    internal static class DependencyResolver
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

                string folder = Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location);

                // Only answer for this add-in's own files. Other add-ins may also be missing, say,
                // System.Memory, and handing them our copy would change how they behave.
                string requester = args.RequestingAssembly?.Location;
                if (!string.IsNullOrEmpty(requester) &&
                    !string.Equals(Path.GetDirectoryName(requester), folder, StringComparison.OrdinalIgnoreCase))
                    return null;

                string candidate = Path.Combine(folder, name + ".dll");
                if (!File.Exists(candidate)) return null;

                // Prefer a copy Revit has already loaded over loading a second one.
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                return loaded ?? Assembly.LoadFrom(candidate);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Runs actions inside a valid Revit API context on behalf of the modeless window and the updater.</summary>
    public class RevitDispatcher : IExternalEventHandler
    {
        private static readonly ConcurrentQueue<Action<UIApplication>> Queue = new ConcurrentQueue<Action<UIApplication>>();
        private static ExternalEvent _event;

        public static void Initialize()
        {
            _event = ExternalEvent.Create(new RevitDispatcher());
        }

        public static void Post(Action<UIApplication> action)
        {
            Queue.Enqueue(action);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (Queue.TryDequeue(out var action))
            {
                try
                {
                    action(app);
                }
                catch (Exception ex)
                {
                    Support.Log("action in Revit", ex);
                    TaskDialog.Show(App.Title, "Text QC could not complete the action:\n\n" + ex.Message + "\n\nDetails: " + Support.LogPath);
                }
            }
        }

        public string GetName() => "Revit Text QC";
    }

    [Transaction(TransactionMode.Manual)]
    public class RunQcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (commandData.Application.ActiveUIDocument == null)
            {
                TaskDialog.Show(App.Title, "Open a project first.");
                return Result.Cancelled;
            }
            ResultsWindow.ShowAndScan(commandData.Application);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SettingsWindow.ShowDialog(commandData.Application, IntPtr.Zero);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CheckUpdatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UpdateManager.CheckInteractively();
            return Result.Succeeded;
        }
    }
}
