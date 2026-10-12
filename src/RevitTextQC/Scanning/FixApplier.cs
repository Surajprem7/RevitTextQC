using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTextQC.Core;

namespace RevitTextQC.Scanning
{
    /// <summary>Writes corrected text back to the model. Must be called inside an open transaction.</summary>
    public static class FixApplier
    {
        public const string RoomNameField = "@RoomName";

        public static void Apply(Document doc, TextItem item, string newText)
        {
            Element element = doc.GetElement(new ElementId(item.ElementId));
            if (element == null) throw new InvalidOperationException("The element no longer exists.");

            switch (item.Source)
            {
                case TextSource.TextNote:
                    ApplyToTextNote((TextNote)element, item.Text, newText);
                    break;

                case TextSource.Dimension:
                    ApplyToDimension((Dimension)element, item, newText);
                    break;

                case TextSource.Parameter:
                    Parameter parameter = item.Field == RoomNameField
                        ? element.get_Parameter(BuiltInParameter.ROOM_NAME)
                        : element.LookupParameter(item.Field);
                    if (parameter == null || parameter.IsReadOnly)
                        throw new InvalidOperationException("The parameter is read-only.");
                    parameter.Set(newText);
                    break;

                case TextSource.SheetName:
                case TextSource.ViewName:
                    element.Name = newText;
                    break;

                default:
                    throw new InvalidOperationException("This kind of text cannot be edited from Text QC.");
            }
        }

        // Only the characters that actually differ are replaced, so bold/italic runs and lists
        // elsewhere in the note keep their formatting.
        private static void ApplyToTextNote(TextNote note, string oldText, string newText)
        {
            FormattedText formatted = note.GetFormattedText();
            string plain = formatted.GetPlainText();
            if (!plain.StartsWith(oldText, StringComparison.Ordinal))
                throw new InvalidOperationException("The text note changed since the scan. Scan again.");

            int prefix = 0;
            int max = Math.Min(oldText.Length, newText.Length);
            while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;

            int suffix = 0;
            while (suffix < max - prefix &&
                   oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

            int start = prefix;
            int length = oldText.Length - prefix - suffix;
            string replacement = newText.Substring(prefix, newText.Length - prefix - suffix);

            // The API needs a non-empty range and non-empty text: widen by one unchanged neighbour.
            if (length == 0 || replacement.Length == 0)
            {
                if (start > 0)
                {
                    start--;
                    length++;
                    replacement = oldText[start] + replacement;
                }
                else if (start + length < oldText.Length)
                {
                    replacement += oldText[start + length];
                    length++;
                }
                else
                {
                    note.Text = newText;
                    return;
                }
            }

            formatted.SetPlainText(new TextRange(start, length), replacement);
            note.SetFormattedText(formatted);
        }

        private static void ApplyToDimension(Dimension dimension, TextItem item, string newText)
        {
            if (item.Index >= 0)
            {
                DimensionSegment segment = dimension.Segments.get_Item(item.Index);
                switch (item.Field)
                {
                    case "Prefix": segment.Prefix = newText; break;
                    case "Suffix": segment.Suffix = newText; break;
                    case "Above": segment.Above = newText; break;
                    case "Below": segment.Below = newText; break;
                    case "ValueOverride": segment.ValueOverride = newText; break;
                }
                return;
            }

            switch (item.Field)
            {
                case "Prefix": dimension.Prefix = newText; break;
                case "Suffix": dimension.Suffix = newText; break;
                case "Above": dimension.Above = newText; break;
                case "Below": dimension.Below = newText; break;
                case "ValueOverride": dimension.ValueOverride = newText; break;
            }
        }

        /// <summary>Selects and zooms to the element behind a finding. Returns a message when it cannot.</summary>
        public static string Show(UIDocument uidoc, TextItem item)
        {
            Document doc = uidoc.Document;
            var id = new ElementId(item.ElementId);
            Element element = doc.GetElement(id);
            if (element == null) return "The element no longer exists. Scan again.";

            switch (item.Source)
            {
                case TextSource.Keynote:
                    return $"Keynote {item.Field} is defined in the keynote file, not in a view.";

                case TextSource.TextNoteType:
                case TextSource.DimensionType:
                    Type instanceClass = item.Source == TextSource.TextNoteType ? typeof(TextNote) : typeof(Dimension);
                    List<ElementId> instances = new FilteredElementCollector(doc).OfClass(instanceClass)
                        .Where(e => e.GetTypeId() == id).Select(e => e.Id).ToList();
                    uidoc.Selection.SetElementIds(instances);
                    return $"Selected {instances.Count} element(s) using \"{item.Text}\". Use the Type Selector to change them.";

                case TextSource.SheetName:
                case TextSource.SheetNumber:
                case TextSource.ViewName:
                case TextSource.Schedule:
                    if (element is View view && !view.IsTemplate)
                    {
                        uidoc.RequestViewChange(view);
                        return null;
                    }
                    return "This view cannot be opened.";

                default:
                    uidoc.Selection.SetElementIds(new List<ElementId> { id });
                    uidoc.ShowElements(id);
                    return null;
            }
        }
    }
}
