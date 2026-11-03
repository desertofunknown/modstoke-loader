// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
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
        private static bool TryRestoreScreenLayout(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                !(source.DeclaringType.FullName == "UnityEngine.Screen" &&
                    (string)source.Name is "GetDisplayLayout" or "GetDisplayLayoutImpl" ||
                  source.DeclaringType.FullName == "UnityEngine.Device.Screen" && source.Name == "GetDisplayLayout"))
                return false;

            entry.Set("signature-rejected", "screen-layout-contract",
                "Display layout requires matched declarations and bodies, a CLR list and exact DisplayInfo field conversions.");
            var module = source.DeclaringModule;
            var sourceScreen = module.TopLevelTypes.FirstOrDefault(type => type.FullName == "UnityEngine.Screen");
            var sourceDevice = module.TopLevelTypes.FirstOrDefault(type => type.FullName == "UnityEngine.Device.Screen");
            var sourceDisplay = module.TopLevelTypes.FirstOrDefault(type => type.FullName == "UnityEngine.DisplayInfo");
            var publicMethods = sourceScreen?.Methods.Where(method => method.Name == "GetDisplayLayout").ToArray();
            var implementations = sourceScreen?.Methods.Where(method => method.Name == "GetDisplayLayoutImpl").ToArray();
            var deviceMethods = sourceDevice?.Methods.Where(method => method.Name == "GetDisplayLayout").ToArray();
            if (publicMethods?.Length != 1 || implementations?.Length != 1 || deviceMethods?.Length != 1 || sourceDisplay == null)
                return true;
            var publicMethod = publicMethods[0];
            var implementation = implementations[0];
            var deviceMethod = deviceMethods[0];
            if (!HasScreenLayoutDeclaration(publicMethod, sourceScreen, sourceDisplay, 100665646, false) ||
                !HasScreenLayoutDeclaration(implementation, sourceScreen, sourceDisplay, 100665650, true) ||
                !HasScreenLayoutDeclaration(deviceMethod, sourceDevice, sourceDisplay, 100673876, false) ||
                !HasScreenLayoutBody(publicMethod, implementation, false) ||
                !HasScreenLayoutBody(deviceMethod, publicMethod, true)) return true;

            var context = owner.AssemblyContext.GlobalContext;
            var screen = context.TryGetTypeByName("UnityEngine.CoreModule", sourceScreen.FullName);
            var device = context.TryGetTypeByName("UnityEngine.CoreModule", sourceDevice.FullName);
            if (!HasScreenLayoutOwner(sourceScreen, screen, imports.Module, resolver, false) ||
                !HasScreenLayoutOwner(sourceDevice, device, imports.Module, resolver, true) ||
                owner != (source == deviceMethod ? device : screen)) return true;

            var display = context.TryGetTypeByName("UnityEngine.CoreModule", sourceDisplay.FullName);
            if (!TryGetScreenFields(sourceDisplay, display, resolver, true,
                    ["handle", "width", "height", "refreshRate", "workArea", "name"],
                    ["System.UInt64", "System.Int32", "System.Int32", "UnityEngine.RefreshRate", "UnityEngine.RectInt", "System.String"],
                    out var displayFields) || display.NewType.DeclaringModule != imports.Module) return true;
            var sourceRate = sourceDisplay.Fields[3].Signature.FieldType.Resolve();
            var sourceArea = sourceDisplay.Fields[4].Signature.FieldType.Resolve();
            var rate = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.RefreshRate");
            var area = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.RectInt");
            if (sourceRate?.DeclaringModule != module || sourceArea?.DeclaringModule != module ||
                !TryGetScreenFields(sourceRate, rate, resolver, false,
                    ["numerator", "denominator"], ["System.UInt32", "System.UInt32"], out var rateFields) ||
                !TryGetScreenFields(sourceArea, area, resolver, false,
                    ["m_XMin", "m_YMin", "m_Width", "m_Height"],
                    ["System.Int32", "System.Int32", "System.Int32", "System.Int32"], out var areaFields) ||
                rate.NewType.DeclaringModule != imports.Module || area.NewType.DeclaringModule != imports.Module) return true;

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

            var displayType = display.NewType.ToTypeSignature();
            // DisplayInfo has no native class identity; this list must remain a real CLR collection.
            var list = new GenericInstanceTypeSignature(importer.ImportType(typeof(List<>)), false, displayType);
            var signature = MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void, list);
            if (HasConflict(screen, publicMethod) || HasConflict(screen, implementation) || HasConflict(device, deviceMethod) ||
                owner.NewType.Methods.Any(method => method.Name == source.Name)) return true;

            const string converterName = "<ConvertDisplayLayoutInfo>";
            var converters = screen.NewType.Methods.Where(method => method.Name == converterName).ToArray();
            var restoredImpl = screen.TryGetRestoredManagedMethod(implementation);
            if (restoredImpl == null ? converters.Length != 0 : converters.Length != 1 ||
                restoredImpl.CilMethodBody == null || !restoredImpl.CilMethodBody.Instructions.Any(instruction =>
                    instruction.OpCode.Code == CilCode.Ldftn && instruction.Operand == converters[0])) return true;

            var target = new MethodDefinition(source.Name, source.Attributes,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            InteropMethodDeclarations.AddParameter(target, list, source.Parameters[0].Name, (ParameterAttributes)0);
            var body = target.CilMethodBody.Instructions;
            MethodDefinition converter = null;
            if (source == implementation)
            {
                var nativeInfo = importer.ImportTypeSignature(typeof(NativeScreenInfo));
                converter = new MethodDefinition(converterName,
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                    MethodSignature.CreateStatic(displayType))
                {
                    ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                    CilMethodBody = new CilMethodBody { InitializeLocals = true }
                };
                InteropMethodDeclarations.AddParameter(converter, nativeInfo, "display", (ParameterAttributes)0);
                var value = new CilLocalVariable(displayType);
                converter.CilMethodBody.LocalVariables.Add(value);
                var conversion = converter.CilMethodBody.Instructions;
                for (var index = 0; index < paths.Length; index++)
                {
                    conversion.Add(CilOpCodes.Ldloca, value);
                    foreach (var field in paths[index].SkipLast(1)) conversion.Add(CilOpCodes.Ldflda, field);
                    conversion.Add(CilOpCodes.Ldarga, converter.Parameters[0]);
                    conversion.Add(CilOpCodes.Ldfld, fields[index]);
                    conversion.Add(CilOpCodes.Stfld, paths[index][^1]);
                }
                conversion.Add(CilOpCodes.Ldloc, value);
                conversion.Add(CilOpCodes.Ret);

                var callback = new GenericInstanceTypeSignature(importer.ImportType(typeof(Func<,>)), false, nativeInfo, displayType);
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldnull);
                body.Add(CilOpCodes.Ldftn, converter);
                body.Add(CilOpCodes.Newobj, new MemberReference(callback.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                        imports.Module.CorLibTypeFactory.Object, imports.Module.CorLibTypeFactory.IntPtr)));
                body.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(typeof(InteropScreenLayoutSupport)
                    .GetMethod(nameof(InteropScreenLayoutSupport.Fill)))).MakeGenericInstanceMethod(displayType));
            }
            else
            {
                if (source == publicMethod)
                {
                    var notNull = new CilInstructionLabel();
                    body.Add(CilOpCodes.Ldarg_0);
                    body.Add(CilOpCodes.Brtrue_S, notNull);
                    body.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(ArgumentNullException).GetConstructor(Type.EmptyTypes)));
                    body.Add(CilOpCodes.Throw);
                    notNull.Instruction = body.Add(CilOpCodes.Ldarg_0);
                }
                else body.Add(CilOpCodes.Ldarg_0);
                // The callee can appear later in the restoration loop; bind its managed signature directly.
                body.Add(CilOpCodes.Call, new MemberReference(screen.NewType,
                    source == publicMethod ? implementation.Name : publicMethod.Name, signature));
            }
            body.Add(CilOpCodes.Ret);
            if (converter != null) screen.NewType.Methods.Add(converter);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("native-screen-layout-adapter", null,
                "CLR List<DisplayInfo> with field-wise native snapshots and preserved public null-check and Device forwarding.");
            return true;

            bool HasConflict(InteropTypeContext type, MethodDefinition original) =>
                type.NewType.Methods.Any(method => method.Name == original.Name &&
                    (method != type.TryGetRestoredManagedMethod(original) ||
                     !SignatureComparer.Default.Equals(method.Signature, signature)));
        }

        private static bool HasScreenLayoutDeclaration(MethodDefinition method, TypeDefinition owner,
            TypeDefinition display, int token, bool implementation)
        {
            if (method.DeclaringType != owner || method.DeclaringModule != display.DeclaringModule ||
                method.DeclaringModule.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                method.MetadataToken.ToInt32() != token ||
                method.Attributes != ((implementation ? MethodAttributes.Private : MethodAttributes.Public) |
                    MethodAttributes.Static | MethodAttributes.HideBySig) ||
                method.ImplAttributes != (implementation ? MethodImplAttributes.InternalCall : MethodImplAttributes.IL) ||
                method.HasMethodBody == implementation || method.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 ||
                method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                method.Parameters.Count != 1 || method.Signature.ParameterTypes.Count != 1 ||
                method.Parameters[0].Definition is not { } parameter || parameter.Attributes != 0 ||
                parameter.MarshalDescriptor != null || parameter.Constant != null || parameter.CustomAttributes.Count != 0 ||
                method.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature list || list.IsValueType ||
                !HasScreenLayoutFrameworkType(list.GenericType, "System.Collections.Generic.List`1") ||
                list.TypeArguments.Count != 1 ||
                list.TypeArguments[0] is not TypeDefOrRefSignature { IsValueType: true } element || element.Resolve() != display)
                return false;
            return implementation ? method.CilMethodBody == null : method.CilMethodBody != null;
        }

        private static bool HasScreenLayoutBody(MethodDefinition method, MethodDefinition callee, bool device)
        {
            var body = method.CilMethodBody;
            var code = body.Instructions;
            CilCode[] expected = device
                ? [CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Ret]
                : [CilCode.Nop, CilCode.Ldarg_0, CilCode.Ldnull, CilCode.Ceq, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Brfalse_S, CilCode.Newobj, CilCode.Throw, CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Ret];
            if (body.ExceptionHandlers.Count != 0 || body.InitializeLocals == device ||
                body.LocalVariables.Count != (device ? 0 : 1) ||
                code.Count != expected.Length || code.Where((instruction, index) => instruction.OpCode.Code != expected[index]).Any() ||
                code[device ? 1 : 10].Operand is not IMethodDescriptor call || call is MethodSpecification ||
                call.Resolve() != callee || !SignatureComparer.Default.Equals(call.Signature, callee.Signature)) return false;
            if (device) return true;
            return body.LocalVariables[0].VariableType is CorLibTypeSignature { ElementType: ElementType.Boolean } &&
                code[6].Operand is CilInstructionLabel branch && branch.Instruction == code[9] &&
                code[7].Operand is IMethodDescriptor constructor && constructor is not MethodSpecification &&
                constructor.Name == ".ctor" && constructor.DeclaringType is ITypeDefOrRef exceptionType &&
                HasScreenLayoutFrameworkType(exceptionType, "System.ArgumentNullException") &&
                InteropDelegateContracts.HasStandardConvention(constructor.Signature) && constructor.Signature.HasThis &&
                constructor.Signature.GenericParameterCount == 0 && constructor.Signature.ParameterTypes.Count == 0 &&
                constructor.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void };
        }

        private static bool HasScreenLayoutFrameworkType(ITypeDefOrRef type, string name)
        {
            var assembly = type?.Scope?.GetAssembly();
            return type?.FullName == name && assembly?.Name == "netstandard" && assembly.Version == new Version(2, 1, 0, 0) &&
                assembly.GetPublicKeyToken() is { } token &&
                token.SequenceEqual(new byte[] { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 });
        }

        private static bool HasScreenLayoutOwner(TypeDefinition source, InteropTypeContext target,
            ModuleDefinition module, InteropUnstripTypeResolver resolver, bool device)
        {
            if (target == null || !source.IsPublic || !source.IsSealed || source.IsAbstract != device ||
                source.IsValueType || source.IsInterface || source.DeclaringType != null || source.GenericParameters.Count != 0 ||
                source.BaseType?.FullName != "System.Object" ||
                !target.NewType.IsPublic || !target.NewType.IsSealed || target.NewType.IsAbstract != device ||
                target.NewType.IsValueType || target.NewType.IsInterface || target.NewType.DeclaringType != null ||
                target.NewType.GenericParameters.Count != 0 || target.NewType.FullName != source.FullName ||
                target.NewType.DeclaringModule != module || resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != target.NewType)
                return false;
            if (device)
                return target.OriginalType == null && target.ClassPointerFieldRef == null &&
                    SignatureComparer.Default.Equals(target.NewType.BaseType?.ToTypeSignature(), module.CorLibTypeFactory.Object);
            return target.OriginalType != null && target.ClassPointerFieldRef != null &&
                target.OriginalType.FullName == source.FullName && !target.OriginalType.IsValueType &&
                !target.OriginalType.IsInterface && target.OriginalType.GenericParameters.Count == 0 &&
                target.OriginalType.DeclaringModule.Assembly.Name == "UnityEngine.CoreModule" &&
                target.NewType.BaseType?.FullName == "Il2CppSystem.Object";
        }
    }
}
