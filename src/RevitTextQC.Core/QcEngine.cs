using System;
using System.Collections.Generic;
using System.Linq;
using RevitTextQC.Core.Rules;

namespace RevitTextQC.Core
{
    /// <summary>Runs the enabled rules over collected text.</summary>
    public class QcEngine
    {
        private readonly List<IQcRule> _rules = new List<IQcRule>();
        private readonly SpellingRule _spelling;

        /// <summary>Problems with the configuration itself (bad regex, dictionary not loaded) or with a rule.</summary>
        public List<string> ConfigurationWarnings { get; } = new List<string>();

        /// <param name="spellChecker">May be null when the dictionary could not be loaded; spelling is then skipped.</param>
        /// <param name="extraWords">Words accepted in addition to the user's custom words, such as a bundled list of trade abbreviations.</param>
        public QcEngine(QcSettings settings, ISpellChecker spellChecker, IEnumerable<string> extraWords = null)
        {
            if (settings.CheckSpelling)
            {
                if (spellChecker != null)
                {
                    IEnumerable<string> words = settings.CustomWords.Concat(extraWords ?? Enumerable.Empty<string>());
                    _spelling = new SpellingRule(spellChecker, words, settings.MinWordLength);
                    _rules.Add(_spelling);
                }
                else
                {
                    ConfigurationWarnings.Add("Spelling was skipped: the dictionary could not be loaded.");
                }
            }
            if (settings.CheckTextStyles) _rules.Add(new TextStyleRule(settings));
            if (settings.CheckCase) _rules.Add(new CaseRule(settings));
            if (settings.CheckWhitespace) _rules.Add(new WhitespaceRule());
            if (settings.CheckForbiddenTerms && settings.ForbiddenTerms.Count > 0)
                _rules.Add(new ForbiddenTermsRule(settings.ForbiddenTerms));
            if (settings.CheckNaming)
            {
                var naming = new NamingRule(settings);
                ConfigurationWarnings.AddRange(naming.ConfigurationWarnings);
                _rules.Add(naming);
            }
        }

        public List<QcIssue> Run(IEnumerable<TextItem> items)
        {
            _spelling?.ResetBudget();

            var issues = new List<QcIssue>();
            var failed = new HashSet<string>();
            foreach (var item in items)
            {
                foreach (var rule in _rules)
                {
                    try
                    {
                        issues.AddRange(rule.Check(item).ToList());
                    }
                    catch (Exception ex)
                    {
                        // One odd piece of text must not abort the whole scan; say so once per rule.
                        if (failed.Add(rule.Id))
                            ConfigurationWarnings.Add($"The {rule.Name} check failed on some text and may be incomplete: {ex.Message}");
                    }
                }
            }
            return issues;
        }

        /// <summary>Fills in spelling suggestions that the scan deferred. Cheap to call on any issue.</summary>
        public void ResolveSuggestions(QcIssue issue)
        {
            _spelling?.ResolveSuggestions(issue);
        }
    }
}
