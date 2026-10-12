using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitTextQC.Core
{
    public enum CaseMode
    {
        Any = 0,
        Upper = 1,
        Lower = 2,
        Title = 3
    }

    /// <summary>Approved fonts, sizes and type names for text notes or dimensions. An empty list means "anything goes".</summary>
    [DataContract]
    public class TextStandards
    {
        /// <summary>Approved type names. Supports * and ? wildcards.</summary>
        [DataMember] public List<string> AllowedTypeNames { get; set; }
        [DataMember] public List<string> AllowedFonts { get; set; }
        [DataMember] public List<double> AllowedSizesMm { get; set; }
        [DataMember] public bool AllowBold { get; set; }
        [DataMember] public bool AllowItalic { get; set; }
        [DataMember] public bool AllowUnderline { get; set; }
        /// <summary>Required width factor, or 0 for any.</summary>
        [DataMember] public double WidthFactor { get; set; }

        public TextStandards() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) { SetDefaults(); }

        private void SetDefaults()
        {
            AllowedTypeNames = new List<string>();
            AllowedFonts = new List<string>();
            AllowedSizesMm = new List<double>();
            AllowBold = true;
            AllowItalic = true;
            AllowUnderline = true;
            WidthFactor = 0;
        }
    }

    [DataContract]
    public class ForbiddenTerm
    {
        [DataMember] public string Term { get; set; }
        /// <summary>Preferred wording, or empty when the term should simply be removed by hand.</summary>
        [DataMember] public string Replacement { get; set; }
    }

    /// <summary>Everything the user can configure. Stored as JSON so an office standard can be shared between users.</summary>
    [DataContract]
    public class QcSettings
    {
        // What to scan
        [DataMember] public bool ScanTextNotes { get; set; }
        [DataMember] public bool ScanDimensions { get; set; }
        [DataMember] public bool ScanParameters { get; set; }
        [DataMember] public bool ScanTags { get; set; }
        [DataMember] public bool ScanSheetsAndViews { get; set; }
        [DataMember] public bool ScanSchedules { get; set; }
        [DataMember] public bool ScanScheduleBody { get; set; }
        [DataMember] public bool ScanKeynotes { get; set; }
        [DataMember] public bool KeynotesUsedOnly { get; set; }
        /// <summary>Instance text parameters to check on every element, by name.</summary>
        [DataMember] public List<string> ParameterNames { get; set; }

        // Spelling
        [DataMember] public bool CheckSpelling { get; set; }
        /// <summary>Dictionary file name without extension: en_US or en_GB.</summary>
        [DataMember] public string Language { get; set; }
        [DataMember] public int MinWordLength { get; set; }
        [DataMember] public List<string> CustomWords { get; set; }

        // Text type and size
        [DataMember] public bool CheckTextStyles { get; set; }
        [DataMember] public TextStandards TextNoteStandards { get; set; }
        [DataMember] public TextStandards DimensionStandards { get; set; }
        [DataMember] public bool FlagInlineFormatting { get; set; }

        // Case and formatting
        [DataMember] public bool CheckCase { get; set; }
        [DataMember] public CaseMode TextNoteCase { get; set; }
        [DataMember] public CaseMode DimensionCase { get; set; }
        [DataMember] public CaseMode SheetNameCase { get; set; }
        [DataMember] public CaseMode ViewNameCase { get; set; }
        [DataMember] public CaseMode ParameterCase { get; set; }
        [DataMember] public bool CheckWhitespace { get; set; }
        [DataMember] public bool CheckForbiddenTerms { get; set; }
        [DataMember] public List<ForbiddenTerm> ForbiddenTerms { get; set; }

        // Naming
        [DataMember] public bool CheckNaming { get; set; }
        [DataMember] public string SheetNumberPattern { get; set; }
        [DataMember] public string SheetNamePattern { get; set; }
        [DataMember] public string ViewNamePattern { get; set; }
        [DataMember] public bool FlagDefaultViewNames { get; set; }

        // Updates
        [DataMember] public bool AutoCheckUpdates { get; set; }
        [DataMember] public bool AutoInstallUpdates { get; set; }

        public QcSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) { SetDefaults(); }

        private void SetDefaults()
        {
            ScanTextNotes = true;
            ScanDimensions = true;
            ScanParameters = true;
            ScanTags = true;
            ScanSheetsAndViews = true;
            ScanSchedules = true;
            ScanScheduleBody = false;
            ScanKeynotes = true;
            KeynotesUsedOnly = true;
            ParameterNames = new List<string> { "Comments" };

            CheckSpelling = true;
            Language = "en_US";
            MinWordLength = 3;
            CustomWords = new List<string>();

            CheckTextStyles = true;
            TextNoteStandards = new TextStandards();
            DimensionStandards = new TextStandards();
            FlagInlineFormatting = true;

            CheckCase = true;
            TextNoteCase = CaseMode.Upper;
            DimensionCase = CaseMode.Any;
            SheetNameCase = CaseMode.Upper;
            ViewNameCase = CaseMode.Any;
            ParameterCase = CaseMode.Any;
            CheckWhitespace = true;
            CheckForbiddenTerms = true;
            ForbiddenTerms = new List<ForbiddenTerm>();

            CheckNaming = true;
            SheetNumberPattern = "";
            SheetNamePattern = "";
            ViewNamePattern = "";
            FlagDefaultViewNames = true;

            AutoCheckUpdates = true;
            AutoInstallUpdates = true;
        }

        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitTextQC");

        public static string DefaultPath => Path.Combine(DefaultFolder, "settings.json");

        /// <summary>Loads the user's settings, falling back to defaults when the file is missing or unreadable.</summary>
        public static QcSettings Load()
        {
            try
            {
                if (File.Exists(DefaultPath)) return LoadFrom(DefaultPath);
            }
            catch (Exception)
            {
                // A corrupt settings file must never stop the add-in from starting.
            }
            return new QcSettings();
        }

        public static QcSettings LoadFrom(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var settings = (QcSettings)new DataContractJsonSerializer(typeof(QcSettings)).ReadObject(stream);
                settings.Normalize();
                return settings;
            }
        }

        public void Save() { SaveTo(DefaultPath); }

        public void SaveTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
            {
                new DataContractJsonSerializer(typeof(QcSettings)).WriteObject(writer, this);
            }
        }

        // A hand-edited file can contain explicit nulls, which bypass the deserialization defaults.
        private void Normalize()
        {
            ParameterNames = ParameterNames ?? new List<string>();
            CustomWords = CustomWords ?? new List<string>();
            ForbiddenTerms = ForbiddenTerms ?? new List<ForbiddenTerm>();
            TextNoteStandards = TextNoteStandards ?? new TextStandards();
            DimensionStandards = DimensionStandards ?? new TextStandards();
            foreach (var s in new[] { TextNoteStandards, DimensionStandards })
            {
                s.AllowedTypeNames = s.AllowedTypeNames ?? new List<string>();
                s.AllowedFonts = s.AllowedFonts ?? new List<string>();
                s.AllowedSizesMm = s.AllowedSizesMm ?? new List<double>();
            }
            if (string.IsNullOrWhiteSpace(Language)) Language = "en_US";
            if (MinWordLength < 1) MinWordLength = 1;
            SheetNumberPattern = SheetNumberPattern ?? "";
            SheetNamePattern = SheetNamePattern ?? "";
            ViewNamePattern = ViewNamePattern ?? "";
        }
    }
}
