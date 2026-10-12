using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core.Rules
{
    public class SpellingRule : IQcRule
    {
        public const string RuleId = "spelling";

        // A word is a run of letters (apostrophes allowed inside) that does not touch a digit,
        // so codes such as M12, 100mm or L1 are never spell checked.
        private static readonly Regex WordRegex = new Regex(
            @"(?<![\p{L}\p{N}_])\p{L}[\p{L}'’]*(?![\p{L}\p{N}_])", RegexOptions.Compiled);

        private readonly ISpellChecker _spell;
        private readonly HashSet<string> _custom;
        private readonly int _minLength;
        private readonly Dictionary<string, bool> _known = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, IList<string>> _suggestions = new Dictionary<string, IList<string>>(StringComparer.Ordinal);

        public string Id => RuleId;
        public string Name => "Spelling";

        public SpellingRule(ISpellChecker spell, IEnumerable<string> customWords, int minWordLength)
        {
            _spell = spell;
            _custom = new HashSet<string>(
                (customWords ?? Enumerable.Empty<string>()).Select(w => (w ?? "").Trim()).Where(w => w.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _minLength = Math.Max(1, minWordLength);
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            if (!item.IsProse || string.IsNullOrWhiteSpace(item.Text)) yield break;

            var reported = new HashSet<string>(StringComparer.Ordinal);
            var matches = WordRegex.Matches(item.Text).Cast<Match>().ToList();
            foreach (var match in matches)
            {
                string word = Clean(match.Value);
                if (word.Length < _minLength || IsMixedCase(word) || IsKnown(word)) continue;
                if (!reported.Add(word)) continue;

                var options = Suggest(word).Take(6).Select(o => MatchCase(word, o)).Distinct().ToList();

                yield return new QcIssue
                {
                    Item = item,
                    RuleId = Id,
                    RuleName = Name,
                    Severity = Severity.Warning,
                    Word = word,
                    Alternatives = options,
                    Suggestion = options.Count > 0 ? ReplaceWord(item.Text, word, options[0]) : null,
                    Message = options.Count > 0
                        ? $"\"{word}\" is not in the dictionary. Did you mean: {string.Join(", ", options)}?"
                        : $"\"{word}\" is not in the dictionary."
                };
            }
        }

        private static string Clean(string token)
        {
            return token.Replace('’', '\'').TrimEnd('\'');
        }

        // uPVC, PVCu, mmHg, McDonald: product names and unit symbols, not prose.
        private static bool IsMixedCase(string word)
        {
            bool upperAfterFirst = word.Skip(1).Any(char.IsUpper);
            bool anyLower = word.Any(char.IsLower);
            return upperAfterFirst && anyLower;
        }

        private static bool IsAllUpper(string word)
        {
            return word.Any(char.IsLetter) && !word.Any(char.IsLower);
        }

        private bool IsKnown(string word)
        {
            if (_custom.Contains(word)) return true;
            if (_known.TryGetValue(word, out bool known)) return known;

            known = _spell.Check(word);
            if (!known && IsAllUpper(word))
            {
                // Drawings are usually lettered in capitals: accept NOTE if "note" or "Note" is a word.
                string lower = word.ToLowerInvariant();
                known = _spell.Check(lower) || _spell.Check(Capitalize(lower));
            }
            _known[word] = known;
            return known;
        }

        private IList<string> Suggest(string word)
        {
            if (_suggestions.TryGetValue(word, out var cached)) return cached;
            IList<string> result;
            try
            {
                string query = IsAllUpper(word) ? word.ToLowerInvariant() : word;
                result = _spell.Suggest(query) ?? new List<string>();
            }
            catch (Exception)
            {
                result = new List<string>();
            }
            _suggestions[word] = result;
            return result;
        }

        private static string Capitalize(string lower)
        {
            return lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        internal static string MatchCase(string original, string replacement)
        {
            if (IsAllUpper(original) && original.Length > 1) return replacement.ToUpperInvariant();
            if (char.IsUpper(original[0]) && replacement.Length > 0 && char.IsLower(replacement[0]))
                return Capitalize(replacement);
            return replacement;
        }

        /// <summary>Replaces every occurrence of a misspelled word in the text with the chosen replacement.</summary>
        public static string ReplaceWord(string text, string word, string replacement)
        {
            var matches = WordRegex.Matches(text).Cast<Match>().ToList();
            var sb = new StringBuilder(text);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (Clean(matches[i].Value) != word) continue;
                sb.Remove(matches[i].Index, word.Length);
                sb.Insert(matches[i].Index, replacement);
            }
            return sb.ToString();
        }
    }
}
