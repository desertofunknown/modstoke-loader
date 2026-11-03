// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private static bool TryRestoreScreenDisplay(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringType.FullName != "UnityEngine.Screen" ||
                (string)source.Name is not ("GetMainWindowDisplayInfo_Injected" or "MoveMainWindowImpl_Injected"))
                return false;

            entry.Set("signature-rejected", "screen-display-contract",
                "Display access requires the matched injected declarations, native Screen identity and exact field conversions.");
            var read = source.Name == "GetMainWindowDisplayInfo_Injected";
            if (source.DeclaringModule.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                source.MetadataToken.ToInt32() != (read ? 0x0600093E : 0x0600093F) ||
                source.Attributes != (MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                source.ImplAttributes != MethodImplAttributes.InternalCall || source.HasMethodBody ||
                source.GenericParameters.Count != 0 || source.Signature.HasThis ||
                source.Signature.GenericParameterCount != 0 || !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                source.Parameters.Count != (read ? 1 : 2) || source.Signature.ParameterTypes.Count != source.Parameters.Count ||
                source.Parameters[0].Definition?.Attributes != (read ? ParameterAttributes.Out : ParameterAttributes.In) ||
                !read && source.Parameters[1].Definition?.Attributes != (ParameterAttributes)0 ||
                source.Signature.ParameterTypes[0] is not ByReferenceTypeSignature displayRef ||
                displayRef.BaseType.Resolve() is not { } sourceDisplay ||
                sourceDisplay.FullName != "UnityEngine.DisplayInfo" || sourceDisplay.DeclaringModule != source.DeclaringModule ||
                owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != source.DeclaringType.FullName || owner.OriginalType.IsValueType ||
                owner.OriginalType.IsInterface || owner.OriginalType.GenericParameters.Count != 0 ||
                owner.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                owner.NewType.FullName != source.DeclaringType.FullName || owner.NewType.DeclaringModule != imports.Module ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.GenericParameters.Count != 0 ||
                resolver.ResolveRaw(source.DeclaringType.ToTypeSignature())?.Resolve() != owner.NewType)
                return true;

            var context = owner.AssemblyContext.GlobalContext;
            var display = context.TryGetTypeByName("UnityEngine.CoreModule", sourceDisplay.FullName);
            if (!TryGetScreenFields(sourceDisplay, display, resolver, true,
                    ["handle", "width", "height", "refreshRate", "workArea", "name"],
                    ["System.UInt64", "System.Int32", "System.Int32", "UnityEngine.RefreshRate", "UnityEngine.RectInt", "System.String"],
                    out var displayFields)) return true;

            var sourceRate = sourceDisplay.Fields[3].Signature.FieldType.Resolve();
            var sourceArea = sourceDisplay.Fields[4].Signature.FieldType.Resolve();
            if (sourceRate?.DeclaringModule != source.DeclaringModule || sourceArea?.DeclaringModule != source.DeclaringModule ||
                !TryGetScreenFields(sourceRate, context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.RefreshRate"),
                    resolver, false, ["numerator", "denominator"], ["System.UInt32", "System.UInt32"], out var rateFields) ||
                !TryGetScreenFields(sourceArea, context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.RectInt"),
                    resolver, false, ["m_XMin", "m_YMin", "m_Width", "m_Height"],
                    ["System.Int32", "System.Int32", "System.Int32", "System.Int32"], out var areaFields))
                return true;

            FieldDefinition[] positionFields = null;
            if (read)
            {
                if (source.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void }) return true;
            }
            else
            {
                var operation = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.AsyncOperation");
                if (source.Signature.ReturnType.FullName != "UnityEngine.AsyncOperation" ||
                    source.Signature.ReturnType.Resolve()?.DeclaringModule != source.DeclaringModule ||
                    operation?.OriginalType == null || operation.ClassPointerFieldRef == null ||
                    operation.OriginalType.FullName != "UnityEngine.AsyncOperation" || operation.OriginalType.IsValueType ||
                    operation.OriginalType.IsInterface || operation.OriginalType.GenericParameters.Count != 0 ||
                    operation.NewType.IsValueType || operation.NewType.IsInterface || operation.NewType.GenericParameters.Count != 0 ||
                    operation.NewType.DeclaringModule != imports.Module ||
                    resolver.ResolveRaw(source.Signature.ReturnType)?.Resolve() != operation.NewType ||
                    source.Signature.ParameterTypes[1] is not ByReferenceTypeSignature positionRef ||
                    positionRef.BaseType.Resolve() is not { } sourcePosition ||
                    sourcePosition.FullName != "UnityEngine.Vector2Int" || sourcePosition.DeclaringModule != source.DeclaringModule ||
                    !TryGetScreenFields(sourcePosition, context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.Vector2Int"),
                        resolver, false, ["m_X", "m_Y"], ["System.Int32", "System.Int32"], out positionFields)) return true;
            }

            var resultType = resolver.Resolve(source.Signature.ReturnType);
            var parameters = source.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
            if (resultType == null || parameters.Any(parameter => parameter == null)) return true;
            var signature = MethodSignature.CreateStatic(resultType, parameters);
            if (owner.NewType.Methods.Any(method => method.Name == source.Name &&
                SignatureComparer.Default.Equals(method.Signature, signature))) return true;
            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(resultType))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody { InitializeLocals = true }
            };
            for (var index = 0; index < parameters.Length; index++)
                InteropMethodDeclarations.AddParameter(target, parameters[index], source.Parameters[index].Name,
                    source.Parameters[index].Definition.Attributes);

            FieldDefinition[][] paths =
            [
                [displayFields[0]], [displayFields[1]], [displayFields[2]],
                [displayFields[3], rateFields[0]], [displayFields[3], rateFields[1]],
                [displayFields[4], areaFields[0]], [displayFields[4], areaFields[1]],
                [displayFields[4], areaFields[2]], [displayFields[4], areaFields[3]], [displayFields[5]]
            ];
            string[] names =
            [
                nameof(NativeScreenInfo.Handle), nameof(NativeScreenInfo.Width), nameof(NativeScreenInfo.Height),
                nameof(NativeScreenInfo.RefreshNumerator), nameof(NativeScreenInfo.RefreshDenominator),
                nameof(NativeScreenInfo.WorkX), nameof(NativeScreenInfo.WorkY),
                nameof(NativeScreenInfo.WorkWidth), nameof(NativeScreenInfo.WorkHeight), nameof(NativeScreenInfo.Name)
            ];
            var importer = imports.Module.DefaultImporter;
            var fields = names.Select(name => importer.ImportField(typeof(NativeScreenInfo).GetField(name))).ToArray();
            for (var index = 0; index < paths.Length; index++)
                if (fields[index].Signature is not FieldSignature fieldSignature ||
                    !SignatureComparer.Default.Equals(paths[index][^1].Signature.FieldType, fieldSignature.FieldType))
                    return true;
            var snapshot = new CilLocalVariable(importer.ImportTypeSignature(typeof(NativeScreenInfo)));
            target.CilMethodBody.LocalVariables.Add(snapshot);
            var body = target.CilMethodBody.Instructions;
            if (read)
            {
                var value = new CilLocalVariable(display.NewType.ToTypeSignature());
                target.CilMethodBody.LocalVariables.Add(value);
                body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(InteropScreenSupport).GetMethod(nameof(InteropScreenSupport.Read))));
                body.Add(CilOpCodes.Stloc, snapshot);
                for (var index = 0; index < paths.Length; index++)
                {
                    body.Add(CilOpCodes.Ldloca, value);
                    foreach (var field in paths[index].SkipLast(1)) body.Add(CilOpCodes.Ldflda, field);
                    body.Add(CilOpCodes.Ldloca, snapshot);
                    body.Add(CilOpCodes.Ldfld, fields[index]);
                    body.Add(CilOpCodes.Stfld, paths[index][^1]);
                }
                // Commit the complete managed value only after native outputs are retained and converted.
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldloc, value);
                body.Add(CilOpCodes.Stobj, display.NewType);
            }
            else
            {
                for (var index = 0; index < paths.Length; index++)
                {
                    body.Add(CilOpCodes.Ldloca, snapshot);
                    body.Add(CilOpCodes.Ldarg_0);
                    foreach (var field in paths[index].SkipLast(1)) body.Add(CilOpCodes.Ldflda, field);
                    body.Add(CilOpCodes.Ldfld, paths[index][^1]);
                    body.Add(CilOpCodes.Stfld, fields[index]);
                }
                body.Add(CilOpCodes.Ldloc, snapshot);
                foreach (var field in positionFields)
                {
                    body.Add(CilOpCodes.Ldarg_1);
                    body.Add(CilOpCodes.Ldfld, field);
                }
                var helper = ((IMethodDefOrRef)importer.ImportMethod(
                    typeof(InteropScreenSupport).GetMethod(nameof(InteropScreenSupport.Move))))
                    .MakeGenericInstanceMethod(resultType);
                body.Add(CilOpCodes.Call, helper);
            }
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("native-screen-display-adapter", null,
                "Field-wise DisplayInfo conversion with protected native calls; no native identity or layout cast for the restored value.");
            return true;
        }

        private static bool TryGetScreenFields(TypeDefinition source, InteropTypeContext target,
            InteropUnstripTypeResolver resolver, bool restored, string[] names, string[] types, out FieldDefinition[] fields)
        {
            fields = null;
            if (source == null || target == null || !source.IsValueType || source.IsEnum || !source.IsSequentialLayout ||
                source.GenericParameters.Count != 0 || source.DeclaringType != null || source.ClassLayout != null ||
                source.Fields.Count != names.Length ||
                target.NewType.FullName != source.FullName || !target.NewType.IsValueType || target.NewType.IsEnum ||
                target.NewType.GenericParameters.Count != 0 || target.NewType.DeclaringType != null ||
                target.NewType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != target.NewType)
                return false;
            if (restored)
            {
                if (target.OriginalType != null || target.ClassPointerFieldRef != null || !target.NewType.IsAutoLayout) return false;
            }
            else if (target.OriginalType == null || target.ClassPointerFieldRef == null ||
                target.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                target.OriginalType.FullName != source.FullName || !target.OriginalType.IsValueType ||
                target.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule") return false;

            fields = target.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != names.Length) return false;
            for (var index = 0; index < names.Length; index++)
            {
                var original = source.Fields[index];
                var field = fields[index];
                if (original.Name != names[index] || original.IsStatic || original.Signature?.FieldType.FullName != types[index] ||
                    original.FieldOffset != null || original.HasFieldMarshal || original.FieldRva != null ||
                    field.Name != names[index] || !field.IsPublic || field.IsInitOnly || field.HasFieldMarshal || field.FieldRva != null ||
                    !SignatureComparer.Default.Equals(field.Signature?.FieldType, resolver.Resolve(original.Signature.FieldType)))
                    return false;
            }
            return true;
        }
    }
}
