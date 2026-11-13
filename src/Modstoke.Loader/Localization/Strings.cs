// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Modstoke.Localization
{
    /// <summary>
    /// One owner's translated text: the Loader's own, or a mod's. Text lives in language files named by code
    /// ("en.json", "ru.json", "zh-Hans.json") in the mod's <c>lang</c> folder or embedded in its assembly under
    /// <c>lang/</c>. Each file is a JSON object of key to text:
    /// <code>
    /// {
    ///   "greeting": "Hello, {name}!",
    ///   "servers": { "one": "{count} server", "other": "{count} servers" },
    ///   "menu": { "back": "Back" }
    /// }
    /// </code>
    /// A text missing in the player's language comes from English, so a new language can be added one file at a
    /// time, and players can add or correct one by dropping a file into
    /// <c>Modstoke/UserData/lang/&lt;owner&gt;/</c>.
    /// </summary>
    public sealed class Strings
    {
        private static readonly Dictionary<string, Strings> Owners = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object OwnersSync = new();

        private readonly object _sync = new();
        private readonly List<Func<string, string>> _sources = new();
        private readonly List<Func<IEnumerable<string>>> _listings = new();
        private readonly Dictionary<string, StringCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        private Strings(string owner)
        {
            Owner = owner;
        }

        /// <summary>The folder players' own language files are read from, one sub-folder per owner.</summary>
        internal static string OverrideRoot { get; set; }

        /// <summary>Finds a mod's <c>lang</c> folder and loaded assembly by its id; null when the id is no mod's.</summary>
        internal static Func<string, (string Folder, Assembly Assembly)?> Resolver { get; set; }

        /// <summary>Finds the id of the mod an assembly belongs to, or null.</summary>
        internal static Func<Assembly, string> OwnerOf { get; set; }

        private bool _folderFound, _assemblyFound;
        private long _nextResolve;

        /// <summary>The mod id (or "modstoke" for the Loader) this text belongs to.</summary>
        public string Owner { get; }

        /// <summary>
        /// The text of one owner, created on first use. A native mod's <c>lang</c> folder and embedded
        /// <c>lang/</c> files are added for it when the mod loads; other owners add theirs with
        /// <see cref="AddFolder"/> or <see cref="AddAssembly"/>.
        /// </summary>
        public static Strings For(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner id is required.", nameof(owner));
            var id = owner.Trim();
            lock (OwnersSync)
            {
                if (Owners.TryGetValue(id, out var known)) return known;
                var created = new Strings(id);
                // Read last, so a player's file wins over the mod's own.
                created._sources.Add(language => OverrideRoot == null ? null : ReadFile(Path.Combine(OverrideRoot, SafeName(id)), language));
                created._listings.Add(() => OverrideRoot == null ? Enumerable.Empty<string>() : ListFolder(Path.Combine(OverrideRoot, SafeName(id))));
                return Owners[id] = created;
            }
        }

        /// <summary>
        /// The text of the mod an assembly belongs to: <c>Strings.For(typeof(MyMod).Assembly)</c>. An assembly
        /// that is no mod's gets text of its own under its assembly name, read from its embedded files.
        /// </summary>
        public static Strings For(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var owner = OwnerOf?.Invoke(assembly);
            return owner != null ? For(owner) : For(assembly.GetName().Name).AddAssembly(assembly);
        }

        /// <summary>Adds a folder of language files ("en.json", "ru.json"...).</summary>
        public Strings AddFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return this;
            return Add(language => ReadFile(folder, language), () => ListFolder(folder));
        }

        /// <summary>Adds language files embedded in an assembly as <c>lang/&lt;code&gt;.json</c>.</summary>
        public Strings AddAssembly(Assembly assembly)
        {
            if (assembly == null) return this;
            return Add(language => ReadResource(assembly, language), () => ListResources(assembly));
        }

        /// <summary>The text for a key in the current language.</summary>
        public string this[string key] => Get(key);

        /// <summary>
        /// The text for a key with its <c>{name}</c> placeholders filled in:
        /// <c>text.Get("greeting", ("name", player))</c>. A placeholder may carry a number or date format,
        /// as in <c>{distance:N1}</c>, which follows the language. A missing key comes back as the key itself.
        /// </summary>
        public string Get(string key, params (string Name, object Value)[] values)
        {
            foreach (var language in Languages.Chain)
            {
                var catalog = Catalog(language);
                if (catalog.Texts.TryGetValue(key, out var text)) return Fill(text, values, language);
                if (catalog.Counted.TryGetValue(key, out var forms)) return Fill(forms[PluralRules.Other], values, language);
            }
            return Missing(key);
        }

        /// <summary>
        /// A counted text in the right plural form for the language, with <c>{count}</c> filled in:
        /// <c>text.Count("servers", 3)</c> gives "3 servers", "3 сервера" or "3 台服务器".
        /// </summary>
        public string Count(string key, long count, params (string Name, object Value)[] values)
        {
            var all = new (string Name, object Value)[(values?.Length ?? 0) + 1];
            all[0] = ("count", count);
            values?.CopyTo(all, 1);
            foreach (var language in Languages.Chain)
            {
                var catalog = Catalog(language);
                if (catalog.Counted.TryGetValue(key, out var forms))
                {
                    var text = forms.TryGetValue(PluralRules.Category(language, count), out var form) ? form : forms[PluralRules.Other];
                    return Fill(text, all, language);
                }
                if (catalog.Texts.TryGetValue(key, out var plain)) return Fill(plain, all, language);
            }
            return Missing(key);
        }

        /// <summary>Whether a key has text in the current language or a fallback.</summary>
        public bool Has(string key)
        {
            foreach (var language in Languages.Chain)
            {
                var catalog = Catalog(language);
                if (catalog.Texts.ContainsKey(key) || catalog.Counted.ContainsKey(key)) return true;
            }
            return false;
        }

        /// <summary>The language codes this owner has files for.</summary>
        public IReadOnlyCollection<string> Provided()
        {
            Func<IEnumerable<string>>[] listings;
            lock (_sync) listings = _listings.ToArray();
            var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var listing in listings)
            {
                try
                {
                    foreach (var code in listing()) codes.Add(code);
                }
                catch (Exception)
                {
                    // A folder that cannot be listed simply offers no languages.
                }
            }
            return codes;
        }

        /// <summary>Drops loaded text so changed files are read again; for translators working with the game open.</summary>
        public void Reload()
        {
            lock (_sync)
            {
                _catalogs.Clear();
                _reported.Clear();
            }
        }

        /// <summary>Reloads every owner's text and tells listeners to redraw.</summary>
        internal static void ReloadAll()
        {
            Strings[] owners;
            lock (OwnersSync) owners = Owners.Values.ToArray();
            foreach (var owner in owners) owner.Reload();
            Languages.RaiseChanged();
        }

        private Strings Add(Func<string, string> source, Func<IEnumerable<string>> listing)
        {
            lock (_sync)
            {
                // Before the player's own files, which stay last.
                _sources.Insert(_sources.Count - 1, source);
                _listings.Add(listing);
                _catalogs.Clear();
            }
            return this;
        }

        // A mod's lang folder and embedded files are found from its package, with no code in the mod. Its
        // assembly may load after the first text is asked for, so that half is looked for again now and then.
        private void FindModFiles()
        {
            if (_folderFound && _assemblyFound || Resolver == null) return;
            var now = Environment.TickCount64;
            if (now < _nextResolve) return;
            _nextResolve = now + 1000;
            (string Folder, Assembly Assembly)? found;
            try
            {
                found = Resolver(Owner);
            }
            catch (Exception)
            {
                return;
            }
            if (found == null)
            {
                // Not a mod: nothing more to find.
                _folderFound = _assemblyFound = true;
                return;
            }
            if (!_folderFound)
            {
                _folderFound = true;
                if (!string.IsNullOrEmpty(found.Value.Folder)) AddFolder(found.Value.Folder);
            }
            if (!_assemblyFound && found.Value.Assembly != null)
            {
                _assemblyFound = true;
                AddAssembly(found.Value.Assembly);
            }
        }

        private StringCatalog Catalog(string language)
        {
            FindModFiles();
            lock (_sync)
            {
                if (_catalogs.TryGetValue(language, out var known)) return known;
                var catalog = new StringCatalog();
                foreach (var source in _sources)
                {
                    try
                    {
                        var json = source(language);
                        if (json != null) catalog.Merge(json, problem => Report(language, problem));
                    }
                    catch (Exception error)
                    {
                        Report(language, "could not be read: " + error.Message);
                    }
                }
                return _catalogs[language] = catalog;
            }
        }

        private void Report(string language, string problem)
        {
            if (_reported.Add(language + "\n" + problem))
                Languages.Warn("Language file " + language + ".json of " + Owner + ": " + problem);
        }

        private string Missing(string key)
        {
            bool first;
            lock (_sync) first = _reported.Add("missing\n" + key);
            if (first) Languages.Warn(Owner + " has no text for \"" + key + "\".");
            return key;
        }

        private static string Fill(string text, (string Name, object Value)[] values, string language)
        {
            if (text.IndexOf('{') < 0 && text.IndexOf('}') < 0) return text;
            var result = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // "{{" and "}}" write one brace.
                if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
                {
                    result.Append(c);
                    i++;
                    continue;
                }
                var end = c == '{' ? text.IndexOf('}', i + 1) : -1;
                if (end < 0)
                {
                    result.Append(c);
                    continue;
                }
                var token = text.Substring(i + 1, end - i - 1);
                var colon = token.IndexOf(':');
                var name = (colon < 0 ? token : token.Substring(0, colon)).Trim();
                var found = false;
                if (values != null)
                {
                    foreach (var value in values)
                    {
                        if (!string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Append(Write(value.Value, colon < 0 ? null : token.Substring(colon + 1), language));
                        found = true;
                        break;
                    }
                }
                // An unknown placeholder stays visible, so a typo in a language file is easy to spot.
                if (!found) result.Append('{').Append(token).Append('}');
                i = end;
            }
            return result.ToString();
        }

        private static string Write(object value, string format, string language)
        {
            if (value == null) return "";
            return value is IFormattable formattable
                ? formattable.ToString(format, Languages.CultureOf(language))
                : value.ToString();
        }

        private static string ReadFile(string folder, string language)
        {
            var path = Path.Combine(folder, language + ".json");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        private static IEnumerable<string> ListFolder(string folder)
            => Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension)
                : Enumerable.Empty<string>();

        // Build systems name embedded files "lang/ru.json", "lang\ru.json" or "My.Mod.lang.ru.json".
        private static string ReadResource(Assembly assembly, string language)
        {
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!IsLanguageResource(name, out var code) || !string.Equals(code, language, StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            return null;
        }

        private static IEnumerable<string> ListResources(Assembly assembly)
        {
            foreach (var name in assembly.GetManifestResourceNames())
                if (IsLanguageResource(name, out var code)) yield return code;
        }

        private static bool IsLanguageResource(string name, out string code)
        {
            code = null;
            var normal = name.Replace('\\', '/');
            if (!normal.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            var stem = normal.Substring(0, normal.Length - 5);
            var slash = stem.LastIndexOf("lang/", StringComparison.OrdinalIgnoreCase);
            var dotted = stem.LastIndexOf(".lang.", StringComparison.OrdinalIgnoreCase);
            if (slash >= 0 && (slash == 0 || stem[slash - 1] is '/' or '.')) code = stem.Substring(slash + 5);
            else if (dotted >= 0) code = stem.Substring(dotted + 6);
            else if (stem.StartsWith("lang.", StringComparison.OrdinalIgnoreCase)) code = stem.Substring(5);
            return !string.IsNullOrEmpty(code) && code.IndexOf('/') < 0;
        }

        private static string SafeName(string owner)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(owner.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }
    }
}
