// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static partial class InteropNativeEventDebuggerTypes
    {
        private const string ReplayDisplayName = "EventDebugger+<>c__DisplayClass34_0";
        private const string ReplayAwaitName = ReplayDisplayName + "+<<DoReplayEvents>g__AwaitForNextEvent|1>d";
        private const string ReplayDoName = "EventDebugger+<DoReplayEvents>d__34";
        private const string ReplayName = "EventDebugger+<ReplayEvents>d__32";
        private static readonly string[] ReplayTypeNames =
        {
            ReplayDisplayName, ReplayAwaitName, ReplayDoName, ReplayName
        };
        private static readonly string[] ReplayInterfaces =
        {
            "System.Collections.Generic.IEnumerator`1<System.Object>",
            "System.Collections.IEnumerator", "System.IDisposable"
        };

        internal static bool IsReplayIterator(TypeDefinition type) => IsPrepared(type) &&
            type.FullName is Namespace + ReplayAwaitName or Namespace + ReplayDoName or Namespace + ReplayName;

        internal static int ReplayConstructorToken(TypeDefinition source) => source?.FullName switch
        {
            Namespace + ReplayDisplayName => 0x06000CED,
            Namespace + ReplayAwaitName => 0x06000CEF,
            Namespace + ReplayDoName => 0x06000CF5,
            Namespace + ReplayName => 0x06000CFB,
            _ => 0
        };

        private static TypeDefinition[] RequireReplayTypes(ModuleDefinition module, TypeDefinition owner)
        {
            var types = module.GetAllTypes().ToArray();
            var replay = new TypeDefinition[ReplayTypeNames.Length];
            for (var index = 0; index < replay.Length; index++)
            {
                var matches = types.Where(type => type.FullName == Namespace + ReplayTypeNames[index]).ToArray();
                if (matches.Length != 1)
                    throw Invalid("expected exactly one source declaration for " + Namespace + ReplayTypeNames[index]);
                replay[index] = matches[0];
            }

            ValidateReplayType(replay[0], owner, 0x020001AE, 2, false);
            ValidateReplayType(replay[1], replay[0], 0x020001AF, 8, true);
            ValidateReplayType(replay[2], owner, 0x020001B0, 13, true);
            ValidateReplayType(replay[3], owner, 0x020001B1, 6, true);
            if (replay[0].NestedTypes[0] != replay[1])
                throw Invalid("the replay await iterator has an unexpected owner");

            ValidateReplayFields(replay[0], 0x04000621,
                ("sortedEvents", "System.Collections.Generic.List`1<" + Namespace + "EventDebuggerEventRecord>", true),
                ("<>4__this", Namespace + "EventDebugger", true));
            ValidateReplayFields(replay[1], 0x04000623,
                ("<>1__state", "System.Int32", false),
                ("<>2__current", "System.Object", false),
                ("currentIndex", "System.Int32", true),
                ("<>4__this", Namespace + ReplayDisplayName, true),
                ("<deltaTimestampMs>5__1", "System.Int64", false),
                ("<timeMs>5__2", "System.Single", false),
                ("<time>5__3", "System.Int64", false),
                ("<delta>5__4", "System.Int64", false));
            ValidateReplayFields(replay[2], 0x0400062B,
                ("<>1__state", "System.Int32", false),
                ("<>2__current", "System.Object", false),
                ("eventBases", "System.Collections.Generic.IEnumerable`1<" + Namespace + "EventDebuggerEventRecord>", true),
                ("refreshList", "System.Action`2<System.Int32, System.Int32>", true),
                ("<>4__this", Namespace + "EventDebugger", true),
                ("<>8__1", Namespace + ReplayDisplayName, false),
                ("<sortedEventsCount>5__2", "System.Int32", false),
                ("<i>5__3", "System.Int32", false),
                ("<eventBase>5__4", Namespace + "EventDebuggerEventRecord", false),
                ("<newEvent>5__5", "UnityEngine.Event", false),
                ("<await>5__6", "System.Collections.IEnumerator", false),
                ("<awaitSkipped>5__7", "System.Collections.IEnumerator", false),
                ("<awaitSkipped>5__8", "System.Collections.IEnumerator", false));
            ValidateReplayFields(replay[3], 0x04000638,
                ("<>1__state", "System.Int32", false),
                ("<>2__current", "System.Object", false),
                ("eventBases", "System.Collections.Generic.IEnumerable`1<" + Namespace + "EventDebuggerEventRecord>", true),
                ("refreshList", "System.Action`2<System.Int32, System.Int32>", true),
                ("<>4__this", Namespace + "EventDebugger", true),
                ("<doReplay>5__1", "System.Collections.IEnumerator", false));

            ValidateReplayMethod(replay[0].Methods[1], 0x06000CEE, "<DoReplayEvents>g__AwaitForNextEvent|1",
                MethodAttributes.Assembly | MethodAttributes.HideBySig, "System.Collections.IEnumerator", "System.Int32");
            return replay;
        }

        private static void ValidateReplayType(TypeDefinition type, TypeDefinition owner, int token, int fields,
            bool iterator)
        {
            if (type.MetadataToken.ToInt32() != token || type.DeclaringType != owner ||
                type.Attributes != (TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit) ||
                type.BaseType?.FullName != "System.Object" || type.GenericParameters.Count != 0 || type.ClassLayout != null ||
                type.Fields.Count != fields || type.Methods.Count != (iterator ? 6 : 2) ||
                type.NestedTypes.Count != (iterator ? 0 : 1) || type.Properties.Count != (iterator ? 2 : 0) ||
                type.Events.Count != 0 || type.MethodImplementations.Count != (iterator ? 5 : 0) ||
                !type.Interfaces.Select(implementation => implementation.Interface?.FullName)
                    .SequenceEqual(iterator ? ReplayInterfaces : System.Array.Empty<string>()))
                throw Invalid("source replay declaration contract does not match for " + type.FullName);

            ValidateNativeAttributes(type, type.FullName);
            foreach (var method in type.Methods)
            {
                ValidateNativeAttributes(method, method.FullName);
                foreach (var parameter in method.ParameterDefinitions)
                    ValidateNativeAttributes(parameter, method.FullName);
            }
            var constructorToken = ReplayConstructorToken(type);
            ValidateReplayMethod(type.Methods[0], constructorToken, ".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                MethodAttributes.RuntimeSpecialName, "System.Void",
                iterator ? new[] { "System.Int32" } : System.Array.Empty<string>());
            if (!iterator)
                return;

            ValidateReplayOverride(type, 1, constructorToken + 1, "System.IDisposable.Dispose", "System.Void",
                "System.IDisposable", "Dispose", "System.Void", false);
            ValidateReplayOverride(type, 2, constructorToken + 2, "MoveNext", "System.Boolean",
                "System.Collections.IEnumerator", "MoveNext", "System.Boolean", false);
            ValidateReplayOverride(type, 3, constructorToken + 3,
                "System.Collections.Generic.IEnumerator<System.Object>.get_Current", "System.Object",
                ReplayInterfaces[0], "get_Current", "!0", true);
            ValidateReplayOverride(type, 4, constructorToken + 4, "System.Collections.IEnumerator.Reset", "System.Void",
                "System.Collections.IEnumerator", "Reset", "System.Void", false);
            ValidateReplayOverride(type, 5, constructorToken + 5, "System.Collections.IEnumerator.get_Current", "System.Object",
                "System.Collections.IEnumerator", "get_Current", "System.Object", true);
            ValidateReplayProperty(type.Properties[0], "System.Collections.Generic.IEnumerator<System.Object>.Current",
                type.Methods[3]);
            ValidateReplayProperty(type.Properties[1], "System.Collections.IEnumerator.Current", type.Methods[5]);
        }

        private static void ValidateReplayFields(TypeDefinition type, int firstToken,
            params (string Name, string Type, bool Public)[] fields)
        {
            if (type.Fields.Count != fields.Length)
                throw Invalid("source replay field count does not match for " + type.FullName);
            for (var index = 0; index < fields.Length; index++)
            {
                var field = type.Fields[index];
                var expected = fields[index];
                if (field.MetadataToken.ToInt32() != firstToken + index || field.Name != expected.Name ||
                    field.Signature?.FieldType.FullName != expected.Type ||
                    field.Attributes != (expected.Public ? FieldAttributes.Public : FieldAttributes.Private) ||
                    field.FieldOffset != null || field.HasFieldMarshal || field.MarshalDescriptor != null ||
                    field.FieldRva != null || field.Constant != null)
                    throw Invalid("source replay field contract does not match for " + field.FullName);
                ValidateNativeAttributes(field, field.FullName);
            }
        }

        private static void ValidateReplayMethod(MethodDefinition method, int token, string name,
            MethodAttributes attributes, string returnType, params string[] parameters)
        {
            if (method.MetadataToken.ToInt32() != token || method.Name != name || method.Attributes != attributes ||
                method.ImplAttributes != 0 || method.CilMethodBody == null || method.GenericParameters.Count != 0 ||
                method.ImplementationMap != null || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                method.Signature.ReturnType.FullName != returnType ||
                !method.Signature.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(parameters))
                throw Invalid("source replay method contract does not match for " + method.FullName);
        }

        private static void ValidateReplayOverride(TypeDefinition type, int index, int token, string name,
            string returnType, string interfaceName, string memberName, string interfaceReturnType, bool getter)
        {
            var method = type.Methods[index];
            var attributes = MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig |
                MethodAttributes.NewSlot | MethodAttributes.Virtual | (getter ? MethodAttributes.SpecialName : 0);
            ValidateReplayMethod(method, token, name, attributes, returnType);
            var overrides = type.MethodImplementations.Where(implementation => implementation.Body == method).ToArray();
            if (overrides.Length != 1)
                throw Invalid("source replay interface body does not match for " + method.FullName);
            var declaration = overrides[0].Declaration;
            if (declaration?.DeclaringType?.FullName != interfaceName || declaration.Name != memberName ||
                !InteropDelegateContracts.HasStandardConvention(declaration.Signature) ||
                !declaration.Signature.HasThis || declaration.Signature.GenericParameterCount != 0 ||
                declaration.Signature.ParameterTypes.Count != 0 ||
                declaration.Signature.ReturnType.FullName != interfaceReturnType)
                throw Invalid("source replay interface declaration does not match for " + method.FullName);
        }

        private static void ValidateReplayProperty(PropertyDefinition property, string name, MethodDefinition getter)
        {
            if (property.Name != name || property.Attributes != 0 || property.Constant != null ||
                property.Signature == null || !property.Signature.HasThis ||
                property.Signature.ReturnType.FullName != "System.Object" || property.Signature.ParameterTypes.Count != 0 ||
                property.Semantics.Count != 1 || property.GetMethod != getter || property.SetMethod != null)
                throw Invalid("source replay property contract does not match for " + property.FullName);
            ValidateNativeAttributes(property, property.FullName);
        }
    }
}
