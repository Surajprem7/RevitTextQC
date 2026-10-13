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
        private readonly Dictionary<ElementId, TextStyleInfo> _styles = new Dictionary<ElementId, TextStyleInfo>();

        /// <summary>Every text note and dimension in scope with its type's style, for the Find tab.</summary>
        public List<StyledElement> StyledElements { get; } = new List<StyledElement>();

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
            StyledElements.Clear();
            // Always walked, even when switched off for QC, because the Find tab lists them regardless.
            CollectTextNotes(_settings.ScanTextNotes ? items : new List<TextItem>());
            CollectDimensions(_settings.ScanDimensions ? items : new List<TextItem>());
            if (_settings.ScanTextNotes) CollectModelText(items);
            if (_settings.ScanParameters) CollectParameters(items);
            if (_settings.ScanTags) CollectTags(items);
            if (_settings.ScanTags) CollectSpatialTags(items);
            if (_settings.ScanSheetsAndViews) CollectSheetsAndViews(items);
            if (_settings.ScanSheetsAndViews) CollectDatumNames(items);
            if (_settings.ScanSchedules) CollectSchedules(items);
            if (_settings.ScanKeynotes && _viewIds == null) CollectKeynotes(items);
            return items;
        }

        /// <summary>Text note and dimension types in use, for the "fill from current model" button in the settings.</summary>
        public List<TextItem> CollectTypesOnly()
        {
            var items = new List<TextItem>();
            StyledElements.Clear();
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
                    TypeId = note.GetTypeId().Value,
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
                AddStyledElement(note, TextSource.TextNote, item.Text, item.Location);
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
            foreach (Dimension dimension in AllDimensions())
            {
                Count(typeUsage, dimension.GetTypeId());
                string location = ViewName(dimension.OwnerViewId);
                AddStyledElement(dimension, TextSource.Dimension, DisplayValue(dimension), location);

                // A single dimension has no segments and carries the text itself. The count is taken from
                // the segment array, not NumberOfSegments, so an index can never run past the array.
                DimensionSegmentArray segmentArray = null;
                try { segmentArray = dimension.Segments; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                int segments = segmentArray?.Size ?? 0;

                if (segments > 0)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        DimensionSegment segment = segmentArray.get_Item(i);
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

        // Spot elevations, coordinates and slopes are Dimension subclasses. Asking for both classes and
        // removing repeats means they are read whether or not Revit returns subclasses for one class.
        private IEnumerable<Dimension> AllDimensions()
        {
            return Elements(c => c.OfClass(typeof(Dimension))).OfType<Dimension>()
                .Concat(Elements(c => c.OfClass(typeof(SpotDimension))).OfType<Dimension>())
                .GroupBy(d => d.Id.Value)
                .Select(g => g.First());
        }

        private void CollectModelText(List<TextItem> items)
        {
            foreach (ModelText modelText in Elements(c => c.OfClass(typeof(ModelText))).OfType<ModelText>())
            {
                string text = modelText.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                items.Add(new TextItem
                {
                    ElementId = modelText.Id.Value,
                    Source = TextSource.ModelText,
                    Text = text,
                    Location = "3D model",
                    Editable = true
                });
            }
        }

        // The names printed in grid bubbles and level heads.
        private void CollectDatumNames(List<TextItem> items)
        {
            foreach (Element datum in Elements(c => c.OfClass(typeof(Grid))).Concat(Elements(c => c.OfClass(typeof(Level)))))
            {
                string name = datum.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                bool isGrid = datum is Grid;
                items.Add(new TextItem
                {
                    ElementId = datum.Id.Value,
                    Source = isGrid ? TextSource.GridName : TextSource.LevelName,
                    Text = name,
                    Location = isGrid ? "Grids" : "Levels",
                    Editable = true
                });
            }
        }

        // Room, area and space tags are a different class from the other tags.
        private void CollectSpatialTags(List<TextItem> items)
        {
            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_RoomTags, BuiltInCategory.OST_AreaTags, BuiltInCategory.OST_MEPSpaceTags
            };
            foreach (SpatialElementTag tag in Elements(c => c.WherePasses(new ElementMulticategoryFilter(categories))
                         .WhereElementIsNotElementType()).OfType<SpatialElementTag>())
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
                    Editable = false
                });
            }
        }

        // ---- inventory: what kinds of text-bearing items the view holds ----------------------

        private static readonly HashSet<BuiltInCategory> ExtraTextCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_ModelText, BuiltInCategory.OST_TitleBlocks
        };

        /// <summary>
        /// Every kind of annotation in scope with a count and whether Text QC reads it. This is the
        /// honest answer to "what lettering is in this view", including what is not checked.
        /// </summary>
        public List<CategoryCount> Inventory()
        {
            var counts = new Dictionary<string, CategoryCount>(StringComparer.OrdinalIgnoreCase);
            foreach (Element element in Elements(c => c.WhereElementIsNotElementType()))
            {
                Category category = element.Category;
                if (category == null) continue;

                BuiltInCategory builtIn = category.Id.Value < 0 ? (BuiltInCategory)(int)category.Id.Value : BuiltInCategory.INVALID;
                bool mayHoldText = category.CategoryType == CategoryType.Annotation || ExtraTextCategories.Contains(builtIn);
                if (!mayHoldText || builtIn == BuiltInCategory.OST_Lines) continue;

                if (!counts.TryGetValue(category.Name, out CategoryCount entry))
                {
                    entry = new CategoryCount { Name = category.Name, Status = StatusFor(builtIn, category.Name) };
                    counts[category.Name] = entry;
                }
                entry.Count++;
            }

            return counts.Values
                .OrderBy(c => c.Status.StartsWith("Not", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(c => c.Count)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string StatusFor(BuiltInCategory category, string name)
        {
            string Switch(bool on, string how) => on ? how : "Off in settings";

            switch (category)
            {
                case BuiltInCategory.OST_TextNotes:
                case BuiltInCategory.OST_ModelText:
                    return Switch(_settings.ScanTextNotes, "Checked");

                case BuiltInCategory.OST_Dimensions:
                case BuiltInCategory.OST_SpotElevations:
                case BuiltInCategory.OST_SpotCoordinates:
                case BuiltInCategory.OST_SpotSlopes:
                    return Switch(_settings.ScanDimensions, "Checked");

                case BuiltInCategory.OST_Grids:
                case BuiltInCategory.OST_Levels:
                    return Switch(_settings.ScanSheetsAndViews, "Checked (names)");

                case BuiltInCategory.OST_GenericAnnotation:
                case BuiltInCategory.OST_RevisionClouds:
                    return Switch(_settings.ScanParameters, "Only the parameters listed above");

                case BuiltInCategory.OST_TitleBlocks:
                    return "Not checked (fields are sheet parameters)";
            }

            if (name.EndsWith("Tags", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Tag", StringComparison.OrdinalIgnoreCase))
                return Switch(_settings.ScanTags, "Checked");
            return "Not checked";
        }

        private static string DisplayValue(Dimension dimension)
        {
            try
            {
                string value = dimension.ValueString;
                return string.IsNullOrEmpty(value) ? "(dimension)" : value;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return "(dimension)"; // multi-segment and some spot dimensions have no single value
            }
        }

        private void AddStyledElement(Element element, TextSource kind, string text, string location)
        {
            ElementId typeId = element.GetTypeId();
            TextStyleInfo style = StyleOf(typeId);
            if (style == null) return;
            StyledElements.Add(new StyledElement
            {
                ElementId = element.Id.Value,
                Kind = kind,
                TypeId = typeId.Value,
                Text = text,
                Location = location,
                Style = style
            });
        }

        private TextStyleInfo StyleOf(ElementId typeId)
        {
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            if (_styles.TryGetValue(typeId, out TextStyleInfo cached)) return cached;

            TextStyleInfo style = null;
            if (_doc.GetElement(typeId) is ElementType type)
            {
                double sizeFeet = type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                style = new TextStyleInfo
                {
                    TypeName = type.Name,
                    Font = type.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString() ?? "",
                    SizeMm = Math.Round(UnitUtils.ConvertFromInternalUnits(sizeFeet, UnitTypeId.Millimeters), 3),
                    Bold = IsOn(type, BuiltInParameter.TEXT_STYLE_BOLD),
                    Italic = IsOn(type, BuiltInParameter.TEXT_STYLE_ITALIC),
                    Underline = IsOn(type, BuiltInParameter.TEXT_STYLE_UNDERLINE),
                    WidthFactor = type.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1
                };
            }
            _styles[typeId] = style;
            return style;
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
                TypeId = dimension.GetTypeId().Value,
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
            foreach (var pair in usage.OrderBy(p => StyleOf(p.Key)?.TypeName))
            {
                TextStyleInfo style = StyleOf(pair.Key);
                if (style == null) continue;

                items.Add(new TextItem
                {
                    ElementId = pair.Key.Value,
                    Source = source,
                    Text = style.TypeName,
                    Location = pair.Value == 1 ? "1 instance" : pair.Value + " instances",
                    UsageCount = pair.Value,
                    Style = style
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

            // long arithmetic: maxRows can be int.MaxValue, which overflowed and silently skipped the section.
            int lastRow = (int)Math.Min(data.LastRowNumber, (long)data.FirstRowNumber + maxRows - 1);
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
