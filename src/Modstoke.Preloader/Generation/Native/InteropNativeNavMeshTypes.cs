// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal static class InteropNativeNavMeshTypes
    {
        private const string AssemblyName = "UnityEngine.AIModule";
        private const string Namespace = "UnityEngine.AI.";
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();
        private static readonly string[] TypeNames =
        {
            "NavMeshData", "NavMeshBuildSettings", "NavMeshBuildDebugSettings", "NavMeshBuildDebugFlags",
            "NavMeshBuildSource", "NavMeshBuildMarkup", "NavMeshBuildSourceShape", "NavMeshCollectGeometry", "NavMeshBuilder"
        };
        private static readonly (string Name, string Type)[] SettingsFields =
        {
            ("m_AgentTypeID", "System.Int32"),
            ("m_AgentRadius", "System.Single"),
            ("m_AgentHeight", "System.Single"),
            ("m_AgentSlope", "System.Single"),
            ("m_AgentClimb", "System.Single"),
            ("m_LedgeDropHeight", "System.Single"),
            ("m_MaxJumpAcrossDistance", "System.Single"),
            ("m_MinRegionArea", "System.Single"),
            ("m_OverrideVoxelSize", "System.Int32"),
            ("m_VoxelSize", "System.Single"),
            ("m_OverrideTileSize", "System.Int32"),
            ("m_TileSize", "System.Int32"),
            ("m_BuildHeightMesh", "System.Int32"),
            ("m_MaxJobWorkers", "System.UInt32"),
            ("m_PreserveTilesOutsideBounds", "System.Int32"),
            ("m_Debug", Namespace + "NavMeshBuildDebugSettings")
        };
        private static readonly (string Name, string Type, int Offset)[] SourceFields =
        {
            ("m_Transform", "UnityEngine.Matrix4x4", 0), ("m_Size", "UnityEngine.Vector3", 64),
            ("m_Shape", Namespace + "NavMeshBuildSourceShape", 76), ("m_Area", "System.Int32", 80),
            ("m_InstanceID", "System.Int32", 84), ("m_ComponentID", "System.Int32", 88),
            ("m_GenerateLinks", "System.Int32", 92)
        };
        private static readonly string[] MarkupFields =
        {
            "m_OverrideArea", "m_Area", "m_InheritIgnoreFromBuild", "m_IgnoreFromBuild",
            "m_OverrideGenerateLinks", "m_GenerateLinks", "m_InstanceID", "m_IgnoreChildren"
        };

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            if (sourceModule == null || targetModule == null ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                sourceModule.Mvid != new Guid("67185cb2-d65c-4e42-8ab9-781bfee20f59"))
                return;

            var registration = typeof(Il2CppInterop.Runtime.IL2CPP).Assembly.GetType(
                "Il2CppInterop.Runtime.Injection.NativeNavMeshRegistration");
            var resolver = registration?.GetMethod("RequirePublishedClass", new[] { typeof(string) });
            var version = registration?.GetField("PublishedGraphVersion");
            if (resolver == null || !resolver.IsStatic || resolver.ReturnType != typeof(IntPtr) ||
                version == null || !version.IsLiteral || version.GetRawConstantValue() is not int graphVersion ||
                graphVersion != 3)
                return;

            if (!MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "1CCA4DFB1E33061FE408C280ADECE567004EE8FD35BA5CAE01ED24367D78D130") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var present = TypeNames.Count(name => targetModule.TopLevelTypes.Any(type => type.FullName == Namespace + name));
            if (present == TypeNames.Length)
                return;
            if (present != 0)
                throw Invalid("the target contains only part of the nine-type declaration graph");

            var core = game.Assemblies.SingleOrDefault(assembly => assembly.Name == "UnityEngine.CoreModule")?.ManifestModule;
            foreach (var name in new[] { "Object", "Component", "Transform", "Vector3", "Quaternion", "Bounds", "Matrix4x4", "AsyncOperation" })
                if (core == null || !core.TopLevelTypes.Any(type => type.FullName == "UnityEngine." + name))
                    throw Invalid("the original game CoreModule is missing UnityEngine." + name);

            var sources = TypeNames.Select(name => RequireType(sourceModule, name)).ToArray();
            ValidateType(sources[0], "UnityEngine.Object", 0, 16, false);
            ValidateType(sources[1], "System.ValueType", 16, 35, true);
            ValidateType(sources[2], "System.ValueType", 1, 2, true);
            ValidateType(sources[3], "System.Enum", 10, 0, false);
            ValidateType(sources[4], "System.ValueType", 7, 16, true);
            ValidateType(sources[5], "System.ValueType", 8, 17, true);
            ValidateType(sources[6], "System.Enum", 7, 0, false);
            ValidateType(sources[7], "System.Enum", 3, 0, false);
            ValidateType(sources[8], "System.Object", 0, 14, false);
            if (!sources[8].IsAbstract || !sources[8].IsSealed ||
                sources[8].Methods.Count(IsBuilderBoundary) != 7 ||
                sources[8].Methods.Any(method => !method.IsStatic))
                throw Invalid("the NavMeshBuilder static method contract does not match");
            for (var index = 0; index < SettingsFields.Length; index++)
                ValidateField(sources[1].Fields[index], SettingsFields[index].Name, SettingsFields[index].Type);
            ValidateField(sources[2].Fields[0], "m_Flags", "System.Byte");
            ValidateEnum(sources[3], "None", "InputGeometry", "Voxels", "Regions", "RawContours",
                "SimplifiedContours", "PolygonMeshes", "PolygonMeshesDetail", "All");
            for (var index = 0; index < SourceFields.Length; index++)
                ValidateField(sources[4].Fields[index], SourceFields[index].Name, SourceFields[index].Type);
            for (var index = 0; index < MarkupFields.Length; index++)
                ValidateField(sources[5].Fields[index], MarkupFields[index], "System.Int32");
            ValidateEnum(sources[6], "Mesh", "Terrain", "Box", "Sphere", "Capsule", "ModifierBox");
            ValidateEnum(sources[7], "RenderMeshes", "PhysicsColliders");

            // One cloner preserves references across the graph without changing source metadata.
            var cloner = new MemberCloner(targetModule);
            foreach (var source in sources)
                cloner.Include(source);
            var result = cloner.Clone();
            var clones = sources.Select(source => result.GetClonedMember(source)).ToArray();
            // Only these seven boundaries have native method bodies. List-based source helpers
            // remain on the managed restoration path; this does not synthesize a native List<T>.
            foreach (var method in clones[8].Methods.Where(method => !IsBuilderBoundary(method)).ToArray())
                clones[8].Methods.Remove(method);
            foreach (var clone in clones)
            {
                ValidateNativeAttributes(clone, clone.FullName);
                foreach (var field in clone.Fields)
                    ValidateNativeAttributes(field, field.FullName);
                foreach (var method in clone.Methods)
                {
                    ValidateNativeAttributes(method, method.FullName);
                    method.CilMethodBody = null;
                }
            }

            clones[1].IsExplicitLayout = true;
            clones[1].ClassLayout = new ClassLayout(4, 64);
            for (var index = 0; index < SettingsFields.Length; index++)
                clones[1].Fields[index].FieldOffset = index * 4;
            clones[2].IsExplicitLayout = true;
            clones[2].ClassLayout = new ClassLayout(1, 1);
            clones[2].Fields[0].FieldOffset = 0;
            clones[0].ClassLayout = null;
            clones[3].ClassLayout = null;
            clones[4].IsExplicitLayout = true;
            clones[4].ClassLayout = new ClassLayout(4, 96);
            for (var index = 0; index < SourceFields.Length; index++)
                clones[4].Fields[index].FieldOffset = SourceFields[index].Offset;
            clones[5].IsExplicitLayout = true;
            clones[5].ClassLayout = new ClassLayout(4, 32);
            for (var index = 0; index < MarkupFields.Length; index++)
                clones[5].Fields[index].FieldOffset = index * 4;
            clones[6].ClassLayout = null;
            clones[7].ClassLayout = null;
            clones[8].ClassLayout = null;

            foreach (var clone in clones)
            {
                targetModule.TopLevelTypes.Add(clone);
                Prepared.Add(clone, new object());
            }
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied 9 prepared native NavMesh declarations, including 7 builder call boundaries; closed list contracts remain separate.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        internal static int? GetFieldOffset(FieldDefinition field) =>
            field != null && IsPrepared(field.DeclaringType) ? field.FieldOffset : null;

        private static bool IsBuilderBoundary(MethodDefinition method) => (string)method.Name is
            "CollectSourcesInternal" or "UpdateNavMeshDataListInternal" or "Cancel" or
            "UpdateNavMeshDataAsyncListInternal" or "CollectSourcesInternal_Injected" or
            "UpdateNavMeshDataListInternal_Injected" or "UpdateNavMeshDataAsyncListInternal_Injected";

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string name)
        {
            var matches = module.TopLevelTypes.Where(type => type.FullName == Namespace + name).ToArray();
            if (matches.Length != 1)
                throw Invalid("expected exactly one source declaration for " + Namespace + name);
            return matches[0];
        }

        private static void ValidateType(TypeDefinition type, string baseName, int fields, int methods, bool sequential)
        {
            if (type.BaseType?.FullName != baseName || type.IsInterface || type.GenericParameters.Count != 0 ||
                type.DeclaringType != null || type.NestedTypes.Count != 0 || type.Interfaces.Count != 0 ||
                type.Fields.Count != fields || type.Methods.Count != methods ||
                (sequential && !type.IsSequentialLayout) ||
                type.Methods.Any(method => method.Signature == null || method.GenericParameters.Count != 0 ||
                    method.Signature.GenericParameterCount != 0))
                throw Invalid("source declaration contract does not match for " + type.FullName);
        }

        private static void ValidateField(FieldDefinition field, string name, string typeName)
        {
            if (field.Name != name || field.Signature?.FieldType.FullName != typeName || field.IsStatic ||
                field.IsLiteral || field.HasFieldMarshal || field.FieldRva != null)
                throw Invalid("source field contract does not match for " + field.FullName);
        }

        private static void ValidateEnum(TypeDefinition type, params string[] names)
        {
            if (!type.IsEnum)
                throw Invalid(type.Name + " is not an enum");
            var value = type.Fields.SingleOrDefault(field => field.Name == "value__");
            if (value == null)
                throw Invalid(type.Name + " is missing value__");
            ValidateField(value, "value__", "System.Int32");
            foreach (var name in names)
            {
                var fields = type.Fields.Where(field => field.Name == name).ToArray();
                if (fields.Length != 1 || !fields[0].IsStatic || !fields[0].IsLiteral || fields[0].Constant == null ||
                    fields[0].Signature?.FieldType.FullName != type.FullName)
                    throw Invalid(type.Name + " constant contract does not match for " + name);
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
            new InvalidDataException("Cannot prepare matched native NavMesh declarations: " + reason + ".");
    }
}
