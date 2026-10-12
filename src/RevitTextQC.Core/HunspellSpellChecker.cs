using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeCantSpell.Hunspell;

namespace RevitTextQC.Core
{
    /// <summary>Spell checker backed by a Hunspell .dic/.aff pair (the format LibreOffice and Firefox use).</summary>
    public class HunspellSpellChecker : ISpellChecker
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, HunspellSpellChecker> Cache =
            new Dictionary<string, HunspellSpellChecker>(StringComparer.OrdinalIgnoreCase);

        private readonly WordList _words;

        private HunspellSpellChecker(WordList words)
        {
            _words = words;
        }

        /// <summary>Loads (once per session) the dictionary for a language such as en_US from a folder.</summary>
        public static HunspellSpellChecker Load(string dictionaryFolder, string language)
        {
            string dic = Path.Combine(dictionaryFolder, language + ".dic");
            string aff = Path.Combine(dictionaryFolder, language + ".aff");
            lock (CacheLock)
            {
                if (Cache.TryGetValue(dic, out var cached)) return cached;
                if (!File.Exists(dic) || !File.Exists(aff))
                    throw new FileNotFoundException($"Dictionary '{language}' was not found in {dictionaryFolder}.");
                var checker = new HunspellSpellChecker(WordList.CreateFromFiles(dic, aff));
                Cache[dic] = checker;
                return checker;
            }
        }

        public static IEnumerable<string> AvailableLanguages(string dictionaryFolder)
        {
            if (!Directory.Exists(dictionaryFolder)) return Enumerable.Empty<string>();
            return Directory.GetFiles(dictionaryFolder, "*.dic")
                .Where(f => File.Exists(Path.ChangeExtension(f, ".aff")))
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n)
                .ToList();
        }

        public bool Check(string word)
        {
            return _words.Check(word);
        }

        public IList<string> Suggest(string word)
        {
            return _words.Suggest(word).ToList();
        }
    }
}
