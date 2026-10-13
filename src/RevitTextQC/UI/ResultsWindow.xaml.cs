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
    internal static class Display
    {
        /// <summary>Multi-line text on one grid line, shortened.</summary>
        public static string OneLine(string text)
        {
            text = (text ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ");

            // Spaces at the very start or end are invisible; show each one as a dot so they can be seen.
            int end = text.Length;
            while (end > 0 && text[end - 1] == ' ') end--;
            int start = 0;
            while (start < end && text[start] == ' ') start++;
            if (start > 0 || end < text.Length)
                text = new string('·', start) + text.Substring(start, end - start) + new string('·', text.Length - end);

            return text.Length > 200 ? text.Substring(0, 200) + "…" : text;
        }
    }

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
        public string Text => Display.OneLine(Issue.Item.Text);

        /// <summary>The misspelled word or forbidden term; used to group the same word across the model.</summary>
        public string Term => string.IsNullOrEmpty(Issue.Word) ? "(no single word)" : Issue.Word.ToUpperInvariant();

        /// <summary>The view, sheet or schedule the text sits in; used to group findings.</summary>
        public string Place
        {
            get
            {
                TextItem item = Issue.Item;
                switch (item.Source)
                {
                    case TextSource.TextNote:
                    case TextSource.Dimension:
                    case TextSource.Tag:
                    case TextSource.Schedule:
                        return string.IsNullOrEmpty(item.Location) ? "(no view)" : item.Location;
                    case TextSource.SheetName:
                    case TextSource.SheetNumber:
                        return "Sheet list";
                    case TextSource.ViewName:
                        return "View list";
                    case TextSource.TextNoteType:
                    case TextSource.DimensionType:
                        return "Text and dimension types";
                    case TextSource.Keynote:
                        return "Keynote table";
                    default:
                        return "Model elements";
                }
            }
        }

        public string Source
        {
            get
            {
                switch (Issue.Item.Source)
                {
                    case TextSource.TextNote: return "Text note";
                    case TextSource.Parameter:
                        return Issue.Item.Field == FixApplier.RoomNameField ? "Room name" : "Parameter";
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

    /// <summary>One line of the element list on the Find tab.</summary>
    public class ElementRow
    {
        public StyledElement Element { get; }

        public ElementRow(StyledElement element)
        {
            Element = element;
        }

        public string Text => Display.OneLine(Element.Text);
        public string Kind => Element.Kind == TextSource.TextNote ? "Text note" : "Dimension";
        public string TypeName => Element.Style.TypeName;
        public string Size => Element.Style.SizeMm > 0 ? TextStyleRule.FormatMm(Element.Style.SizeMm) + " mm" : "";
        public string Font => Element.Style.Font;
        public string Style => TextFinder.StyleLabel(Element.Style);
        public string Location => Element.Location;
        public long ElementId => Element.ElementId;
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
        private readonly bool _ready;
        private DB.Document _doc;
        private QcEngine _engine;
        private List<TextItem> _items = new List<TextItem>();
        private List<StyledElement> _styled = new List<StyledElement>();
        private bool _updatingUi;

        public ResultsWindow()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            IssuesGrid.ItemsSource = _view;
            _ready = true;
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

        // An exception escaping a WPF event handler inside Revit can take Revit down with it,
        // so every handler runs through here.
        private void Safely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                SetStatus("Something went wrong: " + ex.Message);
            }
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        // ---- scanning ----------------------------------------------------------------------

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
                var collector = new TextCollector(doc, uidoc.ActiveView, scope, App.Settings);
                _items = collector.Collect();
                _styled = collector.StyledElements.ToList();
                _engine = ScanService.CreateEngine(App.Settings);
                List<QcIssue> issues = _engine.Run(_items);
                _doc = doc;

                _rows.Clear();
                foreach (QcIssue issue in issues) _rows.Add(new IssueRow(issue));
                UpdateRuleFilter();
                RefreshFindGroups();

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

        /// <summary>Runs an action against the scanned document's window in Revit, if it is the one in front.</summary>
        private void InRevit(Action<RUI.UIDocument> action)
        {
            RevitDispatcher.Post(app =>
            {
                if (!DocumentIsOpen()) return;
                RUI.UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null || !uidoc.Document.Equals(_doc))
                {
                    SetStatus($"Switch to \"{_doc.Title}\" in Revit first.");
                    return;
                }
                action(uidoc);
            });
        }

        // ---- findings: filtering -----------------------------------------------------------

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
            UpdateSummary();
            ApplyGrouping();
            _view.Refresh();
        }

        private void UpdateSummary()
        {
            if (_doc == null) return;
            if (_rows.Count == 0)
            {
                SummaryText.Text = "No findings. The scanned text passes every enabled check.";
                return;
            }

            IEnumerable<string> levels = _rows.GroupBy(r => r.Issue.Severity).OrderByDescending(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key}");
            int fixable = _rows.Count(r => MayHaveFix(r.Issue));
            SummaryText.Text = $"{_rows.Count} finding(s):  {string.Join("  ·  ", levels)}   —   {fixable} can be fixed from here";
        }

        // Grouped lists cannot be virtualised as well as flat ones; past this many rows grouping is dropped.
        private const int MaxGroupedRows = 3000;

        private void ApplyGrouping()
        {
            string property;
            switch (GroupByBox.SelectedIndex)
            {
                case 1: property = nameof(IssueRow.Rule); break;
                case 2: property = nameof(IssueRow.Place); break;
                case 3: property = nameof(IssueRow.Source); break;
                case 4: property = nameof(IssueRow.Severity); break;
                case 5: property = nameof(IssueRow.Term); break;
                default: property = null; break;
            }
            if (_rows.Count > MaxGroupedRows) property = null;

            string current = (_view.GroupDescriptions.FirstOrDefault() as PropertyGroupDescription)?.PropertyName;
            if (current == property) return;

            _view.GroupDescriptions.Clear();
            if (property != null) _view.GroupDescriptions.Add(new PropertyGroupDescription(property));
        }

        private void OnGroupByChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            Safely(() =>
            {
                ApplyGrouping();
                if (GroupByBox.SelectedIndex > 0 && _rows.Count > MaxGroupedRows)
                    SetStatus($"Grouping is switched off above {MaxGroupedRows} findings to keep the list responsive. Narrow it with the Rule filter.");
            });
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (!_ready || _updatingUi) return;
            Safely(() => _view.Refresh());
        }

        // ---- findings: selection -----------------------------------------------------------

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

        private static bool MayHaveFix(QcIssue issue)
        {
            return issue.CanFix || (issue.SuggestionsPending && issue.Item.Editable);
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingUi) return;
            Safely(() =>
            {
                List<IssueRow> selected = SelectedRows;
                ShowDetails(selected);
                if (HighlightBox.IsChecked == true && selected.Count > 0) Highlight(selected);
            });
        }

        private void ShowDetails(List<IssueRow> selected)
        {
            IssueRow single = selected.Count == 1 ? selected[0] : null;

            _updatingUi = true;
            try
            {
                ShowButton.IsEnabled = single != null;
                FixSelectedButton.IsEnabled = selected.Any(r => MayHaveFix(r.Issue));
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
                // Suggestions skipped during a large scan are worked out now, for this one word.
                _engine?.ResolveSuggestions(issue);

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

        // A plain click highlights without leaving the current view; opening views is left to Show in Revit.
        private void Highlight(List<IssueRow> selected)
        {
            if (selected.Count == 1)
            {
                TextItem item = selected[0].Issue.Item;
                InRevit(uidoc => SetStatus(FixApplier.Show(uidoc, item, false) ?? "Highlighted in Revit."));
                return;
            }

            List<long> ids = selected.Select(r => r.Issue.Item)
                .Where(i => !i.IsType && i.Source != TextSource.Keynote)
                .Select(i => i.ElementId).Distinct().ToList();
            InRevit(uidoc => SetStatus($"Selected {FixApplier.Select(uidoc, ids)} element(s) in Revit."));
        }

        private void OnAlternativeChosen(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingUi || !(AlternativesBox.SelectedItem is string word)) return;
            Safely(() =>
            {
                List<IssueRow> selected = SelectedRows;
                if (selected.Count != 1) return;
                QcIssue issue = selected[0].Issue;
                FixBox.Text = ToUi(SpellingRule.ReplaceWord(issue.Item.Text, issue.Word, word));
            });
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedRows.Count == 1) OnShow(sender, e);
        }

        // ---- findings: actions -------------------------------------------------------------

        private void OnShow(object sender, RoutedEventArgs e)
        {
            Safely(() =>
            {
                List<IssueRow> selected = SelectedRows;
                if (selected.Count != 1) return;
                TextItem item = selected[0].Issue.Item;
                InRevit(uidoc => SetStatus(FixApplier.Show(uidoc, item, true) ?? "Shown in Revit."));
            });
        }

        // The per-row Show button: same as Show in Revit, but for the row it sits on.
        private void OnRowShowClick(object sender, RoutedEventArgs e)
        {
            Safely(() =>
            {
                if (!((sender as FrameworkElement)?.DataContext is IssueRow row)) return;
                TextItem item = row.Issue.Item;
                InRevit(uidoc => SetStatus(FixApplier.Show(uidoc, item, true) ?? "Shown in Revit."));
            });
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            Safely(() =>
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
            });
        }

        private void OnFixSelected(object sender, RoutedEventArgs e)
        {
            Safely(() =>
            {
                // A fix changes the text the other findings on the same element were computed from,
                // so each one is looked up again after the previous fix instead of using a stale suggestion.
                var wanted = SelectedRows.Where(r => MayHaveFix(r.Issue))
                    .GroupBy(r => r.Issue.Item)
                    .ToDictionary(g => g.Key, g => g.Select(r => Tuple.Create(r.Issue.RuleId, r.Issue.Word)).Distinct().ToList());
                if (wanted.Count == 0) return;

                RevitDispatcher.Post(app => ApplyFixes(wanted.Keys.ToList(), item => SuggestionsFor(item, wanted[item])));
            });
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
            var originals = new Dictionary<TextItem, string>();
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
                            if (!originals.ContainsKey(item)) originals[item] = item.Text;
                            item.Text = text;
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                }

                DB.TransactionStatus status = applied > 0 ? transaction.Commit() : transaction.RollBack();
                if (applied > 0 && status != DB.TransactionStatus.Committed)
                {
                    // Revit refused the change at commit (for example the element belongs to another
                    // user in a workshared model). The model is unchanged, so our copy must be too.
                    foreach (var pair in originals) pair.Key.Text = pair.Value;
                    applied = 0;
                    errors.Insert(0, "Revit did not accept the change; nothing was modified.");
                }
            }

            Reevaluate(items);
            string message = $"Applied {applied} fix(es).";
            if (errors.Count > 0) message += $" {errors.Count} problem(s): {errors[0]}";
            SetStatus(message);
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
            Safely(() =>
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
            });
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            IntPtr owner = new WindowInteropHelper(this).Handle;
            RevitDispatcher.Post(app =>
            {
                if (!SettingsWindow.ShowDialog(app, owner)) return;
                RefreshFindGroups(); // the approved lists may have changed
                SetStatus("Settings saved. Press Scan to check the model with the new settings.");
            });
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            Safely(() =>
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

                File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
                SetStatus($"Exported {rows.Count} finding(s) to {dialog.FileName}.");
            });
        }

        // Text from the model that starts with = + - or @ would be run as a formula by Excel;
        // a leading apostrophe makes Excel treat it as plain text.
        private static string Quote(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        // ---- find tab ----------------------------------------------------------------------

        private void RefreshFindGroups()
        {
            var by = (FindBy)Math.Max(0, FindByBox.SelectedIndex);
            var scope = (FindIn)Math.Max(0, FindInBox.SelectedIndex);

            List<FindGroup> groups = TextFinder.Group(_styled, by, scope, App.Settings);
            if (NotApprovedBox.IsChecked == true) groups = groups.Where(g => g.Approved == false).ToList();

            string previous = (GroupsGrid.SelectedItem as FindGroup)?.Label;
            GroupsGrid.ItemsSource = groups;
            GroupsGrid.SelectedItem = groups.FirstOrDefault(g => g.Label == previous);

            int elements = groups.Sum(g => g.Count);
            GroupsCaption.Text = _doc == null
                ? "Press Scan to list what the model uses."
                : $"{groups.Count} value(s) used by {elements} element(s)";
            if (GroupsGrid.SelectedItem == null) ShowGroup(null);
        }

        private void OnFindOptionsChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Safely(RefreshFindGroups);
        }

        private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            Safely(() => ShowGroup(GroupsGrid.SelectedItem as FindGroup));
        }

        private void ShowGroup(FindGroup group)
        {
            ElementsGrid.ItemsSource = group?.Elements.Select(el => new ElementRow(el)).ToList();
            SelectGroupButton.IsEnabled = group != null && group.Count > 0;
            ShowElementButton.IsEnabled = false;
            ElementsCaption.Text = group == null
                ? "Select a value on the left to list the text using it."
                : $"{group.Count} element(s) using {group.Label}" + (group.Approved == false ? " — not approved" : "");
        }

        private List<ElementRow> SelectedElements => ElementsGrid.SelectedItems.Cast<ElementRow>().ToList();

        private void OnElementSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            Safely(() =>
            {
                List<ElementRow> selected = SelectedElements;
                ShowElementButton.IsEnabled = selected.Count == 1;
                if (HighlightBox.IsChecked != true || selected.Count == 0) return;

                if (selected.Count == 1)
                {
                    long id = selected[0].ElementId;
                    InRevit(uidoc => SetStatus(FixApplier.ShowElement(uidoc, id, false)));
                }
                else
                {
                    List<long> ids = selected.Select(r => r.ElementId).ToList();
                    InRevit(uidoc => SetStatus($"Selected {FixApplier.Select(uidoc, ids)} element(s) in Revit."));
                }
            });
        }

        private void OnElementDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OnShowElement(sender, e);
        }

        private void OnShowElement(object sender, RoutedEventArgs e)
        {
            Safely(() =>
            {
                List<ElementRow> selected = SelectedElements;
                if (selected.Count != 1) return;
                long id = selected[0].ElementId;
                InRevit(uidoc => SetStatus(FixApplier.ShowElement(uidoc, id, true)));
            });
        }

        private void OnSelectGroup(object sender, RoutedEventArgs e)
        {
            Safely(() =>
            {
                if (!(GroupsGrid.SelectedItem is FindGroup group)) return;
                List<long> ids = group.Elements.Select(el => el.ElementId).ToList();
                InRevit(uidoc =>
                {
                    int count = FixApplier.Select(uidoc, ids);
                    SetStatus($"Selected {count} element(s) using {group.Label}. Change them with the Type Selector in Revit.");
                });
            });
        }
    }

    internal static class ScanService
    {
        private static List<string> _tradeTerms;

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
            return new QcEngine(settings, spell, TradeTerms());
        }

        // Construction abbreviations shipped with the add-in (FFL, DPC, TYP ...), accepted as correctly spelled.
        private static List<string> TradeTerms()
        {
            if (_tradeTerms != null) return _tradeTerms;
            var terms = new List<string>();
            try
            {
                string file = Path.Combine(DictionaryFolder, "aec-terms.txt");
                if (File.Exists(file))
                    terms = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            }
            catch (IOException) { }
            _tradeTerms = terms;
            return terms;
        }
    }
}
