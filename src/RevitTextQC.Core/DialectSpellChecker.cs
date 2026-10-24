using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core
{
    /// <summary>
    /// Checks spelling in British or Australian English without rejecting American words. A British dictionary
    /// has no "elevator" (British says lift) and no "restroom", but a drawing for a Gulf client uses them
    /// correctly. What should be reported is an American <i>spelling</i> of a word the language has (color,
    /// center, organize, labeled), not a word the language simply does not use.
    /// </summary>
    public class DialectSpellChecker : ISpellChecker
    {
        private readonly ISpellChecker _primary;
        private readonly ISpellChecker _american;

        public DialectSpellChecker(ISpellChecker primary, ISpellChecker american)
        {
            _primary = primary;
            _american = american;
        }

        public bool Check(string word)
        {
            if (_primary.Check(word)) return true;
            if (!_american.Check(word)) return false;

            // Right in American English but not in this language: wrong only if it is an American spelling of a
            // word this language has.
            foreach (string british in AmericanSpelling.BritishForms(word))
                if (_primary.Check(british)) return false;
            return true;
        }

        public IList<string> Suggest(string word)
        {
            return _primary.Suggest(word);
        }
    }

    /// <summary>The usual ways American spelling differs from British and Australian spelling.</summary>
    public static class AmericanSpelling
    {
        private class Rule
        {
            public Regex Pattern;
            public string Replacement;
        }

        // Each rule is tried at every place it matches, on its own; the caller keeps only candidates that are real
        // words, so a rule that does not apply simply produces nothing useful.
        private static readonly Rule[] Rules =
        {
            Rule_(@"or(?=(?:s|ed|ing|ful|able|ite|ites|y|ies)?$)", "our"),     // color, labor, neighbor, favorite
            Rule_(@"er(?=s?$)", "re"),                                          // center, fiber, theater
            Rule_(@"er(?=(?:line|lines|piece|pieces|fold|folds)$)", "re"),      // centerline
            Rule_(@"iz(?=(?:e|es|ed|ing|ation|ations|able|er|ers)$)", "is"),    // organize, realization
            Rule_(@"yz(?=(?:e|es|ed|ing|er|ers)$)", "ys"),                      // analyze, paralyze
            Rule_(@"ens(?=es?$)", "enc"),                                       // defense, offense
            Rule_(@"og(?=(?:s|ed|ing)?$)", "ogue"),                             // catalog, dialog
            Rule_(@"um$", "ium"),                                               // aluminum
            Rule_(@"(?<=[aeiou])l(?=(?:ed|ing|er|ers|or|ors)$)", "ll")          // labeled, modeling, leveled
        };

        private static Rule Rule_(string pattern, string replacement)
        {
            return new Rule { Pattern = new Regex(pattern, RegexOptions.Compiled), Replacement = replacement };
        }

        // Words whose British form is not a pattern: American stem, British stem.
        private static readonly KeyValuePair<string, string>[] Stems =
        {
            P("mold", "mould"), P("plow", "plough"), P("gray", "grey"), P("sulfur", "sulphur"), P("skeptic", "sceptic"),
            P("cozy", "cosy"), P("jewelry", "jewellery"), P("pajama", "pyjama"), P("maneuver", "manoeuvre"),
            P("esthetic", "aesthetic"), P("pediatric", "paediatric"), P("fetus", "foetus"), P("aging", "ageing"),
            P("acknowledgment", "acknowledgement"), P("judgment", "judgement"), P("fulfill", "fulfil"),
            P("installment", "instalment"), P("enrollment", "enrolment"), P("skillful", "skilful"), P("willful", "wilful"),
            P("artifact", "artefact"), P("mustache", "moustache"), P("sizable", "sizeable")
        };

        private static KeyValuePair<string, string> P(string american, string british)
        {
            return new KeyValuePair<string, string>(american, british);
        }

        private static readonly HashSet<string> Endings = new HashSet<string> { "", "s", "es", "ed", "ing", "er", "ers", "y", "ies" };

        /// <summary>Possible British spellings of an American word. Most are not words; the caller checks each.</summary>
        public static IEnumerable<string> BritishForms(string word)
        {
            if (string.IsNullOrEmpty(word)) yield break;

            string lower = word.ToLowerInvariant();
            var seen = new HashSet<string> { lower };

            foreach (string candidate in Candidates(lower))
                if (seen.Add(candidate)) yield return RestoreCase(word, candidate);
        }

        private static IEnumerable<string> Candidates(string word)
        {
            foreach (Rule rule in Rules)
                foreach (Match match in rule.Pattern.Matches(word))
                    yield return word.Substring(0, match.Index) + rule.Replacement + word.Substring(match.Index + match.Length);

            foreach (var stem in Stems)
                if (word.StartsWith(stem.Key, StringComparison.Ordinal) && Endings.Contains(word.Substring(stem.Key.Length)))
                    yield return stem.Value + word.Substring(stem.Key.Length);
        }

        private static string RestoreCase(string original, string candidate)
        {
            if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c))) return candidate.ToUpperInvariant();
            if (char.IsUpper(original[0])) return char.ToUpperInvariant(candidate[0]) + candidate.Substring(1);
            return candidate;
        }
    }
}
