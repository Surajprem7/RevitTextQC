using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using RevitTextQC.Core;
using RevitTextQC.Core.Rules;
using RevitTextQC.Scanning;
using RUI = Autodesk.Revit.UI;

namespace RevitTextQC.UI
{
    public partial class SettingsWindow : Window
    {
        private static readonly string[] CaseLabels = { "Any", "UPPERCASE", "lowercase", "Title Case" };

        private string _projectKey;
        private string _projectName;

        private readonly StandardsEditor _textNotes;
        private readonly StandardsEditor _dimensions;

        private SettingsWindow(Func<List<TextItem>> typesInModel)
        {
            InitializeComponent();

            _textNotes = new StandardsEditor("Text notes", TextSource.TextNoteType, typesInModel);
            _dimensions = new StandardsEditor("Dimensions", TextSource.DimensionType, typesInModel);
            StandardsHost.Children.Add(_textNotes);
            StandardsHost.Children.Add(_dimensions);

            foreach (ComboBox box in new[] { TextNoteCase, DimensionCase, SheetNameCase, ViewNameCase, ParameterCase })
                box.ItemsSource = CaseLabels;

            LanguageBox.ItemsSource = HunspellSpellChecker.AvailableLanguages(ScanService.DictionaryFolder)
                .Select(code => new LanguageChoice(code, HunspellSpellChecker.DisplayName(code))).ToList();
            VersionText.Text = $"Text QC {App.CurrentVersion.ToString(3)} for Revit {App.RevitVersion}";
        }

        /// <summary>Shows the settings dialog. Call from a Revit API context. Returns true when the user saved.</summary>
        public static bool ShowDialog(RUI.UIApplication app, IntPtr owner)
        {
            RUI.UIDocument uidoc = app.ActiveUIDocument;
            Func<List<TextItem>> typesInModel = null;
            if (uidoc != null)
            {
                typesInModel = () => new TextCollector(uidoc.Document, uidoc.ActiveView, ScanScope.EntireProject, App.Settings)
                    .CollectTypesOnly();
            }

            var window = new SettingsWindow(typesInModel);
            if (uidoc != null)
            {
                // The spelling language is remembered for this project.
                window._projectKey = uidoc.Document.ProjectInformation.UniqueId;
                window._projectName = uidoc.Document.Title;
            }
            new WindowInteropHelper(window).Owner = owner != IntPtr.Zero ? owner : app.MainWindowHandle;
            window.LoadFrom(App.Settings);

            // Read now: the dialog is modal, and the Revit API can only be used before it opens.
            window.ShowInventory(uidoc);
            return window.ShowDialog() == true;
        }

        private void ShowInventory(RUI.UIDocument uidoc)
        {
            if (uidoc == null)
            {
                InventoryTitle.Text = "Text in the active view / sheet";
                InventoryHint.Text = "Open a project to see what the active view contains.";
                return;
            }

            try
            {
                List<CategoryCount> counts = new TextCollector(uidoc.Document, uidoc.ActiveView, ScanScope.ActiveView, App.Settings)
                    .Inventory();
                InventoryGrid.ItemsSource = counts;
                InventoryTitle.Text = "Text in the active view / sheet: " + uidoc.ActiveView?.Name;
                InventoryHint.Text = counts.Count == 0
                    ? "No annotation was found in this view."
                    : $"{counts.Count} kind(s) of annotation found. Anything marked Not checked is not read by Text QC.";
            }
            catch (Exception ex)
            {
                InventoryHint.Text = "The active view could not be read: " + ex.Message;
            }
        }

        private void LoadFrom(QcSettings s)
        {
            ScanTextNotes.IsChecked = s.ScanTextNotes;
            ScanDimensions.IsChecked = s.ScanDimensions;
            ScanParameters.IsChecked = s.ScanParameters;
            ScanTags.IsChecked = s.ScanTags;
            ScanSheetsAndViews.IsChecked = s.ScanSheetsAndViews;
            ScanSchedules.IsChecked = s.ScanSchedules;
            ScanScheduleBody.IsChecked = s.ScanScheduleBody;
            ScanKeynotes.IsChecked = s.ScanKeynotes;
            KeynotesUsedOnly.IsChecked = s.KeynotesUsedOnly;
            ScanTitleBlock.IsChecked = s.ScanTitleBlock;
            ScanTitleBlockText.IsChecked = s.ScanTitleBlockText;
            ScanVisibleText.IsChecked = s.ScanVisibleText;
            ParameterNames.Text = Lines.Join(s.ParameterNames);

            CheckSpelling.IsChecked = s.CheckSpelling;
            LanguageBox.SelectedValue = s.LanguageFor(_projectKey);
            MinWordLength.Text = s.MinWordLength.ToString(CultureInfo.InvariantCulture);
            CustomWords.Text = Lines.Join(s.CustomWords.OrderBy(w => w, StringComparer.OrdinalIgnoreCase));

            CheckTextStyles.IsChecked = s.CheckTextStyles;
            FlagInlineFormatting.IsChecked = s.FlagInlineFormatting;
            _textNotes.Load(s.TextNoteStandards);
            _dimensions.Load(s.DimensionStandards);

            CheckCase.IsChecked = s.CheckCase;
            TextNoteCase.SelectedIndex = (int)s.TextNoteCase;
            DimensionCase.SelectedIndex = (int)s.DimensionCase;
            SheetNameCase.SelectedIndex = (int)s.SheetNameCase;
            ViewNameCase.SelectedIndex = (int)s.ViewNameCase;
            ParameterCase.SelectedIndex = (int)s.ParameterCase;
            CaseExceptions.Text = Lines.Join(s.CaseExceptions);
            CheckWhitespace.IsChecked = s.CheckWhitespace;
            CheckForbiddenTerms.IsChecked = s.CheckForbiddenTerms;
            ForbiddenTerms.Text = Lines.Join(s.ForbiddenTerms.Select(t =>
                string.IsNullOrWhiteSpace(t.Replacement) ? t.Term : t.Term + " => " + t.Replacement));

            CheckNaming.IsChecked = s.CheckNaming;
            SheetNumberPattern.Text = s.SheetNumberPattern;
            SheetNamePattern.Text = s.SheetNamePattern;
            ViewNamePattern.Text = s.ViewNamePattern;
            FlagDefaultViewNames.IsChecked = s.FlagDefaultViewNames;

            AutoCheckUpdates.IsChecked = s.AutoCheckUpdates;
            AutoInstallUpdates.IsChecked = s.AutoInstallUpdates;
        }

        private QcSettings Read()
        {
            var s = new QcSettings
            {
                ScanTextNotes = ScanTextNotes.IsChecked == true,
                ScanDimensions = ScanDimensions.IsChecked == true,
                ScanParameters = ScanParameters.IsChecked == true,
                ScanTags = ScanTags.IsChecked == true,
                ScanSheetsAndViews = ScanSheetsAndViews.IsChecked == true,
                ScanSchedules = ScanSchedules.IsChecked == true,
                ScanScheduleBody = ScanScheduleBody.IsChecked == true,
                ScanKeynotes = ScanKeynotes.IsChecked == true,
                KeynotesUsedOnly = KeynotesUsedOnly.IsChecked == true,
                ScanTitleBlock = ScanTitleBlock.IsChecked == true,
                ScanTitleBlockText = ScanTitleBlockText.IsChecked == true,
                ScanVisibleText = ScanVisibleText.IsChecked == true,
                ParameterNames = Lines.Split(ParameterNames.Text),

                CheckSpelling = CheckSpelling.IsChecked == true,
                Language = LanguageBox.SelectedValue as string ?? App.Settings.LanguageFor(_projectKey),
                ProjectLanguages = App.Settings.ProjectLanguages.Select(p => new ProjectLanguage { Project = p.Project, Name = p.Name, Language = p.Language }).ToList(),
                MinWordLength = int.TryParse(MinWordLength.Text.Trim(), out int min) && min > 0 ? min : 3,
                CustomWords = Lines.Split(CustomWords.Text),

                CheckTextStyles = CheckTextStyles.IsChecked == true,
                FlagInlineFormatting = FlagInlineFormatting.IsChecked == true,
                TextNoteStandards = _textNotes.Save(),
                DimensionStandards = _dimensions.Save(),

                CheckCase = CheckCase.IsChecked == true,
                TextNoteCase = (CaseMode)Math.Max(0, TextNoteCase.SelectedIndex),
                DimensionCase = (CaseMode)Math.Max(0, DimensionCase.SelectedIndex),
                SheetNameCase = (CaseMode)Math.Max(0, SheetNameCase.SelectedIndex),
                ViewNameCase = (CaseMode)Math.Max(0, ViewNameCase.SelectedIndex),
                ParameterCase = (CaseMode)Math.Max(0, ParameterCase.SelectedIndex),
                CaseExceptions = Lines.Split(CaseExceptions.Text),
                CheckWhitespace = CheckWhitespace.IsChecked == true,
                CheckForbiddenTerms = CheckForbiddenTerms.IsChecked == true,
                ForbiddenTerms = Lines.Split(ForbiddenTerms.Text).Select(ParseTerm).Where(t => t.Term.Length > 0).ToList(),

                CheckNaming = CheckNaming.IsChecked == true,
                SheetNumberPattern = SheetNumberPattern.Text.Trim(),
                SheetNamePattern = SheetNamePattern.Text.Trim(),
                ViewNamePattern = ViewNamePattern.Text.Trim(),
                FlagDefaultViewNames = FlagDefaultViewNames.IsChecked == true,

                AutoCheckUpdates = AutoCheckUpdates.IsChecked == true,
                AutoInstallUpdates = AutoInstallUpdates.IsChecked == true
            };
            return s;
        }

        private static ForbiddenTerm ParseTerm(string line)
        {
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            return arrow < 0
                ? new ForbiddenTerm { Term = line.Trim(), Replacement = "" }
                : new ForbiddenTerm { Term = line.Substring(0, arrow).Trim(), Replacement = line.Substring(arrow + 2).Trim() };
        }

        private bool PatternsAreValid(QcSettings s)
        {
            var naming = new NamingRule(s);
            if (naming.ConfigurationWarnings.Count == 0) return true;
            MessageBox.Show(this, string.Join("\n\n", naming.ConfigurationWarnings), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            QcSettings s = Read();
            if (!PatternsAreValid(s)) return;
            s.SetLanguageFor(_projectKey, _projectName, s.Language); // remembered for this project

            try
            {
                s.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The settings could not be saved:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            App.Settings = s;
            DialogResult = true;
        }

        private void OnImport(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Text QC settings (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                LoadFrom(QcSettings.LoadFrom(dialog.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "This file could not be read as Text QC settings:\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "Text QC settings (*.json)|*.json", FileName = "TextQC-settings.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                QcSettings export = Read();
                export.ProjectLanguages = new List<ProjectLanguage>(); // a client file carries the standard, not this PC's project list
                export.SaveTo(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The file could not be written:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>One entry of the spelling language list: the file name and what the user sees.</summary>
    public class LanguageChoice
    {
        public LanguageChoice(string code, string label)
        {
            Code = code;
            Label = label;
        }

        public string Code { get; }
        public string Label { get; }
    }

    internal static class Lines
    {
        public static string Join(IEnumerable<string> values)
        {
            return string.Join(Environment.NewLine, values);
        }

        public static List<string> Split(string text)
        {
            return (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        }
    }

    /// <summary>Editor for one <see cref="TextStandards"/> block; used twice, for text notes and for dimensions.</summary>
    internal class StandardsEditor : GroupBox
    {
        private readonly TextBox _types = ListBox(70);
        private readonly TextBox _fonts = ListBox(48);
        private readonly TextBox _sizes = ListBox(48);
        private readonly CheckBox _bold = new CheckBox { Content = "Bold", Margin = new Thickness(0, 0, 12, 0) };
        private readonly CheckBox _italic = new CheckBox { Content = "Italic", Margin = new Thickness(0, 0, 12, 0) };
        private readonly CheckBox _underline = new CheckBox { Content = "Underline" };
        private readonly TextBox _width = new TextBox { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };

        public StandardsEditor(string header, TextSource typeSource, Func<List<TextItem>> typesInModel)
        {
            Header = header;
            Margin = new Thickness(0, 0, 6, 0);
            Padding = new Thickness(6);

            var panel = new StackPanel();
            panel.Children.Add(Label("Approved type names"));
            panel.Children.Add(_types);
            panel.Children.Add(Label("Approved fonts"));
            panel.Children.Add(_fonts);
            panel.Children.Add(Label("Approved sizes (mm)"));
            panel.Children.Add(_sizes);

            panel.Children.Add(Label("Allowed in the type"));
            var styles = new StackPanel { Orientation = Orientation.Horizontal };
            styles.Children.Add(_bold);
            styles.Children.Add(_italic);
            styles.Children.Add(_underline);
            panel.Children.Add(styles);

            panel.Children.Add(Label("Width factor (0 = any)"));
            panel.Children.Add(_width);

            var fill = new Button
            {
                Content = "Fill from current model",
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = typesInModel != null,
                ToolTip = "Lists the types, fonts and sizes used in the open model so you can delete the ones that are not standard."
            };
            fill.Click += (s, e) =>
            {
                // Reads the model; a failure here must not escape into Revit's message loop.
                try
                {
                    Fill(typesInModel().Where(t => t.Source == typeSource).ToList());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("The model could not be read:\n" + ex.Message, "Text QC Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            panel.Children.Add(fill);

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static TextBox ListBox(double height)
        {
            return new TextBox
            {
                Height = height,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 2) };
        }

        public void Load(TextStandards standards)
        {
            _types.Text = Lines.Join(standards.AllowedTypeNames);
            _fonts.Text = Lines.Join(standards.AllowedFonts);
            _sizes.Text = Lines.Join(standards.AllowedSizesMm.Select(TextStyleRule.FormatMm));
            _bold.IsChecked = standards.AllowBold;
            _italic.IsChecked = standards.AllowItalic;
            _underline.IsChecked = standards.AllowUnderline;
            _width.Text = standards.WidthFactor.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public TextStandards Save()
        {
            return new TextStandards
            {
                AllowedTypeNames = Lines.Split(_types.Text),
                AllowedFonts = Lines.Split(_fonts.Text),
                AllowedSizesMm = Lines.Split(_sizes.Text.Replace(';', '\n')).Select(ParseNumber).Where(n => n > 0).Distinct().ToList(),
                AllowBold = _bold.IsChecked == true,
                AllowItalic = _italic.IsChecked == true,
                AllowUnderline = _underline.IsChecked == true,
                WidthFactor = Math.Max(0, ParseNumber(_width.Text))
            };
        }

        private void Fill(List<TextItem> types)
        {
            _types.Text = Lines.Join(types.Select(t => t.Style.TypeName).Distinct().OrderBy(n => n));
            _fonts.Text = Lines.Join(types.Select(t => t.Style.Font).Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f));
            _sizes.Text = Lines.Join(types.Select(t => Math.Round(t.Style.SizeMm, 2)).Distinct().OrderBy(s => s)
                .Select(TextStyleRule.FormatMm));
        }

        // Accepts both 2.5 and 2,5.
        private static double ParseNumber(string text)
        {
            string cleaned = Regex.Replace(text ?? "", @"[^\d.,]", "").Replace(',', '.');
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        }
    }
}
