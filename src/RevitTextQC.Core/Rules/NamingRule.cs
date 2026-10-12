using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core.Rules
{
    /// <summary>Sheet numbers, sheet names and view names against the office naming patterns.</summary>
    public class NamingRule : IQcRule
    {
        public const string RuleId = "naming";

        // Names Revit generates on its own: "Section 3", "Level 1 Copy 2", "{3D}".
        private static readonly Regex DefaultViewName = new Regex(
            @"(\bcopy( of)?\s*\d*$)|(^(section|elevation|callout|drafting|legend|detail|schedule|3d view|sheet|area plan|walkthrough|rendering)\s+\d+$)|(^\{3D.*\}$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Regex _sheetNumber;
        private readonly Regex _sheetName;
        private readonly Regex _viewName;
        private readonly bool _flagDefaults;

        public string Id => RuleId;
        public string Name => "Naming";

        /// <summary>Patterns that could not be compiled; shown to the user instead of silently ignored.</summary>
        public List<string> ConfigurationWarnings { get; } = new List<string>();

        public NamingRule(QcSettings settings)
        {
            _sheetNumber = Compile(settings.SheetNumberPattern, "Sheet number pattern");
            _sheetName = Compile(settings.SheetNamePattern, "Sheet name pattern");
            _viewName = Compile(settings.ViewNamePattern, "View name pattern");
            _flagDefaults = settings.FlagDefaultViewNames;
        }

        // The whole value has to match, so users do not need to remember the ^ and $ anchors.
        private Regex Compile(string pattern, string label)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try
            {
                // The timeout stops a badly written pattern from hanging Revit on a long name.
                return new Regex("^(?:" + pattern.Trim() + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                ConfigurationWarnings.Add($"{label} is not a valid regular expression and was skipped: {ex.Message}");
                return null;
            }
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            string text = item.Text ?? "";
            switch (item.Source)
            {
                case TextSource.SheetNumber:
                    if (Fails(_sheetNumber, text))
                        yield return Issue(item, Severity.Warning, $"Sheet number \"{text}\" does not follow the sheet numbering pattern.");
                    break;

                case TextSource.SheetName:
                    if (Fails(_sheetName, text))
                        yield return Issue(item, Severity.Warning, $"Sheet name \"{text}\" does not follow the sheet naming pattern.");
                    if (_flagDefaults && string.Equals(text.Trim(), "Unnamed", StringComparison.OrdinalIgnoreCase))
                        yield return Issue(item, Severity.Warning, "Sheet still has the default name \"Unnamed\".");
                    break;

                case TextSource.ViewName:
                    if (Fails(_viewName, text))
                        yield return Issue(item, Severity.Warning, $"View name \"{text}\" does not follow the view naming pattern.");
                    if (_flagDefaults && DefaultViewName.IsMatch(text.Trim()))
                        yield return Issue(item, Severity.Info, $"View name \"{text}\" looks like a default or copied name.");
                    break;
            }
        }

        // A pattern that times out is the engine's problem to report (it throws), not a naming finding.
        private static bool Fails(Regex pattern, string text)
        {
            return pattern != null && !pattern.IsMatch(text);
        }

        private QcIssue Issue(TextItem item, Severity severity, string message)
        {
            return new QcIssue { Item = item, RuleId = Id, RuleName = Name, Severity = severity, Message = message };
        }
    }
}
