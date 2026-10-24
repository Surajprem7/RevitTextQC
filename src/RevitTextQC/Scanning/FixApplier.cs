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

            if (item.Source == TextSource.GridName || item.Source == TextSource.LevelName)
            {
                element.Name = newText;
                return;
            }
            if (item.Source == TextSource.ModelText)
            {
                ((ModelText)element).Text = newText;
                return;
            }
            if (item.Source == TextSource.Revision)
            {
                ((Revision)element).Description = newText;
                return;
            }
            if (item.Source == TextSource.TitleBlock)
            {
                Parameter field = element.LookupParameter(item.Field);
                if (field == null || field.IsReadOnly) throw new InvalidOperationException("The field is read-only.");
                field.Set(newText);
                return;
            }

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

        /// <summary>
        /// Selects a set of elements in the open view, skipping any deleted since the scan or living in a view
        /// that is not open (on a sheet, the viewport of their view is selected instead). Returns how many were selected.
        /// </summary>
        public static int Select(UIDocument uidoc, IEnumerable<long> elementIds)
        {
            List<ElementId> ids = elementIds.Select(id => new ElementId(id)).ToList();
            return ids.Count - SheetFocus.Select(uidoc, ids);
        }

        /// <summary>Selects one element and brings it into view. On a sheet it stays on the sheet. Returns a status message.</summary>
        /// <param name="viewHint">The view the text was read from, when known (0 when not).</param>
        public static string ShowElement(UIDocument uidoc, long elementId, bool openViews, long viewHint = 0)
        {
            Document doc = uidoc.Document;
            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null) return "The element no longer exists. Scan again.";

            // On a sheet, stay on the sheet and zoom to where the text is printed.
            if (uidoc.ActiveView is ViewSheet sheet)
            {
                string onSheet = SheetFocus.ShowElement(uidoc, sheet, element, viewHint);
                if (onSheet != null) return onSheet;
            }

            // A plain click never takes the user to another view. Annotation records its view; a model element
            // does not, so ask whether the open view draws it.
            ElementId owner = element.OwnerViewId;
            bool hasOwner = owner != null && owner != ElementId.InvalidElementId;
            bool inOpenView = hasOwner ? owner == uidoc.ActiveView.Id : SheetFocus.IsShownIn(doc, uidoc.ActiveView.Id, element.Id);
            if (!inOpenView && !openViews)
            {
                string where = hasOwner ? " in view \"" + (doc.GetElement(owner) as View)?.Name + "\"" : "";
                return "This text is not in the open view" + where + ". Press Show to open it.";
            }

            // In the open view, or Show on something in another view: Revit zooms to it, opening its view if needed.
            uidoc.Selection.SetElementIds(new List<ElementId> { element.Id });
            uidoc.ShowElements(element.Id);
            return "Shown in Revit.";
        }

        /// <summary>Selects and zooms to the element behind a finding. Returns a message when it cannot.</summary>
        /// <param name="openViews">
        /// true for an explicit "Show in Revit": may open another view. false for a plain click on a
        /// finding: highlights the element but never takes the user away from the view they are in
        /// unless the element cannot be seen there.
        /// </param>
        public static string Show(UIDocument uidoc, TextItem item, bool openViews = true)
        {
            Document doc = uidoc.Document;
            var id = new ElementId(item.ElementId);
            Element element = doc.GetElement(id);
            if (element == null) return "The element no longer exists. Scan again.";

            // On a sheet, title block fields, views and schedules are shown on the sheet itself.
            if (uidoc.ActiveView is ViewSheet openSheet)
            {
                string onSheet = SheetFocus.ShowItem(uidoc, openSheet, item, element);
                if (onSheet != null) return onSheet;
            }

            switch (item.Source)
            {
                case TextSource.Keynote:
                    return $"Keynote {item.Field} is defined in the keynote file, not in a view.";

                case TextSource.Revision:
                    return "Revision text is edited under Manage > Sheet Issues/Revisions. Press Apply fix to change it here.";

                case TextSource.TitleBlock:
                    if (element is ViewSheet titleSheet)
                    {
                        if (!openViews) return "Press Show to open this sheet.";
                        uidoc.RequestViewChange(titleSheet);
                        return null;
                    }
                    return "This is a project information field (Manage > Project Information). Press Apply fix to change it here.";

                case TextSource.TitleBlockText:
                    if (doc.GetElement(element.OwnerViewId) is ViewSheet owningSheet)
                    {
                        if (!openViews) return "This title block text is on sheet " + owningSheet.SheetNumber + ". Press Show to open the sheet.";
                        uidoc.RequestViewChange(owningSheet);
                        return null;
                    }
                    return "This text is typed inside the title block family.";

                case TextSource.TextNoteType:
                case TextSource.DimensionType:
                    Type instanceClass = item.Source == TextSource.TextNoteType ? typeof(TextNote) : typeof(Dimension);
                    List<ElementId> instances = new FilteredElementCollector(doc).OfClass(instanceClass)
                        .Where(e => e.GetTypeId() == id).Select(e => e.Id).ToList();
                    int left = SheetFocus.Select(uidoc, instances);
                    string rest = left > 0 ? $" ({left} more are in views that are not open)" : "";
                    return $"Selected {instances.Count - left} element(s) using \"{item.Text}\"{rest}. Use the Type Selector to change them.";

                case TextSource.SheetName:
                case TextSource.SheetNumber:
                case TextSource.ViewName:
                case TextSource.Schedule:
                    if (!openViews) return "Double-click or press Show in Revit to open this view.";
                    if (element is View view && !view.IsTemplate)
                    {
                        uidoc.RequestViewChange(view);
                        return null;
                    }
                    return "This view cannot be opened.";

                default:
                    return ShowElement(uidoc, item.ElementId, openViews, item.ViewId);
            }
        }

    }
}
