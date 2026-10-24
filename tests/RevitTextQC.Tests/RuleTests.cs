using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitTextQC.Core;
using RevitTextQC.Core.Rules;
using Xunit;

namespace RevitTextQC.Tests
{
    internal class FakeSpellChecker : ISpellChecker
    {
        private readonly HashSet<string> _words;
        private readonly Dictionary<string, string> _suggestions;

        public FakeSpellChecker(IEnumerable<string> words, Dictionary<string, string> suggestions = null)
        {
            _words = new HashSet<string>(words, StringComparer.Ordinal);
            _suggestions = suggestions ?? new Dictionary<string, string>();
        }

        public bool Check(string word) => _words.Contains(word);

        public IList<string> Suggest(string word) =>
            _suggestions.TryGetValue(word, out var s) ? new List<string> { s } : new List<string>();
    }

    public class SpellingRuleTests
    {
        private static SpellingRule Rule(params string[] custom) => new SpellingRule(
            new FakeSpellChecker(
                new[] { "see", "detail", "concrete", "wall", "the", "London", "don't" },
                new Dictionary<string, string> { { "concret", "concrete" }, { "wal", "wall" } }),
            custom, 3);

        private static TextItem Note(string text) =>
            new TextItem { Source = TextSource.TextNote, Text = text, Editable = true };

        [Fact]
        public void Uppercase_drawing_text_is_checked_against_lowercase_dictionary()
        {
            Assert.Empty(Rule().Check(Note("SEE DETAIL")));
            Assert.Empty(Rule().Check(Note("LONDON")));
        }

        [Fact]
        public void Misspelling_is_reported_with_case_matched_fix()
        {
            var issue = Assert.Single(Rule().Check(Note("SEE CONCRET WAL, CONCRET")), i => i.Word == "CONCRET");
            Assert.Equal("SEE CONCRETE WAL, CONCRETE", issue.Suggestion);
            Assert.True(issue.CanFix);
        }

        [Fact]
        public void Codes_units_and_mixed_case_names_are_ignored()
        {
            Assert.Empty(Rule().Check(Note("M12 100mm 2x4 uPVC L1 RC")));
        }

        [Fact]
        public void Custom_words_are_accepted_ignoring_case()
        {
            Assert.Empty(Rule("soffit").Check(Note("SOFFIT Soffit")));
            Assert.Single(Rule().Check(Note("SOFFIT Soffit")).Select(i => i.Word.ToLowerInvariant()).Distinct());
        }

        [Fact]
        public void Apostrophes_stay_inside_words()
        {
            Assert.Empty(Rule().Check(Note("don't 'see' the wall")));
        }

        [Fact]
        public void Sheet_numbers_and_types_are_not_spell_checked()
        {
            Assert.Empty(Rule().Check(new TextItem { Source = TextSource.SheetNumber, Text = "XYZZY" }));
            Assert.Empty(Rule().Check(new TextItem { Source = TextSource.TextNoteType, Text = "XYZZY" }));
        }
    }

    public class TextStyleRuleTests
    {
        private static TextItem Type(string name, string font, double size, bool bold = false) => new TextItem
        {
            Source = TextSource.TextNoteType,
            Text = name,
            UsageCount = 3,
            Style = new TextStyleInfo { TypeName = name, Font = font, SizeMm = size, Bold = bold, WidthFactor = 1 }
        };

        [Fact]
        public void Empty_standards_accept_everything()
        {
            Assert.Empty(new TextStyleRule(new QcSettings()).Check(Type("Anything", "Comic Sans MS", 7.3, true)));
        }

        [Fact]
        public void Unapproved_font_and_size_are_reported_separately()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowedFonts.Add("Arial");
            settings.TextNoteStandards.AllowedSizesMm.AddRange(new[] { 2.5, 3.5 });
            var rule = new TextStyleRule(settings);

            Assert.Empty(rule.Check(Type("2.5mm Arial", "arial", 2.5)));
            var issues = rule.Check(Type("3mm Romans", "Romans", 3.0)).ToList();
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("Font \"Romans\""));
            Assert.Contains(issues, i => i.Message.Contains("Text size 3 mm"));
        }

        [Fact]
        public void Type_names_support_wildcards_and_short_circuit_other_checks()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowedTypeNames.Add("STD_*");
            settings.TextNoteStandards.AllowedFonts.Add("Arial");
            var rule = new TextStyleRule(settings);

            Assert.Empty(rule.Check(Type("STD_2.5mm", "Arial", 2.5)));
            var issue = Assert.Single(rule.Check(Type("Text 1", "Romans", 2.5)));
            Assert.Contains("not an approved type", issue.Message);
        }

        [Fact]
        public void Dimension_types_use_their_own_standards()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowedFonts.Add("Arial");
            var dim = Type("Linear", "Romans", 2.5);
            dim.Source = TextSource.DimensionType;
            Assert.Empty(new TextStyleRule(settings).Check(dim));
        }

        [Fact]
        public void Bold_can_be_disallowed_and_inline_formatting_flagged()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowBold = false;
            var rule = new TextStyleRule(settings);
            Assert.Single(rule.Check(Type("Bold", "Arial", 2.5, bold: true)));

            var note = new TextItem { Source = TextSource.TextNote, Text = "x", HasInlineFormatting = true };
            Assert.Single(rule.Check(note));
        }
    }

    public class FormattingRuleTests
    {
        [Fact]
        public void Uppercase_rule_suggests_capitals_unless_all_caps_format_is_on()
        {
            var rule = new CaseRule(new QcSettings());
            var note = new TextItem { Source = TextSource.TextNote, Text = "See Detail 5", Editable = true };
            Assert.Equal("SEE DETAIL 5", Assert.Single(rule.Check(note)).Suggestion);

            note.DisplayedAllCaps = true;
            Assert.Empty(rule.Check(note));
        }

        [Fact]
        public void Title_case_keeps_acronyms_and_small_words()
        {
            Assert.Equal("Plan of the HVAC Room", CaseRule.Convert("plan of the HVAC room", CaseMode.Title));
        }

        [Fact]
        public void Whitespace_problems_are_combined_into_one_fix()
        {
            var note = new TextItem { Source = TextSource.TextNote, Text = " SEE  DETAIL , TYP. \rNEXT LINE", Editable = true };
            var issue = Assert.Single(new WhitespaceRule().Check(note));
            Assert.Equal("SEE DETAIL, TYP.\rNEXT LINE", issue.Suggestion);
        }

        [Fact]
        public void Decimals_are_not_treated_as_punctuation()
        {
            var note = new TextItem { Source = TextSource.TextNote, Text = "OFFSET .5 MIN" };
            Assert.Empty(new WhitespaceRule().Check(note));
        }

        [Fact]
        public void Forbidden_term_matches_whole_words_and_keeps_case()
        {
            var rule = new ForbiddenTermsRule(new[] { new ForbiddenTerm { Term = "approx", Replacement = "approximately" } });
            var hit = new TextItem { Source = TextSource.TextNote, Text = "APPROX 5m", Editable = true };
            Assert.Equal("APPROXIMATELY 5m", Assert.Single(rule.Check(hit)).Suggestion);
            Assert.Empty(rule.Check(new TextItem { Source = TextSource.TextNote, Text = "APPROXIMATE" }));
        }
    }

    public class AuditRegressionTests
    {
        private class SlowSpellChecker : ISpellChecker
        {
            public int SuggestCalls;
            public bool Check(string word) => false;
            public IList<string> Suggest(string word)
            {
                SuggestCalls++;
                return new List<string> { "fixed" };
            }
        }

        [Fact]
        public void Uppercase_rule_leaves_units_and_codes_alone()
        {
            var rule = new CaseRule(new QcSettings());
            var ok = new TextItem { Source = TextSource.TextNote, Text = "100mm THK SLAB, 25 kN LOAD AT 150 c/c, M12 BOLTS, 20 m²", Editable = true };
            Assert.Empty(rule.Check(ok));

            var mixed = new TextItem { Source = TextSource.TextNote, Text = "Slab 100mm thk, 25 kN", Editable = true };
            Assert.Equal("SLAB 100mm THK, 25 kN", Assert.Single(rule.Check(mixed)).Suggestion);
        }

        [Fact]
        public void Suggestions_stop_when_the_time_budget_is_spent_and_resolve_on_demand()
        {
            var spell = new SlowSpellChecker();
            var rule = new SpellingRule(spell, null, 3) { SuggestBudget = TimeSpan.Zero };
            var item = new TextItem { Source = TextSource.TextNote, Text = "abcd efgh", Editable = true };

            List<QcIssue> issues = rule.Check(item).ToList();
            Assert.Equal(2, issues.Count);
            Assert.Equal(0, spell.SuggestCalls);
            Assert.All(issues, i => Assert.True(i.SuggestionsPending));
            Assert.All(issues, i => Assert.False(i.CanFix));

            rule.ResolveSuggestions(issues[0]);
            Assert.False(issues[0].SuggestionsPending);
            Assert.Equal("fixed efgh", issues[0].Suggestion);
            Assert.Equal(1, spell.SuggestCalls);
        }

        [Fact]
        public void Bundled_trade_terms_are_accepted_alongside_custom_words()
        {
            var settings = new QcSettings { CheckCase = false, CheckWhitespace = false };
            settings.CustomWords.Add("soffit");
            var engine = new QcEngine(settings, new SlowSpellChecker(), new[] { "FFL", "dpc" });
            var item = new TextItem { Source = TextSource.TextNote, Text = "FFL DPC SOFFIT", Editable = true };
            Assert.Empty(engine.Run(new[] { item }));
        }

        private class ThrowingSpellChecker : ISpellChecker
        {
            public bool Check(string word) => throw new InvalidOperationException("boom");
            public IList<string> Suggest(string word) => new List<string>();
        }

        [Fact]
        public void A_failing_rule_is_reported_once_and_does_not_stop_the_other_rules()
        {
            var engine = new QcEngine(new QcSettings(), new ThrowingSpellChecker());
            var items = new[]
            {
                new TextItem { Source = TextSource.TextNote, Text = "lower case", Editable = true },
                new TextItem { Source = TextSource.TextNote, Text = "more lower", Editable = true }
            };

            List<QcIssue> issues = engine.Run(items);
            Assert.Equal(2, issues.Count(i => i.RuleId == CaseRule.RuleId));
            Assert.Single(engine.ConfigurationWarnings);
        }

        [Fact]
        public void Types_without_a_font_or_size_are_not_reported_against_those_lists()
        {
            var settings = new QcSettings();
            settings.DimensionStandards.AllowedFonts.Add("Arial");
            settings.DimensionStandards.AllowedSizesMm.Add(2.5);
            var spot = new TextItem
            {
                Source = TextSource.DimensionType, Text = "Spot", UsageCount = 1,
                Style = new TextStyleInfo { TypeName = "Spot", Font = "", SizeMm = 0, WidthFactor = 1 }
            };
            Assert.Empty(new TextStyleRule(settings).Check(spot));
        }

        [Fact]
        public void Settings_file_cannot_point_the_dictionary_outside_its_folder_or_smuggle_nulls()
        {
            string path = Path.Combine(Path.GetTempPath(), "RevitTextQC.Tests", Guid.NewGuid() + ".json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"Language\":\"..\\\\..\\\\evil\",\"TextNoteStandards\":{\"AllowedFonts\":[null,\" Arial \"]}}");
                var loaded = QcSettings.LoadFrom(path);
                Assert.Equal("en_US", loaded.Language);
                Assert.Equal("Arial", Assert.Single(loaded.TextNoteStandards.AllowedFonts));
                Assert.NotEmpty(loaded.CaseExceptions);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    public class CaseMessageTests
    {
        [Fact]
        public void Dimensions_written_with_a_lowercase_x_are_not_a_case_finding()
        {
            var rule = new CaseRule(new QcSettings());
            var note = new TextItem
            {
                Source = TextSource.TextNote, Editable = true,
                Text = "1 OFF 600x600x100 M5 PANEL FILTERS TO BE MOUNTED IN SLIDE. C/W HINGED ACCESS PANEL"
            };
            Assert.Empty(rule.Check(note));

            var spaced = new TextItem { Source = TextSource.TextNote, Editable = true, Text = "600x300 x 125 FILTER SLIDE" };
            Assert.Empty(rule.Check(spaced));
        }

        [Fact]
        public void Message_names_the_words_that_need_changing()
        {
            var rule = new CaseRule(new QcSettings());
            var note = new TextItem { Source = TextSource.TextNote, Editable = true, Text = "PROVIDE concrete SLAB, 600x600 base" };
            string message = Assert.Single(rule.Check(note)).Message;
            Assert.Contains("\"concrete\"", message);
            Assert.DoesNotContain("600x600", message);

            var many = new TextItem { Source = TextSource.TextNote, Editable = true, Text = "one two three four five" };
            Assert.Contains("and 2 more", Assert.Single(rule.Check(many)).Message);
        }

        [Fact]
        public void Spacing_message_says_where_the_space_is_and_where_a_tag_gets_it_from()
        {
            var rule = new WhitespaceRule();

            var tag = new TextItem { Source = TextSource.Tag, Text = "FCU-00-22 " };
            string message = Assert.Single(rule.Check(tag)).Message;
            Assert.Contains("1 space after the last character", message);
            Assert.Contains("tag", message, StringComparison.OrdinalIgnoreCase);

            var lead = new TextItem { Source = TextSource.TextNote, Text = "  NOTE", Editable = true };
            Assert.Contains("2 spaces before the first character", Assert.Single(rule.Check(lead)).Message);
        }
    }

    public class DxfTextTests
    {
        private static readonly string Sample = string.Join("\n", new[]
        {
            "0", "SECTION", "2", "ENTITIES",
            "0", "MTEXT", "8", "G-____-TTLB-OTLN", "10", "2.497", "20", "1.480", "1", "{\\fArial|b0|i0|c0|p34;Consutant}",
            "0", "MTEXT", "8", "A-____-NOTE-TEXT", "10", "1.0", "20", "2.0", "3", "Part one ", "1", "and part two\\Pnew line",
            "0", "TEXT", "8", "G-ANNO-TTLB", "10", "0.5", "20", "0.25", "1", "Caf\\U+00E9 %%d",
            "0", "LINE", "8", "X", "10", "5", "20", "6",
            "0", "ENDSEC", "0", "EOF", ""
        });

        [Fact]
        public void Reads_text_entities_with_layer_and_position_and_ignores_other_entities()
        {
            List<DxfText> found = DxfTextReader.Read(new StringReader(Sample));
            Assert.Equal(3, found.Count);

            Assert.Equal("Consutant", found[0].Text);
            Assert.Equal("G-____-TTLB-OTLN", found[0].Layer);
            Assert.Equal(2.497, found[0].X, 3);
            Assert.Equal(1.480, found[0].Y, 3);
        }

        [Fact]
        public void Joins_long_mtext_pieces_and_removes_formatting_codes()
        {
            List<DxfText> found = DxfTextReader.Read(new StringReader(Sample));
            Assert.Equal("Part one and part two\nnew line", found[1].Text);
            Assert.Equal("Café °", found[2].Text);
        }

        [Theory]
        [InlineData("G-____-TTLB-OTLN", true)]
        [InlineData("G-ANNO-TTLB", true)]
        [InlineData("Title Block", true)]
        [InlineData("A-____-NOTE-TEXT", false)]
        [InlineData("", false)]
        public void Recognises_the_title_block_layer(string layer, bool expected)
        {
            Assert.Equal(expected, DxfTextReader.IsTitleBlockLayer(layer));
        }
    }

    public class SuggestionTests
    {
        private static QcIssue Issue(string text, FakeSpellChecker spell) =>
            Assert.Single(new SpellingRule(spell, null, 3).Check(new TextItem { Source = TextSource.TextNote, Text = text, Editable = true }));

        [Fact]
        public void A_word_one_slip_away_is_suggested_first_even_when_the_dictionary_misses_it()
        {
            // The dictionary offers "Hon", which is not what anyone meant by "Phon".
            var spell = new FakeSpellChecker(new[] { "Phone", "Hon" }, new Dictionary<string, string> { { "Phon", "Hon" } });
            QcIssue issue = Issue("Phon", spell);
            Assert.Equal("Phone", issue.Alternatives[0]);
            Assert.Equal("Phone / Hon", issue.Replacement);
        }

        [Fact]
        public void Swapped_letters_and_all_capitals_are_handled()
        {
            var spell = new FakeSpellChecker(new[] { "foundation" });
            Assert.Equal("FOUNDATION", Issue("FOUNDATOIN", spell).Alternatives[0]);
        }

        [Fact]
        public void Words_that_are_not_close_to_anything_get_only_the_dictionarys_suggestions()
        {
            var spell = new FakeSpellChecker(new[] { "Hon" }, new Dictionary<string, string> { { "Xqzv", "Hon" } });
            Assert.Equal(new[] { "Hon" }, Issue("Xqzv", spell).Alternatives);
        }
    }

    public class LinkTests
    {
        private static SpellingRule Rule() =>
            new SpellingRule(new FakeSpellChecker(new[] { "see", "and", "or", "steel", "email", "Email" }), null, 3);

        private static TextItem Note(string text) => new TextItem { Source = TextSource.TextNote, Text = text, Editable = true };

        [Theory]
        [InlineData("www.autodesk.com/revit")]
        [InlineData("https://example.org/some/path")]
        [InlineData("info@firm.com")]
        [InlineData("C:\\Projects\\Snowdon\\model")]
        public void Web_addresses_emails_and_paths_are_not_spell_checked(string text)
        {
            Assert.Empty(Rule().Check(Note(text)));
        }

        [Fact]
        public void Words_joined_by_a_slash_and_words_next_to_a_link_are_still_checked()
        {
            Assert.Equal("concret", Assert.Single(Rule().Check(Note("steel/concret"))).Word);

            // Only the real misspelling is reported, not the pieces of the web address beside it.
            List<string> words = Rule().Check(Note("see www.firm.com and wal")).Select(i => i.Word).ToList();
            Assert.Equal(new[] { "wal" }, words);
        }
    }

    public class AccentTests
    {
        [Fact]
        public void An_accented_word_is_accepted_when_its_plain_spelling_is_a_word()
        {
            var rule = new SpellingRule(new FakeSpellChecker(new[] { "cafe", "Cafe" }), null, 3);
            Assert.Empty(rule.Check(new TextItem { Source = TextSource.TextNote, Text = "Café CAFÉ café" }));
            Assert.Single(rule.Check(new TextItem { Source = TextSource.TextNote, Text = "Caffé" }));
        }
    }

    public class ResultColumnTests
    {
        private static TextItem Note(string text) => new TextItem { Source = TextSource.TextNote, Text = text, Editable = true };

        [Fact]
        public void Spelling_problem_is_the_word_and_the_suggestion_is_its_replacement()
        {
            var rule = new SpellingRule(
                new FakeSpellChecker(new[] { "see", "concrete" }, new Dictionary<string, string> { { "concret", "concrete" } }), null, 3);
            QcIssue issue = Assert.Single(rule.Check(Note("SEE CONCRET")));
            Assert.Equal("CONCRET", issue.Problem);
            Assert.Equal("CONCRETE", issue.Replacement);
        }

        [Fact]
        public void Case_problem_lists_the_words_and_the_suggestion_their_new_form()
        {
            var rule = new CaseRule(new QcSettings());
            QcIssue issue = Assert.Single(rule.Check(Note("PROVIDE concrete SLAB, base 600x600")));
            Assert.Equal("concrete, base", issue.Problem);
            Assert.Equal("CONCRETE, BASE", issue.Replacement);
        }

        [Fact]
        public void Spacing_problem_says_what_and_where()
        {
            QcIssue issue = Assert.Single(new WhitespaceRule().Check(Note("NOTE  ONE ")));
            Assert.Contains("double spaces", issue.Problem);
            Assert.Contains("1 space after the last character", issue.Problem);
        }

        [Fact]
        public void Style_problem_names_the_font_and_the_suggestion_the_approved_ones()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowedFonts.Add("Arial");
            var type = new TextItem
            {
                Source = TextSource.TextNoteType, Text = "Text 1", UsageCount = 2,
                Style = new TextStyleInfo { TypeName = "Text 1", Font = "Romans", SizeMm = 2.5, WidthFactor = 1 }
            };
            QcIssue issue = Assert.Single(new TextStyleRule(settings).Check(type));
            Assert.Equal("Font Romans", issue.Problem);
            Assert.Equal("Arial", issue.Replacement);
        }

        [Fact]
        public void Names_in_title_block_fields_are_not_spell_checked_but_other_fields_are()
        {
            var settings = new QcSettings { CheckCase = false, CheckWhitespace = false };
            var engine = new QcEngine(settings, new FakeSpellChecker(new[] { "Project", "Address" }));

            var person = new TextItem { Source = TextSource.TitleBlock, Text = "Suraj Prem", Field = "Drawn By", SkipSpelling = true };
            var address = new TextItem { Source = TextSource.TitleBlock, Text = "Projct Address", Field = "Project Address" };
            List<QcIssue> issues = engine.Run(new[] { person, address });

            QcIssue only = Assert.Single(issues);
            Assert.Equal("Projct", only.Problem);
        }

        [Fact]
        public void Title_block_and_revision_text_are_ordinary_text_for_spacing_and_forbidden_terms()
        {
            var settings = new QcSettings { CheckSpelling = false, CheckCase = false };
            settings.ForbiddenTerms.Add(new ForbiddenTerm { Term = "TBC", Replacement = "to be confirmed" });
            var engine = new QcEngine(settings, null);

            var revision = new TextItem { Source = TextSource.Revision, Text = "ISSUED  FOR TBC", Field = "Description", Editable = true };
            Assert.Equal(2, engine.Run(new[] { revision }).Count);
        }
    }

    public class TextFinderTests
    {
        private static StyledElement El(long id, TextSource kind, string type, string font, double size, bool bold = false) =>
            new StyledElement
            {
                ElementId = id, Kind = kind, Text = "t" + id,
                Style = new TextStyleInfo { TypeName = type, Font = font, SizeMm = size, Bold = bold, WidthFactor = 1 }
            };

        private static readonly List<StyledElement> Model = new List<StyledElement>
        {
            El(1, TextSource.TextNote, "2.5mm Arial", "Arial", 2.5),
            El(2, TextSource.TextNote, "2.5mm Arial", "Arial", 2.5),
            El(3, TextSource.TextNote, "3.5mm Romans", "Romans", 3.5, bold: true),
            El(4, TextSource.Dimension, "Linear 2.5", "Arial", 2.5),
            El(5, TextSource.Dimension, "Linear 10", "Arial", 10)
        };

        [Fact]
        public void Groups_by_size_in_numeric_order_across_text_and_dimensions()
        {
            List<FindGroup> groups = TextFinder.Group(Model, FindBy.Size, FindIn.All, new QcSettings());
            Assert.Equal(new[] { "2.5 mm", "3.5 mm", "10 mm" }, groups.Select(g => g.Label));
            Assert.Equal(new[] { 3, 1, 1 }, groups.Select(g => g.Count));
            Assert.All(groups, g => Assert.Null(g.Approved));
        }

        [Fact]
        public void Scope_limits_to_text_notes_or_dimensions()
        {
            Assert.Equal(2, TextFinder.Group(Model, FindBy.TextType, FindIn.TextNotes, new QcSettings()).Count);
            Assert.Equal(2, TextFinder.Group(Model, FindBy.TextType, FindIn.Dimensions, new QcSettings()).Count);
            Assert.Equal(4, TextFinder.Group(Model, FindBy.TextType, FindIn.All, new QcSettings()).Count);
        }

        [Fact]
        public void Approval_follows_the_standards_for_each_kind()
        {
            var settings = new QcSettings();
            settings.TextNoteStandards.AllowedFonts.Add("Arial");
            List<FindGroup> fonts = TextFinder.Group(Model, FindBy.Font, FindIn.All, settings);
            Assert.True(fonts.Single(g => g.Label == "Arial").Approved);
            Assert.False(fonts.Single(g => g.Label == "Romans").Approved);

            settings.TextNoteStandards.AllowBold = false;
            List<FindGroup> styles = TextFinder.Group(Model, FindBy.Style, FindIn.TextNotes, settings);
            Assert.True(styles.Single(g => g.Label == "Regular").Approved);
            Assert.False(styles.Single(g => g.Label == "Bold").Approved);
        }
    }

    public class NamingRuleTests
    {
        [Fact]
        public void Sheet_number_must_match_the_whole_pattern()
        {
            var rule = new NamingRule(new QcSettings { SheetNumberPattern = @"[A-Z]-\d{3}" });
            Assert.Empty(rule.Check(new TextItem { Source = TextSource.SheetNumber, Text = "A-101" }));
            Assert.Single(rule.Check(new TextItem { Source = TextSource.SheetNumber, Text = "A-1010" }));
        }

        [Fact]
        public void Default_view_names_are_flagged()
        {
            var rule = new NamingRule(new QcSettings());
            Assert.Single(rule.Check(new TextItem { Source = TextSource.ViewName, Text = "Level 1 Copy 1" }));
            Assert.Single(rule.Check(new TextItem { Source = TextSource.ViewName, Text = "Section 12" }));
            Assert.Empty(rule.Check(new TextItem { Source = TextSource.ViewName, Text = "Building Section A" }));
        }

        [Fact]
        public void Invalid_pattern_is_reported_not_thrown()
        {
            var engine = new QcEngine(new QcSettings { SheetNamePattern = "([A-Z" , CheckSpelling = false }, null);
            Assert.Single(engine.ConfigurationWarnings);
        }
    }

    public class InfrastructureTests
    {
        [Fact]
        public void Settings_round_trip_through_json_and_keep_defaults_for_missing_values()
        {
            string path = Path.Combine(Path.GetTempPath(), "RevitTextQC.Tests", Guid.NewGuid() + ".json");
            try
            {
                var settings = new QcSettings { Language = "en_GB", TextNoteCase = CaseMode.Title };
                settings.TextNoteStandards.AllowedSizesMm.Add(2.5);
                settings.ForbiddenTerms.Add(new ForbiddenTerm { Term = "TBC", Replacement = "" });
                settings.SaveTo(path);

                var loaded = QcSettings.LoadFrom(path);
                Assert.Equal("en_GB", loaded.Language);
                Assert.Equal(CaseMode.Title, loaded.TextNoteCase);
                Assert.Equal(2.5, Assert.Single(loaded.TextNoteStandards.AllowedSizesMm));
                Assert.Equal("TBC", Assert.Single(loaded.ForbiddenTerms).Term);

                File.WriteAllText(path, "{\"Language\":\"en_GB\",\"CustomWords\":null}");
                var partial = QcSettings.LoadFrom(path);
                Assert.True(partial.CheckSpelling);
                Assert.Equal(3, partial.MinWordLength);
                Assert.NotNull(partial.CustomWords);
                Assert.Equal("Comments", Assert.Single(partial.ParameterNames));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Theory]
        [InlineData("v1.2.3", "1.2.2", true)]
        [InlineData("v1.2", "1.2.0.0", false)]
        [InlineData("1.10.0", "1.9.9", true)]
        [InlineData("v0.1.0", "0.1.0", false)]
        [InlineData("v2.0.0-beta", "1.9.0", true)]
        [InlineData("nightly", "1.0.0", false)]
        public void Version_comparison(string tag, string current, bool expected)
        {
            Assert.Equal(expected, VersionUtil.IsNewer(VersionUtil.Parse(tag), Version.Parse(current)));
        }

        [Fact]
        public void Release_json_is_parsed()
        {
            const string json = @"{""tag_name"":""v1.4.0"",""html_url"":""https://example.test/r"",""body"":""notes"",
                ""assets"":[{""name"":""RevitTextQC-2026.zip"",""size"":10,""digest"":""sha256:ABC"",
                ""browser_download_url"":""https://example.test/a.zip"",""unknown"":{""x"":1}}]}";
            var release = ReleaseInfo.Parse(json);
            Assert.Equal(new Version(1, 4, 0, 0), release.Version);
            var asset = release.FindAsset("revittextqc-2026.zip");
            Assert.Equal("ABC", asset.Sha256);
            Assert.Null(release.FindAsset("RevitTextQC-2024.zip"));
        }

        [Fact]
        public void Hunspell_dictionary_loads_and_finds_real_typos()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Dictionaries");
            var engine = new QcEngine(
                new QcSettings { CheckCase = false },
                HunspellSpellChecker.Load(folder, "en_US"));

            var issues = engine.Run(new[]
            {
                new TextItem { Source = TextSource.TextNote, Text = "PROVIDE CONCRETE FOUNDATOIN TO ENGINEER'S DETAIL", Editable = true }
            });

            var issue = Assert.Single(issues);
            Assert.Equal("FOUNDATOIN", issue.Word);
            Assert.Equal("PROVIDE CONCRETE FOUNDATION TO ENGINEER'S DETAIL", issue.Suggestion);
        }
    }
}
