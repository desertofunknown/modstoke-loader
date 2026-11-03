// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
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
    internal static class InteropNativeVideoTypes
    {
        private const string AssemblyName = "UnityEngine.VideoModule";
        private const string Namespace = "UnityEngineInternal.Video.";
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();
        private static readonly string[] TypeNames =
        {
            "VideoPlayback", "VideoPlaybackMgr", "VideoError", "VideoPixelFormat", "VideoAlphaLayout"
        };
        private static readonly string[] CallbackNames =
        {
            "VideoPlayback+Callback", "VideoPlaybackMgr+Callback", "VideoPlaybackMgr+MessageCallback"
        };

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            if (sourceModule == null || targetModule == null ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                sourceModule.Mvid != new Guid("1ae682bf-8f22-44db-b0ff-265d12925a1f"))
                return;

            // Resolve the contract dynamically so older runtime versions remain usable.
            var registration = typeof(Il2CppInterop.Runtime.IL2CPP).Assembly.GetType(
                "Il2CppInterop.Runtime.Injection.NativeVideoRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var resolver = registration?.GetMethod("RequirePublishedClass", flags, null, new[] { typeof(string) }, null);
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (resolver == null || resolver.ContainsGenericParameters || resolver.ReturnType != typeof(IntPtr) ||
                version == null || !version.IsLiteral || version.GetRawConstantValue() is not int graphVersion ||
                graphVersion != 1)
                return;

            if (!MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "9F835D11026D88821EC78173D88625A101A2876575EACCA2308DEA6FD5A3FCD2") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var targetTypes = targetModule.GetAllTypes().ToArray();
            var counts = TypeNames.Concat(CallbackNames)
                .Select(name => targetTypes.Count(type => type.FullName == Namespace + name)).ToArray();
            if (counts.All(count => count == 1))
                return;
            if (counts.Any(count => count != 0))
                throw Invalid("the target contains an incomplete or duplicate eight-type declaration graph");

            RequireGameTypes(game, "UnityEngine.CoreModule", "UnityEngine.", "Object", "Texture");
            RequireGameTypes(game, "UnityEngine.AudioModule", "UnityEngine.", "AudioSource");
            // Original Unity netstandard references are resolved against the native game's mscorlib.
            RequireGameTypes(game, "mscorlib", "System.", "Object", "ValueType", "Enum", "Delegate",
                "MulticastDelegate", "IDisposable", "IAsyncResult", "AsyncCallback", "Void", "String",
                "Boolean", "IntPtr", "Int32", "UInt16", "UInt32", "Int64", "UInt64", "Single", "Double");

            var sources = TypeNames.Select(name => RequireType(sourceModule.TopLevelTypes, Namespace + name)).ToArray();
            ValidateOwner(sources[0], 34, 1, false);
            ValidateOwner(sources[1], 9, 2, true);
            ValidateEnum(sources[2], 11);
            ValidateEnum(sources[3], 5);
            ValidateEnum(sources[4], 3);
            var callbacks = new[]
            {
                RequireType(sources[0].NestedTypes, Namespace + CallbackNames[0]),
                RequireType(sources[1].NestedTypes, Namespace + CallbackNames[1]),
                RequireType(sources[1].NestedTypes, Namespace + CallbackNames[2])
            };
            for (var index = 0; index < callbacks.Length; index++)
                ValidateCallback(callbacks[index], index == 0 ? sources[0] : sources[1], index == 2);

            var provider = RequireMethod(sources[0], "GetAudioSampleProvider",
                "UnityEngine.Experimental.Audio.AudioSampleProvider", "System.UInt16");
            if (provider.IsStatic || provider.IsInternalCall || provider.CilMethodBody == null ||
                sources[0].Methods.Count(method => method.IsInternalCall) != 32 ||
                sources[1].Methods.Count(method => method.IsInternalCall) != 7)
                throw Invalid("the managed helper and native playback boundaries do not match");
            RequireMethod(sources[0], "GetAudioSampleProviderId", "System.UInt32", "System.UInt16");
            RequireMethod(sources[0], "GetTexture", "System.Boolean", "UnityEngine.Texture", "System.Int64&");
            RequireMethod(sources[0], "SetAudioTarget", "System.Void", "System.UInt16", "System.Boolean",
                "System.Boolean", "UnityEngine.AudioSource");
            RequireMethod(sources[1], "CreateVideoPlayback", Namespace + "VideoPlayback", "System.String",
                Namespace + CallbackNames[2], Namespace + CallbackNames[1], Namespace + CallbackNames[1], "System.Boolean");
            var dispose = RequireMethod(sources[1], "Dispose", "System.Void");
            var countGetter = RequireMethod(sources[1], "get_videoPlaybackCount", "System.UInt64");
            var countProperty = sources[1].Properties.Single();
            if (!dispose.IsPublic || !dispose.IsVirtual || !dispose.IsFinal || dispose.IsStatic ||
                countProperty.Name != "videoPlaybackCount" || countProperty.GetMethod != countGetter ||
                countProperty.SetMethod != null || countGetter.IsStatic)
                throw Invalid("the manager disposal or playback-count contract does not match");

            var graph = sources.Concat(callbacks).ToArray();
            foreach (var source in graph)
                ValidateNativeAttributes(source);

            // One recursive clone preserves cross-references and the original nested delegate owners.
            var cloner = new MemberCloner(targetModule);
            foreach (var source in sources)
                cloner.Include(source, true);
            var result = cloner.Clone();
            var clones = graph.Select(source => result.GetClonedMember(source)).ToArray();
            // This helper must be restored as ordinary managed IL, never as a native call boundary.
            clones[0].Methods.Remove(result.GetClonedMember(provider));
            foreach (var clone in clones)
                foreach (var method in clone.Methods)
                    method.MethodBody = null;

            for (var index = 0; index < sources.Length; index++)
                targetModule.TopLevelTypes.Add(clones[index]);
            foreach (var clone in clones)
                Prepared.Add(clone, new object());
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied 8 prepared native video declarations; GetAudioSampleProvider remains on the managed restoration path.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static void RequireGameTypes(InteropAssemblyMetadata game, string assemblyName, string ns,
            params string[] names)
        {
            var module = game.Assemblies.SingleOrDefault(assembly => assembly.Name == assemblyName)?.ManifestModule;
            foreach (var name in names)
                if (module == null || module.TopLevelTypes.Count(type => type.FullName == ns + name) != 1)
                    throw Invalid("the original game " + assemblyName + " is missing " + ns + name);
        }

        private static TypeDefinition RequireType(IEnumerable<TypeDefinition> types, string name)
        {
            var matches = types.Where(type => type.FullName == name).ToArray();
            if (matches.Length != 1)
                throw Invalid("expected exactly one source declaration for " + name);
            return matches[0];
        }

        private static void ValidateType(TypeDefinition type, string baseName, int fields, int methods, int nested,
            int interfaces, int properties)
        {
            if (type.BaseType?.FullName != baseName || type.IsInterface || type.GenericParameters.Count != 0 ||
                type.Fields.Count != fields || type.Methods.Count != methods || type.NestedTypes.Count != nested ||
                type.Interfaces.Count != interfaces || type.Properties.Count != properties || type.Events.Count != 0 ||
                type.Methods.Any(method => method.Signature == null || method.GenericParameters.Count != 0 ||
                    method.Signature.GenericParameterCount != 0))
                throw Invalid("source declaration contract does not match for " + type.FullName);
        }

        private static void ValidateOwner(TypeDefinition type, int methods, int nested, bool manager)
        {
            ValidateType(type, "System.Object", 1, methods, nested, manager ? 1 : 0, manager ? 1 : 0);
            var pointer = type.Fields[0];
            if (type.DeclaringType != null || type.Attributes != TypeAttributes.BeforeFieldInit ||
                pointer.Name != "m_Ptr" || pointer.Signature?.FieldType.FullName != "System.IntPtr" ||
                pointer.Attributes != FieldAttributes.Assembly ||
                pointer.FieldRva != null || pointer.Constant != null ||
                manager && type.Interfaces[0].Interface?.FullName != "System.IDisposable")
                throw Invalid("source owner or pointer contract does not match for " + type.FullName);
            var constructor = RequireMethod(type, ".ctor", "System.Void");
            if (!constructor.IsPublic || constructor.IsStatic || !constructor.IsRuntimeSpecialName)
                throw Invalid("source constructor contract does not match for " + type.FullName);
        }

        private static void ValidateCallback(TypeDefinition type, TypeDefinition owner, bool message)
        {
            ValidateType(type, "System.MulticastDelegate", 0, 4, 0, 0, 0);
            if (type.DeclaringType != owner || !type.IsNestedPublic || !type.IsSealed ||
                type.Methods.Any(method => method.IsStatic || !method.IsPublic || !method.IsRuntime))
                throw Invalid("source callback contract does not match for " + type.FullName);
            RequireMethod(type, ".ctor", "System.Void", "System.Object", "System.IntPtr");
            RequireMethod(type, "Invoke", "System.Void", message ? new[] { "System.String" } : Array.Empty<string>());
            RequireMethod(type, "BeginInvoke", "System.IAsyncResult", message
                ? new[] { "System.String", "System.AsyncCallback", "System.Object" }
                : new[] { "System.AsyncCallback", "System.Object" });
            RequireMethod(type, "EndInvoke", "System.Void", "System.IAsyncResult");
        }

        private static void ValidateEnum(TypeDefinition type, int fields)
        {
            ValidateType(type, "System.Enum", fields, 0, 0, 0, 0);
            var value = type.Fields.SingleOrDefault(field => field.Name == "value__");
            if (!type.IsEnum || !type.IsSealed || type.DeclaringType != null || value == null || value.IsStatic ||
                value.IsLiteral || value.Signature?.FieldType.FullName != "System.Int32" ||
                type.Fields.Where(field => field != value).Any(field => !field.IsStatic || !field.IsLiteral ||
                    field.Signature?.FieldType.FullName != type.FullName || field.Constant?.Type != ElementType.I4))
                throw Invalid("source I4 enum contract does not match for " + type.FullName);
        }

        private static MethodDefinition RequireMethod(TypeDefinition type, string name, string returnType,
            params string[] parameters)
        {
            var matches = type.Methods.Where(method => method.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].Signature.ReturnType.FullName != returnType ||
                !matches[0].Signature.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(parameters))
                throw Invalid("source method contract does not match for " + type.FullName + "::" + name);
            return matches[0];
        }

        private static void ValidateNativeAttributes(TypeDefinition type)
        {
            ValidateNativeAttributes(type, type.FullName);
            foreach (var field in type.Fields)
                ValidateNativeAttributes(field, field.FullName);
            foreach (var property in type.Properties)
                ValidateNativeAttributes(property, property.FullName);
            foreach (var method in type.Methods)
            {
                ValidateNativeAttributes(method, method.FullName);
                foreach (var parameter in method.ParameterDefinitions)
                    ValidateNativeAttributes(parameter, method.FullName);
            }
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
            new InvalidDataException("Cannot prepare matched native video declarations: " + reason + ".");
    }
}
