using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib
{
	internal static class PatchArgumentExtensions
	{
		static IEnumerable<HarmonyArgument> AllHarmonyArguments(object[] attributes)
		{
			return attributes.Select(attr =>
			{
				if (attr.GetType().Name != nameof(HarmonyArgument)) return null;
				return AccessTools.MakeDeepCopy<HarmonyArgument>(attr);
			})
			.OfType<HarmonyArgument>();
		}

		internal static HarmonyArgument GetArgumentAttribute(this ParameterInfo parameter)
		{
			var attributes = parameter.GetCustomAttributes(true);
			return AllHarmonyArguments(attributes).FirstOrDefault();
		}

		internal static IEnumerable<HarmonyArgument> GetArgumentAttributes(this MethodInfo method)
		{
			var attributes = method.GetCustomAttributes(true);
			return AllHarmonyArguments(attributes);
		}

		internal static IEnumerable<HarmonyArgument> GetArgumentAttributes(this Type type)
		{
			var attributes = type.GetCustomAttributes(true);
			return AllHarmonyArguments(attributes);
		}

		internal static string GetRealName(this IEnumerable<HarmonyArgument> attributes, string name, string[] originalParameterNames)
		{
			// Enumeration can execute attribute conversion or caller code. Stop at the first
			// matching attribute before asking the native planner to resolve its descriptor.
			var attribute = attributes.FirstOrDefault(p => p.OriginalName == name);
			return RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Name, attribute, originalParameterNames, null).Name;
		}

		internal static int GetArgumentIndex(this MethodInfo patch, string[] originalParameterNames, ParameterInfo patchParam)
		{
			if (patch is DynamicMethod)
				return RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Direct, null, originalParameterNames, patchParam.Name).Index;

			var result = RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Parameter,
				patchParam.GetArgumentAttribute(), originalParameterNames, null);
			if (result.Action == RustKernel.ArgumentMapAction.Complete)
				return result.Index;

			// Read the name and reflect the next scope only when the preceding scope
			// resolves to null. A non-null name that is not found still ends the search.
			var name = patchParam.Name;
			if (patch is null)
				return RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Fallback, null, originalParameterNames, name).Index;

			var attribute = patch.GetArgumentAttributes().FirstOrDefault(p => p.OriginalName == name);
			result = RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Method, attribute, originalParameterNames, name);
			if (result.Action == RustKernel.ArgumentMapAction.Complete)
				return result.Index;

			var type = patch.DeclaringType;
			attribute = type is null ? null : type.GetArgumentAttributes().FirstOrDefault(p => p.OriginalName == name);
			return RustKernel.ResolveArgumentMap(RustKernel.ArgumentMapStage.Type, attribute, originalParameterNames, name).Index;
		}
	}
}
