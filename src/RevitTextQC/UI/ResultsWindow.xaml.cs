using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using RevitTextQC.Core;
using RevitTextQC.Core.Rules;
using RevitTextQC.Scanning;
using DB = Autodesk.Revit.DB;
using RUI = Autodesk.Revit.UI;

namespace RevitTextQC.UI
{
    /// <summary>One line of the findings grid.</summary>
    public class IssueRow
    {
        public QcIssue Issue { get; }

        public IssueRow(QcIssue issue)
        {
            Issue = issue;
        }

        public string Severity => Issue.Severity.ToString();
        public string Rule => Issue.RuleName;
        public string Message => Issue.Message;
        public long ElementId => Issue.Item.ElementId;
        public string FixLabel => Issue.CanFix ? "Auto" : Issue.Item.Editable ? "Edit" : "";

        public string Text
        {
            get
            {
                string text = (Issue.Item.Text ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ");
                return text.Length > 200 ? text.Substring(0, 200) + "…" : text;
            }
        }

        public string Source
        {
            get
            {
                switch (Issue.Item.Source)
                {
                    case TextSource.TextNote: return "Text note";
                    case TextSource.SheetNumber: return "Sheet number";
                    case TextSource.SheetName: return "Sheet name";
                    case TextSource.ViewName: return "View name";
                    case TextSource.TextNoteType: return "Text type";
                    case TextSource.DimensionType: return "Dimension type";
                    default: return Issue.Item.Source.ToString();
                }
            }
        }

        public string Location
        {
            get
            {
                TextItem item = Issue.Item;
                string detail = item.Field == FixApplier.RoomNameField ? "Name" : item.Field;
                if (string.IsNullOrEmpty(detail)) return item.Location ?? "";
                return string.IsNullOrEmpty(item.Location) ? detail : item.Location + " · " + detail;
            }
        }
    }

    internal class RuleFilter
    {
        public string RuleId { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }

    public partial class ResultsWindow : Window
    {
        private static ResultsWindow _instance;

        private readonly ObservableCollection<IssueRow> _rows = new ObservableCollection<IssueRow>();
        private readonly ICollectionView _view;
        private DB.Document _doc;
        private QcEngine _engine;
        private List<TextItem> _items = new List<TextItem>();
        private bool _updatingUi;

        public ResultsWindow()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            IssuesGrid.ItemsSource = _view;
        }

        /// <summary>Opens (or brings forward) the window and scans. Call from a Revit API context.</summary>
        public static void ShowAndScan(RUI.UIApplication app)
        {
            if (_instance == null)
            {
                _instance = new ResultsWindow();
                new WindowInteropHelper(_instance).Owner = app.MainWindowHandle;
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
            }
            _instance.Scan(app);
        }

        private void Scan(RUI.UIApplication app)
        {
            RUI.UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                SetStatus("Open a project first.");
                return;
            }

            ScanScope scope = ScopeBox.SelectedIndex == 1 ? ScanScope.EntireProject : ScanScope.ActiveView;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var watch = Stopwatch.StartNew();
                DB.Document doc = uidoc.Document;
                _items = new TextCollector(doc, uidoc.ActiveView, scope, App.Settings).Collect();
                _engine = ScanService.CreateEngine(App.Settings);
                List<QcIssue> issues = _engine.Run(_items);
                _doc = doc;

                _rows.Clear();
                foreach (QcIssue issue in issues) _rows.Add(new IssueRow(issue));
                UpdateRuleFilter();

                string where = scope == ScanScope.EntireProject ? "entire project" : "\"" + uidoc.ActiveView?.Name + "\"";
                string status = $"{issues.Count} finding(s) in {_items.Count} texts — {doc.Title}, {where} ({watch.ElapsedMilliseconds} ms).";
                if (_engine.ConfigurationWarnings.Count > 0)
                    status += "  ⚠ " + string.Join(" ", _engine.ConfigurationWarnings);
                SetStatus(status);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        // ---- filtering ---------------------------------------------------------------------

        private bool Matches(object o)
        {
            var row = (IssueRow)o;
            if (RuleBox.SelectedItem is RuleFilter filter && filter.RuleId != null && row.Issue.RuleId != filter.RuleId)
                return false;

            string search = SearchBox.Text.Trim();
            if (search.Length == 0) return true;
            return Contains(row.Text, search) || Contains(row.Message, search) || Contains(row.Location, search);
        }

        private static bool Contains(string text, string search)
        {
            return text != null && text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateRuleFilter()
        {
            _updatingUi = true;
            try
            {
                string selected = (RuleBox.SelectedItem as RuleFilter)?.RuleId;
                var filters = new List<RuleFilter> { new RuleFilter { RuleId = null, Label = $"All rules ({_rows.Count})" } };
                filters.AddRange(_rows.GroupBy(r => r.Issue.RuleId).OrderBy(g => g.First().Rule)
                    .Select(g => new RuleFilter { RuleId = g.Key, Label = $"{g.First().Rule} ({g.Count()})" }));
                RuleBox.ItemsSource = filters;
                RuleBox.SelectedItem = filters.FirstOrDefault(f => f.RuleId == selected) ?? filters[0];
            }
            finally
            {
                _updatingUi = false;
            }
            _view.Refresh();
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_updatingUi || _view == null) return;
            _view.Refresh();
        }

        // ---- selection ---------------------------------------------------------------------

        private List<IssueRow> SelectedRows => IssuesGrid.SelectedItems.Cast<IssueRow>().ToList();

        private static string ToUi(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        // Revit separates lines in text notes with a single carriage return.
        private static string FromUi(string text)
        {
            return (text ?? "").Replace("\r\n", "\r").Replace("\n", "\r");
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            List<IssueRow> selected = SelectedRows;
            IssueRow single = selected.Count == 1 ? selected[0] : null;

            _updatingUi = true;
            try
            {
                ShowButton.IsEnabled = single != null;
                FixSelectedButton.IsEnabled = selected.Any(r => r.Issue.CanFix);
                AddWordButton.IsEnabled = selected.Any(r => r.Issue.RuleId == SpellingRule.RuleId && r.Issue.Word != null);

                bool editable = single != null && single.Issue.Item.Editable;
                FixBox.IsEnabled = editable;
                ApplyButton.IsEnabled = editable;

                if (single == null)
                {
                    DetailMessage.Text = selected.Count == 0
                        ? "Select a finding to see the details."
                        : $"{selected.Count} findings selected.";
                    FixBox.Text = "";
                    AlternativesBox.ItemsSource = null;
                    AlternativesBox.IsEnabled = false;
                    return;
                }

                QcIssue issue = single.Issue;
                DetailMessage.Text = issue.Message +
                    (issue.Item.Editable ? "" : "  (This text cannot be edited from Text QC; fix it at its source.)");
                FixBox.Text = ToUi(issue.Suggestion ?? issue.Item.Text);

                bool hasAlternatives = editable && issue.RuleId == SpellingRule.RuleId && issue.Alternatives.Count > 0;
                AlternativesBox.ItemsSource = hasAlternatives ? issue.Alternatives : null;
                AlternativesBox.SelectedIndex = hasAlternatives ? 0 : -1;
                AlternativesBox.IsEnabled = hasAlternatives;
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void OnAlternativeChosen(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingUi || !(AlternativesBox.SelectedItem is string word)) return;
            List<IssueRow> selected = SelectedRows;
            if (selected.Count != 1) return;
            QcIssue issue = selected[0].Issue;
            FixBox.Text = ToUi(SpellingRule.ReplaceWord(issue.Item.Text, issue.Word, word));
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedRows.Count == 1) OnShow(sender, e);
        }

        // ---- actions -----------------------------------------------------------------------

        private void OnScan(object sender, RoutedEventArgs e)
        {
            RevitDispatcher.Post(Scan);
        }

        private bool DocumentIsOpen()
        {
            if (_doc != null && _doc.IsValidObject) return true;
            SetStatus("The scanned document is closed. Scan again.");
            return false;
        }

        private void OnShow(object sender, RoutedEventArgs e)
        {
            List<IssueRow> selected = SelectedRows;
            if (selected.Count != 1) return;
            TextItem item = selected[0].Issue.Item;

            RevitDispatcher.Post(app =>
            {
                if (!DocumentIsOpen()) return;
                RUI.UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null || !uidoc.Document.Equals(_doc))
                {
                    SetStatus($"Switch to \"{_doc.Title}\" in Revit to show this element.");
                    return;
                }
                SetStatus(FixApplier.Show(uidoc, item) ?? "Shown in Revit.");
            });
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            List<IssueRow> selected = SelectedRows;
            if (selected.Count != 1) return;
            TextItem item = selected[0].Issue.Item;
            string newText = FromUi(FixBox.Text);
            if (newText == item.Text)
            {
                SetStatus("The corrected text is the same as the current text.");
                return;
            }

            RevitDispatcher.Post(app => ApplyFixes(new[] { item }, i => new[] { newText }));
        }

        private void OnFixSelected(object sender, RoutedEventArgs e)
        {
            // A fix changes the text the other findings on the same element were computed from,
            // so each one is looked up again after the previous fix instead of using a stale suggestion.
            var wanted = SelectedRows.Where(r => r.Issue.CanFix)
                .GroupBy(r => r.Issue.Item)
                .ToDictionary(g => g.Key, g => g.Select(r => Tuple.Create(r.Issue.RuleId, r.Issue.Word)).Distinct().ToList());
            if (wanted.Count == 0) return;

            RevitDispatcher.Post(app => ApplyFixes(wanted.Keys.ToList(), item => SuggestionsFor(item, wanted[item])));
        }

        private IEnumerable<string> SuggestionsFor(TextItem item, List<Tuple<string, string>> keys)
        {
            foreach (var key in keys)
            {
                QcIssue current = _engine.Run(new[] { item })
                    .FirstOrDefault(i => i.RuleId == key.Item1 && i.CanFix &&
                                         string.Equals(i.Word, key.Item2, StringComparison.OrdinalIgnoreCase));
                if (current != null) yield return current.Suggestion;
            }
        }

        /// <summary>Writes new text for each item in one undoable transaction, then re-checks those items.</summary>
        private void ApplyFixes(IList<TextItem> items, Func<TextItem, IEnumerable<string>> newTexts)
        {
            if (!DocumentIsOpen()) return;

            int applied = 0;
            var errors = new List<string>();
            using (var transaction = new DB.Transaction(_doc, "Text QC: fix text"))
            {
                transaction.Start();
                foreach (TextItem item in items)
                {
                    // Lazy on purpose: each text is computed after the previous one has been applied.
                    foreach (string text in newTexts(item))
                    {
                        try
                        {
                            FixApplier.Apply(_doc, item, text);
                            item.Text = text;
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                }
                if (applied > 0) transaction.Commit();
                else transaction.RollBack();
            }

            Reevaluate(items);
            string status = $"Applied {applied} fix(es).";
            if (errors.Count > 0) status += $" {errors.Count} failed: {errors[0]}";
            SetStatus(status);
        }

        private void Reevaluate(IEnumerable<TextItem> items)
        {
            var changed = new HashSet<TextItem>(items);
            foreach (IssueRow row in _rows.Where(r => changed.Contains(r.Issue.Item)).ToList()) _rows.Remove(row);
            foreach (QcIssue issue in _engine.Run(changed)) _rows.Add(new IssueRow(issue));
            UpdateRuleFilter();
        }

        private void OnAddWord(object sender, RoutedEventArgs e)
        {
            var words = SelectedRows
                .Where(r => r.Issue.RuleId == SpellingRule.RuleId && r.Issue.Word != null)
                .Select(r => r.Issue.Word)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (words.Count == 0) return;

            foreach (string word in words)
                if (!App.Settings.CustomWords.Contains(word, StringComparer.OrdinalIgnoreCase))
                    App.Settings.CustomWords.Add(word);
            App.Settings.Save();

            _engine = ScanService.CreateEngine(App.Settings);
            var accepted = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
            foreach (IssueRow row in _rows.Where(r => r.Issue.RuleId == SpellingRule.RuleId && r.Issue.Word != null &&
                                                      accepted.Contains(r.Issue.Word)).ToList())
                _rows.Remove(row);
            UpdateRuleFilter();
            SetStatus($"Added to the custom dictionary: {string.Join(", ", words)}.");
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            IntPtr owner = new WindowInteropHelper(this).Handle;
            RevitDispatcher.Post(app =>
            {
                if (SettingsWindow.ShowDialog(app, owner))
                    SetStatus("Settings saved. Press Scan to check the model with the new settings.");
            });
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            List<IssueRow> rows = _view.Cast<IssueRow>().ToList();
            if (rows.Count == 0)
            {
                SetStatus("There is nothing to export.");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV file (*.csv)|*.csv",
                FileName = "TextQC-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv"
            };
            if (dialog.ShowDialog(this) != true) return;

            var csv = new StringBuilder();
            csv.AppendLine("Level,Rule,Text,Finding,Suggested text,Kind,Where,Element Id");
            foreach (IssueRow row in rows)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    row.Severity, row.Rule, row.Text, row.Message,
                    row.Issue.CanFix ? row.Issue.Suggestion.Replace("\r", " ") : "",
                    row.Source, row.Location, row.ElementId.ToString()
                }.Select(Quote)));
            }

            try
            {
                File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
                SetStatus($"Exported {rows.Count} finding(s) to {dialog.FileName}.");
            }
            catch (IOException ex)
            {
                SetStatus("Export failed: " + ex.Message);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }
    }

    internal static class ScanService
    {
        public static string DictionaryFolder => Path.Combine(App.InstallFolder, "Dictionaries");

        public static QcEngine CreateEngine(QcSettings settings)
        {
            ISpellChecker spell = null;
            if (settings.CheckSpelling)
            {
                try
                {
                    spell = HunspellSpellChecker.Load(DictionaryFolder, settings.Language);
                }
                catch (Exception)
                {
                    // The engine reports that spelling was skipped.
                }
            }
            return new QcEngine(settings, spell);
        }
    }
}
