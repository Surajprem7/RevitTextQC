using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTextQC.Core;

namespace RevitTextQC.Scanning
{
    /// <summary>
    /// Shows a finding on the sheet that is open, without leaving it. Revit cannot select an element that
    /// lives inside a placed view while a sheet is active, so the view's frame (its viewport) is selected
    /// and the sheet is zoomed to where the text is printed.
    /// </summary>
    internal static class SheetFocus
    {
        // About 90 mm each way on paper: a small note is shown with its surroundings, not filling the screen.
        private const double MinHalfFeet = 0.3;

        /// <summary>Handles findings whose text is printed on the open sheet. Returns null when it is not (the caller carries on).</summary>
        public static string ShowItem(UIDocument uidoc, ViewSheet sheet, TextItem item, Element element)
        {
            switch (item.Source)
            {
                case TextSource.TitleBlock:
                    // Another sheet's own field is not on this sheet.
                    if (element is ViewSheet other && other.Id != sheet.Id) return null;
                    return ShowTitleBlock(uidoc, sheet, "Field \"" + item.Field + "\" is printed in the title block.");

                case TextSource.Revision:
                    return ShowTitleBlock(uidoc, sheet, "The revision description is printed in the title block's revision table.");

                case TextSource.SheetName:
                case TextSource.SheetNumber:
                    return element.Id == sheet.Id
                        ? ShowTitleBlock(uidoc, sheet, "The sheet " + (item.Source == TextSource.SheetName ? "name" : "number") + " is printed in the title block.")
                        : null;

                case TextSource.ViewName:
                    return element is View view ? ShowViewport(uidoc, sheet, view.Id) : null;

                case TextSource.Schedule:
                    return ShowSchedule(uidoc, sheet, element.Id);

                default:
                    return null;
            }
        }

        /// <summary>Shows a text note, tag, family or dimension that is on the sheet or inside a view placed on it.</summary>
        public static string ShowElement(UIDocument uidoc, ViewSheet sheet, Element element)
        {
            Document doc = uidoc.Document;
            ElementId owner = element.OwnerViewId;

            // Printed on the sheet itself: text notes, the title block, schedules, revision clouds.
            if (owner == sheet.Id)
            {
                Select(uidoc, new[] { element.Id });
                if (!Zoom(uidoc, sheet, element.get_BoundingBox(sheet))) uidoc.ShowElements(element.Id);
                return "Highlighted on the sheet.";
            }

            if (owner == null || owner == ElementId.InvalidElementId) return null;

            // Inside a view placed on the sheet.
            Viewport viewport = FindViewport(doc, sheet, owner);
            if (viewport == null) return null;

            Select(uidoc, new[] { viewport.Id });
            BoundingBoxXYZ place = LocationOnSheet(element, doc.GetElement(owner) as View, viewport, out bool exact);
            Zoom(uidoc, sheet, place);
            return exact
                ? "Shown on the sheet, inside view \"" + ViewName(doc, owner) + "\"."
                : "Zoomed to view \"" + ViewName(doc, owner) + "\" on the sheet.";
        }

        // ---- pieces ------------------------------------------------------------------------

        // Revit does not say where a title block prints each field, so the title block is selected and the
        // message names the field; the whole title block is shown, which is often the whole sheet.
        private static string ShowTitleBlock(UIDocument uidoc, ViewSheet sheet, string what)
        {
            Element titleBlock = new FilteredElementCollector(uidoc.Document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
            if (titleBlock == null) return "This sheet has no title block. " + what;

            Select(uidoc, new[] { titleBlock.Id });
            if (!Zoom(uidoc, sheet, titleBlock.get_BoundingBox(sheet))) uidoc.ShowElements(titleBlock.Id);
            return "Title block selected. " + what;
        }

        private static string ShowViewport(UIDocument uidoc, ViewSheet sheet, ElementId viewId)
        {
            Viewport viewport = FindViewport(uidoc.Document, sheet, viewId);
            if (viewport == null) return null;

            Select(uidoc, new[] { viewport.Id });
            Zoom(uidoc, sheet, Frame(viewport.GetBoxOutline()));
            return "View shown on the sheet.";
        }

        private static string ShowSchedule(UIDocument uidoc, ViewSheet sheet, ElementId scheduleId)
        {
            ScheduleSheetInstance instance = new FilteredElementCollector(uidoc.Document, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                .FirstOrDefault(s => s.ScheduleId == scheduleId);
            if (instance == null) return null;

            Select(uidoc, new[] { instance.Id });
            if (!Zoom(uidoc, sheet, instance.get_BoundingBox(sheet))) uidoc.ShowElements(instance.Id);
            return "Schedule shown on the sheet.";
        }

        private static Viewport FindViewport(Document doc, ViewSheet sheet, ElementId viewId)
        {
            foreach (ElementId id in sheet.GetAllViewports())
                if (doc.GetElement(id) is Viewport viewport && viewport.ViewId == viewId) return viewport;
            return null;
        }

        private static string ViewName(Document doc, ElementId id)
        {
            return (doc.GetElement(id) as View)?.Name ?? "";
        }

        /// <summary>
        /// Where an element inside a placed view is printed on the sheet. A view's contents are drawn at the
        /// view scale around the centre of the viewport, so a point is mapped from the view's crop box to the
        /// viewport. Falls back to the whole viewport when the element has no extent, the view has no active
        /// crop, or the result does not land inside the viewport.
        /// </summary>
        private static BoundingBoxXYZ LocationOnSheet(Element element, View view, Viewport viewport, out bool exact)
        {
            Outline outline = viewport.GetBoxOutline();
            exact = false;
            try
            {
                if (view == null || !view.CropBoxActive || view.Scale <= 0) return Frame(outline);
                BoundingBoxXYZ box = element.get_BoundingBox(view);
                if (box == null) return Frame(outline);

                BoundingBoxXYZ crop = view.CropBox;
                Transform toView = crop.Transform.Inverse;
                double cropX = (crop.Min.X + crop.Max.X) / 2, cropY = (crop.Min.Y + crop.Max.Y) / 2;
                XYZ centre = viewport.GetBoxCenter();
                double scale = view.Scale;

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                        foreach (double z in new[] { box.Min.Z, box.Max.Z })
                        {
                            XYZ local = toView.OfPoint(new XYZ(x, y, z));
                            double sheetX = centre.X + (local.X - cropX) / scale;
                            double sheetY = centre.Y + (local.Y - cropY) / scale;
                            minX = Math.Min(minX, sheetX);
                            minY = Math.Min(minY, sheetY);
                            maxX = Math.Max(maxX, sheetX);
                            maxY = Math.Max(maxY, sheetY);
                        }

                var placed = new BoundingBoxXYZ { Min = new XYZ(minX, minY, 0), Max = new XYZ(maxX, maxY, 0) };
                if (!CentreInside(outline, placed)) return Frame(outline);

                exact = true;
                return placed;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return Frame(outline);
            }
        }

        private static BoundingBoxXYZ Frame(Outline outline)
        {
            return new BoundingBoxXYZ
            {
                Min = new XYZ(outline.MinimumPoint.X, outline.MinimumPoint.Y, 0),
                Max = new XYZ(outline.MaximumPoint.X, outline.MaximumPoint.Y, 0)
            };
        }

        // Text near the edge of its view can sit a little outside the frame, so allow some slack.
        private static bool CentreInside(Outline outline, BoundingBoxXYZ box)
        {
            double x = (box.Min.X + box.Max.X) / 2, y = (box.Min.Y + box.Max.Y) / 2;
            double slack = 0.1 * Math.Max(outline.MaximumPoint.X - outline.MinimumPoint.X, outline.MaximumPoint.Y - outline.MinimumPoint.Y);
            return x >= outline.MinimumPoint.X - slack && x <= outline.MaximumPoint.X + slack
                   && y >= outline.MinimumPoint.Y - slack && y <= outline.MaximumPoint.Y + slack;
        }

        private static bool Zoom(UIDocument uidoc, View view, BoundingBoxXYZ box)
        {
            if (box == null) return false;
            UIView uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            if (uiView == null) return false;

            double centreX = (box.Min.X + box.Max.X) / 2;
            double centreY = (box.Min.Y + box.Max.Y) / 2;
            double half = Math.Max(Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) * 0.6, MinHalfFeet);
            uiView.ZoomAndCenterRectangle(new XYZ(centreX - half, centreY - half, 0), new XYZ(centreX + half, centreY + half, 0));
            return true;
        }

        /// <summary>
        /// Selects what can be selected in the open view. An element inside a placed view cannot be selected
        /// from the sheet, so its viewport is selected instead. Returns how many elements were left out.
        /// </summary>
        public static int Select(UIDocument uidoc, IEnumerable<ElementId> ids)
        {
            Document doc = uidoc.Document;
            View active = uidoc.ActiveView;
            var selectable = new List<ElementId>();
            int total = 0, skipped = 0;

            foreach (ElementId id in ids)
            {
                total++;
                Element element = doc.GetElement(id);
                if (element == null) { skipped++; continue; }

                ElementId owner = element.OwnerViewId;
                bool noView = owner == null || owner == ElementId.InvalidElementId;
                if (noView || owner == active.Id)
                {
                    selectable.Add(id);
                }
                else if (active is ViewSheet sheet && FindViewport(doc, sheet, owner) is Viewport viewport)
                {
                    if (!selectable.Contains(viewport.Id)) selectable.Add(viewport.Id);
                }
                else
                {
                    skipped++;
                }
            }

            try
            {
                uidoc.Selection.SetElementIds(selectable);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Revit refuses a selection it cannot show; leave the current selection alone.
                return total;
            }
            return skipped;
        }
    }
}
