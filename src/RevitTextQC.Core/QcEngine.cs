using System.Collections.Generic;
using RevitTextQC.Core.Rules;

namespace RevitTextQC.Core
{
    /// <summary>Runs the enabled rules over collected text.</summary>
    public class QcEngine
    {
        private readonly List<IQcRule> _rules = new List<IQcRule>();

        /// <summary>Problems with the configuration itself (bad regex, dictionary not loaded).</summary>
        public List<string> ConfigurationWarnings { get; } = new List<string>();

        /// <param name="spellChecker">May be null when the dictionary could not be loaded; spelling is then skipped.</param>
        public QcEngine(QcSettings settings, ISpellChecker spellChecker)
        {
            if (settings.CheckSpelling)
            {
                if (spellChecker != null)
                    _rules.Add(new SpellingRule(spellChecker, settings.CustomWords, settings.MinWordLength));
                else
                    ConfigurationWarnings.Add("Spelling was skipped: the dictionary could not be loaded.");
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
            var issues = new List<QcIssue>();
            foreach (var item in items)
                foreach (var rule in _rules)
                    issues.AddRange(rule.Check(item));
            return issues;
        }
    }
}
