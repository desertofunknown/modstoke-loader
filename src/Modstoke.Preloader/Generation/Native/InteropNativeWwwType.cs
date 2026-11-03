// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using ElementType = AsmResolver.PE.DotNet.Metadata.Tables.ElementType;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Modstoke.Preloader
{
    internal static class InteropNativeWwwType
    {
        internal const string AssemblyName = "UnityEngine.UnityWebRequestWWWModule";
        internal const string TypeName = "UnityEngine.WWW";
        internal const int FirstMethodToken = 0x06000001;
        internal const int MethodCount = 47;
        private static readonly Guid SourceMvid = new("51a8b350-562c-460c-a41b-4d62f6375839");
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();
        private static readonly ConditionalWeakTable<TypeDefinition, object> Sources = new();
        private static readonly (string Name, string Result, int Flags, string[] Parameters, string[] ParameterNames)[] Methods =
        [
            ("EscapeURL", "System.String", 0x0096, ["System.String"], ["s"]),
            ("EscapeURL", "System.String", 0x0096, ["System.String", "System.Text.Encoding"], ["s", "e"]),
            ("UnEscapeURL", "System.String", 0x0096, ["System.String"], ["s"]),
            ("UnEscapeURL", "System.String", 0x0096, ["System.String", "System.Text.Encoding"], ["s", "e"]),
            ("LoadFromCacheOrDownload", "UnityEngine.WWW", 0x0096, ["System.String", "System.Int32"], ["url", "version"]),
            ("LoadFromCacheOrDownload", "UnityEngine.WWW", 0x0096, ["System.String", "System.Int32", "System.UInt32"], ["url", "version", "crc"]),
            ("LoadFromCacheOrDownload", "UnityEngine.WWW", 0x0096, ["System.String", "UnityEngine.Hash128"], ["url", "hash"]),
            ("LoadFromCacheOrDownload", "UnityEngine.WWW", 0x0096, ["System.String", "UnityEngine.Hash128", "System.UInt32"], ["url", "hash", "crc"]),
            ("LoadFromCacheOrDownload", "UnityEngine.WWW", 0x0096, ["System.String", "UnityEngine.CachedAssetBundle", "System.UInt32"], ["url", "cachedBundle", "crc"]),
            (".ctor", "System.Void", 0x1886, ["System.String"], ["url"]),
            (".ctor", "System.Void", 0x1886, ["System.String", "UnityEngine.WWWForm"], ["url", "form"]),
            (".ctor", "System.Void", 0x1886, ["System.String", "System.Byte[]"], ["url", "postData"]),
            (".ctor", "System.Void", 0x1886, ["System.String", "System.Byte[]", "System.Collections.Hashtable"], ["url", "postData", "headers"]),
            (".ctor", "System.Void", 0x1886, ["System.String", "System.Byte[]", "System.Collections.Generic.Dictionary`2<System.String,System.String>"], ["url", "postData", "headers"]),
            (".ctor", "System.Void", 0x1883, ["System.String", "System.String", "UnityEngine.Hash128", "System.UInt32"], ["url", "name", "hash", "crc"]),
            ("get_assetBundle", "UnityEngine.AssetBundle", 0x0886, [], []),
            ("get_audioClip", "UnityEngine.Object", 0x0886, [], []),
            ("get_bytes", "System.Byte[]", 0x0886, [], []),
            ("get_movie", "UnityEngine.Object", 0x0886, [], []),
            ("get_size", "System.Int32", 0x0886, [], []),
            ("get_bytesDownloaded", "System.Int32", 0x0886, [], []),
            ("get_error", "System.String", 0x0886, [], []),
            ("get_isDone", "System.Boolean", 0x0886, [], []),
            ("get_progress", "System.Single", 0x0886, [], []),
            ("get_responseHeaders", "System.Collections.Generic.Dictionary`2<System.String,System.String>", 0x0886, [], []),
            ("get_data", "System.String", 0x0886, [], []),
            ("get_text", "System.String", 0x0886, [], []),
            ("CreateTextureFromDownloadedData", "UnityEngine.Texture2D", 0x0081, ["System.Boolean"], ["markNonReadable"]),
            ("get_texture", "UnityEngine.Texture2D", 0x0886, [], []),
            ("get_textureNonReadable", "UnityEngine.Texture2D", 0x0886, [], []),
            ("LoadImageIntoTexture", "System.Void", 0x0086, ["UnityEngine.Texture2D"], ["texture"]),
            ("get_threadPriority", "UnityEngine.ThreadPriority", 0x0886, [], []),
            ("set_threadPriority", "System.Void", 0x0886, ["UnityEngine.ThreadPriority"], ["value"]),
            ("get_uploadProgress", "System.Single", 0x0886, [], []),
            ("get_url", "System.String", 0x0886, [], []),
            ("get_keepWaiting", "System.Boolean", 0x08c6, [], []),
            ("Dispose", "System.Void", 0x01e6, [], []),
            ("GetAudioClipInternal", "UnityEngine.Object", 0x0083, ["System.Boolean", "System.Boolean", "System.Boolean", "UnityEngine.AudioType"], ["threeD", "stream", "compressed", "audioType"]),
            ("GetAudioClip", "UnityEngine.AudioClip", 0x0086, [], []),
            ("GetAudioClip", "UnityEngine.AudioClip", 0x0086, ["System.Boolean"], ["threeD"]),
            ("GetAudioClip", "UnityEngine.AudioClip", 0x0086, ["System.Boolean", "System.Boolean"], ["threeD", "stream"]),
            ("GetAudioClip", "UnityEngine.AudioClip", 0x0086, ["System.Boolean", "System.Boolean", "UnityEngine.AudioType"], ["threeD", "stream", "audioType"]),
            ("GetAudioClipCompressed", "UnityEngine.AudioClip", 0x0086, [], []),
            ("GetAudioClipCompressed", "UnityEngine.AudioClip", 0x0086, ["System.Boolean"], ["threeD"]),
            ("GetAudioClipCompressed", "UnityEngine.AudioClip", 0x0086, ["System.Boolean", "UnityEngine.AudioType"], ["threeD", "audioType"]),
            ("GetMovieTexture", "UnityEngine.MovieTexture", 0x0086, [], []),
            ("WaitUntilDoneIfPossible", "System.Boolean", 0x0081, [], [])
        ];

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var core = game.Assemblies.SingleOrDefault(assembly => assembly.Name == "UnityEngine.CoreModule")?.ManifestModule;
            var audio = game.Assemblies.SingleOrDefault(assembly => assembly.Name == "UnityEngine.AudioModule")?.ManifestModule;
            if (sourceModule == null || targetModule == null || core == null || audio == null || sourceModule.Mvid != SourceMvid ||
                !InteropWwwAssembly.IsPrepared(targetModule, sourceModule) ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                !TryGetRuntimeContract(out _) ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "B828F2A9F87C024616D1F82CAFC3F8AAB2AA960BD38E1A6D1FED5814C5D2CBB5") ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, "UnityEngine.CoreModule.dll"),
                    "6B465079537366D7F7933C77E3D6453BF8098CBD020A86EB66642532A9D5279A") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var existing = targetModule.GetAllTypes().Where(type => type.FullName == TypeName).ToArray();
            if (existing.Length == 1 && IsPrepared(existing[0])) return;
            if (existing.Length != 0) throw Invalid("the seeded module already contains a WWW declaration");
            var source = RequireType(sourceModule, TypeName);
            var nativeBase = RequireType(core, "UnityEngine.CustomYieldInstruction");
            if (targetModule.Name != AssemblyName + ".dll" || core.Name != "UnityEngine.CoreModule.dll" ||
                nativeBase.IsValueType || nativeBase.IsInterface || nativeBase.IsSealed ||
                nativeBase.GenericParameters.Count != 0 || nativeBase.DeclaringType != null ||
                nativeBase.BaseType?.FullName != "System.Object" || nativeBase.Fields.Count != 0 ||
                !InteropNativeMovieTextureType.IsPrepared(RequireType(audio, "UnityEngine.MovieTexture")))
                throw Invalid("the native base or prepared audio identities are unavailable");
            InteropNativeAudioType.ValidateEnum(RequireType(core, "UnityEngine.AudioType"), false);
            ValidateSource(source);
            Sources.Add(source, new object());
            foreach (var constructor in source.Methods.Where(method => method.IsConstructor))
                if (!IsSourceConstructor(constructor))
                    throw Invalid("a source constructor differs from its same-object base initialization contract");

            var cloner = new MemberCloner(targetModule);
            cloner.Include(source, true);
            var clone = cloner.Clone().GetClonedMember(source);
            clone.BaseType = nativeBase;
            // Keep the original fields and direct interface; executable members use source restoration.
            clone.Properties.Clear();
            clone.Methods.Clear();
            targetModule.TopLevelTypes.Add(clone);
            Prepared.Add(clone, new object());
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied the prepared native WWW declaration; all 47 methods remain on the managed restoration path.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        internal static bool IsSourceConstructor(MethodDefinition method)
        {
            if (method?.DeclaringType is not { } owner || !Sources.TryGetValue(owner, out _) ||
                owner.FullName != TypeName || method.DeclaringModule.Assembly.Name != AssemblyName ||
                method.DeclaringModule.Mvid != SourceMvid || method.MetadataToken.ToInt32() is < 0x0600000A or > 0x0600000F ||
                method.Name != ".ctor" || method.IsStatic || method.CilMethodBody is not { } body ||
                body.Instructions.Count < 5 || body.Instructions[0].OpCode.Code != CilCode.Ldarg_0 ||
                body.Instructions[1].OpCode.Code != CilCode.Call || body.Instructions[1].Operand is not IMethodDescriptor call ||
                body.Instructions[2].OpCode.Code != CilCode.Nop || body.Instructions[3].OpCode.Code != CilCode.Nop ||
                !IsBaseConstructor(call, method)) return false;
            return body.Instructions.Count(instruction => instruction.OpCode.Code == CilCode.Call &&
                instruction.Operand is IMethodDescriptor called && called.Name == ".ctor") == 1;
        }

        internal static bool IsBaseConstructor(IMethodDescriptor call, MethodDefinition constructor)
        {
            if (call is MethodSpecification || call.Name != ".ctor" || call.DeclaringType?.FullName != "UnityEngine.CustomYieldInstruction" ||
                !SignatureComparer.Default.Equals(call.DeclaringType, constructor.DeclaringType.BaseType) ||
                !SignatureComparer.Default.Equals(call.Signature, MethodSignature.CreateInstance(constructor.DeclaringModule.CorLibTypeFactory.Void)) ||
                call.Resolve() is not { } source || source.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringModule.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                source.MetadataToken.ToInt32() != 0x0600182C || source.Attributes != (MethodAttributes.Family | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) || source.GenericParameters.Count != 0 ||
                source.ImplAttributes != MethodImplAttributes.IL || source.CustomAttributes.Count != 0 ||
                source.CilMethodBody is not { } body || body.LocalVariables.Count != 0 || body.ExceptionHandlers.Count != 0 ||
                body.Instructions.Count != 4 || !body.Instructions.Select(instruction => instruction.OpCode.Code).SequenceEqual(
                    new[] { CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Ret }) ||
                body.Instructions[1].Operand is not IMethodDescriptor objectConstructor || objectConstructor is MethodSpecification ||
                objectConstructor.Name != ".ctor" || objectConstructor.DeclaringType?.FullName != "System.Object" ||
                !SignatureComparer.Default.Equals(objectConstructor.Signature,
                    MethodSignature.CreateInstance(source.DeclaringModule.CorLibTypeFactory.Void)))
                return false;
            return SignatureComparer.Default.Equals(source.Signature, call.Signature);
        }

        internal static MethodInfo RequireRuntimeMethod(string name)
        {
            if (!TryGetRuntimeContract(out var methods))
                throw new NotSupportedException("The runtime native WWW contract is incompatible.");
            return methods.Single(method => method.Name == name);
        }

        private static void ValidateSource(TypeDefinition source)
        {
            if (source.MetadataToken.ToInt32() != 0x02000002 || source.Attributes != (TypeAttributes.Public | TypeAttributes.BeforeFieldInit) ||
                source.BaseType?.FullName != "UnityEngine.CustomYieldInstruction" ||
                source.BaseType.Resolve()?.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringType != null || source.GenericParameters.Count != 0 || source.ClassLayout != null ||
                source.Fields.Count != 4 || source.Interfaces.Count != 1 || source.Interfaces[0].Interface?.FullName != "System.IDisposable" ||
                source.Events.Count != 0 || source.NestedTypes.Count != 0 || source.MethodImplementations.Count != 0 ||
                source.Methods.Count != MethodCount || source.Properties.Count != 18)
                throw Invalid("the source WWW declaration differs from its inspected contract");
            string[] fields = ["<threadPriority>k__BackingField", "_uwr", "_assetBundle", "_responseHeaders"];
            string[] fieldTypes = ["UnityEngine.ThreadPriority", "UnityEngine.Networking.UnityWebRequest", "UnityEngine.AssetBundle",
                "System.Collections.Generic.Dictionary`2<System.String,System.String>"];
            for (var index = 0; index < fields.Length; index++)
            {
                var field = source.Fields[index];
                if (field.MetadataToken.ToInt32() != 0x04000001 + index || field.Name != fields[index] ||
                    field.Attributes != FieldAttributes.Private || field.Signature?.FieldType.FullName != fieldTypes[index] ||
                    field.Constant != null || field.FieldOffset != null)
                    throw Invalid("a source field differs from its inspected declaration");
            }
            for (var index = 0; index < Methods.Length; index++)
            {
                var method = source.Methods[index];
                var expected = Methods[index];
                if (method.MetadataToken.ToInt32() != FirstMethodToken + index || method.Name != expected.Name ||
                    (int)method.Attributes != expected.Flags || method.ImplAttributes != MethodImplAttributes.IL ||
                    method.ImplementationMap != null || method.GenericParameters.Count != 0 || method.CilMethodBody == null ||
                    !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.GenericParameterCount != 0 ||
                    method.Signature.HasThis != (index >= 9) || method.Signature.ReturnType.FullName != expected.Result ||
                    !method.Signature.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(expected.Parameters) ||
                    !method.Parameters.Select(parameter => parameter.Name?.ToString()).SequenceEqual(expected.ParameterNames))
                    throw Invalid("a source method differs from its inspected declaration");
                for (var parameterIndex = 0; parameterIndex < method.Parameters.Count; parameterIndex++)
                {
                    var parameter = method.Parameters[parameterIndex].Definition;
                    var optional = index == 8 && parameterIndex == 2;
                    if (parameter == null || parameter.Attributes != (optional ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : 0) ||
                        optional && (parameter.Constant?.Type != ElementType.U4 || parameter.Constant.Value?.Data is not { Length: 4 } ||
                            parameter.Constant.Value.InterpretData(ElementType.U4) is not uint value || value != 0) ||
                        !optional && parameter.Constant != null)
                        throw Invalid("a source parameter differs from its inspected declaration");
                }
            }
            string[] properties = ["assetBundle", "audioClip", "bytes", "movie", "size", "bytesDownloaded", "error", "isDone",
                "progress", "responseHeaders", "data", "text", "texture", "textureNonReadable", "threadPriority", "uploadProgress", "url", "keepWaiting"];
            int[] getters = [15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28, 29, 31, 33, 34, 35];
            for (var index = 0; index < properties.Length; index++)
            {
                var property = source.Properties[index];
                if (property.MetadataToken.ToInt32() != 0x17000001 + index || property.Name != properties[index] || property.Attributes != 0 ||
                    property.GetMethod != source.Methods[getters[index]] || property.SetMethod != (index == 14 ? source.Methods[32] : null) ||
                    !SignatureComparer.Default.Equals(property.Signature,
                        PropertySignature.CreateInstance(source.Methods[getters[index]].Signature.ReturnType)))
                    throw Invalid("a source property differs from its inspected declaration");
            }
        }

        private static bool TryGetRuntimeContract(out MethodInfo[] result)
        {
            result = null;
            var registration = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection.NativeWwwRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (version == null || !version.IsLiteral || version.FieldType != typeof(int) ||
                version.GetRawConstantValue() is not int graphVersion || graphVersion != 1) return false;
            string[] names = ["RequirePublishedClass", "InitializeBase", "BindBodies", "ReadCachedAssetBundle"];
            var methods = registration.GetMethods(flags).Where(method => names.Contains(method.Name)).ToArray();
            if (methods.Length != names.Length || names.Any(name => methods.Count(method => method.Name == name) != 1)) return false;
            var resolver = methods.Single(method => method.Name == names[0]);
            var initializer = methods.Single(method => method.Name == names[1]);
            var binder = methods.Single(method => method.Name == names[2]);
            var reader = methods.Single(method => method.Name == names[3]);
            if (resolver.IsGenericMethod || resolver.ReturnType != typeof(IntPtr) || resolver.GetParameters().Length != 0 ||
                initializer.IsGenericMethod || initializer.ReturnType != typeof(void) ||
                !initializer.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Il2CppObjectBase) }) ||
                !binder.IsGenericMethodDefinition || binder.ReturnType != typeof(void) || binder.GetGenericArguments().Length != 1 ||
                !binder.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(IntPtr), typeof(Delegate[]), typeof(Type), typeof(Type) }) ||
                !reader.IsGenericMethodDefinition || reader.GetGenericArguments().Length != 1 ||
                reader.ReturnType != reader.GetGenericArguments()[0] ||
                !reader.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(IntPtr) }))
                return false;
            foreach (var method in new[] { binder, reader })
            {
                var parameter = method.GetGenericArguments()[0];
                if (parameter.GenericParameterAttributes != GenericParameterAttributes.None ||
                    !parameter.GetGenericParameterConstraints().SequenceEqual(new[] { typeof(Il2CppObjectBase) })) return false;
            }
            if (methods.Any(method => method.CallingConvention != CallingConventions.Standard ||
                method.GetParameters().Append(method.ReturnParameter).Any(parameter =>
                    parameter.Attributes != System.Reflection.ParameterAttributes.None ||
                    parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0))) return false;
            result = methods;
            return true;
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string name)
        {
            var matches = module.GetAllTypes().Where(type => type.FullName == name).ToArray();
            return matches.Length == 1 ? matches[0] : throw Invalid("expected one declaration for " + name);
        }

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static InvalidDataException Invalid(string detail) => new("Native WWW preparation failed: " + detail + ".");
    }
}

