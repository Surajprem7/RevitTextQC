using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitTextQC.Core;

namespace RevitTextQC.Scanning
{
    public enum ScanScope
    {
        ActiveView,
        EntireProject
    }

    /// <summary>Reads every kind of text the QC rules look at out of a Revit document.</summary>
    public class TextCollector
    {
        private const int MaxScheduleBodyRows = 5000;

        private readonly Document _doc;
        private readonly QcSettings _settings;
        private readonly Dictionary<ElementId, string> _viewNames = new Dictionary<ElementId, string>();

        // null = whole project, otherwise the views the scan is limited to.
        private readonly List<ElementId> _viewIds;
        private readonly View _activeView;

        public TextCollector(Document doc, View activeView, ScanScope scope, QcSettings settings)
        {
            _doc = doc;
            _settings = settings;
            _activeView = activeView;
            _viewIds = scope == ScanScope.ActiveView && activeView != null ? ViewsInScope(activeView) : null;
        }

        public List<TextItem> Collect()
        {
            var items = new List<TextItem>();
            if (_settings.ScanTextNotes) CollectTextNotes(items);
            if (_settings.ScanDimensions) CollectDimensions(items);
            if (_settings.ScanParameters) CollectParameters(items);
            if (_settings.ScanTags) CollectTags(items);
            if (_settings.ScanSheetsAndViews) CollectSheetsAndViews(items);
            if (_settings.ScanSchedules) CollectSchedules(items);
            if (_settings.ScanKeynotes && _viewIds == null) CollectKeynotes(items);
            return items;
        }

        /// <summary>Text note and dimension types in use, for the "fill from current model" button in the settings.</summary>
        public List<TextItem> CollectTypesOnly()
        {
            var items = new List<TextItem>();
            CollectTextNotes(items);
            CollectDimensions(items);
            return items.Where(i => i.IsType).ToList();
        }

        private static List<ElementId> ViewsInScope(View activeView)
        {
            var ids = new List<ElementId> { activeView.Id };
            if (activeView is ViewSheet sheet) ids.AddRange(sheet.GetAllPlacedViews());
            return ids;
        }

        private IEnumerable<Element> Elements(Func<FilteredElementCollector, FilteredElementCollector> filter)
        {
            if (_viewIds == null) return filter(new FilteredElementCollector(_doc)).ToElements();

            var result = new List<Element>();
            var seen = new HashSet<ElementId>();
            foreach (ElementId viewId in _viewIds)
            {
                try
                {
                    foreach (Element e in filter(new FilteredElementCollector(_doc, viewId)))
                        if (seen.Add(e.Id)) result.Add(e);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Some view kinds (schedules, browsers) cannot be used to filter elements.
                }
            }
            return result;
        }

        private string ViewName(ElementId viewId)
        {
            if (viewId == null || viewId == ElementId.InvalidElementId) return "";
            if (!_viewNames.TryGetValue(viewId, out string name))
            {
                name = (_doc.GetElement(viewId) as View)?.Name ?? "";
                _viewNames[viewId] = name;
            }
            return name;
        }

        private void CollectTextNotes(List<TextItem> items)
        {
            var typeUsage = new Dictionary<ElementId, int>();
            foreach (TextNote note in Elements(c => c.OfClass(typeof(TextNote))).Cast<TextNote>())
            {
                Count(typeUsage, note.GetTypeId());

                var item = new TextItem
                {
                    ElementId = note.Id.Value,
                    Source = TextSource.TextNote,
                    Location = ViewName(note.OwnerViewId),
                    Editable = true
                };

                try
                {
                    FormattedText formatted = note.GetFormattedText();
                    // Revit stores a trailing line break on most notes; it is not part of what the user typed.
                    item.Text = formatted.GetPlainText().TrimEnd('\r', '\n');
                    item.DisplayedAllCaps = formatted.GetAllCapsStatus() == FormatStatus.All;

                    var type = _doc.GetElement(note.GetTypeId()) as TextNoteType;
                    item.HasInlineFormatting =
                        Overrides(formatted.GetBoldStatus(), IsOn(type, BuiltInParameter.TEXT_STYLE_BOLD)) ||
                        Overrides(formatted.GetItalicStatus(), IsOn(type, BuiltInParameter.TEXT_STYLE_ITALIC)) ||
                        Overrides(formatted.GetUnderlineStatus(), IsOn(type, BuiltInParameter.TEXT_STYLE_UNDERLINE));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    item.Text = (note.Text ?? "").TrimEnd('\r', '\n');
                }

                items.Add(item);
            }
            AddTypeItems(items, typeUsage, TextSource.TextNoteType);
        }

        private static bool Overrides(FormatStatus status, bool typeHasIt)
        {
            return status == FormatStatus.Mixed || (status == FormatStatus.All && !typeHasIt);
        }

        private void CollectDimensions(List<TextItem> items)
        {
            var typeUsage = new Dictionary<ElementId, int>();
            foreach (Dimension dimension in Elements(c => c.OfClass(typeof(Dimension))).Cast<Dimension>())
            {
                Count(typeUsage, dimension.GetTypeId());
                string location = ViewName(dimension.OwnerViewId);

                int segments = 0;
                try { segments = dimension.NumberOfSegments; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }

                if (segments > 0)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        DimensionSegment segment = dimension.Segments.get_Item(i);
                        AddDimensionText(items, dimension, i, location, "Prefix", () => segment.Prefix);
                        AddDimensionText(items, dimension, i, location, "Suffix", () => segment.Suffix);
                        AddDimensionText(items, dimension, i, location, "Above", () => segment.Above);
                        AddDimensionText(items, dimension, i, location, "Below", () => segment.Below);
                        AddDimensionText(items, dimension, i, location, "ValueOverride", () => segment.ValueOverride);
                    }
                }
                else
                {
                    AddDimensionText(items, dimension, -1, location, "Prefix", () => dimension.Prefix);
                    AddDimensionText(items, dimension, -1, location, "Suffix", () => dimension.Suffix);
                    AddDimensionText(items, dimension, -1, location, "Above", () => dimension.Above);
                    AddDimensionText(items, dimension, -1, location, "Below", () => dimension.Below);
                    AddDimensionText(items, dimension, -1, location, "ValueOverride", () => dimension.ValueOverride);
                }
            }
            AddTypeItems(items, typeUsage, TextSource.DimensionType);
        }

        private static void AddDimensionText(List<TextItem> items, Dimension dimension, int index, string location,
            string field, Func<string> read)
        {
            string text;
            try { text = read(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return; } // field not supported by this dimension kind

            if (string.IsNullOrEmpty(text)) return;
            items.Add(new TextItem
            {
                ElementId = dimension.Id.Value,
                Source = TextSource.Dimension,
                Text = text,
                Location = location,
                Field = field,
                Index = index,
                Editable = true
            });
        }

        private static void Count(Dictionary<ElementId, int> usage, ElementId typeId)
        {
            if (typeId == null || typeId == ElementId.InvalidElementId) return;
            usage.TryGetValue(typeId, out int count);
            usage[typeId] = count + 1;
        }

        private void AddTypeItems(List<TextItem> items, Dictionary<ElementId, int> usage, TextSource source)
        {
            foreach (var pair in usage.OrderBy(p => _doc.GetElement(p.Key)?.Name))
            {
                if (!(_doc.GetElement(pair.Key) is ElementType type)) continue;

                double sizeFeet = type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                items.Add(new TextItem
                {
                    ElementId = type.Id.Value,
                    Source = source,
                    Text = type.Name,
                    Location = pair.Value == 1 ? "1 instance" : pair.Value + " instances",
                    UsageCount = pair.Value,
                    Style = new TextStyleInfo
                    {
                        TypeName = type.Name,
                        Font = type.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString() ?? "",
                        SizeMm = Math.Round(UnitUtils.ConvertFromInternalUnits(sizeFeet, UnitTypeId.Millimeters), 3),
                        Bold = IsOn(type, BuiltInParameter.TEXT_STYLE_BOLD),
                        Italic = IsOn(type, BuiltInParameter.TEXT_STYLE_ITALIC),
                        Underline = IsOn(type, BuiltInParameter.TEXT_STYLE_UNDERLINE),
                        WidthFactor = type.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1
                    }
                });
            }
        }

        private static bool IsOn(Element type, BuiltInParameter parameter)
        {
            return type?.get_Parameter(parameter)?.AsInteger() == 1;
        }

        private void CollectParameters(List<TextItem> items)
        {
            // Room, space and area names.
            foreach (Element element in Elements(c => c.OfClass(typeof(SpatialElement))))
            {
                Parameter name = element.get_Parameter(BuiltInParameter.ROOM_NAME);
                string text = name?.AsString();
                if (string.IsNullOrEmpty(text)) continue;
                items.Add(new TextItem
                {
                    ElementId = element.Id.Value,
                    Source = TextSource.Parameter,
                    Text = text,
                    Location = element.Category?.Name ?? "",
                    Field = FixApplier.RoomNameField,
                    Editable = !name.IsReadOnly
                });
            }

            var names = _settings.ParameterNames.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct().ToList();
            if (names.Count == 0) return;

            foreach (Element element in Elements(c => c.WhereElementIsNotElementType()))
            {
                if (element.Category == null) continue;
                foreach (string name in names)
                {
                    Parameter parameter = element.LookupParameter(name);
                    if (parameter == null || parameter.StorageType != StorageType.String || !parameter.HasValue) continue;
                    string text = parameter.AsString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    items.Add(new TextItem
                    {
                        ElementId = element.Id.Value,
                        Source = TextSource.Parameter,
                        Text = text,
                        Location = element.Category.Name,
                        Field = name,
                        Editable = !parameter.IsReadOnly
                    });
                }
            }
        }

        private void CollectTags(List<TextItem> items)
        {
            foreach (IndependentTag tag in Elements(c => c.OfClass(typeof(IndependentTag))).Cast<IndependentTag>())
            {
                string text;
                try { text = tag.TagText; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; } // orphaned tag

                if (string.IsNullOrWhiteSpace(text)) continue;
                items.Add(new TextItem
                {
                    ElementId = tag.Id.Value,
                    Source = TextSource.Tag,
                    Text = text,
                    Location = ViewName(tag.OwnerViewId),
                    Field = tag.Category?.Name,
                    Editable = false // the text comes from the tagged element's parameters
                });
            }
        }

        private void CollectSheetsAndViews(List<TextItem> items)
        {
            IEnumerable<View> views = _viewIds == null
                ? new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                : _viewIds.Select(id => _doc.GetElement(id)).OfType<View>();

            foreach (View view in views)
            {
                if (view.IsTemplate) continue;

                if (view is ViewSheet sheet)
                {
                    items.Add(new TextItem
                    {
                        ElementId = sheet.Id.Value, Source = TextSource.SheetNumber, Text = sheet.SheetNumber,
                        Location = sheet.Name, Editable = false
                    });
                    items.Add(new TextItem
                    {
                        ElementId = sheet.Id.Value, Source = TextSource.SheetName, Text = sheet.Name,
                        Location = sheet.SheetNumber, Editable = true
                    });
                    continue;
                }

                switch (view.ViewType)
                {
                    case ViewType.ProjectBrowser:
                    case ViewType.SystemBrowser:
                    case ViewType.Internal:
                    case ViewType.Undefined:
                    case ViewType.DrawingSheet:
                        continue;
                }
                if (view is ViewSchedule schedule && IsInternalSchedule(schedule)) continue;

                items.Add(new TextItem
                {
                    ElementId = view.Id.Value, Source = TextSource.ViewName, Text = view.Name,
                    Location = view.ViewType.ToString(), Editable = true
                });
            }
        }

        private static bool IsInternalSchedule(ViewSchedule schedule)
        {
            return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule;
        }

        private void CollectSchedules(List<TextItem> items)
        {
            foreach (ViewSchedule schedule in SchedulesInScope())
            {
                if (schedule.IsTemplate || IsInternalSchedule(schedule)) continue;

                var seen = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    ScheduleDefinition definition = schedule.Definition;
                    for (int i = 0; i < definition.GetFieldCount(); i++)
                    {
                        ScheduleField field = definition.GetField(i);
                        if (!field.IsHidden) AddScheduleText(items, schedule, field.ColumnHeading, "Column heading", seen);
                    }

                    ReadSection(items, schedule, SectionType.Header, "Title", int.MaxValue, seen);
                    if (_settings.ScanScheduleBody)
                        ReadSection(items, schedule, SectionType.Body, "Cell", MaxScheduleBodyRows, seen);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // A schedule that cannot be read is skipped rather than failing the whole scan.
                }
            }
        }

        private IEnumerable<ViewSchedule> SchedulesInScope()
        {
            if (_viewIds == null)
                return new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>();

            var result = new List<ViewSchedule>();
            if (_activeView is ViewSchedule active) result.Add(active);
            if (_activeView is ViewSheet)
            {
                foreach (ScheduleSheetInstance instance in new FilteredElementCollector(_doc, _activeView.Id)
                             .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                {
                    if (_doc.GetElement(instance.ScheduleId) is ViewSchedule placed && result.All(s => s.Id != placed.Id))
                        result.Add(placed);
                }
            }
            return result;
        }

        private static void ReadSection(List<TextItem> items, ViewSchedule schedule, SectionType section, string field,
            int maxRows, HashSet<string> seen)
        {
            TableSectionData data = schedule.GetTableData().GetSectionData(section);
            if (data == null) return;

            int lastRow = Math.Min(data.LastRowNumber, data.FirstRowNumber + maxRows - 1);
            for (int row = data.FirstRowNumber; row <= lastRow; row++)
                for (int column = data.FirstColumnNumber; column <= data.LastColumnNumber; column++)
                    AddScheduleText(items, schedule, schedule.GetCellText(section, row, column), field, seen);
        }

        private static void AddScheduleText(List<TextItem> items, ViewSchedule schedule, string text, string field,
            HashSet<string> seen)
        {
            // Merged cells and repeated values report the same text many times; one finding per schedule is enough.
            if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter) || !seen.Add(text)) return;
            items.Add(new TextItem
            {
                ElementId = schedule.Id.Value,
                Source = TextSource.Schedule,
                Text = text,
                Location = schedule.Name,
                Field = field,
                Editable = false
            });
        }

        private void CollectKeynotes(List<TextItem> items)
        {
            KeynoteTable table = KeynoteTable.GetKeynoteTable(_doc);
            if (table == null) return;

            HashSet<string> usedKeys = _settings.KeynotesUsedOnly ? UsedKeynoteKeys() : null;
            try
            {
                foreach (KeyBasedTreeEntry entry in table.GetKeyBasedTreeEntries())
                {
                    if (!(entry is KeynoteEntry keynote) || string.IsNullOrWhiteSpace(keynote.KeynoteText)) continue;
                    if (usedKeys != null && !usedKeys.Contains(keynote.Key)) continue;
                    items.Add(new TextItem
                    {
                        ElementId = table.Id.Value,
                        Source = TextSource.Keynote,
                        Text = keynote.KeynoteText,
                        Location = "Keynote table",
                        Field = keynote.Key,
                        Editable = false // lives in the external keynote file
                    });
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Keynote file not loaded.
            }
        }

        private HashSet<string> UsedKeynoteKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typesAndMaterials = new FilteredElementCollector(_doc).WhereElementIsElementType().ToElements()
                .Concat(new FilteredElementCollector(_doc).OfClass(typeof(Material)).ToElements());
            foreach (Element element in typesAndMaterials)
            {
                string key = element.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
                if (!string.IsNullOrWhiteSpace(key)) keys.Add(key.Trim());
            }
            return keys;
        }
    }
}
