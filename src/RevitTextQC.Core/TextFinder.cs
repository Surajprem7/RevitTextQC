using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RevitTextQC.Core.Rules;

namespace RevitTextQC.Core
{
    public enum FindBy
    {
        TextType,
        Size,
        Font,
        Style
    }

    public enum FindIn
    {
        All,
        TextNotes,
        Dimensions
    }

    /// <summary>A text note or dimension together with the style its type gives it.</summary>
    public class StyledElement
    {
        public long ElementId { get; set; }
        /// <summary><see cref="TextSource.TextNote"/> or <see cref="TextSource.Dimension"/>.</summary>
        public TextSource Kind { get; set; }
        public long TypeId { get; set; }
        public string Text { get; set; }
        public string Location { get; set; }
        public TextStyleInfo Style { get; set; }
    }

    /// <summary>All elements sharing one text type, size, font or style.</summary>
    public class FindGroup
    {
        public string Label { get; set; }
        public List<StyledElement> Elements { get; set; } = new List<StyledElement>();
        public int Count => Elements.Count;

        /// <summary>true/false against the office standards, or null when no standard is set for this property.</summary>
        public bool? Approved { get; set; }

        public string Standard => Approved == null ? "" : Approved.Value ? "Approved" : "Not approved";
    }

    /// <summary>Groups the text notes and dimensions of a model by type, size, font or style.</summary>
    public static class TextFinder
    {
        public static List<FindGroup> Group(IEnumerable<StyledElement> elements, FindBy by, FindIn scope, QcSettings settings)
        {
            var groups = new Dictionary<string, FindGroup>(StringComparer.OrdinalIgnoreCase);
            var sortKeys = new Dictionary<FindGroup, double>();

            foreach (StyledElement element in elements)
            {
                if (element.Style == null) continue;
                if (scope == FindIn.TextNotes && element.Kind != TextSource.TextNote) continue;
                if (scope == FindIn.Dimensions && element.Kind != TextSource.Dimension) continue;

                string label = Label(element, by, scope);
                if (!groups.TryGetValue(label, out FindGroup group))
                {
                    group = new FindGroup { Label = label };
                    groups[label] = group;
                    sortKeys[group] = by == FindBy.Size ? element.Style.SizeMm : 0;
                }
                group.Elements.Add(element);
            }

            foreach (FindGroup group in groups.Values)
            {
                List<bool?> verdicts = group.Elements.Select(e => IsApproved(e, by, settings)).ToList();
                group.Approved = verdicts.All(v => v == null) ? (bool?)null : verdicts.All(v => v != false);
            }

            return groups.Values
                .OrderBy(g => sortKeys[g])
                .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Label(StyledElement element, FindBy by, FindIn scope)
        {
            TextStyleInfo style = element.Style;
            switch (by)
            {
                case FindBy.TextType:
                    // A text type and a dimension type can share a name; keep them apart when both are listed.
                    string name = string.IsNullOrEmpty(style.TypeName) ? "(unnamed)" : style.TypeName;
                    if (scope != FindIn.All) return name;
                    return (element.Kind == TextSource.TextNote ? "Text: " : "Dimension: ") + name;

                case FindBy.Size:
                    return style.SizeMm > 0 ? TextStyleRule.FormatMm(style.SizeMm) + " mm" : "(no size)";

                case FindBy.Font:
                    return string.IsNullOrEmpty(style.Font) ? "(no font)" : style.Font;

                default:
                    return StyleLabel(style);
            }
        }

        public static string StyleLabel(TextStyleInfo style)
        {
            var parts = new List<string>();
            if (style.Bold) parts.Add("Bold");
            if (style.Italic) parts.Add("Italic");
            if (style.Underline) parts.Add("Underline");
            string label = parts.Count == 0 ? "Regular" : string.Join(" + ", parts);
            if (style.WidthFactor > 0 && Math.Abs(style.WidthFactor - 1) > 0.001)
                label += ", width " + style.WidthFactor.ToString("0.###", CultureInfo.InvariantCulture);
            return label;
        }

        private static bool? IsApproved(StyledElement element, FindBy by, QcSettings settings)
        {
            TextStyleInfo style = element.Style;
            TextStandards standards = element.Kind == TextSource.TextNote ? settings.TextNoteStandards : settings.DimensionStandards;

            switch (by)
            {
                case FindBy.TextType:
                    if (standards.AllowedTypeNames.Count == 0) return null;
                    return standards.AllowedTypeNames.Any(p => TextStyleRule.WildcardMatch(p, style.TypeName));

                case FindBy.Size:
                    if (standards.AllowedSizesMm.Count == 0 || style.SizeMm <= 0) return null;
                    return standards.AllowedSizesMm.Any(s => Math.Abs(s - style.SizeMm) <= 0.011);

                case FindBy.Font:
                    if (standards.AllowedFonts.Count == 0 || string.IsNullOrEmpty(style.Font)) return null;
                    return standards.AllowedFonts.Any(f => string.Equals(f.Trim(), style.Font, StringComparison.OrdinalIgnoreCase));

                default:
                    bool restricted = !standards.AllowBold || !standards.AllowItalic || !standards.AllowUnderline || standards.WidthFactor > 0;
                    if (!restricted) return null;
                    return !(style.Bold && !standards.AllowBold)
                        && !(style.Italic && !standards.AllowItalic)
                        && !(style.Underline && !standards.AllowUnderline)
                        && !(standards.WidthFactor > 0 && Math.Abs(standards.WidthFactor - style.WidthFactor) > 0.001);
            }
        }
    }
}
