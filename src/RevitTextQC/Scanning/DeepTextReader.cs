using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitTextQC.Scanning
{
    /// <summary>
    /// Collects the text Revit draws in a view, whichever element draws it: title blocks, annotation
    /// families with labels, keynote symbols, viewport titles. Revit's custom exporter replays a view's
    /// graphics and reports every string with the element it belongs to. Nothing is changed in the model.
    /// </summary>
    internal class DeepTextReader : IExportContext2D
    {
        public class Hit
        {
            public ElementId ElementId { get; set; }
            public ElementId ViewId { get; set; }
            public string Text { get; set; }
        }

        public List<Hit> Hits { get; } = new List<Hit>();

        private ElementId _view = ElementId.InvalidElementId;
        private readonly Stack<ElementId> _elements = new Stack<ElementId>();

        private ElementId Current => _elements.Count > 0 ? _elements.Peek() : ElementId.InvalidElementId;

        // ---- what we want ------------------------------------------------------------------

        public void OnText(TextNode node)
        {
            string text = node?.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            Hits.Add(new Hit { ElementId = Current, ViewId = _view, Text = text });
        }

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            _view = node.ViewId;
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId) { }

        // Whether Revit reports 2D elements through the 2D or the plain callbacks, the stack stays balanced.
        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            _elements.Push(elementId);
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (_elements.Count > 0) _elements.Pop();
        }

        public RenderNodeAction OnElementBegin2D(ElementNode node)
        {
            _elements.Push(node.ElementId);
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd2D(ElementNode node)
        {
            if (_elements.Count > 0) _elements.Pop();
        }

        // Linked models never show their text in the host's views.
        public RenderNodeAction OnLinkBegin(LinkNode node) => RenderNodeAction.Skip;
        public void OnLinkEnd(LinkNode node) { }

        // ---- everything else is not needed -------------------------------------------------

        public bool Start() => true;
        public void Finish() { }
        public bool IsCanceled() => false;

        public RenderNodeAction OnInstanceBegin(InstanceNode node) => RenderNodeAction.Proceed;
        public void OnInstanceEnd(InstanceNode node) { }
        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Skip;
        public void OnFaceEnd(FaceNode node) { }
        public RenderNodeAction OnFaceEdge2D(FaceEdgeNode node) => RenderNodeAction.Skip;
        public RenderNodeAction OnFaceSilhouette2D(FaceSilhouetteNode node) => RenderNodeAction.Skip;
        public RenderNodeAction OnCurve(CurveNode node) => RenderNodeAction.Skip;
        public RenderNodeAction OnPolyline(PolylineNode node) => RenderNodeAction.Skip;
        public void OnLineSegment(LineSegment segment) { }
        public void OnPolylineSegments(PolylineSegments segments) { }
        public void OnRPC(RPCNode node) { }
        public void OnLight(LightNode node) { }
        public void OnMaterial(MaterialNode node) { }
        public void OnPolymesh(PolymeshTopology node) { }
    }
}
