// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropRectangularArraySupport
    {
        private static readonly ConditionalWeakTable<InteropRewriteContext, State> States = new();

        private sealed class State
        {
            internal readonly Dictionary<string, Wrapper> Wrappers = new(StringComparer.Ordinal);
            internal readonly HashSet<string> OpaqueShapes = new(System.StringComparer.Ordinal);
            internal bool BodiesReady;
            internal string UnavailableReason;
        }

        internal sealed class Wrapper
        {
            internal TypeDefinition Type;
            internal TypeSignature Element;
            internal InteropTypeContext ElementContext;
            internal InteropAssemblyContext AssemblyContext;
            internal bool IsReferenceElement;
            internal int Rank;
            internal MethodDefinition PointerConstructor;
            internal MethodDefinition LengthConstructor;
            internal MethodDefinition BoundsConstructor;
            internal MethodDefinition Get;
            internal MethodDefinition Set;
            internal MethodDefinition AddressGet;
            internal MethodDefinition AddressSet;
        }

        internal static bool TryResolve(InteropRewriteContext context, InteropRuntimeReferences imports,
            ArrayBaseTypeSignature array, out TypeSignature rewritten, out string reason)
        {
            rewritten = null;
            if (!CanRepresent(context, array, out reason))
                return false;
            if (!TryDescribeElement(context, array.BaseType, out var element, out var owner, out var key, out reason))
                return false;
            var state = States.GetValue(context, _ => new State());
            key += ":rank=" + array.Rank;
            if (!state.Wrappers.TryGetValue(key, out var wrapper))
            {
                wrapper = InteropRectangularArrayRuntimeEmitter.Declare(context, element, owner,
                    array.BaseType, RewriteElement(context, owner, array.BaseType), key, array.Rank);
                state.Wrappers.Add(key, wrapper);
                if (state.BodiesReady)
                    InteropRectangularArrayRuntimeEmitter.Fill(context, wrapper);
            }
            rewritten = imports.Module.DefaultImporter.ImportTypeSignature(wrapper.Type.ToTypeSignature());
            return true;
        }

        internal static bool CanRepresent(InteropRewriteContext context, ArrayBaseTypeSignature array, out string reason)
        {
            reason = null;
            if (array is SzArrayTypeSignature || array.Rank < 1 || array.Rank > 32)
            {
                reason = "unsupported-rectangular-rank: Expected a nonvector rank from 1 through 32.";
                return false;
            }
            if (!TryDescribeElement(context, array.BaseType, out _, out _, out _, out reason))
                return false;
            // Do not use the restoration plan here: it resolves arrays itself, and declarations are
            // requested before restored types, native method bodies and value fields exist.
            var corlib = context.TryGetAssemblyByName("mscorlib");
            var nativeArray = corlib?.TryGetTypeByName("System.Array");
            if (nativeArray?.OriginalType == null ||
                corlib.TryGetTypeByName("System.Type")?.OriginalType == null ||
                corlib.TryGetTypeByName("System.Object")?.OriginalType == null ||
                !HasNativeArraySurface(nativeArray))
            {
                reason = "rectangular-native-surface-unavailable: Native System.Array shape/indexing APIs or element identity are absent.";
                return false;
            }
            if (States.TryGetValue(context, out var state) && state.UnavailableReason != null)
            {
                reason = state.UnavailableReason;
                return false;
            }
            return true;
        }

        private static InteropTypeContext FindElement(InteropRewriteContext context, TypeSignature element)
        {
            while (element is ArrayBaseTypeSignature array)
                element = array.BaseType;
            if (element is GenericInstanceTypeSignature generic)
                element = generic.GenericType.ToTypeSignature();
            if (element is not (CorLibTypeSignature or TypeDefOrRefSignature))
                return null;
            var assemblyName = InteropRestoredTypeSupport.GetAssemblyName(element);
            if (element is CorLibTypeSignature)
                assemblyName = "mscorlib";
            var candidate = context.TryGetTypeByName(assemblyName, element.FullName);
            if (candidate?.OriginalType != null)
                return candidate;
            if (assemblyName != "UnityEngine")
                return null;
            var matches = context.Assemblies.Where(assembly => assembly.OriginalAssembly.Name.StartsWith("UnityEngine"))
                .Select(assembly => assembly.TryGetTypeByName(element.FullName))
                .Where(type => type?.OriginalType != null).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static bool TryDescribeElement(InteropRewriteContext context, TypeSignature source,
            out InteropTypeContext element, out InteropAssemblyContext owner, out string key, out string reason)
        {
            element = FindElement(context, source);
            owner = null;
            key = null;
            reason = "unsupported-rectangular-element: A closed metadata-backed native element is required; " +
                "open generics, pointers and restored-only types are not supported.";
            const string ownerReason = "unsupported-rectangular-element-owner: No element assembly can reference the complete closed native shape without adding a reverse dependency.";
            string nestedReason = null;
            var dependencies = new HashSet<InteropAssemblyContext>();
            if (!Collect(source, dependencies, out key))
            {
                reason = nestedReason ?? reason;
                return false;
            }
            // Place the helper in an existing dependency owner. For List<GameType>, this is
            // the game assembly, not corlib; generated corlib must never refer back to the game.
            owner = FindOwner(element, dependencies);
            if (owner == null)
            {
                reason = ownerReason;
                return false;
            }
            reason = null;
            return true;

            bool Collect(TypeSignature type, HashSet<InteropAssemblyContext> required, out string identity)
            {
                identity = null;
                if (type is ArrayBaseTypeSignature array)
                {
                    if (array.Rank < 1 || array.Rank > 32)
                    {
                        nestedReason = "unsupported-rectangular-rank: Expected a rank from 1 through 32 at every array level.";
                        return false;
                    }
                    var nestedDependencies = new HashSet<InteropAssemblyContext>();
                    if (!Collect(array.BaseType, nestedDependencies, out var nestedIdentity)) return false;
                    // An enclosing generic owner cannot make an otherwise unrepresentable inner helper valid.
                    if (array is not SzArrayTypeSignature &&
                        FindOwner(FindElement(context, array.BaseType), nestedDependencies) == null)
                    {
                        nestedReason = ownerReason;
                        return false;
                    }
                    required.UnionWith(nestedDependencies);
                    identity = (array is SzArrayTypeSignature ? "vector" : "array:rank=" + array.Rank) +
                        "<" + nestedIdentity + ">";
                    return true;
                }
                var native = FindElement(context, type);
                if (native?.OriginalType == null || native.ComputedTypeSpecifics is
                        InteropTypeContext.TypeSpecifics.NotComputed or InteropTypeContext.TypeSpecifics.Computing ||
                    native.OriginalType.FullName is "System.Void" or "System.TypedReference")
                    return false;
                var definition = native.OriginalType;
                required.Add(native.AssemblyContext);
                identity = native.AssemblyContext.OriginalAssembly.FullName + ":" + definition.FullName;
                if (type is not GenericInstanceTypeSignature generic)
                    return definition.GenericParameters.Count == 0;
                if (generic.TypeArguments.Count == 0 || generic.TypeArguments.Count != definition.GenericParameters.Count)
                    return false;
                if (definition.FullName == "System.Nullable`1" && native.AssemblyContext == context.CorLib)
                {
                    var value = generic.TypeArguments.Count == 1 ? generic.TypeArguments[0] : null;
                    var underlying = value == null || value is ArrayBaseTypeSignature ? null : FindElement(context, value);
                    if (underlying?.OriginalType == null || !InteropTypeReferences.IsValueType(underlying.OriginalType) ||
                        underlying.OriginalType.FullName == "System.Nullable`1")
                    {
                        nestedReason = "unsupported-rectangular-nullable: A closed non-nullable native value argument is required.";
                        return false;
                    }
                }
                var arguments = new string[generic.TypeArguments.Count];
                for (var index = 0; index < arguments.Length; index++)
                    if (!Collect(generic.TypeArguments[index], required, out arguments[index])) return false;
                identity += "<" + string.Join(";", arguments) + ">";
                return true;
            }

            InteropAssemblyContext FindOwner(InteropTypeContext definition, HashSet<InteropAssemblyContext> required) =>
                required.OrderBy(candidate => candidate == definition.AssemblyContext ? 0 : 1)
                    .ThenBy(candidate => candidate.OriginalAssembly.Name.ToString(), StringComparer.Ordinal)
                    .FirstOrDefault(candidate => required.All(dependency => CanReference(candidate, dependency)));

            bool CanReference(InteropAssemblyContext from, InteropAssemblyContext to)
            {
                if (from == to || to == context.CorLib) return true;
                var visited = new HashSet<InteropAssemblyContext>();
                var pending = new Stack<InteropAssemblyContext>();
                pending.Push(from);
                while (pending.Count != 0)
                {
                    var next = pending.Pop();
                    if (!visited.Add(next)) continue;
                    foreach (var reference in next.OriginalAssembly.ManifestModule.AssemblyReferences)
                    {
                        var dependency = context.TryGetAssemblyByName(reference.Name);
                        if (dependency == to) return true;
                        if (dependency != null && dependency != context.CorLib) pending.Push(dependency);
                    }
                }
                return false;
            }
        }

        private static TypeSignature RewriteElement(InteropRewriteContext context,
            InteropAssemblyContext owner, TypeSignature source)
        {
            if (source is ArrayBaseTypeSignature)
                return InteropTypeReferences.Rewrite(owner, source);
            var native = FindElement(context, source);
            var importer = owner.NewAssembly.ManifestModule.DefaultImporter;
            if (source is GenericInstanceTypeSignature generic)
                return new GenericInstanceTypeSignature(importer.ImportType(native.NewType),
                    InteropTypeReferences.IsValueType(native.NewType),
                    generic.TypeArguments.Select(argument => RewriteElement(context, owner, argument)).ToArray());
            var runtime = native.AssemblyContext == context.CorLib
                ? typeof(string).Assembly.GetType(native.OriginalType.FullName) : null;
            return runtime != null && (runtime.IsPrimitive || runtime == typeof(string))
                ? importer.ImportTypeSignature(runtime) : importer.ImportTypeSignature(native.NewType.ToTypeSignature());
        }

        internal static bool IsReferenceElement(InteropRewriteContext context, TypeSignature element) =>
            element is ArrayBaseTypeSignature ||
            FindElement(context, element) is { OriginalType: { } original } &&
            !InteropTypeReferences.IsValueType(original);

        internal static TypeSignature RewriteNativeOrOpaque(InteropRewriteContext context, InteropRuntimeReferences imports,
            ArrayBaseTypeSignature array)
        {
            if (TryResolve(context, imports, array, out var rewritten, out var reason))
                return rewritten;
            // Preserve existing invocation wrappers without claiming indexed access for unsupported shapes.
            if (States.GetValue(context, _ => new State()).OpaqueShapes.Add(array.FullName))
                InteropGenerationDiagnostics.Log(BepInEx.Logging.LogLevel.Warning,
                    "Native array remains an opaque object wrapper: " + array.FullName + ". " + reason);
            return imports.Il2CppObjectBase;
        }

        // Call once after native method bodies are generated, before restoration. New restoration-only
        // shapes are completed immediately thereafter; signatures requested by earlier passes are stable.
        internal static void Complete(InteropRewriteContext context)
        {
            var state = States.GetValue(context, _ => new State());
            if (state.BodiesReady)
                return;
            if (!HasGeneratedArraySurface(context))
                state.UnavailableReason = "rectangular-native-surface-unavailable: Native System.Array bodies were not generated.";
            foreach (var wrapper in state.Wrappers.Values)
            {
                if (state.UnavailableReason == null)
                    InteropRectangularArrayRuntimeEmitter.Fill(context, wrapper);
                else
                    InteropRectangularArrayRuntimeEmitter.FillUnavailable(wrapper, state.UnavailableReason);
            }
            state.BodiesReady = true;
        }

        internal static Wrapper Find(InteropRewriteContext context, TypeSignature rewritten)
        {
            if (!States.TryGetValue(context, out var state))
                return null;
            return state.Wrappers.Values.FirstOrDefault(wrapper =>
                SignatureComparer.Default.Equals(wrapper.Type.ToTypeSignature(), rewritten));
        }

        internal static MethodDefinition NativeMethod(InteropRewriteContext context, string name,
            bool isStatic, params string[] parameters)
        {
            var method = context.CorLib.GetTypeByName("System.Array").Methods.SingleOrDefault(candidate =>
                Matches(candidate.OriginalMethod, name, isStatic, parameters));
            if (method?.NonGenericMethodInfoPointerField == null ||
                method.NewMethod.CilMethodBody?.Instructions.Count is not > 0)
                throw new InvalidOperationException("Rectangular array support requires a generated native System.Array." + name);
            return method.NewMethod;
        }

        private static bool HasNativeArraySurface(InteropTypeContext type) =>
            type.Has("CreateInstance", true, "System.Type", "System.Int32[]", "System.Int32[]") &&
            type.Has("GetValue", false, "System.Int32[]") &&
            type.Has("SetValue", false, "System.Object", "System.Int32[]") &&
            type.Has("get_Length", false) && type.Has("get_Rank", false) &&
            type.Has("GetLength", false, "System.Int32") && type.Has("GetLowerBound", false, "System.Int32") &&
            type.Has("GetUpperBound", false, "System.Int32");

        private static bool HasGeneratedArraySurface(InteropRewriteContext context)
        {
            var type = context.TryGetAssemblyByName("mscorlib")?.TryGetTypeByName("System.Array");
            return type?.OriginalType != null && HasNativeArraySurface(type) && type.OriginalType.Methods.Where(method =>
                    Matches(method, "CreateInstance", true, new[] { "System.Type", "System.Int32[]", "System.Int32[]" }) ||
                    Matches(method, "GetValue", false, new[] { "System.Int32[]" }) ||
                    Matches(method, "SetValue", false, new[] { "System.Object", "System.Int32[]" }) ||
                    Matches(method, "get_Length", false, System.Array.Empty<string>()) ||
                    Matches(method, "get_Rank", false, System.Array.Empty<string>()) ||
                    Matches(method, "GetLength", false, new[] { "System.Int32" }) ||
                    Matches(method, "GetLowerBound", false, new[] { "System.Int32" }) ||
                    Matches(method, "GetUpperBound", false, new[] { "System.Int32" }))
                .All(method => type.TryGetMethodByOldMethod(method) is { NonGenericMethodInfoPointerField: not null } generated &&
                    generated.NewMethod.CilMethodBody?.Instructions.Count > 0);
        }

        private static bool Has(this InteropTypeContext type, string name, bool isStatic, params string[] parameters) =>
            type.OriginalType.Methods.Any(method => Matches(method, name, isStatic, parameters));

        private static bool Matches(MethodDefinition method, string name, bool isStatic, string[] parameters) =>
            method.Name == name && method.IsStatic == isStatic && method.GenericParameters.Count == 0 &&
            method.Signature != null && method.Signature.ReturnType.FullName == (name switch
            {
                "CreateInstance" => "System.Array",
                "GetValue" => "System.Object",
                "SetValue" => "System.Void",
                _ => "System.Int32"
            }) && method.Signature.ParameterTypes.Select(type => type.FullName).SequenceEqual(parameters);
    }
}
