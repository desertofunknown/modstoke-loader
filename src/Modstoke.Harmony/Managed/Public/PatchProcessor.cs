using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib.Internal.Patching;
using HarmonyLib.Public.Patching;

namespace HarmonyLib
{
	/// <summary>A PatchProcessor handles patches on a method/constructor</summary>
	///
	/// <remarks>Creates an empty patch processor</remarks>
	/// <param name="instance">The Harmony instance</param>
	/// <param name="original">The original method/constructor</param>
	///
	public class PatchProcessor(Harmony instance, MethodBase original)
	{
		readonly Harmony instance = instance;
		readonly MethodBase original = original;

		HarmonyMethod prefix;
		HarmonyMethod postfix;
		HarmonyMethod transpiler;
		HarmonyMethod finalizer;
		HarmonyMethod ilmanipulator;

		internal static readonly object locker = new();

		/// <summary>Adds a prefix</summary>
		/// <param name="prefix">The prefix as a <see cref="HarmonyMethod"/></param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddPrefix(HarmonyMethod prefix)
		{
			this.prefix = prefix;
			return this;
		}

		/// <summary>Adds a prefix</summary>
		/// <param name="fixMethod">The prefix method</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddPrefix(MethodInfo fixMethod)
		{
			prefix = new HarmonyMethod(fixMethod);
			return this;
		}

		/// <summary>Adds a postfix</summary>
		/// <param name="postfix">The postfix as a <see cref="HarmonyMethod"/></param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddPostfix(HarmonyMethod postfix)
		{
			this.postfix = postfix;
			return this;
		}

		/// <summary>Adds a postfix</summary>
		/// <param name="fixMethod">The postfix method</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddPostfix(MethodInfo fixMethod)
		{
			postfix = new HarmonyMethod(fixMethod);
			return this;
		}

		/// <summary>Adds a transpiler</summary>
		/// <param name="transpiler">The transpiler as a <see cref="HarmonyMethod"/></param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddTranspiler(HarmonyMethod transpiler)
		{
			this.transpiler = transpiler;
			return this;
		}

		/// <summary>Adds a transpiler</summary>
		/// <param name="fixMethod">The transpiler method</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddTranspiler(MethodInfo fixMethod)
		{
			transpiler = new HarmonyMethod(fixMethod);
			return this;
		}

		/// <summary>Adds a finalizer</summary>
		/// <param name="finalizer">The finalizer as a <see cref="HarmonyMethod"/></param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddFinalizer(HarmonyMethod finalizer)
		{
			this.finalizer = finalizer;
			return this;
		}

		/// <summary>Adds a finalizer</summary>
		/// <param name="fixMethod">The finalizer method</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddFinalizer(MethodInfo fixMethod)
		{
			finalizer = new HarmonyMethod(fixMethod);
			return this;
		}

		/// <summary>Adds an ilmanipulator</summary>
		/// <param name="ilmanipulator">The ilmanipulator as a <see cref="HarmonyMethod"/></param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddILManipulator(HarmonyMethod ilmanipulator)
		{
			this.ilmanipulator = ilmanipulator;
			return this;
		}

		/// <summary>Adds an ilmanipulator</summary>
		/// <param name="fixMethod">The ilmanipulator method</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor AddILManipulator(MethodInfo fixMethod)
		{
			ilmanipulator = new HarmonyMethod(fixMethod);
			return this;
		}

		/// <summary>Gets all patched original methods in the appdomain</summary>
		/// <returns>An enumeration of patched method/constructor</returns>
		///
		public static IEnumerable<MethodBase> GetAllPatchedMethods()
		{
			return PatchManager.GetPatchedMethods();
		}

		/// <summary>Applies all registered patches</summary>
		/// <returns>The generated replacement method</returns>
		///
		public MethodInfo Patch()
		{
			if (original is null)
				throw new NullReferenceException($"Null method for {instance.Id}");

			if (original.IsDeclaredMember() is false)
			{
				var declaredMember = original.GetDeclaredMember();
				throw new ArgumentException($"You can only patch implemented methods/constructors. Patch the declared method {declaredMember.FullDescription()} instead.");
			}

			lock (locker)
			{
				var patchInfo = original.ToPatchInfo();

				return ApplyPlan(patchInfo, RustKernel.ProcessorOperation.Add, HarmonyPatchType.All, null, null);
			}
		}

		/// <summary>Unpatches patches of a given type and/or Harmony ID</summary>
		/// <param name="type">The <see cref="HarmonyPatchType"/> patch type</param>
		/// <param name="harmonyID">Harmony ID or <c>*</c> for any</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor Unpatch(HarmonyPatchType type, string harmonyID)
		{
			lock (locker)
			{
				var patchInfo = original.ToPatchInfo();

				ApplyPlan(patchInfo, RustKernel.ProcessorOperation.RemoveOwner, type, harmonyID, null);
				return this;
			}
		}

		/// <summary>Unpatches a specific patch</summary>
		/// <param name="patch">The method of the patch</param>
		/// <returns>A <see cref="PatchProcessor"/> for chaining calls</returns>
		///
		public PatchProcessor Unpatch(MethodInfo patch)
		{
			lock (locker)
			{
				var patchInfo = original.ToPatchInfo();

				ApplyPlan(patchInfo, RustKernel.ProcessorOperation.RemoveMethod, HarmonyPatchType.All, null, patch);
				return this;
			}
		}

		// Execute the recipe under locker. Read each mutable HarmonyMethod only when its
		// category is reached; a failed category leaves earlier writes visible as before.
		MethodInfo ApplyPlan(PatchInfo patchInfo, RustKernel.ProcessorOperation operation,
			HarmonyPatchType type, string harmonyID, MethodInfo patch)
		{
			Span<RustKernel.ProcessorAction> actions = stackalloc RustKernel.ProcessorAction[7];
			var count = RustKernel.PlanProcessor(operation, type, actions);
			MethodInfo replacement = null;
			foreach (var action in actions[..count])
			{
				switch (action)
				{
					case RustKernel.ProcessorAction.AddPrefix: patchInfo.AddPrefixes(instance.Id, prefix); break;
					case RustKernel.ProcessorAction.AddPostfix: patchInfo.AddPostfixes(instance.Id, postfix); break;
					case RustKernel.ProcessorAction.AddTranspiler: patchInfo.AddTranspilers(instance.Id, transpiler); break;
					case RustKernel.ProcessorAction.AddFinalizer: patchInfo.AddFinalizers(instance.Id, finalizer); break;
					case RustKernel.ProcessorAction.AddILManipulator: patchInfo.AddILManipulators(instance.Id, ilmanipulator); break;
					case RustKernel.ProcessorAction.RemovePrefix: patchInfo.RemovePrefix(harmonyID); break;
					case RustKernel.ProcessorAction.RemovePostfix: patchInfo.RemovePostfix(harmonyID); break;
					case RustKernel.ProcessorAction.RemoveTranspiler: patchInfo.RemoveTranspiler(harmonyID); break;
					case RustKernel.ProcessorAction.RemoveFinalizer: patchInfo.RemoveFinalizer(harmonyID); break;
					case RustKernel.ProcessorAction.RemoveILManipulator: patchInfo.RemoveILManipulator(harmonyID); break;
					case RustKernel.ProcessorAction.RemoveMethod: patchInfo.RemovePatch(patch); break;
					case RustKernel.ProcessorAction.UpdateWrapper: replacement = PatchFunctions.UpdateWrapper(original, patchInfo); break;
					case RustKernel.ProcessorAction.RememberReplacement: PatchManager.AddReplacementOriginal(original, replacement); break;
				}
			}
			return replacement;
		}

		/// <summary>Gets patch information on an original</summary>
		/// <param name="method">The original method/constructor</param>
		/// <returns>The patch information as <see cref="Patches"/></returns>
		///
		public static Patches GetPatchInfo(MethodBase method)
		{
			PatchInfo patchInfo = method.GetPatchInfo();
			if (patchInfo is null) return null;
			return new Patches(patchInfo.prefixes, patchInfo.postfixes, patchInfo.transpilers, patchInfo.finalizers, patchInfo.ilmanipulators);
		}

		/// <summary>Sort patch methods by their priority rules</summary>
		/// <param name="original">The original method</param>
		/// <param name="patches">Patches to sort</param>
		/// <returns>The sorted patch methods</returns>
		///
		public static List<MethodInfo> GetSortedPatchMethods(MethodBase original, Patch[] patches) => PatchFunctions.GetSortedPatchMethods(original, patches, false);

		/// <summary>Gets Harmony version for all active Harmony instances</summary>
		/// <param name="currentVersion">[out] The current Harmony version</param>
		/// <returns>A dictionary containing assembly version keyed by Harmony ID</returns>
		///
		public static Dictionary<string, Version> VersionInfo(out Version currentVersion)
		{
			currentVersion = typeof(Harmony).Assembly.GetName().Version;
			var assemblies = new List<(string Owner, Assembly Assembly)>();
			void AddAssembly(Patch fix)
			{
				var owner = fix.owner;
				var assembly = fix.PatchMethod.DeclaringType.Assembly;
				// Dictionary assignment used to reject a null owner after resolving the value.
				if (owner is null) throw new ArgumentNullException("key");
				assemblies.Add((owner, assembly));
			}
			GetAllPatchedMethods().Do(method =>
			{
				var info = method.GetPatchInfo();
				info.prefixes.Do(AddAssembly);
				info.postfixes.Do(AddAssembly);
				info.transpilers.Do(AddAssembly);
				info.finalizers.Do(AddAssembly);
				info.ilmanipulators.Do(AddAssembly);
			});

			var result = new Dictionary<string, Version>();
			foreach (var entry in RustKernel.ReduceProcessorOwners(assemblies))
			{
				var assemblyName = assemblies[(int)entry.Last].Assembly.GetReferencedAssemblies().FirstOrDefault(a => a.FullName.StartsWith("0Harmony, Version", StringComparison.Ordinal));
				if (assemblyName is not null)
					result[assemblies[(int)entry.First].Owner] = assemblyName.Version;
			}
			return result;
		}

		/// <summary>Creates a new empty <see cref="ILGenerator">generator</see> to use when reading method bodies</summary>
		/// <returns>A new <see cref="ILGenerator"/></returns>
		///
		public static ILGenerator CreateILGenerator()
		{
			var method = new DynamicMethodDefinition($"ILGenerator_{Guid.NewGuid()}", typeof(void), []);
			return method.GetILGenerator();
		}

		/// <summary>Creates a new <see cref="ILGenerator">generator</see> matching the method/constructor to use when reading method bodies</summary>
		/// <param name="original">The original method/constructor to copy method information from</param>
		/// <returns>A new <see cref="ILGenerator"/></returns>
		///
		public static ILGenerator CreateILGenerator(MethodBase original)
		{
			var returnType = original is MethodInfo m ? m.ReturnType : typeof(void);
			var parameterTypes = original.GetParameters().Select(pi => pi.ParameterType).ToList();
			if (original.IsStatic is false) parameterTypes.Insert(0, original.DeclaringType);
			var method = new DynamicMethodDefinition($"ILGenerator_{original.Name}", returnType, [.. parameterTypes]);
			return method.GetILGenerator();
		}

		/// <summary>Returns the methods unmodified list of code instructions</summary>
		/// <param name="original">The original method/constructor</param>
		/// <param name="generator">Optionally an existing generator that will be used to create all local variables and labels contained in the result (if not specified, an internal generator is used)</param>
		/// <returns>A list containing all the original <see cref="CodeInstruction"/></returns>
		///
		public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, ILGenerator generator = null) => PatchFunctions.ApplyTranspilers(original, generator ?? CreateILGenerator(original)).ToList();

		/// <summary>Returns the methods unmodified list of code instructions</summary>
		/// <param name="original">The original method/constructor</param>
		/// <param name="generator">A new generator that now contains all local variables and labels contained in the result</param>
		/// <returns>A list containing all the original <see cref="CodeInstruction"/></returns>
		///
		public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, out ILGenerator generator)
		{
			generator = CreateILGenerator(original);
			return PatchFunctions.ApplyTranspilers(original, generator).ToList();
		}

		/// <summary>Returns the methods current list of code instructions after all existing transpilers have been applied</summary>
		/// <param name="original">The original method/constructor</param>
		/// <param name="maxTranspilers">Apply only the first count of transpilers</param>
		/// <param name="generator">Optionally an existing generator that will be used to create all local variables and labels contained in the result (if not specified, an internal generator is used)</param>
		/// <returns>A list of <see cref="CodeInstruction"/></returns>
		///
		public static List<CodeInstruction> GetCurrentInstructions(MethodBase original, int maxTranspilers = int.MaxValue, ILGenerator generator = null) => PatchFunctions.ApplyTranspilers(original, generator ?? CreateILGenerator(original), maxTranspilers).ToList();

		/// <summary>Returns the methods current list of code instructions after all existing transpilers have been applied</summary>
		/// <param name="original">The original method/constructor</param>
		/// <param name="generator">A new generator that now contains all local variables and labels contained in the result</param>
		/// <param name="maxTranspilers">Apply only the first count of transpilers</param>
		/// <returns>A list of <see cref="CodeInstruction"/></returns>
		///
		public static List<CodeInstruction> GetCurrentInstructions(MethodBase original, out ILGenerator generator, int maxTranspilers = int.MaxValue)
		{
			generator = CreateILGenerator(original);
			return PatchFunctions.ApplyTranspilers(original, generator, maxTranspilers).ToList();
		}

		/// <summary>A low level way to read the body of a method. Used for quick searching in methods</summary>
		/// <param name="method">The original method</param>
		/// <returns>All instructions as opcode/operand pairs</returns>
		///
		public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method)
		{
			var original = method.GetMethodPatcher().CopyOriginal();
			if (original == null)
				return null;
			return new ILManipulator(original.Definition.Body, false).GetRawInstructions();
		}

		/// <summary>A low level way to read the body of a method. Used for quick searching in methods</summary>
		/// <param name="method">The original method</param>
		/// <param name="generator">An existing generator that will be used to create all local variables and labels contained in the result</param>
		/// <returns>All instructions as opcode/operand pairs</returns>
		///
		public static IEnumerable<KeyValuePair<OpCode, object>> ReadMethodBody(MethodBase method, ILGenerator generator)
		{
			var original = method.GetMethodPatcher().CopyOriginal();
			if (original == null)
				return null;
			var manipulator = new ILManipulator(original.Definition.Body, false);
			// Force label generation
			_ = manipulator.GetInstructions(generator);
			return manipulator.GetRawInstructions();
		}
	}
}
