using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core.Rules
{
    /// <summary>Checks the type, font and size of text notes and dimensions against the approved standards.</summary>
    public class TextStyleRule : IQcRule
    {
        public const string RuleId = "style";
        private const double SizeToleranceMm = 0.011;
        private const double WidthTolerance = 0.001;

        private readonly QcSettings _settings;

        public string Id => RuleId;
        public string Name => "Text type & size";

        public TextStyleRule(QcSettings settings)
        {
            _settings = settings;
        }

        public IEnumerable<QcIssue> Check(TextItem item)
        {
            if (item.Source == TextSource.TextNote)
            {
                if (_settings.FlagInlineFormatting && item.HasInlineFormatting)
                    yield return Issue(item, Severity.Info,
                        "Text note has manual bold, italic or underline formatting that overrides its text type.");
                yield break;
            }

            if (!item.IsType || item.Style == null) yield break;

            var style = item.Style;
            var standards = item.Source == TextSource.TextNoteType
                ? _settings.TextNoteStandards
                : _settings.DimensionStandards;
            string kind = item.Source == TextSource.TextNoteType ? "Text type" : "Dimension type";
            string used = item.UsageCount == 1 ? "used once" : $"used {item.UsageCount} times";
            string size = FormatMm(style.SizeMm);

            if (standards.AllowedTypeNames.Count > 0 && !standards.AllowedTypeNames.Any(p => WildcardMatch(p, style.TypeName)))
            {
                yield return Issue(item, Severity.Warning,
                    $"{kind} \"{style.TypeName}\" ({style.Font}, {size} mm) is not an approved type; {used}.");
                yield break;
            }

            // Some dimension kinds carry no font or size of their own; there is nothing to compare then.
            if (standards.AllowedFonts.Count > 0 && !string.IsNullOrEmpty(style.Font) &&
                !standards.AllowedFonts.Any(f => string.Equals(f.Trim(), style.Font, StringComparison.OrdinalIgnoreCase)))
            {
                yield return Issue(item, Severity.Warning,
                    $"Font \"{style.Font}\" is not approved (approved: {string.Join(", ", standards.AllowedFonts)}); {used}.");
            }

            if (standards.AllowedSizesMm.Count > 0 && style.SizeMm > 0 &&
                !standards.AllowedSizesMm.Any(s => Math.Abs(s - style.SizeMm) <= SizeToleranceMm))
            {
                yield return Issue(item, Severity.Warning,
                    $"Text size {size} mm is not approved (approved: {string.Join(", ", standards.AllowedSizesMm.Select(FormatMm))} mm); {used}.");
            }

            if (standards.WidthFactor > 0 && Math.Abs(standards.WidthFactor - style.WidthFactor) > WidthTolerance)
            {
                yield return Issue(item, Severity.Warning,
                    $"Width factor {style.WidthFactor.ToString("0.###", CultureInfo.InvariantCulture)} should be {standards.WidthFactor.ToString("0.###", CultureInfo.InvariantCulture)}; {used}.");
            }

            var notAllowed = new List<string>();
            if (style.Bold && !standards.AllowBold) notAllowed.Add("bold");
            if (style.Italic && !standards.AllowItalic) notAllowed.Add("italic");
            if (style.Underline && !standards.AllowUnderline) notAllowed.Add("underline");
            if (notAllowed.Count > 0)
            {
                yield return Issue(item, Severity.Warning,
                    $"{kind} uses {string.Join(", ", notAllowed)}, which is not allowed; {used}.");
            }
        }

        private QcIssue Issue(TextItem item, Severity severity, string message)
        {
            return new QcIssue { Item = item, RuleId = Id, RuleName = Name, Severity = severity, Message = message };
        }

        public static string FormatMm(double mm)
        {
            return mm.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static bool WildcardMatch(string pattern, string value)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            string regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? "", regex, RegexOptions.IgnoreCase);
        }
    }
}
