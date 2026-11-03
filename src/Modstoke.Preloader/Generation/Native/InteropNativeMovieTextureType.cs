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
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Modstoke.Preloader
{
    internal static class InteropNativeMovieTextureType
    {
        internal const string AssemblyName = "UnityEngine.AudioModule";
        internal const string TypeName = "UnityEngine.MovieTexture";
        internal const int FirstMethodToken = 0x06000146;
        internal const int ConstructorToken = 0x06000147;
        internal const int MethodCount = 11;
        private static readonly Guid SourceMvid = new("7eea753e-2c77-4819-ad69-c9242cbac7a4");
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var core = game.Assemblies.SingleOrDefault(assembly => assembly.Name == "UnityEngine.CoreModule")?.ManifestModule;
            if (sourceModule == null || targetModule == null || core == null || sourceModule.Mvid != SourceMvid ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                !TryGetRuntimeContract(out _, out _, out _) ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "08D16C0CE6701F770C3050DF90183B843C9D61AC25506FB8ED69E2C50B72B7E8") ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, "UnityEngine.CoreModule.dll"),
                    "6B465079537366D7F7933C77E3D6453BF8098CBD020A86EB66642532A9D5279A") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var existing = targetModule.GetAllTypes().Where(type => type.FullName == TypeName).ToArray();
            if (existing.Length == 1) return;
            if (existing.Length != 0) throw Invalid("the target has duplicate MovieTexture declarations");
            var source = RequireType(sourceModule, TypeName);
            var texture = RequireType(core, "UnityEngine.Texture");
            var audioClip = RequireType(targetModule, "UnityEngine.AudioClip");
            if (targetModule.Name != AssemblyName + ".dll" || core.Name != "UnityEngine.CoreModule.dll" ||
                texture.IsValueType || texture.IsInterface || texture.IsSealed || texture.GenericParameters.Count != 0 ||
                texture.DeclaringType != null || texture.BaseType?.FullName != "UnityEngine.Object" ||
                audioClip.IsValueType || audioClip.IsInterface || audioClip.GenericParameters.Count != 0 || audioClip.DeclaringType != null)
                throw Invalid("the original Texture base or AudioClip return identity is unavailable");
            ValidateSource(source);

            var cloner = new MemberCloner(targetModule);
            cloner.Include(source, true);
            var clone = cloner.Clone().GetClonedMember(source);
            clone.BaseType = texture;
            // Native metadata owns these signatures; all executable bodies come from source restoration.
            clone.Properties.Clear();
            clone.Methods.Clear();
            targetModule.TopLevelTypes.Add(clone);
            Prepared.Add(clone, new object());
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied the prepared native MovieTexture declaration; all eleven methods remain on the managed restoration path.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        internal static bool IsSourceConstructor(MethodDefinition method)
        {
            if (method?.DeclaringType?.FullName != TypeName || method.DeclaringModule.Assembly.Name != AssemblyName ||
                method.DeclaringModule.Mvid != SourceMvid || method.MetadataToken.ToInt32() != ConstructorToken ||
                method.Attributes != (MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) ||
                method.ImplAttributes != MethodImplAttributes.IL || method.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(method.Signature, MethodSignature.CreateInstance(method.DeclaringModule.CorLibTypeFactory.Void)) ||
                method.CilMethodBody is not { } body || body.LocalVariables.Count != 0 || body.ExceptionHandlers.Count != 0 ||
                body.Instructions.Count != 5) return false;
            return body.Instructions.Select(instruction => instruction.OpCode.Code).SequenceEqual(
                    new[] { CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Nop, CilCode.Ret }) &&
                body.Instructions[1].Operand is IMethodDescriptor call && IsTextureBaseConstructor(call, method);
        }

        internal static bool IsTextureBaseConstructor(IMethodDescriptor call, MethodDefinition constructor)
        {
            if (call is MethodSpecification || call.Name != ".ctor" || call.DeclaringType?.FullName != "UnityEngine.Texture" ||
                !SignatureComparer.Default.Equals(call.DeclaringType, constructor.DeclaringType.BaseType) ||
                !SignatureComparer.Default.Equals(call.Signature, MethodSignature.CreateInstance(constructor.DeclaringModule.CorLibTypeFactory.Void)) ||
                call.Resolve() is not { } source || source.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringModule.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                source.MetadataToken.ToInt32() != 0x06001205 || source.Attributes != (MethodAttributes.Family | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) || source.GenericParameters.Count != 0 ||
                source.ImplAttributes != MethodImplAttributes.IL || source.CilMethodBody == null || source.CustomAttributes.Count != 0)
                return false;
            return SignatureComparer.Default.Equals(source.Signature, call.Signature);
        }

        internal static MethodInfo RequireRuntimeMethod(string name)
        {
            if (!TryGetRuntimeContract(out var resolver, out var initializer, out var binder))
                throw new NotSupportedException("The runtime native MovieTexture contract is incompatible.");
            return name switch
            {
                "RequirePublishedClass" => resolver,
                "InitializeTextureBase" => initializer,
                "BindBodies" => binder,
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
        }

        private static void ValidateSource(TypeDefinition source)
        {
            if (source.MetadataToken.ToInt32() != 0x0200001E || source.Attributes != (TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit) ||
                source.BaseType?.FullName != "UnityEngine.Texture" || source.BaseType.Resolve()?.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringType != null || source.GenericParameters.Count != 0 || source.ClassLayout != null ||
                source.Fields.Count != 0 || source.Interfaces.Count != 0 || source.Events.Count != 0 || source.NestedTypes.Count != 0 ||
                source.MethodImplementations.Count != 0 || source.Methods.Count != MethodCount || source.Properties.Count != 5)
                throw Invalid("the source MovieTexture declaration differs from its inspected contract");
            string[] names = ["FeatureRemoved", ".ctor", "Play", "Stop", "Pause", "get_audioClip", "get_loop", "set_loop",
                "get_isPlaying", "get_isReadyToPlay", "get_duration"];
            string[] results = ["System.Void", "System.Void", "System.Void", "System.Void", "System.Void", "UnityEngine.AudioClip",
                "System.Boolean", "System.Void", "System.Boolean", "System.Boolean", "System.Single"];
            for (var index = 0; index < names.Length; index++)
            {
                var method = source.Methods[index];
                var attributes = MethodAttributes.HideBySig | (index < 2 ? MethodAttributes.Private : MethodAttributes.Public);
                if (index == 0) attributes |= MethodAttributes.Static;
                if (index == 1) attributes |= MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName;
                if (index >= 5) attributes |= MethodAttributes.SpecialName;
                if (method.MetadataToken.ToInt32() != FirstMethodToken + index || method.Name != names[index] || method.Attributes != attributes ||
                    method.ImplAttributes != MethodImplAttributes.IL || method.ImplementationMap != null || method.GenericParameters.Count != 0 ||
                    !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.GenericParameterCount != 0 ||
                    method.Signature.HasThis != (index != 0) || method.Signature.ReturnType.FullName != results[index] ||
                    method.Signature.ParameterTypes.Count != (index == 7 ? 1 : 0) ||
                    index == 7 && method.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                    method.CilMethodBody == null || method.CilMethodBody.ExceptionHandlers.Count != 0)
                    throw Invalid("a source MovieTexture method differs from its inspected declaration");
            }
            if (!IsSourceConstructor(source.Methods[1])) throw Invalid("the source Texture constructor call differs from its inspected contract");
            string[] properties = ["audioClip", "loop", "isPlaying", "isReadyToPlay", "duration"];
            int[] getters = [5, 6, 8, 9, 10];
            for (var index = 0; index < properties.Length; index++)
            {
                var property = source.Properties[index];
                if (property.MetadataToken.ToInt32() != 0x1700006B + index || property.Name != properties[index] || property.Attributes != 0 ||
                    property.GetMethod != source.Methods[getters[index]] || property.SetMethod != (index == 1 ? source.Methods[7] : null) ||
                    !SignatureComparer.Default.Equals(property.Signature,
                        PropertySignature.CreateInstance(source.Methods[getters[index]].Signature.ReturnType)))
                    throw Invalid("a source MovieTexture property differs from its inspected declaration");
            }
        }

        private static bool TryGetRuntimeContract(out MethodInfo resolver, out MethodInfo initializer, out MethodInfo binder)
        {
            resolver = initializer = binder = null;
            var registration = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection.NativeMovieTextureRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (version == null || !version.IsLiteral || version.FieldType != typeof(int) || version.GetRawConstantValue() is not int graphVersion || graphVersion != 1)
                return false;
            var methods = registration.GetMethods(flags);
            var resolvers = methods.Where(method => method.Name == "RequirePublishedClass").ToArray();
            var initializers = methods.Where(method => method.Name == "InitializeTextureBase").ToArray();
            var binders = methods.Where(method => method.Name == "BindBodies").ToArray();
            if (resolvers.Length != 1 || initializers.Length != 1 || binders.Length != 1) return false;
            resolver = resolvers[0];
            initializer = initializers[0];
            binder = binders[0];
            if (resolver.IsGenericMethod || resolver.ReturnType != typeof(IntPtr) || resolver.GetParameters().Length != 0 ||
                initializer.IsGenericMethod || initializer.ReturnType != typeof(void) ||
                !initializer.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Il2CppObjectBase) }) ||
                !binder.IsGenericMethodDefinition || binder.ReturnType != typeof(void) || binder.GetGenericArguments().Length != 1)
                return false;
            var self = binder.GetGenericArguments()[0];
            var action = typeof(Action<>).MakeGenericType(self);
            var boolean = typeof(Func<,>).MakeGenericType(self, typeof(bool));
            Type[] expected = [typeof(IntPtr), typeof(Action), action, action, action, action,
                typeof(Func<,>).MakeGenericType(self, typeof(Il2CppObjectBase)), boolean,
                typeof(Action<,>).MakeGenericType(self, typeof(bool)), boolean, boolean,
                typeof(Func<,>).MakeGenericType(self, typeof(float))];
            return self.GenericParameterAttributes == GenericParameterAttributes.None &&
                self.GetGenericParameterConstraints().SequenceEqual(new[] { typeof(Il2CppObjectBase) }) &&
                binder.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected) &&
                new[] { resolver, initializer, binder }.All(method => method.CallingConvention == CallingConventions.Standard &&
                    method.GetParameters().Append(method.ReturnParameter).All(parameter => parameter.Attributes == ParameterAttributes.None &&
                        parameter.GetRequiredCustomModifiers().Length == 0 && parameter.GetOptionalCustomModifiers().Length == 0));
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

        private static InvalidDataException Invalid(string message) => new("Native MovieTexture preparation failed: " + message + ".");
    }
}
