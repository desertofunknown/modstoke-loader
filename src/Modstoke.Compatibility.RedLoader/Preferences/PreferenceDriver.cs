// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Reflection;
using RedLoader.Preferences;
using File = RedLoader.Preferences.IO.File;

namespace RedLoader;

internal static class PreferenceDriver
{
    private static readonly Lazy<Bindings> Owner = new(() => new Bindings());

    internal static void Load() => Owner.Value.Load();
    internal static void Save() => Owner.Value.Save();
    internal static void Reload(File file, bool printmsg) => Owner.Value.Reload(file, printmsg);
    internal static void SaveCategory(ConfigCategory category, bool printmsg) => Owner.Value.SaveCategory(category, printmsg);
    internal static void SaveReflectiveCategory(ReflectiveConfigCategory category, bool printmsg) => Owner.Value.SaveReflectiveCategory(category, printmsg);
    internal static void ReadLegacyFile(File file) => Owner.Value.ReadLegacyFile(file);
    internal static void ReadFile(File file) => Owner.Value.ReadFile(file);
    internal static void WriteFile(File file) => Owner.Value.WriteFile(file);

    private sealed class Bindings
    {
        internal readonly Action Load;
        internal readonly Action Save;
        internal readonly Action<File, bool> Reload;
        internal readonly Action<ConfigCategory, bool> SaveCategory;
        internal readonly Action<ReflectiveConfigCategory, bool> SaveReflectiveCategory;
        internal readonly Action<File> ReadLegacyFile;
        internal readonly Action<File> ReadFile;
        internal readonly Action<File> WriteFile;

        internal Bindings()
        {
            var owner = Assembly.Load("Modstoke.Loader")
                .GetType("Modstoke.Loader.RedLoaderPreferences", true);
            Load = Bind<Action>(owner, "Load");
            Save = Bind<Action>(owner, "Save");
            Reload = Bind<Action<File, bool>>(owner, "Reload");
            SaveCategory = Bind<Action<ConfigCategory, bool>>(owner, "SaveCategory");
            SaveReflectiveCategory = Bind<Action<ReflectiveConfigCategory, bool>>(owner, "SaveReflectiveCategory");
            ReadLegacyFile = Bind<Action<File>>(owner, "ReadLegacyFile");
            ReadFile = Bind<Action<File>>(owner, "ReadFile");
            WriteFile = Bind<Action<File>>(owner, "WriteFile");
        }

        private static T Bind<T>(Type owner, string name) where T : Delegate
        {
            var parameters = typeof(T).GetMethod("Invoke").GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null)
                ?? throw new MissingMethodException(owner.FullName, name);
            return (T)method.CreateDelegate(typeof(T));
        }
    }
}
