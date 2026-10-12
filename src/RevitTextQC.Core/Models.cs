using System.Collections.Generic;

namespace RevitTextQC.Core
{
    /// <summary>Where a piece of text came from in the Revit model.</summary>
    public enum TextSource
    {
        TextNote,
        Dimension,
        Parameter,
        Tag,
        Schedule,
        Keynote,
        SheetNumber,
        SheetName,
        ViewName,
        /// <summary>A text note type (style check only).</summary>
        TextNoteType,
        /// <summary>A dimension type (style check only).</summary>
        DimensionType
    }

    public enum Severity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>Font and size of a text or dimension type.</summary>
    public class TextStyleInfo
    {
        public string TypeName { get; set; }
        public string Font { get; set; }
        public double SizeMm { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public double WidthFactor { get; set; }
    }

    /// <summary>One piece of text collected from the model, ready to be checked.</summary>
    public class TextItem
    {
        public long ElementId { get; set; }
        public TextSource Source { get; set; }
        public string Text { get; set; }

        /// <summary>View, sheet or category the text sits in. Display only.</summary>
        public string Location { get; set; }

        /// <summary>Parameter name, dimension field (Prefix, Suffix, ...) or keynote key.</summary>
        public string Field { get; set; }

        /// <summary>Dimension segment index, or -1.</summary>
        public int Index { get; set; } = -1;

        /// <summary>Id of the text note type or dimension type of a text note or dimension, otherwise 0.</summary>
        public long TypeId { get; set; }

        /// <summary>Set for <see cref="TextSource.TextNoteType"/> and <see cref="TextSource.DimensionType"/>.</summary>
        public TextStyleInfo Style { get; set; }

        /// <summary>Number of instances using the type (type items only).</summary>
        public int UsageCount { get; set; }

        /// <summary>Text note has bold/italic/underline applied to part of the text, overriding its type.</summary>
        public bool HasInlineFormatting { get; set; }

        /// <summary>Text note is displayed in capitals through the All Caps format, whatever the stored text is.</summary>
        public bool DisplayedAllCaps { get; set; }

        /// <summary>The add-in can write a corrected value back to the model.</summary>
        public bool Editable { get; set; }

        public bool IsType => Source == TextSource.TextNoteType || Source == TextSource.DimensionType;

        /// <summary>Free text that spelling, whitespace and forbidden-term rules apply to.</summary>
        public bool IsProse => !IsType && Source != TextSource.SheetNumber;
    }

    public class QcIssue
    {
        public TextItem Item { get; set; }
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public Severity Severity { get; set; }
        public string Message { get; set; }

        /// <summary>Full corrected text for the item, or null when there is no automatic fix.</summary>
        public string Suggestion { get; set; }

        /// <summary>Other candidate words for a misspelling, best first. The first one produced <see cref="Suggestion"/>.</summary>
        public IList<string> Alternatives { get; set; } = new List<string>();

        /// <summary>Spelling suggestions were skipped to keep the scan fast and are worked out on demand.</summary>
        public bool SuggestionsPending { get; set; }

        /// <summary>The misspelled word or forbidden term the issue is about, if any.</summary>
        public string Word { get; set; }

        public bool CanFix => Suggestion != null && Item != null && Item.Editable && Suggestion != Item.Text;
    }

    public interface IQcRule
    {
        string Id { get; }
        string Name { get; }
        IEnumerable<QcIssue> Check(TextItem item);
    }

    public interface ISpellChecker
    {
        bool Check(string word);
        IList<string> Suggest(string word);
    }
}
