using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitTextQC.Core
{
    /// <summary>One piece of text found in a DXF drawing.</summary>
    public class DxfText
    {
        /// <summary>The words, with the file's formatting codes removed.</summary>
        public string Text { get; set; }
        public string Layer { get; set; }
        /// <summary>Insertion point, in the units the drawing was exported in.</summary>
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Reads the text (TEXT, MTEXT and ATTRIB entities) out of an ASCII DXF file. Revit exports a sheet to DXF
    /// with everything printed on it, including the fixed text typed inside the title block, which Revit's
    /// API does not expose any other way.
    /// </summary>
    public static class DxfTextReader
    {
        public static List<DxfText> Read(string path)
        {
            using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
            {
                return Read(reader);
            }
        }

        public static List<DxfText> Read(TextReader reader)
        {
            var found = new List<DxfText>();
            var raw = new StringBuilder();
            string layer = "";
            double x = 0, y = 0;
            bool haveX = false, haveY = false;
            bool inText = false;

            void Finish()
            {
                if (!inText) return;
                string text = Clean(raw.ToString());
                if (text.Length > 0)
                    found.Add(new DxfText { Text = text, Layer = layer, X = x, Y = y });
                inText = false;
            }

            string codeLine;
            while ((codeLine = reader.ReadLine()) != null)
            {
                string value = reader.ReadLine();
                if (value == null) break;

                string code = codeLine.Trim();
                if (code == "0")
                {
                    Finish();
                    string type = value.Trim();
                    inText = type == "MTEXT" || type == "TEXT" || type == "ATTRIB";
                    raw.Clear();
                    layer = "";
                    x = y = 0;
                    haveX = haveY = false;
                    continue;
                }
                if (!inText) continue;

                switch (code)
                {
                    // An MTEXT longer than 250 characters arrives in code 3 pieces followed by the last piece in code 1.
                    case "1":
                    case "3":
                        raw.Append(value);
                        break;
                    case "8":
                        layer = value.Trim();
                        break;
                    case "10":
                        if (!haveX && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double px)) { x = px; haveX = true; }
                        break;
                    case "20":
                        if (!haveY && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double py)) { y = py; haveY = true; }
                        break;
                }
            }
            Finish();
            return found;
        }

        // {\fArial|b0|i0;text}  \H2.5x;  \S1/2;  \P  \U+00E9  %%u  %%c
        private static readonly Regex FormatCode = new Regex(@"\\[A-Za-z][^;\\{}]*;", RegexOptions.Compiled);
        private static readonly Regex UnicodeEscape = new Regex(@"\\U\+([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        /// <summary>Turns a DXF string into the words a person would read.</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string text = UnicodeEscape.Replace(raw, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            text = text.Replace("\\P", "\n").Replace("\\~", " ").Replace("\\\\", "\u0001");
            text = FormatCode.Replace(text, "");
            text = text.Replace("{", "").Replace("}", "").Replace("\u0001", "\\");
            text = text.Replace("%%c", "\u00D8").Replace("%%d", "\u00B0").Replace("%%p", "\u00B1")
                       .Replace("%%u", "").Replace("%%o", "").Replace("%%%", "%");
            return text.Trim();
        }

        /// <summary>True for the layer Revit puts title blocks on (AIA and NCS naming use TTLB).</summary>
        public static bool IsTitleBlockLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            string upper = layer.ToUpperInvariant();
            return upper.Contains("TTLB") || upper.Contains("TITLEBLOCK") || upper.Contains("TITLE BLOCK") || upper.Contains("TITLE_BLOCK");
        }
    }
}
