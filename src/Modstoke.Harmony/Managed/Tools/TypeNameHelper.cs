// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using static HarmonyLib.RustKernel;

namespace HarmonyLib.Tools
{
	// Adapted from https://github.com/benaadams/Ben.Demystifier/blob/master/src/Ben.Demystifier/TypeNameHelper.cs
	internal static class TypeNameHelper
	{
		private static readonly Dictionary<Type, uint> BuiltInTypeNames = new Dictionary<Type, uint>
		{
			{typeof(void), 0},
			{typeof(bool), 1},
			{typeof(byte), 2},
			{typeof(char), 3},
			{typeof(decimal), 4},
			{typeof(double), 5},
			{typeof(float), 6},
			{typeof(int), 7},
			{typeof(long), 8},
			{typeof(object), 9},
			{typeof(sbyte), 10},
			{typeof(short), 11},
			{typeof(string), 12},
			{typeof(uint), 13},
			{typeof(ulong), 14},
			{typeof(ushort), 15}
		};

		private static readonly Dictionary<string, uint> FSharpTypeNames = new Dictionary<string, uint>
		{
			{"Unit", 16},
			{"FSharpOption", 17},
			{"FSharpAsync", 18},
			{"FSharpOption`1", 17},
			{"FSharpAsync`1", 18}
		};

		/// <summary>
		///    Pretty print a full type name.
		/// </summary>
		/// <param name="type">The <see cref="Type" />.</param>
		/// <returns>The pretty printed full type name.</returns>
		///
		public static string GetTypeDisplayName(Type type)
		{
			var builder = new TypeNameDescription();
			ProcessType(builder, type);
			return builder.Render();
		}

		private static void ProcessType(TypeNameDescription builder, Type type)
		{
			if (type.IsGenericType)
			{
				var underlyingType = Nullable.GetUnderlyingType(type);
				if (underlyingType != null)
				{
					ProcessType(builder, underlyingType);
					builder.Add(TypeNameKind.Nullable);
				}
				else
				{
					var genericArguments = type.GetGenericArguments();
					ProcessGenericType(builder, type, genericArguments, genericArguments.Length);
				}
			}
			else if (type.IsArray)
				ProcessArrayType(builder, type);
			else if (BuiltInTypeNames.TryGetValue(type, out var builtInName))
				builder.Add(TypeNameKind.Alias, argument: builtInName);
			else if (type.Namespace == nameof(System))
				builder.Add(TypeNameKind.Text, type.Name);
			else if (type.Assembly.ManifestModule.Name == "FSharp.Core.dll"
			      && FSharpTypeNames.TryGetValue(type.Name, out builtInName))
				builder.Add(TypeNameKind.Alias, argument: builtInName);
			else if (type.IsGenericParameter)
				builder.Add(TypeNameKind.Text, type.Name);
			else
				builder.Add(TypeNameKind.Text, type.FullName ?? type.Name);
		}

		private static void ProcessArrayType(TypeNameDescription builder, Type type)
		{
			var innerType = type;
			while (innerType.IsArray) innerType = innerType.GetElementType();

			ProcessType(builder, innerType);

			while (type.IsArray)
			{
				var commas = unchecked(type.GetArrayRank() - 1);
				if (commas < 0) throw new ArgumentOutOfRangeException("repeatCount");
				builder.Add(TypeNameKind.ArrayRank, argument: (uint)commas + 1);
				type = type.GetElementType();
			}
		}

		private static void ProcessGenericType(TypeNameDescription builder, Type type, Type[] genericArguments, int length)
		{
			var offset = 0;
			if (type.IsNested) offset = type.DeclaringType.GetGenericArguments().Length;

			if (type.IsNested)
			{
				ProcessGenericType(builder, type.DeclaringType, genericArguments, offset);
				builder.Add(TypeNameKind.Nested);
			}
			else if (!string.IsNullOrEmpty(type.Namespace))
			{
				builder.Add(TypeNameKind.Namespace, type.Namespace);
			}

			var genericPartIndex = type.Name.IndexOf('`');
			if (genericPartIndex <= 0)
			{
				builder.Add(TypeNameKind.Text, type.Name);
				return;
			}

			if (type.Assembly.ManifestModule.Name == "FSharp.Core.dll"
			 && FSharpTypeNames.TryGetValue(type.Name, out var builtInName))
			{
				builder.Add(TypeNameKind.Alias, argument: builtInName);
				builder.Add(TypeNameKind.GenericStart);
			}
			else
				builder.Add(TypeNameKind.GenericName, type.Name, (uint)genericPartIndex);
			for (var i = offset; i < length; i++)
			{
				ProcessType(builder, genericArguments[i]);
				if (i + 1 == length) continue;

				builder.Add(TypeNameKind.Separator, argument: genericArguments[i + 1].IsGenericParameter ? 1u : 0u);
			}

			builder.Add(TypeNameKind.GenericEnd);
		}
	}
}
