using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace RevitTextQC.UI
{
    /// <summary>
    /// The add-in's look. The styles live in Theme.xaml, which is embedded in the assembly and read here, so
    /// there is no dependency on a WPF application object (Revit has none) or on resource URIs.
    /// </summary>
    internal static class Theme
    {
        private const string ResourceName = "RevitTextQC.UI.Theme.xaml";

        /// <summary>Merges the theme into a window or control. Call before its own XAML is loaded.</summary>
        public static void Apply(FrameworkElement element)
        {
            try
            {
                using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) return;
                    element.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
                }
            }
            catch (System.Exception ex)
            {
                // A window without the theme is plainer but fully usable; never refuse to open for looks.
                Support.Log("theme could not be loaded", ex);
            }
        }
    }
}
