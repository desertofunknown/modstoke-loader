// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    // Helpers live with their native element type; corlib never depends on downstream game assemblies.
    internal sealed class InteropRectangularArrayRuntimeEmitter
    {
        private readonly InteropRectangularArraySupport.Wrapper wrapper;
        private readonly InteropRuntimeReferences imports;
        private readonly TypeSignature nativeArray;
        private readonly TypeSignature nativeObject;
        private readonly TypeSignature nativeType;
        private readonly IMethodDescriptor create;
        private readonly IMethodDescriptor get;
        private readonly IMethodDescriptor set;
        private readonly IMethodDescriptor getLength;
        private readonly IMethodDescriptor getLowerBound;

        private InteropRectangularArrayRuntimeEmitter(InteropRewriteContext context,
            InteropRectangularArraySupport.Wrapper wrapper)
        {
            this.wrapper = wrapper;
            imports = wrapper.AssemblyContext.Imports;
            var importer = imports.Module.DefaultImporter;
            nativeArray = importer.ImportTypeSignature(context.CorLib.GetTypeByName("System.Array").NewType.ToTypeSignature());
            nativeObject = importer.ImportTypeSignature(context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature());
            nativeType = importer.ImportTypeSignature(context.CorLib.GetTypeByName("System.Type").NewType.ToTypeSignature());
            create = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "CreateInstance", true,
                "System.Type", "System.Int32[]", "System.Int32[]"));
            get = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "GetValue", false, "System.Int32[]"));
            set = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "SetValue", false, "System.Object", "System.Int32[]"));
            getLength = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "GetLength", false, "System.Int32"));
            getLowerBound = importer.ImportMethod(InteropRectangularArraySupport.NativeMethod(context, "GetLowerBound", false, "System.Int32"));
        }

        internal static InteropRectangularArraySupport.Wrapper Declare(InteropRewriteContext context,
            InteropTypeContext element, InteropAssemblyContext owner, TypeSignature source,
            TypeSignature rewritten, string identity, int rank)
        {
            var module = owner.NewAssembly.ManifestModule;
            var original = element.OriginalType;
            var runtimeType = typeof(string).Assembly.GetType(original.FullName);
            var primitive = source is not ArrayBaseTypeSignature &&
                element.AssemblyContext == context.CorLib && runtimeType != null &&
                (runtimeType.IsPrimitive || runtimeType == typeof(string));
            var name = primitive ? "Native" + original.Name + "Array" + rank + "D" :
                "NativeArray" + rank + "D_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    source is ArrayBaseTypeSignature or GenericInstanceTypeSignature ? identity :
                        element.AssemblyContext.OriginalAssembly.Name + ":" + original.FullName)));
            var type = new TypeDefinition("Modstoke.Interop", name,
                TypeAttributes.Public | TypeAttributes.Sealed,
                module.DefaultImporter.ImportType(context.CorLib.GetTypeByName("System.Array").NewType));
            if (module.TopLevelTypes.Any(candidate => candidate.FullName == type.FullName))
                throw new InvalidOperationException("Rectangular array helper name already exists: " + type.FullName);
            module.TopLevelTypes.Add(type);
            var indices = Enumerable.Repeat<TypeSignature>(module.CorLibTypeFactory.Int32, rank).ToArray();
            return new InteropRectangularArraySupport.Wrapper
            {
                Type = type,
                Element = rewritten,
                ElementContext = element,
                AssemblyContext = owner,
                IsReferenceElement = InteropRectangularArraySupport.IsReferenceElement(context, source),
                Rank = rank,
                PointerConstructor = AddConstructor(type, module.CorLibTypeFactory.IntPtr),
                LengthConstructor = AddConstructor(type, indices),
                BoundsConstructor = AddConstructor(type, indices.Concat(indices).ToArray()),
                Get = AddMethod(type, "Get", MethodSignature.CreateInstance(rewritten, indices)),
                Set = AddMethod(type, "Set", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void,
                    indices.Concat(new[] { rewritten }).ToArray())),
                AddressGet = AddMethod(type, "AddressGet", MethodSignature.CreateInstance(rewritten, indices)),
                AddressSet = AddMethod(type, "AddressSet", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void,
                    indices.Concat(new[] { rewritten }).ToArray()))
            };
        }

        internal static void Fill(InteropRewriteContext context, InteropRectangularArraySupport.Wrapper wrapper)
        {
            var emitter = new InteropRectangularArrayRuntimeEmitter(context, wrapper);
            emitter.EmitClassIdentity();
            emitter.EmitPointerConstructor();
            emitter.EmitAllocation(wrapper.LengthConstructor, false);
            emitter.EmitAllocation(wrapper.BoundsConstructor, true);
            emitter.EmitGet();
            emitter.EmitSet();
            emitter.EmitAddressAccessor(wrapper.AddressGet, wrapper.Get);
            emitter.EmitAddressAccessor(wrapper.AddressSet, wrapper.Set);
        }

        internal static void FillUnavailable(InteropRectangularArraySupport.Wrapper wrapper, string reason)
        {
            var constructor = wrapper.Type.DeclaringModule.DefaultImporter.ImportMethod(
                typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
            foreach (var method in wrapper.Type.Methods)
            {
                method.CilMethodBody.Instructions.Clear();
                method.CilMethodBody.Instructions.Add(CilOpCodes.Ldstr, reason);
                method.CilMethodBody.Instructions.Add(CilOpCodes.Newobj, constructor);
                method.CilMethodBody.Instructions.Add(CilOpCodes.Throw);
            }
        }

        private static MethodDefinition AddMethod(TypeDefinition owner, string name, MethodSignature signature)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.HideBySig, signature)
            {
                CilMethodBody = new CilMethodBody { InitializeLocals = true }
            };
            owner.Methods.Add(method);
            return method;
        }

        private static MethodDefinition AddConstructor(TypeDefinition owner, params TypeSignature[] parameters)
        {
            var method = AddMethod(owner, ".ctor", MethodSignature.CreateInstance(
                owner.DeclaringModule.CorLibTypeFactory.Void, parameters));
            method.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName;
            return method;
        }

        private IFieldDescriptor ClassPointer(TypeSignature type) => imports.Module.DefaultImporter.ImportField(
            new MemberReference(new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                false, type).ToTypeDefOrRef(), "NativeClassPtr", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));

        private IMethodDescriptor Runtime(string name) =>
            imports.Module.DefaultImporter.ImportMethod(typeof(IL2CPP).GetMethod(name));

        private IMethodDescriptor ValueBridge(string name, params TypeSignature[] arguments)
        {
            var method = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(name));
            return method.MakeGenericInstanceMethod(arguments);
        }

        private IMethodDescriptor PointerConstructor(TypeSignature type) => new MemberReference(type.ToTypeDefOrRef(),
            ".ctor", MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.IntPtr));

        private void EmitClassIdentity()
        {
            var method = new MethodDefinition(".cctor",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void)) { CilMethodBody = new CilMethodBody() };
            wrapper.Type.Methods.Add(method);
            var body = method.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Call, ValueBridge(nameof(InteropGenericValueSupport.RequireNativeType), wrapper.Element));
            body.Add(CilOpCodes.Ldsfld, ClassPointer(wrapper.Element));
            body.Add(CilOpCodes.Dup);
            var elementPresent = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, elementPresent);
            body.Add(CilOpCodes.Pop);
            Throw(body, typeof(InvalidOperationException));
            elementPresent.Instruction = body.Add(CilOpCodes.Ldc_I4, wrapper.Rank);
            body.Add(CilOpCodes.Ldc_I4_1);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_bounded_array_class_get)));
            body.Add(CilOpCodes.Dup);
            var arrayPresent = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, arrayPresent);
            body.Add(CilOpCodes.Pop);
            Throw(body, typeof(InvalidOperationException));
            arrayPresent.Instruction = body.Add(CilOpCodes.Stsfld, ClassPointer(wrapper.Type.ToTypeSignature()));
            body.Add(CilOpCodes.Ret);
        }

        private void EmitPointerConstructor()
        {
            var body = wrapper.PointerConstructor.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, PointerConstructor(nativeArray));
            var actualClass = Local(wrapper.PointerConstructor, imports.Module.CorLibTypeFactory.IntPtr);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Stloc, actualClass);
            body.Add(CilOpCodes.Ldloc, actualClass);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_get_rank)));
            body.Add(CilOpCodes.Ldc_I4, wrapper.Rank);
            var rankValid = new CilInstructionLabel();
            body.Add(CilOpCodes.Beq, rankValid);
            Throw(body, typeof(InvalidCastException));
            rankValid.Instruction = body.Add(CilOpCodes.Ldloc, actualClass);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_get_type.Value);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_type_get_type)));
            body.Add(CilOpCodes.Ldc_I4, (int)ElementType.Array);
            var shapeValid = new CilInstructionLabel();
            body.Add(CilOpCodes.Beq, shapeValid);
            Throw(body, typeof(InvalidCastException));
            shapeValid.Instruction = body.Add(CilOpCodes.Nop);
            if (wrapper.IsReferenceElement)
            {
                body.Add(CilOpCodes.Ldloc, actualClass);
                body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_get_element_class)));
                body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_is_valuetype)));
                var reference = new CilInstructionLabel();
                body.Add(CilOpCodes.Brfalse, reference);
                Throw(body, typeof(InvalidCastException));
                reference.Instruction = body.Add(CilOpCodes.Nop);
            }
            // Only reference elements participate in covariance; boxed values are not reference storage.
            body.Add(CilOpCodes.Ldsfld, ClassPointer(wrapper.Type.ToTypeSignature()));
            body.Add(CilOpCodes.Ldloc, actualClass);
            var valid = new CilInstructionLabel();
            if (wrapper.IsReferenceElement)
            {
                body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_is_assignable_from)));
                body.Add(CilOpCodes.Brtrue, valid);
            }
            else
                body.Add(CilOpCodes.Beq, valid);
            Throw(body, typeof(InvalidCastException));
            valid.Instruction = body.Add(CilOpCodes.Ret);
        }

        private void EmitAllocation(MethodDefinition method, bool bounded)
        {
            var body = method.CilMethodBody.Instructions;
            // CLI bounded constructors alternate lower bound and length for each dimension.
            for (var dimension = 0; dimension < wrapper.Rank; dimension++)
            {
                body.Add(CilOpCodes.Ldarg, method.Parameters[bounded ? dimension * 2 + 1 : dimension]);
                body.Add(CilOpCodes.Ldc_I4_0);
                var nonnegative = new CilInstructionLabel();
                body.Add(CilOpCodes.Bge, nonnegative);
                Throw(body, typeof(OverflowException));
                nonnegative.Instruction = body.Add(CilOpCodes.Nop);
            }
            if (wrapper.Rank == 1)
            {
                EmitBoundedRankOneAllocation(method, bounded);
                return;
            }
            var lengths = EmitVector(method, dimension =>
                body.Add(CilOpCodes.Ldarg, method.Parameters[bounded ? dimension * 2 + 1 : dimension]));
            var lowerBounds = EmitVector(method, dimension =>
            {
                if (bounded)
                    body.Add(CilOpCodes.Ldarg, method.Parameters[dimension * 2]);
                else
                    body.Add(CilOpCodes.Ldc_I4_0);
            });
            var array = Local(method, nativeArray);
            var elementType = Local(method, nativeType);
            body.Add(CilOpCodes.Ldsfld, ClassPointer(wrapper.Element));
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_get_type.Value);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_type_get_object)));
            body.Add(CilOpCodes.Newobj, PointerConstructor(nativeType));
            body.Add(CilOpCodes.Stloc, elementType);
            body.Add(CilOpCodes.Ldloc, elementType);
            body.Add(CilOpCodes.Ldloc, lengths);
            body.Add(CilOpCodes.Ldloc, lowerBounds);
            body.Add(CilOpCodes.Call, create);
            body.Add(CilOpCodes.Stloc, array);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            body.Add(CilOpCodes.Call, wrapper.PointerConstructor);
            KeepAlive(body, array);
            KeepAlive(body, elementType);
            KeepAlive(body, lengths);
            KeepAlive(body, lowerBounds);
            body.Add(CilOpCodes.Ret);
        }

        private void EmitBoundedRankOneAllocation(MethodDefinition method, bool bounded)
        {
            var body = method.CilMethodBody.Instructions;
            var length = Local(method, imports.Module.CorLibTypeFactory.UInt64);
            var lower = Local(method, imports.Module.CorLibTypeFactory.UInt64);
            body.Add(CilOpCodes.Ldarg, method.Parameters[bounded ? 1 : 0]);
            body.Add(CilOpCodes.Conv_U8);
            body.Add(CilOpCodes.Stloc, length);
            if (bounded)
                body.Add(CilOpCodes.Ldarg, method.Parameters[0]);
            else
                body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_I8);
            body.Add(CilOpCodes.Stloc, lower);
            body.Add(CilOpCodes.Ldloc, lower);
            body.Add(CilOpCodes.Ldloc, length);
            body.Add(CilOpCodes.Add);
            body.Add(CilOpCodes.Ldc_I8, 1L);
            body.Add(CilOpCodes.Sub);
            body.Add(CilOpCodes.Ldc_I8, (long)int.MaxValue);
            var valid = new CilInstructionLabel();
            body.Add(CilOpCodes.Ble, valid);
            Throw(body, typeof(ArgumentOutOfRangeException));
            // array_new_full retains the supplied ARRAY class even for a zero lower bound.
            // The pinned x64 export takes size_t lengths and bounds, then narrows bounds to Int32.
            valid.Instruction = body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, ClassPointer(wrapper.Type.ToTypeSignature()));
            body.Add(CilOpCodes.Ldloca, length);
            body.Add(CilOpCodes.Ldloca, lower);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_array_new_full)));
            body.Add(CilOpCodes.Call, wrapper.PointerConstructor);
            body.Add(CilOpCodes.Ret);
        }

        private void EmitGet()
        {
            var method = wrapper.Get;
            var body = method.CilMethodBody.Instructions;
            var indices = EmitVector(method, dimension => body.Add(CilOpCodes.Ldarg, method.Parameters[dimension]));
            var boxed = Local(method, nativeObject);
            var value = Local(method, wrapper.Element);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, indices);
            body.Add(CilOpCodes.Call, get);
            body.Add(CilOpCodes.Stloc, boxed);
            body.Add(CilOpCodes.Ldloc, boxed);
            body.Add(CilOpCodes.Call, ValueBridge(nameof(InteropGenericValueSupport.UnboxAny), wrapper.Element));
            body.Add(CilOpCodes.Stloc, value);
            KeepAlive(body, boxed);
            KeepAlive(body, indices);
            KeepThisAlive(body);
            body.Add(CilOpCodes.Ldloc, value);
            body.Add(CilOpCodes.Ret);
        }

        private void EmitSet()
        {
            var method = wrapper.Set;
            var body = method.CilMethodBody.Instructions;
            EmitBoundsCheck(method);
            var indices = EmitVector(method, dimension => body.Add(CilOpCodes.Ldarg, method.Parameters[dimension]));
            var boxed = Local(method, nativeObject);
            body.Add(CilOpCodes.Ldarg, method.Parameters[wrapper.Rank]);
            body.Add(CilOpCodes.Call, ValueBridge(nameof(InteropGenericValueSupport.Box), wrapper.Element, nativeObject));
            body.Add(CilOpCodes.Stloc, boxed);
            if (wrapper.IsReferenceElement)
                EmitReferenceStoreCheck(body, boxed);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, boxed);
            body.Add(CilOpCodes.Ldloc, indices);
            body.Add(CilOpCodes.Call, set);
            KeepAlive(body, boxed);
            KeepAlive(body, indices);
            KeepThisAlive(body);
            body.Add(CilOpCodes.Ret);
        }

        private void EmitReferenceStoreCheck(CilInstructionCollection body, CilLocalVariable value)
        {
            var valid = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldloc, value);
            body.Add(CilOpCodes.Brfalse, valid);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_get_element_class)));
            body.Add(CilOpCodes.Ldloc, value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Call, Runtime(nameof(IL2CPP.il2cpp_class_is_assignable_from)));
            body.Add(CilOpCodes.Brtrue, valid);
            Throw(body, typeof(ArrayTypeMismatchException));
            valid.Instruction = body.Add(CilOpCodes.Nop);
        }

        private void EmitAddressAccessor(MethodDefinition method, MethodDefinition accessor)
        {
            var body = method.CilMethodBody.Instructions;
            EmitBoundsCheck(method);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Ldsfld, ClassPointer(wrapper.Type.ToTypeSignature()));
            var exact = new CilInstructionLabel();
            body.Add(CilOpCodes.Beq, exact);
            Throw(body, typeof(ArrayTypeMismatchException));
            exact.Instruction = body.Add(CilOpCodes.Ldarg_0);
            foreach (var parameter in method.Parameters)
                body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, accessor);
            body.Add(CilOpCodes.Ret);
        }

        private void EmitBoundsCheck(MethodDefinition method)
        {
            var body = method.CilMethodBody.Instructions;
            for (var dimension = 0; dimension < wrapper.Rank; dimension++)
            {
                body.Add(CilOpCodes.Ldarg, method.Parameters[dimension]);
                body.Add(CilOpCodes.Conv_I8);
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldc_I4, dimension);
                body.Add(CilOpCodes.Call, getLowerBound);
                body.Add(CilOpCodes.Conv_I8);
                body.Add(CilOpCodes.Sub);
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldc_I4, dimension);
                body.Add(CilOpCodes.Call, getLength);
                body.Add(CilOpCodes.Conv_U8);
                var valid = new CilInstructionLabel();
                body.Add(CilOpCodes.Blt_Un, valid);
                Throw(body, typeof(IndexOutOfRangeException));
                valid.Instruction = body.Add(CilOpCodes.Nop);
            }
        }

        private CilLocalVariable EmitVector(MethodDefinition method, Action<int> loadValue)
        {
            var integer = imports.Module.CorLibTypeFactory.Int32;
            var vector = Local(method, new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, integer));
            var body = method.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldc_I8, (long)wrapper.Rank);
            body.Add(CilOpCodes.Newobj, imports.Il2CppStructArrayctor_size.Get(integer));
            body.Add(CilOpCodes.Stloc, vector);
            for (var dimension = 0; dimension < wrapper.Rank; dimension++)
            {
                body.Add(CilOpCodes.Ldloc, vector);
                body.Add(CilOpCodes.Ldc_I4, dimension);
                loadValue(dimension);
                body.Add(CilOpCodes.Callvirt, imports.Il2CppArrayBase_set_Item.Get(integer));
            }
            return vector;
        }

        private static CilLocalVariable Local(MethodDefinition method, TypeSignature type)
        {
            var local = new CilLocalVariable(type);
            method.CilMethodBody.LocalVariables.Add(local);
            return local;
        }

        private void KeepAlive(CilInstructionCollection body, CilLocalVariable local)
        {
            body.Add(CilOpCodes.Ldloc, local);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive))));
        }

        private void KeepThisAlive(CilInstructionCollection body)
        {
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive))));
        }

        private void Throw(CilInstructionCollection body, Type exception)
        {
            body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(exception.GetConstructor(Type.EmptyTypes)));
            body.Add(CilOpCodes.Throw);
        }
    }
}
