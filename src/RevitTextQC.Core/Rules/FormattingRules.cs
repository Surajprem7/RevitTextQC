using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core.Rules
{
    /// <summary>Enforces UPPERCASE / lowercase / Title Case per kind of text.</summary>
    public class CaseRule : IQcRule
    {
        public const string RuleId = "case";
        private static readonly HashSet<string> SmallWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "as", "at", "by", "for", "in", "of", "on", "or", "the", "to", "with"
        };
        private static readonly Regex WordRegex = new Regex(@"\p{L}[\p{L}'’]*", RegexOptions.Compiled);

        private readonly QcSettings _settings;

        public string Id => RuleId;
        public string Name => "Letter case";

        public CaseRule(QcSettings settings)
        {
            _settings = settings;
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            CaseMode mode = ModeFor(item.Source);
            if (mode == CaseMode.Any || string.IsNullOrWhiteSpace(item.Text)) yield break;

            // Revit's All Caps format already shows the note in capitals.
            if (mode == CaseMode.Upper && item.DisplayedAllCaps) yield break;

            string expected = Convert(item.Text, mode);
            if (expected == item.Text) yield break;

            yield return new QcIssue
            {
                Item = item,
                RuleId = Id,
                RuleName = Name,
                Severity = Severity.Warning,
                Suggestion = expected,
                Message = $"Text should be {Describe(mode)}."
            };
        }

        private CaseMode ModeFor(TextSource source)
        {
            switch (source)
            {
                case TextSource.TextNote: return _settings.TextNoteCase;
                case TextSource.Dimension: return _settings.DimensionCase;
                case TextSource.SheetName: return _settings.SheetNameCase;
                case TextSource.ViewName: return _settings.ViewNameCase;
                case TextSource.Parameter: return _settings.ParameterCase;
                default: return CaseMode.Any;
            }
        }

        private static string Describe(CaseMode mode)
        {
            switch (mode)
            {
                case CaseMode.Upper: return "UPPERCASE";
                case CaseMode.Lower: return "lowercase";
                default: return "Title Case";
            }
        }

        public static string Convert(string text, CaseMode mode)
        {
            switch (mode)
            {
                case CaseMode.Upper: return text.ToUpper(CultureInfo.InvariantCulture);
                case CaseMode.Lower: return text.ToLower(CultureInfo.InvariantCulture);
                case CaseMode.Title: return ToTitle(text);
                default: return text;
            }
        }

        private static string ToTitle(string text)
        {
            bool first = true;
            return WordRegex.Replace(text, m =>
            {
                string word = m.Value;
                bool keepSmall = !first && SmallWords.Contains(word);
                first = false;
                // Leave acronyms (HVAC, RCP) alone.
                if (word.Length > 1 && !word.Any(char.IsLower)) return word;
                if (keepSmall) return word.ToLowerInvariant();
                return char.ToUpperInvariant(word[0]) + word.Substring(1);
            });
        }
    }

    /// <summary>Double spaces, stray spaces at the ends, spaces before punctuation and empty text notes.</summary>
    public class WhitespaceRule : IQcRule
    {
        public const string RuleId = "whitespace";
        private static readonly Regex DoubleSpace = new Regex(@"(?<=\S) {2,}(?=\S)", RegexOptions.Compiled);
        private static readonly Regex TrailingOnLine = new Regex(@"[ \t]+(?=[\r\n])", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforePunctuation = new Regex(@"(?<=\S) +(?=[,;.](\s|$))", RegexOptions.Compiled);

        public string Id => RuleId;
        public string Name => "Spacing";

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            if (!item.IsProse || item.Text == null) yield break;

            if (item.Text.Trim().Length == 0)
            {
                if (item.Source == TextSource.TextNote)
                    yield return new QcIssue
                    {
                        Item = item, RuleId = Id, RuleName = Name, Severity = Severity.Warning,
                        Message = "Text note is empty."
                    };
                yield break;
            }

            var found = new List<string>();
            string fixedText = item.Text;

            string trimmed = fixedText.Trim(' ', '\t');
            if (trimmed != fixedText) { found.Add("leading or trailing spaces"); fixedText = trimmed; }

            string noTrailing = TrailingOnLine.Replace(fixedText, "");
            if (noTrailing != fixedText) { found.Add("spaces at the end of a line"); fixedText = noTrailing; }

            string single = DoubleSpace.Replace(fixedText, " ");
            if (single != fixedText) { found.Add("double spaces"); fixedText = single; }

            string punctuation = SpaceBeforePunctuation.Replace(fixedText, "");
            if (punctuation != fixedText) { found.Add("space before punctuation"); fixedText = punctuation; }

            if (found.Count == 0) yield break;

            yield return new QcIssue
            {
                Item = item,
                RuleId = Id,
                RuleName = Name,
                Severity = Severity.Info,
                Suggestion = fixedText,
                Message = "Spacing: " + string.Join(", ", found) + "."
            };
        }
    }

    /// <summary>Words and phrases the office does not want on drawings, with an optional preferred wording.</summary>
    public class ForbiddenTermsRule : IQcRule
    {
        public const string RuleId = "forbidden";

        private readonly List<KeyValuePair<ForbiddenTerm, Regex>> _terms = new List<KeyValuePair<ForbiddenTerm, Regex>>();

        public string Id => RuleId;
        public string Name => "Forbidden term";

        public ForbiddenTermsRule(IEnumerable<ForbiddenTerm> terms)
        {
            foreach (var term in terms ?? Enumerable.Empty<ForbiddenTerm>())
            {
                if (term == null || string.IsNullOrWhiteSpace(term.Term)) continue;
                var regex = new Regex(
                    @"(?<![\p{L}\p{N}])" + Regex.Escape(term.Term.Trim()) + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase);
                _terms.Add(new KeyValuePair<ForbiddenTerm, Regex>(term, regex));
            }
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            if (!item.IsProse || string.IsNullOrEmpty(item.Text)) yield break;

            foreach (var pair in _terms)
            {
                var match = pair.Value.Match(item.Text);
                if (!match.Success) continue;

                string replacement = (pair.Key.Replacement ?? "").Trim();
                bool hasReplacement = replacement.Length > 0;
                yield return new QcIssue
                {
                    Item = item,
                    RuleId = Id,
                    RuleName = Name,
                    Severity = Severity.Warning,
                    Word = pair.Key.Term.Trim(),
                    Suggestion = hasReplacement
                        ? pair.Value.Replace(item.Text, m => SpellingRule.MatchCase(m.Value, replacement))
                        : null,
                    Message = hasReplacement
                        ? $"\"{match.Value}\" should not be used. Use \"{replacement}\" instead."
                        : $"\"{match.Value}\" should not be used."
                };
            }
        }
    }
}
