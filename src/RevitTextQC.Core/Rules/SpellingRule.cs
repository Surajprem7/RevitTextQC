using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core.Rules
{
    public class SpellingRule : IQcRule
    {
        public const string RuleId = "spelling";
        private const int MaxAlternatives = 6;

        // A word is a run of letters (apostrophes allowed inside) that does not touch a digit,
        // so codes such as M12, 100mm or L1 are never spell checked.
        private static readonly Regex WordRegex = new Regex(
            @"(?<![\p{L}\p{N}_])\p{L}[\p{L}'’]*(?![\p{L}\p{N}_])", RegexOptions.Compiled);

        private readonly ISpellChecker _spell;
        private readonly HashSet<string> _custom;
        private readonly int _minLength;
        private readonly Dictionary<string, bool> _known = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, IList<string>> _suggestions = new Dictionary<string, IList<string>>(StringComparer.Ordinal);
        private readonly Stopwatch _suggestTime = new Stopwatch();

        public string Id => RuleId;
        public string Name => "Spelling";

        /// <summary>
        /// Looking up suggestions costs 5-30 ms per unknown word, which adds up to minutes on a large
        /// project. Past this budget a scan only reports the word; suggestions are then worked out
        /// when the user selects the finding (see <see cref="ResolveSuggestions"/>).
        /// </summary>
        public TimeSpan SuggestBudget { get; set; } = TimeSpan.FromSeconds(4);

        public SpellingRule(ISpellChecker spell, IEnumerable<string> customWords, int minWordLength)
        {
            _spell = spell;
            _custom = new HashSet<string>(
                (customWords ?? Enumerable.Empty<string>()).Select(w => (w ?? "").Trim()).Where(w => w.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _minLength = Math.Max(1, minWordLength);
        }

        /// <summary>Starts a fresh suggestion budget; called at the start of every scan.</summary>
        public void ResetBudget()
        {
            _suggestTime.Reset();
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            if (!item.IsProse || item.SkipSpelling || string.IsNullOrWhiteSpace(item.Text)) yield break;

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in WordRegex.Matches(item.Text))
            {
                string word = Clean(match.Value);
                if (word.Length < _minLength || IsMixedCase(word) || IsKnown(word)) continue;
                if (!reported.Add(word)) continue;

                var issue = new QcIssue
                {
                    Item = item,
                    RuleId = Id,
                    RuleName = Name,
                    Severity = Severity.Warning,
                    Word = word,
                    Problem = word,
                    Message = $"\"{word}\" is not in the dictionary."
                };

                if (_suggestions.ContainsKey(word) || _suggestTime.Elapsed < SuggestBudget)
                    Fill(issue);
                else
                    issue.SuggestionsPending = true;

                yield return issue;
            }
        }

        /// <summary>Works out the suggestions a scan skipped to stay fast.</summary>
        public void ResolveSuggestions(QcIssue issue)
        {
            if (issue == null || !issue.SuggestionsPending || issue.RuleId != Id) return;
            Fill(issue);
        }

        private void Fill(QcIssue issue)
        {
            issue.SuggestionsPending = false;
            string word = issue.Word;
            List<string> options = Suggest(word).Take(MaxAlternatives).Select(o => MatchCase(word, o)).Distinct().ToList();
            if (options.Count == 0) return;

            issue.Alternatives = options;
            issue.Suggestion = ReplaceWord(issue.Item.Text, word, options[0]);
            // The column has room for two; the first is not always the best, so show a second choice.
            issue.Replacement = string.Join(" / ", options.Take(2));
            issue.Message = $"\"{word}\" is not in the dictionary. Did you mean: {string.Join(", ", options)}?";
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

            known = Lookup(word);
            if (!known)
            {
                // Café is cafe with an accent; the English dictionary lists only the plain spelling.
                string plain = RemoveDiacritics(word);
                known = plain != word && Lookup(plain);
            }
            _known[word] = known;
            return known;
        }

        private bool Lookup(string word)
        {
            if (_spell.Check(word)) return true;
            if (!IsAllUpper(word)) return false;

            // Drawings are usually lettered in capitals: accept NOTE if "note" or "Note" is a word.
            string lower = word.ToLowerInvariant();
            return _spell.Check(lower) || _spell.Check(Capitalize(lower));
        }

        private static string RemoveDiacritics(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private IList<string> Suggest(string word)
        {
            if (_suggestions.TryGetValue(word, out var cached)) return cached;

            IList<string> result;
            _suggestTime.Start();
            try
            {
                string query = IsAllUpper(word) ? word.ToLowerInvariant() : word;
                result = _spell.Suggest(query) ?? new List<string>();
            }
            catch (Exception)
            {
                result = new List<string>();
            }
            finally
            {
                _suggestTime.Stop();
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
