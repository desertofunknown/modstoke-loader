// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Modstoke.Preloader
{
    internal static class InteropNativeAudioType
    {
        private const string AssemblyName = "UnityEngine.CoreModule";
        private const string ModuleName = AssemblyName + ".dll";
        private const string TypeName = "UnityEngine.AudioType";
        private static readonly string[] LiteralNames =
        {
            "UNKNOWN", "ACC", "AIFF", "IT", "MOD", "MPEG", "OGGVORBIS", "S3M", "WAV", "XM", "XMA", "VAG", "AUDIOQUEUE"
        };
        private static readonly int[] LiteralValues = { 0, 1, 2, 10, 12, 13, 14, 17, 20, 21, 22, 23, 24 };

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceAssembly = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName);
            var sourceModule = sourceAssembly?.ManifestModule;
            var targets = game.Assemblies.Where(assembly =>
                string.Equals(assembly.Name?.ToString(), AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                assembly.Modules.Any(module => string.Equals(module.Name?.ToString(), ModuleName,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            if (sourceModule == null || targets.Length == 0 || targets.Length == 1 && targets[0].ManifestModule == null ||
                sourceModule.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                !HasAssemblyIdentity(sourceAssembly) ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                !HasRuntimeContract() ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, ModuleName),
                    "6B465079537366D7F7933C77E3D6453BF8098CBD020A86EB66642532A9D5279A") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            if (targets.Length != 1 || !HasAssemblyIdentity(targets[0]))
                throw Invalid("the target contains a conflicting assembly or module identity");
            var targetModule = targets[0].ManifestModule;
            var sources = sourceModule.GetAllTypes().Where(type => type.FullName == TypeName).ToArray();
            if (sources.Length != 1)
                throw Invalid("expected exactly one source AudioType declaration");
            var source = sources[0];
            ValidateEnum(source, true);

            var existing = targetModule.GetAllTypes().Where(type => type.FullName == TypeName).ToArray();
            if (existing.Length > 1)
                throw Invalid("the target contains duplicate AudioType declarations");
            if (existing.Length == 1)
            {
                ValidateEnum(existing[0], false);
                return;
            }

            var cloner = new MemberCloner(targetModule);
            cloner.Include(source);
            var clone = cloner.Clone().GetClonedMember(source);
            // Keep the source's signed System.Enum reference; ordinary enum generation supplies the CLR base.
            targetModule.TopLevelTypes.Add(clone);
            InteropGenerationDiagnostics.Log(LogLevel.Info, "Supplied the prepared native AudioType declaration.");
        }

        private static bool HasAssemblyIdentity(AssemblyDefinition assembly) =>
            assembly.Name == AssemblyName && assembly.Modules.Count == 1 && assembly.ManifestModule?.Name == ModuleName &&
            assembly.Version == new Version(0, 0, 0, 0) && string.IsNullOrEmpty(assembly.Culture?.ToString()) &&
            !assembly.HasPublicKey && assembly.PublicKey is not { Length: > 0 };

        private static bool HasRuntimeContract()
        {
            var registration = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection.NativeAudioTypeRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (version == null || !version.IsLiteral || version.FieldType != typeof(int) ||
                version.GetRawConstantValue() is not int graphVersion || graphVersion != 1)
                return false;
            var resolvers = registration.GetMethods(flags).Where(method => method.Name == "RequirePublishedClass").ToArray();
            if (resolvers.Length != 1) return false;
            var resolver = resolvers[0];
            return !resolver.IsGenericMethod && !resolver.ContainsGenericParameters && resolver.ReturnType == typeof(IntPtr) &&
                resolver.CallingConvention == CallingConventions.Standard && resolver.GetParameters().Length == 0 &&
                resolver.ReturnParameter.Attributes == System.Reflection.ParameterAttributes.None &&
                resolver.ReturnParameter.GetRequiredCustomModifiers().Length == 0 &&
                resolver.ReturnParameter.GetOptionalCustomModifiers().Length == 0;
        }

        internal static void ValidateEnum(TypeDefinition type, bool source)
        {
            var attributes = source ? type.Attributes : type.Attributes & ~TypeAttributes.BeforeFieldInit;
            if (attributes != (TypeAttributes.Public | TypeAttributes.Sealed) || type.DeclaringType != null ||
                !type.IsEnum || type.BaseType?.FullName != "System.Enum" || type.GenericParameters.Count != 0 ||
                type.ClassLayout != null || type.Methods.Count != 0 || type.NestedTypes.Count != 0 ||
                type.Interfaces.Count != 0 || type.Properties.Count != 0 || type.Events.Count != 0 ||
                type.MethodImplementations.Count != 0 || type.Fields.Count != LiteralNames.Length + 1 ||
                source && (type.MetadataToken.ToInt32() != 0x020000F2 || type.CustomAttributes.Count != 0))
                throw Invalid("the " + (source ? "source" : "target") + " AudioType enum contract does not match");

            var values = type.Fields.Where(field => field.Name == "value__").ToArray();
            if (values.Length != 1)
                throw Invalid("expected exactly one AudioType value__ field");
            var value = values[0];
            ValidateField(value, source, 0x040002B6);
            if (value.Attributes != (FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RuntimeSpecialName) ||
                value.Signature.FieldType is not CorLibTypeSignature { ElementType: ElementType.I4 } || value.Constant != null)
                throw Invalid("the AudioType value__ field contract does not match");

            for (var index = 0; index < LiteralNames.Length; index++)
            {
                var fields = type.Fields.Where(field => field.Name == LiteralNames[index]).ToArray();
                if (fields.Length != 1)
                    throw Invalid("expected exactly one AudioType literal " + LiteralNames[index]);
                var field = fields[0];
                ValidateField(field, source, 0x040002B7 + index);
                if (field.Attributes != (FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault) ||
                    field.Signature.FieldType is not TypeDefOrRefSignature { IsValueType: true } signature ||
                    !SignatureComparer.Default.Equals(signature.Type, type) || field.Constant?.Type != ElementType.I4 ||
                    field.Constant.Value?.Data is not { Length: 4 } ||
                    field.Constant.Value.InterpretData(ElementType.I4) is not int constant || constant != LiteralValues[index])
                    throw Invalid("the AudioType literal contract does not match for " + LiteralNames[index]);
            }
        }

        private static void ValidateField(FieldDefinition field, bool source, int token)
        {
            if (field.Signature?.FieldType == null || field.Signature.Attributes != CallingConventionAttributes.Field ||
                field.Signature.ExtraData is { Length: > 0 } || field.Signature.FieldType.ExtraData is { Length: > 0 } ||
                field.FieldRva != null || field.FieldOffset != null || field.HasFieldMarshal || field.MarshalDescriptor != null ||
                source && (field.MetadataToken.ToInt32() != token || field.CustomAttributes.Count != 0))
                throw Invalid("the AudioType field metadata contract does not match for " + field.Name);
        }

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static InvalidDataException Invalid(string reason) =>
            new InvalidDataException("Cannot prepare the matched native AudioType declaration: " + reason + ".");
    }
}
