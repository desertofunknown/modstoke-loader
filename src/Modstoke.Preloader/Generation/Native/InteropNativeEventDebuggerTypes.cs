// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;
using BindingFlags = System.Reflection.BindingFlags;

namespace Modstoke.Preloader
{
    internal static partial class InteropNativeEventDebuggerTypes
    {
        private const string AssemblyName = "UnityEngine.UIElementsModule";
        private const string Namespace = "UnityEngine.UIElements.";
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();
        private static readonly string[] TypeNames =
        {
            "EventDebuggerEventRecord", "EventDebuggerTrace", "EventDebuggerCallTrace",
            "EventDebuggerDefaultActionTrace", "EventDebuggerPathTrace"
        };
        private static readonly (string Name, string Type)[] RecordFields =
        {
            ("eventBaseName", "System.String"),
            ("eventTypeId", "System.Int64"),
            ("eventId", "System.UInt64"),
            ("triggerEventId", "System.UInt64"),
            ("timestamp", "System.Int64"),
            ("target", Namespace + "IEventHandler"),
            ("skipElements", "System.Collections.Generic.List`1<" + Namespace + "IEventHandler>"),
            ("hasUnderlyingPhysicalEvent", "System.Boolean"),
            ("isPropagationStopped", "System.Boolean"),
            ("isImmediatePropagationStopped", "System.Boolean"),
            ("isDefaultPrevented", "System.Boolean"),
            ("propagationPhase", Namespace + "PropagationPhase"),
            ("currentTarget", Namespace + "IEventHandler"),
            ("dispatch", "System.Boolean"),
            ("originalMousePosition", "UnityEngine.Vector2"),
            ("modifiers", "UnityEngine.EventModifiers"),
            ("mousePosition", "UnityEngine.Vector2"),
            ("clickCount", "System.Int32"),
            ("button", "System.Int32"),
            ("pressedButtons", "System.Int32"),
            ("delta", "UnityEngine.Vector3"),
            ("character", "System.Char"),
            ("keyCode", "UnityEngine.KeyCode"),
            ("commandName", "System.String"),
            ("deviceType", Namespace + "NavigationDeviceType"),
            ("navigationDirection", Namespace + "NavigationMoveEvent+Direction")
        };

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            if (sourceModule == null || targetModule == null ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                sourceModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb"))
                return;

            // Older runtime versions must not receive declarations they cannot publish.
            var registration = typeof(Il2CppInterop.Runtime.IL2CPP).Assembly.GetType(
                "Il2CppInterop.Runtime.Injection.NativeEventDebuggerRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var resolver = registration?.GetMethod("RequirePublishedClass", flags, null, new[] { typeof(string) }, null);
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (resolver == null || resolver.ContainsGenericParameters || resolver.ReturnType != typeof(IntPtr) ||
                version == null || !version.IsLiteral || version.GetRawConstantValue() is not int graphVersion ||
                graphVersion != 2)
                return;

            if (!MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "EE6E54289BF2F8FCBAD2C494053FA6661AE4B29C0B712B80FEED0A2C159ABE67") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var targetTypes = targetModule.GetAllTypes().ToArray();
            var targetOwners = targetTypes.Where(type => type.FullName == Namespace + "EventDebugger").ToArray();
            if (targetOwners.Length != 1 || targetOwners[0].DeclaringType != null)
                throw Invalid("expected exactly one top-level target EventDebugger declaration");
            var targetOwner = targetOwners[0];
            var counts = TypeNames.Concat(ReplayTypeNames)
                .Select(name => targetTypes.Count(type => type.FullName == Namespace + name)).ToArray();
            if (counts.All(count => count == 1))
                return;
            if (counts.Any(count => count != 0))
                throw Invalid("the target contains an incomplete or duplicate nine-type declaration graph");

            var sourceOwner = RequireType(sourceModule, "EventDebugger");
            RequireGameTypes(game, AssemblyName, Namespace + "IEventHandler", Namespace + "PropagationPhase",
                Namespace + "PropagationPaths", Namespace + "NavigationDeviceType", Namespace + "NavigationMoveEvent+Direction");
            RequireGameTypes(game, "UnityEngine.CoreModule", "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.KeyCode");
            RequireGameTypes(game, "UnityEngine.IMGUIModule", "UnityEngine.EventModifiers", "UnityEngine.Event");
            // The source netstandard references bind to the native game's corlib during rewriting.
            RequireGameTypes(game, "mscorlib", "System.Object", "System.String", "System.Boolean", "System.Char",
                "System.Int32", "System.Int64", "System.UInt64", "System.Single", "System.Void",
                "System.Collections.Generic.List`1", "System.Collections.Generic.IEnumerable`1", "System.Action`2",
                "System.Collections.IEnumerator", "System.Collections.Generic.IEnumerator`1", "System.IDisposable");

            var sources = TypeNames.Select(name => RequireType(sourceModule, name)).ToArray();
            ValidateType(sources[0], "System.Object", 26, 55, 26, true);
            ValidateType(sources[1], "System.Object", 4, 6, 4, false);
            ValidateType(sources[2], Namespace + "EventDebuggerTrace", 5, 6, 5, false);
            ValidateType(sources[3], Namespace + "EventDebuggerTrace", 1, 3, 2, false);
            ValidateType(sources[4], Namespace + "EventDebuggerTrace", 1, 2, 1, false);
            for (var index = 0; index < RecordFields.Length; index++)
                ValidateField(sources[0].Fields[index], RecordFields[index].Name, RecordFields[index].Type, false);
            ValidateField(sources[1].Fields[0], "eventBase", Namespace + "EventDebuggerEventRecord", true);
            ValidateField(sources[1].Fields[1], "focusedElement", Namespace + "IEventHandler", true);
            ValidateField(sources[1].Fields[2], "mouseCapture", Namespace + "IEventHandler", true);
            ValidateField(sources[1].Fields[3], "duration", "System.Int64", false);
            ValidateField(sources[2].Fields[0], "callbackHashCode", "System.Int32", true);
            ValidateField(sources[2].Fields[1], "callbackName", "System.String", true);
            ValidateField(sources[2].Fields[2], "propagationHasStopped", "System.Boolean", true);
            ValidateField(sources[2].Fields[3], "immediatePropagationHasStopped", "System.Boolean", true);
            ValidateField(sources[2].Fields[4], "defaultHasBeenPrevented", "System.Boolean", true);
            ValidateField(sources[3].Fields[0], "phase", Namespace + "PropagationPhase", true);
            ValidateField(sources[4].Fields[0], "paths", Namespace + "PropagationPaths", true);
            foreach (var source in sources)
            {
                ValidateNativeAttributes(source, source.FullName);
                foreach (var field in source.Fields)
                    ValidateNativeAttributes(field, field.FullName);
            }

            var replay = RequireReplayTypes(sourceModule, sourceOwner);
            var graph = sources.Concat(replay).ToArray();
            // One recursive clone keeps record references and the nested await iterator in the same graph.
            var cloner = new MemberCloner(targetModule);
            foreach (var source in sources.Concat(replay.Where(type => type.DeclaringType == sourceOwner)))
                cloner.Include(source, true);
            var result = cloner.Clone();
            var clones = graph.Select(source => result.GetClonedMember(source)).ToArray();
            foreach (var clone in clones)
            {
                // All behavior is restored managed IL; none of these source methods has a native pointer.
                clone.Properties.Clear();
                clone.Events.Clear();
                clone.MethodImplementations.Clear();
                clone.Methods.Clear();
            }

            foreach (var source in sources)
                targetModule.TopLevelTypes.Add(result.GetClonedMember(source));
            foreach (var source in replay.Where(type => type.DeclaringType == sourceOwner))
                targetOwner.NestedTypes.Add(result.GetClonedMember(source));
            foreach (var clone in clones)
                Prepared.Add(clone, new object());
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied 9 prepared native EventDebugger declarations; all methods remain on the managed restoration path.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static void RequireGameTypes(InteropAssemblyMetadata game, string assemblyName, params string[] names)
        {
            var module = game.Assemblies.SingleOrDefault(assembly => assembly.Name == assemblyName)?.ManifestModule;
            var types = module?.GetAllTypes().ToArray();
            foreach (var name in names)
                if (types == null || types.Count(type => type.FullName == name) != 1)
                    throw Invalid("the original game " + assemblyName + " is missing " + name);
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string name)
        {
            var matches = module.TopLevelTypes.Where(type => type.FullName == Namespace + name).ToArray();
            if (matches.Length != 1)
                throw Invalid("expected exactly one source declaration for " + Namespace + name);
            return matches[0];
        }

        private static void ValidateType(TypeDefinition type, string baseName, int fields, int methods,
            int properties, bool serializable)
        {
            var attributes = TypeAttributes.BeforeFieldInit | (serializable ? TypeAttributes.Serializable : 0);
            if (type.Attributes != attributes || type.BaseType?.FullName != baseName ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || type.NestedTypes.Count != 0 ||
                type.Interfaces.Count != 0 || type.MethodImplementations.Count != 0 || type.ClassLayout != null ||
                type.Fields.Count != fields || type.Methods.Count != methods ||
                type.Properties.Count != properties || type.Events.Count != 0 ||
                type.Methods.Any(method => method.Signature == null || method.IsVirtual || method.IsAbstract ||
                    method.IsInternalCall || method.IsRuntime || method.GenericParameters.Count != 0 ||
                    method.Signature.GenericParameterCount != 0))
                throw Invalid("source declaration contract does not match for " + type.FullName);
        }

        private static void ValidateField(FieldDefinition field, string name, string typeName, bool readOnly)
        {
            var attributes = FieldAttributes.Private | (readOnly ? FieldAttributes.InitOnly : 0);
            if (field.Name != "<" + name + ">k__BackingField" || field.Signature?.FieldType.FullName != typeName ||
                field.Attributes != attributes || field.FieldOffset != null || field.HasFieldMarshal ||
                field.MarshalDescriptor != null || field.FieldRva != null || field.Constant != null)
                throw Invalid("source field contract does not match for " + field.FullName);
        }

        private static void ValidateNativeAttributes(IHasCustomAttribute member, string name)
        {
            if (member.ExtractToken() != 0 || member.ExtractOffset() != 0 || member.ExtractRva() != 0 ||
                member.CustomAttributes.Any(attribute =>
                    attribute.Constructor?.DeclaringType?.Name == "TokenAttribute" ||
                    attribute.Constructor?.DeclaringType?.Name == "AddressAttribute"))
                throw Invalid("unexpected native token or address attributes on " + name);
        }

        private static InvalidDataException Invalid(string reason) =>
            new InvalidDataException("Cannot prepare matched native EventDebugger declarations: " + reason + ".");
    }
}
