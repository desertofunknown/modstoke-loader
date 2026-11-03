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
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Modstoke.Preloader
{
    internal static class InteropNativeStyleEnumType
    {
        internal const string AssemblyName = "UnityEngine.UIElementsModule";
        internal const string OwnerName = "UnityEngine.UIElements.StylePropertyAnimationSystem";
        internal const string TypeName = OwnerName + "+ValuesEnum";
        internal const string BaseTypeName = OwnerName + "+ValuesDiscrete`1";
        internal const int ConstructorToken = 100670307;
        internal const int UpdateAllToken = 100670305;
        internal const int UpdateOneToken = 100670306;
        private static readonly ConditionalWeakTable<TypeDefinition, object> Prepared = new();

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var sourceModule = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            var targetModule = game.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName)?.ManifestModule;
            if (sourceModule == null || targetModule == null ||
                sourceModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                !TryGetRuntimeContract(out _, out _, out _) ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, AssemblyName + ".dll"),
                    "EE6E54289BF2F8FCBAD2C494053FA6661AE4B29C0B712B80FEED0A2C159ABE67") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B") ||
                !MatchesHash(Path.Combine(Path.GetDirectoryName(options.GameAssemblyPath) ?? "", "UnityPlayer.dll"),
                    "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6"))
                return;

            var existing = targetModule.GetAllTypes().Where(type => type.FullName == TypeName).ToArray();
            if (existing.Length == 1) return;
            if (existing.Length != 0) throw Invalid("the target has duplicate ValuesEnum declarations");
            var targetOwner = RequireType(targetModule, OwnerName);
            var targetBase = RequireType(targetModule, BaseTypeName);
            var sourceOwner = RequireType(sourceModule, OwnerName);
            var source = RequireType(sourceModule, TypeName);
            if (targetOwner.DeclaringType != null || targetOwner.GenericParameters.Count != 0 ||
                targetBase.DeclaringType != targetOwner || targetBase.GenericParameters.Count != 1 ||
                targetBase.IsValueType || targetBase.IsInterface || !targetBase.IsAbstract)
                throw Invalid("the target ValuesDiscrete<T> base contract does not match");
            if (source.MetadataToken.ToInt32() != 33555247 || source.DeclaringType != sourceOwner ||
                source.Attributes != (TypeAttributes.NestedPrivate | TypeAttributes.BeforeFieldInit) ||
                source.BaseType?.ToTypeSignature() is not GenericInstanceTypeSignature sourceBase ||
                sourceBase.GenericType.FullName != BaseTypeName || sourceBase.TypeArguments.Count != 1 ||
                sourceBase.TypeArguments[0].FullName != "System.Int32" ||
                source.GenericParameters.Count != 0 || source.ClassLayout != null || source.Fields.Count != 0 ||
                source.Methods.Count != 3 || source.NestedTypes.Count != 0 || source.Interfaces.Count != 0 ||
                source.Properties.Count != 0 || source.Events.Count != 0 || source.MethodImplementations.Count != 0)
                throw Invalid("the source ValuesEnum declaration contract does not match");
            ValidateNativeAttributes(source, source.FullName);
            var overrides = MethodAttributes.Family | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            ValidateMethod(source.Methods[0], UpdateAllToken, "UpdateComputedStyle", overrides);
            ValidateMethod(source.Methods[1], UpdateOneToken, "UpdateComputedStyle", overrides, "System.Int32");
            ValidateMethod(source.Methods[2], ConstructorToken, ".ctor", MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName);

            var cloner = new MemberCloner(targetModule);
            cloner.Include(source);
            var clone = cloner.Clone().GetClonedMember(source);
            // The source parent was not cloned. Bind inheritance to the existing target definition explicitly.
            clone.BaseType = new GenericInstanceTypeSignature(targetBase, false,
                targetModule.CorLibTypeFactory.Int32).ToTypeDefOrRef();
            clone.Methods.Clear();
            targetOwner.NestedTypes.Add(clone);
            Prepared.Add(clone, new object());
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Supplied the prepared native ValuesEnum declaration; both overrides remain on the managed restoration path.");
        }

        internal static bool IsPrepared(TypeDefinition type) => type != null && Prepared.TryGetValue(type, out _);

        internal static MethodInfo RequireRuntimeMethod(string name)
        {
            if (!TryGetRuntimeContract(out var resolver, out var initializer, out var binder))
                throw new NotSupportedException("The runtime native ValuesEnum contract is incompatible.");
            return name switch
            {
                "RequirePublishedClass" => resolver,
                "Initialize" => initializer,
                "BindOverrides" => binder,
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
        }

        private static bool TryGetRuntimeContract(out MethodInfo resolver, out MethodInfo initializer, out MethodInfo binder)
        {
            resolver = initializer = binder = null;
            var registration = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection.NativeStyleEnumRegistration");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var version = registration?.GetField("PublishedGraphVersion", flags);
            if (version == null || !version.IsLiteral || version.FieldType != typeof(int) ||
                version.GetRawConstantValue() is not int graphVersion || graphVersion != 1)
                return false;
            var methods = registration.GetMethods(flags);
            var resolvers = methods.Where(method => method.Name == "RequirePublishedClass").ToArray();
            var initializers = methods.Where(method => method.Name == "Initialize").ToArray();
            var binders = methods.Where(method => method.Name == "BindOverrides").ToArray();
            if (resolvers.Length != 1 || initializers.Length != 1 || binders.Length != 1) return false;
            resolver = resolvers[0];
            initializer = initializers[0];
            binder = binders[0];
            var arguments = binder.GetGenericArguments();
            if (!HasRuntimeSignature(resolver, typeof(IntPtr)) || resolver.ContainsGenericParameters ||
                !HasRuntimeSignature(initializer, typeof(void), typeof(Il2CppObjectBase)) || initializer.ContainsGenericParameters ||
                !binder.IsGenericMethodDefinition || arguments.Length != 1 ||
                arguments[0].GenericParameterAttributes != GenericParameterAttributes.None ||
                !arguments[0].GetGenericParameterConstraints().SequenceEqual(new[] { typeof(Il2CppObjectBase) }))
                return false;
            return HasRuntimeSignature(binder, typeof(void), typeof(IntPtr),
                typeof(Action<>).MakeGenericType(arguments[0]), typeof(Action<,>).MakeGenericType(arguments[0], typeof(int)));
        }

        private static bool HasRuntimeSignature(MethodInfo method, Type result, params Type[] parameters) =>
            method.ReturnType == result && method.CallingConvention == CallingConventions.Standard &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters) &&
            method.GetParameters().Append(method.ReturnParameter).All(parameter =>
                parameter.Attributes == System.Reflection.ParameterAttributes.None &&
                parameter.GetRequiredCustomModifiers().Length == 0 && parameter.GetOptionalCustomModifiers().Length == 0);

        private static void ValidateMethod(MethodDefinition method, int token, string name,
            MethodAttributes attributes, params string[] parameters)
        {
            if (method.MetadataToken.ToInt32() != token || method.Name != name || method.Attributes != attributes ||
                method.ImplAttributes != 0 || method.CilMethodBody == null || method.GenericParameters.Count != 0 ||
                method.ImplementationMap != null || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                method.Signature.ReturnType.FullName != "System.Void" ||
                !method.Signature.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(parameters))
                throw Invalid("the source method contract does not match for " + method.FullName);
            ValidateNativeAttributes(method, method.FullName);
            foreach (var parameter in method.ParameterDefinitions) ValidateNativeAttributes(parameter, method.FullName);
        }

        private static void ValidateNativeAttributes(IHasCustomAttribute member, string name)
        {
            if (member.ExtractToken() != 0 || member.ExtractOffset() != 0 || member.ExtractRva() != 0 ||
                member.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.Name == "TokenAttribute" ||
                    attribute.Constructor?.DeclaringType?.Name == "AddressAttribute"))
                throw Invalid("unexpected native token or address attributes on " + name);
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string name)
        {
            var matches = module.GetAllTypes().Where(type => type.FullName == name).ToArray();
            if (matches.Length != 1) throw Invalid("expected exactly one declaration for " + name);
            return matches[0];
        }

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static InvalidDataException Invalid(string reason) =>
            new InvalidDataException("Cannot prepare the matched native ValuesEnum declaration: " + reason + ".");
    }
}
