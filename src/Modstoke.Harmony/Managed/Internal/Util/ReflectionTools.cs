using MonoMod.Utils;
using System;
using System.Reflection;
using System.Reflection.Emit;
using static HarmonyLib.AccessTools;

namespace HarmonyLib.Internal.Util
{
	internal class ReflectionTools
	{
		internal static readonly bool isWindows = Environment.OSVersion.Platform.Equals(PlatformID.Win32NT);

		internal struct TypeAndName
		{
			internal Type type;
			internal string name;
		}

		internal static TypeAndName TypColonName(string typeColonName)
		{
			if (typeColonName is null)
				throw new ArgumentNullException(nameof(typeColonName));
			var separator = RustKernel.ReflectionMemberSeparator(typeColonName);
			if (separator < 0)
				throw new ArgumentException($" must be specified as 'Namespace.Type1.Type2:MemberName", nameof(typeColonName));
			// Materialize both halves before TypeByName, as Split did, without an
			// intermediate array or unused segments for malformed input.
			var typeName = typeColonName[..separator];
			var memberName = typeColonName[(separator + 1)..];
			return new TypeAndName() { type = TypeByName(typeName), name = memberName };
		}

		internal static void ValidateFieldType<F>(FieldInfo fieldInfo)
		{
			var returnType = typeof(F);
			var fieldType = fieldInfo.FieldType;
			if (returnType == fieldType)
				return;
			if (fieldType.IsEnum)
			{
				var underlyingType = Enum.GetUnderlyingType(fieldType);
				if (returnType != underlyingType)
					throw new ArgumentException("FieldRefAccess return type must be the same as FieldType or " +
						$"FieldType's underlying integral type ({underlyingType}) for enum types");
			}
			else if (fieldType.IsValueType)
			{
				// Boxing/unboxing is not allowed for ref values of value types.
				throw new ArgumentException("FieldRefAccess return type must be the same as FieldType for value types");
			}
			else
			{
				if (returnType.IsAssignableFrom(fieldType) is false)
					throw new ArgumentException("FieldRefAccess return type must be assignable from FieldType for reference types");
			}
		}

		internal static FieldRef<T, F> FieldRefAccess<T, F>(FieldInfo fieldInfo, bool needCastclass)
		{
			ValidateFieldType<F>(fieldInfo);
			var delegateInstanceType = typeof(T);
			var declaringType = fieldInfo.DeclaringType;

			var dm = new DynamicMethodDefinition($"__refget_{delegateInstanceType.Name}_fi_{fieldInfo.Name}",
				typeof(F).MakeByRefType(), [delegateInstanceType]);

			var il = dm.GetILGenerator();
			// Backwards compatibility: This supports static fields, even those defined in structs.
			// Native recipes omit instance loading/casting for static fields, and retain
			// the runtime cast when T is a parent class or interface of the declaring type.
			EmitFieldAccess(il, fieldInfo, declaringType, fieldInfo.IsStatic, needCastclass);

			return dm.Generate().CreateDelegate<FieldRef<T, F>>();
		}

		internal static StructFieldRef<T, F> StructFieldRefAccess<T, F>(FieldInfo fieldInfo) where T : struct
		{
			ValidateFieldType<F>(fieldInfo);

			var dm = new DynamicMethodDefinition($"__refget_{typeof(T).Name}_struct_fi_{fieldInfo.Name}",
				typeof(F).MakeByRefType(), [typeof(T).MakeByRefType()]);

			var il = dm.GetILGenerator();
			EmitFieldAccess(il, fieldInfo, null, isStatic: false, needCastclass: false);

			return dm.Generate().CreateDelegate<StructFieldRef<T, F>>();
		}

		internal static FieldRef<F> StaticFieldRefAccess<F>(FieldInfo fieldInfo)
		{
			if (fieldInfo.IsStatic is false)
				throw new ArgumentException("Field must be static");
			ValidateFieldType<F>(fieldInfo);

			var dm = new DynamicMethodDefinition($"__refget_{fieldInfo.DeclaringType?.Name ?? "null"}_static_fi_{fieldInfo.Name}",
				typeof(F).MakeByRefType(), []);

			var il = dm.GetILGenerator();
			EmitFieldAccess(il, fieldInfo, null, isStatic: true, needCastclass: false);

			return dm.Generate().CreateDelegate<FieldRef<F>>();
		}

		private static void EmitFieldAccess(ILGenerator il, FieldInfo fieldInfo, Type declaringType, bool isStatic, bool needCastclass)
		{
			foreach (var opcode in RustKernel.ReflectionFieldRecipe(isStatic, needCastclass))
			{
				switch (opcode.OperandType)
				{
					case OperandType.InlineField: il.Emit(opcode, fieldInfo); break;
					case OperandType.InlineType: il.Emit(opcode, declaringType); break;
					default: il.Emit(opcode); break;
				}
			}
		}

		internal static FieldInfo GetInstanceField(Type type, string fieldName)
		{
			var fieldInfo = Field(type, fieldName);
			if (fieldInfo is null)
				throw new MissingFieldException(type.Name, fieldName);
			if (fieldInfo.IsStatic)
				throw new ArgumentException("Field must not be static");
			return fieldInfo;
		}

		internal static bool FieldRefNeedsClasscast(Type delegateInstanceType, Type declaringType)
		{
			var needCastclass = false;
			if (delegateInstanceType != declaringType)
			{
				needCastclass = delegateInstanceType.IsAssignableFrom(declaringType);
				if (needCastclass is false && declaringType.IsAssignableFrom(delegateInstanceType) is false)
					throw new ArgumentException("FieldDeclaringType must be assignable from or to T (FieldRefAccess instance type) - " +
						"\"instanceOfT is FieldDeclaringType\" must be possible");
			}
			return needCastclass;
		}

		internal static void ValidateStructField<T, F>(FieldInfo fieldInfo) where T : struct
		{
			if (fieldInfo.IsStatic)
				throw new ArgumentException("Field must not be static");
			if (fieldInfo.DeclaringType != typeof(T))
				throw new ArgumentException("FieldDeclaringType must be T (StructFieldRefAccess instance type)");
		}
	}
}
