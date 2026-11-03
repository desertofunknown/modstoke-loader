using System.Linq;
using Tomlet;
using Tomlet.Exceptions;
using Tomlet.Models;

namespace RedLoader.Preferences.IO
{
    internal class File
    {
        private bool _waserror = false;

        internal bool WasError
        {
            get => _waserror;
            set
            {
                _waserror = value;
                if (value == true)
                {
                    IsSaving = false;
                    try { RLog.Warning($"Defaulting {FilePath} to Fallback Functionality to further avoid File Corruption..."); }
                    catch { /* Warning observers must not undo file protection or abort other files. */ }
                }
            }
        }

        internal string FilePath = null;
        internal string LegacyFilePath = null;
        internal bool IsSaving = false;
        internal bool ShouldSave = true;
        internal TomlDocument document = TomlDocument.CreateEmpty();

        internal File(string filepath, string legacyfilepath = null, bool shouldsave = true)
        {
            FilePath = filepath;
            LegacyFilePath = legacyfilepath;
            ShouldSave = shouldsave;
        }

        internal void LegacyLoad()
        {
            PreferenceDriver.ReadLegacyFile(this);
        }

        internal void Load()
        {
            PreferenceDriver.ReadFile(this);
        }

        internal void Save()
        {
            PreferenceDriver.WriteFile(this);
        }
        
        private static string QuoteKey(string key) =>
            key.Contains('"') 
                ? $"'{key}'"
                : $"\"{key}\"";

        internal void InsertIntoDocument(string category, string key, TomlValue value, bool should_inline = false)
        {
            if (!document.ContainsKey(category))
                document.PutValue(category, new TomlTable());
            
            try
            {
                var categoryTable = document.GetSubTable(category);
                categoryTable.ForceNoInline = !should_inline;
                categoryTable.PutValue(QuoteKey(key), value);
            }
            catch (TomlTypeMismatchException)
            {
                //Ignore
            }
            catch (TomlNoSuchValueException)
            {
                //Ignore
            }
        }

        internal bool RemoveEntryFromDocument(string category, string key)
        {
            if (!document.ContainsKey(category))
                return false;

            try
            {
                var categoryTable = document.GetSubTable(category);
                return categoryTable.Entries.Remove(key);
            }
            catch (TomlTypeMismatchException)
            {
                return false;
            }
            catch (TomlNoSuchValueException)
            {
                return false;
            }
        }

        internal bool RemoveCategoryFromDocument(string category)
        {
            if (!document.ContainsKey(category))
                return false;
            try
            {
                return document.Entries.Remove(category);
            }
            catch (TomlTypeMismatchException)
            {
                return false;
            }
            catch (TomlNoSuchValueException)
            {
                return false;
            }
        }

        internal bool RenameEntryInDocument(string category, string key, string newKey)
        {
            if (!document.ContainsKey(category))
                return false;

            try
            {
                var categoryTable = document.GetSubTable(category);
                if (!categoryTable.Entries.ContainsKey(key) || categoryTable.Entries.ContainsKey(newKey))
                    return false;

                TomlValue value = categoryTable.Entries[key];
                categoryTable.Entries.Remove(key);
                categoryTable.Entries.Add(newKey, value);
                return true;
            }
            catch (TomlTypeMismatchException)
            {
                return false;
            }
            catch (TomlNoSuchValueException)
            {
                return false;
            }
        }

        internal TomlTable TryGetCategoryTable(string category)
        {
            lock (document)
            {
                try
                {
                    return document.GetSubTable(category);
                }
                catch (TomlTypeMismatchException)
                {
                    //Ignore
                }
                catch (TomlNoSuchValueException)
                {
                    //Ignore
                }

                return null;
            }
        }

        internal void SetupEntryFromRawValue(ConfigEntry entry)
        {
            lock (document)
            {
                try
                {
                    var categoryTable = document.GetSubTable(entry.Category.Identifier);
                    var value = categoryTable.GetValue(QuoteKey(entry.Identifier));
                    entry.Load(value);
                }
                catch (TomlTypeMismatchException)
                {
                    //Ignore
                }
                catch (TomlNoSuchValueException)
                {
                    //Ignore
                }
            }
        }
    }
}
