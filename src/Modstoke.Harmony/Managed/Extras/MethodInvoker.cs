using MonoMod.Utils;
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib
{
	// Based on https://github.com/MonoMod/MonoMod/blob/master/MonoMod.Utils/FastReflectionHelper.cs

	/// <summary>A delegate to invoke a method</summary>
	/// <param name="target">The instance</param>
	/// <param name="parameters">The method parameters</param>
	/// <returns>The method result</returns>
	public delegate object FastInvokeHandler(object target, params object[] parameters);

	/// <summary>A helper class to invoke method with delegates</summary>
	public static class MethodInvoker
	{
		/// <summary>Creates a fast invocation handler from a method</summary>
		/// <param name="methodInfo">The method to invoke</param>
		/// <param name="directBoxValueAccess">Controls if boxed value object is accessed/updated directly</param>
		/// <returns>The <see cref="FastInvokeHandler"/></returns>
		/// <remarks>
		/// <para>
		/// The <c>directBoxValueAccess</c> option controls how value types passed by reference (e.g. ref int, out my_struct) are handled in the arguments array
		/// passed to the fast invocation handler.
		/// Since the arguments array is an object array, any value types contained within it are actually references to a boxed value object.
		/// Like any other object, there can be other references to such boxed value objects, other than the reference within the arguments array.
		/// <example>For example,
		/// <code>
		/// var val = 5;
		/// var box = (object)val;
		/// var arr = new object[] { box };
		/// handler(arr); // for a method with parameter signature: ref/out/in int
		/// </code>
		/// </example>
		/// </para>
		/// <para>
		/// If <c>directBoxValueAccess</c> is <c>true</c>, the boxed value object is accessed (and potentially updated) directly when the handler is called,
		/// such that all references to the boxed object reflect the potentially updated value.
		/// In the above example, if the method associated with the handler updates the passed (boxed) value to 10, both <c>box</c> and <c>arr[0]</c>
		/// now reflect the value 10. Note that the original <c>val</c> is not updated, since boxing always copies the value into the new boxed value object.
		/// </para>
		/// <para>
		/// If <c>directBoxValueAccess</c> is <c>false</c> (default), the boxed value object in the arguments array is replaced with a "reboxed" value object,
		/// such that potential updates to the value are reflected only in the arguments array.
		/// In the above example, if the method associated with the handler updates the passed (boxed) value to 10, only <c>arr[0]</c> now reflects the value 10.
		/// </para>
		/// </remarks>
		public static FastInvokeHandler GetHandler(MethodInfo methodInfo, bool directBoxValueAccess = false)
		{
			var dynamicMethod = new DynamicMethodDefinition($"FastInvoke_{methodInfo.Name}_{(directBoxValueAccess ? "direct" : "indirect")}", typeof(object), [typeof(object), typeof(object[])]);
			var il = dynamicMethod.GetILGenerator();
			var generateLocalBoxObject = true;

			if (!methodInfo.IsStatic)
			{
				EmitRecipe(il, RustKernel.InvokerLoadTarget, null, 0, null, ref generateLocalBoxObject);
				EmitUnboxIfNeeded(il, methodInfo.DeclaringType);
			}

			var ps = methodInfo.GetParameters();
			for (var i = 0; i < ps.Length; i++)
			{
				var argType = ps[i].ParameterType;
				var argIsByRef = argType.IsByRef;
				if (argIsByRef)
					argType = argType.GetElementType();
				var argIsValueType = argType.IsValueType;
				var recipe = RustKernel.InvokerParameter(argIsByRef, argIsValueType, directBoxValueAccess);
				EmitRecipe(il, recipe, argType, i, null, ref generateLocalBoxObject);
			}

			EmitRecipe(il, methodInfo.IsStatic ? RustKernel.InvokerCall : RustKernel.InvokerCallVirtual,
				null, 0, methodInfo, ref generateLocalBoxObject);

			if (methodInfo.ReturnType == typeof(void))
				EmitRecipe(il, RustKernel.InvokerLoadNull, null, 0, null, ref generateLocalBoxObject);
			else
				EmitBoxIfNeeded(il, methodInfo.ReturnType);

			EmitRecipe(il, RustKernel.InvokerReturn, null, 0, null, ref generateLocalBoxObject);

			var invoder = dynamicMethod.Generate().CreateDelegate<FastInvokeHandler>();
			return invoder;
		}

		internal static void Emit(ILGenerator il, OpCode opcode) => il.Emit(opcode);

		internal static void Emit(ILGenerator il, OpCode opcode, Type type) => il.Emit(opcode, type);

		internal static void EmitCall(ILGenerator il, OpCode opcode, MethodInfo methodInfo) => il.EmitCall(opcode, methodInfo, null);

		static void EmitUnboxIfNeeded(ILGenerator il, Type type)
		{
			if (type.IsValueType)
			{
				var generateLocalBoxObject = true;
				EmitRecipe(il, RustKernel.InvokerUnboxTarget, type, 0, null, ref generateLocalBoxObject);
			}
		}

		static void EmitBoxIfNeeded(ILGenerator il, Type type)
		{
			if (type.IsValueType)
			{
				var generateLocalBoxObject = true;
				EmitRecipe(il, RustKernel.InvokerBoxResult, type, 0, null, ref generateLocalBoxObject);
			}
		}

		internal static void EmitFastInt(ILGenerator il, int value)
		{
			var generateLocalBoxObject = true;
			EmitRecipe(il, RustKernel.InvokerInteger(value), null, value, null, ref generateLocalBoxObject);
		}

		// Reflection and CLR emission remain here; the ordered recipes are planned once in Rust.
		static void EmitRecipe(ILGenerator il, int recipe, Type type, int index, MethodInfo method,
			ref bool generateLocalBoxObject)
		{
			foreach (var instruction in RustKernel.InvokerInstructions(recipe))
			{
				switch (instruction.Operand)
				{
					case RustKernel.InvokerOperand.None:
						Emit(il, instruction.Opcode);
						break;
					case RustKernel.InvokerOperand.Type:
						Emit(il, instruction.Opcode, type);
						break;
					case RustKernel.InvokerOperand.ObjectType:
						Emit(il, instruction.Opcode, typeof(object));
						break;
					case RustKernel.InvokerOperand.Method:
						EmitCall(il, instruction.Opcode, method);
						break;
					case RustKernel.InvokerOperand.Index:
						EmitFastInt(il, index);
						break;
					case RustKernel.InvokerOperand.SignedByte:
						il.Emit(instruction.Opcode, (sbyte)index);
						break;
					case RustKernel.InvokerOperand.Integer:
						il.Emit(instruction.Opcode, index);
						break;
					case RustKernel.InvokerOperand.BoxLocal:
						if (generateLocalBoxObject)
						{
							generateLocalBoxObject = false;
							_ = il.DeclareLocal(typeof(object), false);
						}
						break;
				}
			}
		}
	}
}
