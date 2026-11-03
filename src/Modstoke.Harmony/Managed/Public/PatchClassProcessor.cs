using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib.Public.Patching;
using HarmonyLib.Tools;

namespace HarmonyLib
{
	/// <summary>A PatchClassProcessor used to turn <see cref="HarmonyAttribute"/> on a class/type into patches</summary>
	///
	public class PatchClassProcessor
	{
		readonly Harmony instance;

		readonly Type containerType;
		readonly HarmonyMethod containerAttributes;
		readonly Dictionary<Type, MethodInfo> auxilaryMethods;

		readonly List<AttributePatch> patchMethods;

		static readonly List<Type> auxilaryTypes =
		[
			typeof(HarmonyPrepare),
			typeof(HarmonyCleanup),
			typeof(HarmonyTargetMethod),
			typeof(HarmonyTargetMethods)
		];

		/// <summary name="Category">Name of the patch class's category</summary>
		public string Category { get; set; }

		/// <summary>Creates a patch class processor by pointing out a class. Similar to PatchAll() but without searching through all classes.</summary>
		/// <param name="instance">The Harmony instance</param>
		/// <param name="type">The class to process (need to have at least a [HarmonyPatch] attribute if allowUnannotatedType is set to <b>false</b>)</param>
		///
		// ReSharper disable once IntroduceOptionalParameters.Global => cannot rewrite public ctor for binary compatibility with Harmony 2
		public PatchClassProcessor(Harmony instance, Type type): this(instance, type, false) {}

		/// <summary>Creates a patch class processor by pointing out a class. Similar to PatchAll() but without searching through all classes.</summary>
		/// <param name="instance">The Harmony instance</param>
		/// <param name="type">The class to process (need to have at least a [HarmonyPatch] attribute if allowUnannotatedType is set to <b>false</b>)</param>
		/// <param name="allowUnannotatedType">If <b>true</b>, the type doesn't need to have any <see cref="HarmonyPatch"/> attributes present for processing</param>
		///
		public PatchClassProcessor(Harmony instance, Type type, bool allowUnannotatedType)
		{
			if (instance is null)
				throw new ArgumentNullException(nameof(instance));
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			this.instance = instance;
			containerType = type;

			var harmonyAttributes = HarmonyMethodExtensions.GetFromType(type);
			if (!allowUnannotatedType && (harmonyAttributes is null || harmonyAttributes.Count == 0))
				return;

			containerAttributes = HarmonyMethod.Merge(harmonyAttributes);
			containerAttributes.methodType ??= MethodType.Normal;

			Category = containerAttributes.category;

			auxilaryMethods = [];
			foreach (var auxType in auxilaryTypes)
			{
				var method = PatchTools.GetPatchMethod(containerType, auxType.FullName);
				if (method is not null) auxilaryMethods[auxType] = method;
			}

			patchMethods = PatchTools.GetPatchMethods(containerType, containerAttributes.declaringType != null);
			foreach (var patchMethod in patchMethods)
			{
				var method = patchMethod.info.method;
				patchMethod.info = containerAttributes.Merge(patchMethod.info);
				patchMethod.info.method = method;
			}
		}

		/// <summary>Applies the patches</summary>
		/// <returns>A list of all created replacement methods or null if patch class is not annotated</returns>
		///
		public List<MethodInfo> Patch()
		{
			if (containerAttributes is null)
				return null;
			return ProcessClass(false);
		}

		/// <summary>REmoves the patches</summary>
		///
		public void Unpatch()
		{
			if (containerAttributes is null)
				return;
			_ = ProcessClass(true);
		}

		List<MethodInfo> ProcessClass(bool unpatch)
		{
			Exception exception = null;
			var replacements = new List<MethodInfo>();
			List<MethodBase> originals = null;
			MethodBase lastOriginal = null;
			var prepared = false;
			var step = RustKernel.AdvancePatchClass(RustKernel.ClassAction.Start, unpatch, false, prepared, 0);
			while (step.Action != RustKernel.ClassAction.Done)
			{
				var failed = false;
				try
				{
					switch (step.Action)
					{
						case RustKernel.ClassAction.Prepare:
							prepared = RunMethod<HarmonyPrepare, bool>(true, false);
							break;
						case RustKernel.ClassAction.Targets:
							originals = GetBulkMethods();
							break;
						case RustKernel.ClassAction.ReverseSingle:
							lastOriginal = originals[0];
							goto case RustKernel.ClassAction.Reverse;
						case RustKernel.ClassAction.Reverse:
							ReversePatch(ref lastOriginal);
							break;
						case RustKernel.ClassAction.Bulk:
							replacements = BulkPatch(originals, ref lastOriginal, unpatch);
							break;
						case RustKernel.ClassAction.Attributes:
							replacements = PatchWithAttributes(ref lastOriginal, unpatch);
							break;
						case RustKernel.ClassAction.Cleanup:
							RunMethod<HarmonyCleanup>(ref exception, exception);
							break;
						case RustKernel.ClassAction.CleanupEmpty:
							RunMethod<HarmonyCleanup>(ref exception);
							break;
						case RustKernel.ClassAction.Report:
							ReportException(exception, lastOriginal);
							break;
					}
				}
				catch (Exception ex) when (step.CatchException != 0)
				{
					exception = ex;
					failed = true;
				}
				step = RustKernel.AdvancePatchClass(step.Action, unpatch, failed, prepared, originals?.Count ?? 0);
			}
			return replacements;
		}

		void ReversePatch(ref MethodBase lastOriginal)
		{
			for (var i = 0; i < patchMethods.Count; i++)
			{
				var patchMethod = patchMethods[i];
				if (patchMethod.type == HarmonyPatchType.ReversePatch)
				{
					var annotatedOriginal = patchMethod.info.GetOriginalMethod();
					if (annotatedOriginal is not null)
						lastOriginal = annotatedOriginal;

					if (lastOriginal is null)
					{
						if (patchMethod.info.optional == true)
						{
							Logger.Log(Logger.LogChannel.Warn, () => $"Skipping optional reverse patch {patchMethod.info.method.FullDescription()} - target method not found");
							continue;
						}

						throw new ArgumentException($"Undefined target method for reverse patch method {patchMethod.info.method.FullDescription()}");
					}

					var reversePatcher = instance.CreateReversePatcher(lastOriginal, patchMethod.info);
					lock (PatchProcessor.locker)
						_ = reversePatcher.Patch();
				}
			}
		}

		List<MethodInfo> BulkPatch(List<MethodBase> originals, ref MethodBase lastOriginal, bool unpatch)
		{
			var jobs = new PatchJobs<MethodInfo>();
			for (var i = 0; i < originals.Count; i++)
			{
				lastOriginal = originals[i];
				var job = jobs.GetJob(lastOriginal);
				var plan = RustKernel.PlanClassAnnotations(patchMethods);
				for (var patchIndex = 0; patchIndex < plan.ValidCount; patchIndex++)
					job.AddPatch(patchMethods[patchIndex]);
				if (plan.Error != 0)
				{
					var note = "You cannot combine TargetMethod, TargetMethods or [HarmonyPatchAll] with individual annotations";
					var info = patchMethods[(int)plan.ValidCount].info;
					throw new ArgumentException(plan.Error switch
					{
						1 => $"{note} [{info.methodName}]",
						2 => $"{note} [{info.methodType}]",
						3 => $"{note} [{info.argumentTypes.Description()}]",
						_ => throw new NullReferenceException()
					});
				}
			}
			foreach (var job in jobs.GetJobs())
			{
				lastOriginal = job.original;
				if (unpatch)
					ProcessUnpatchJob(job);
				else
					ProcessPatchJob(job);
			}
			return jobs.GetReplacements();
		}

		List<MethodInfo> PatchWithAttributes(ref MethodBase lastOriginal, bool unpatch)
		{
			var jobs = new PatchJobs<MethodInfo>();
			foreach (var patchMethod in patchMethods)
			{
				lastOriginal = patchMethod.info.GetOriginalMethod();
				if (lastOriginal is null)
				{
					if (patchMethod.info.optional == true)
					{
						Logger.Log(Logger.LogChannel.Warn, () => $"Skipping optional patch {patchMethod.info.method.FullDescription()} - target method not found");
						continue;
					}

					throw new ArgumentException($"Undefined target method for patch method {patchMethod.info.method.FullDescription()}");
				}

				var job = jobs.GetJob(lastOriginal);
				job.AddPatch(patchMethod);
			}
			foreach (var job in jobs.GetJobs())
			{
				lastOriginal = job.original;
				if (unpatch)
					ProcessUnpatchJob(job);
				else
					ProcessPatchJob(job);
			}
			return jobs.GetReplacements();
		}

		void ProcessPatchJob(PatchJobs<MethodInfo>.Job job)
		{
			MethodInfo replacement = default;

			var individualPrepareResult = RunMethod<HarmonyPrepare, bool>(true, false, null, job.original);
			Exception exception = null;
			if (individualPrepareResult)
			{
				lock (PatchProcessor.locker)
				{
					try
					{
						var patchInfo = job.original.ToPatchInfo();

						patchInfo.AddPrefixes(instance.Id, [.. job.prefixes]);
						patchInfo.AddPostfixes(instance.Id, [.. job.postfixes]);
						patchInfo.AddTranspilers(instance.Id, [.. job.transpilers]);
						patchInfo.AddFinalizers(instance.Id, [.. job.finalizers]);
						patchInfo.AddILManipulators(instance.Id, [.. job.ilmanipulators]);

						replacement = PatchFunctions.UpdateWrapper(job.original, patchInfo);
						PatchManager.AddReplacementOriginal(job.original, replacement);
					}
					catch (Exception ex)
					{
						exception = ex;
					}
				}
			}
			RunMethod<HarmonyCleanup>(ref exception, job.original, exception);
			ReportException(exception, job.original);
			job.replacement = replacement;
		}

		void ProcessUnpatchJob(PatchJobs<MethodInfo>.Job job)
		{
			var patchInfo = job.original.GetPatchInfo() ?? new PatchInfo();

			var hasBody = job.original.HasMethodBody();
			if (hasBody)
			{
				job.postfixes.Do(patch => patchInfo.RemovePatch(patch.method));
				job.prefixes.Do(patch => patchInfo.RemovePatch(patch.method));
			}
			job.transpilers.Do(patch => patchInfo.RemovePatch(patch.method));
			if (hasBody)
				job.finalizers.Do(patch => patchInfo.RemovePatch(patch.method));

			var replacement = PatchFunctions.UpdateWrapper(job.original, patchInfo);
			PatchManager.AddReplacementOriginal(job.original, replacement);
		}

		List<MethodBase> GetBulkMethods()
		{
			var result = new List<MethodBase>();
			var action = RustKernel.AdvanceClassTargets(RustKernel.ClassTargetAction.Start, false);
			while (action != RustKernel.ClassTargetAction.Done)
			{
				var present = false;
				List<MethodBase> validate = null;
				switch (action)
				{
					case RustKernel.ClassTargetAction.InspectAll:
						present = containerType.GetCustomAttributes(true).Any(a => a.GetType().FullName == PatchTools.harmonyPatchAllFullName);
						break;
					case RustKernel.ClassTargetAction.Declared:
						var type = containerAttributes.declaringType;
						if (type is null)
							throw new ArgumentException($"Using {PatchTools.harmonyPatchAllFullName} requires an additional attribute for specifying the Class/Type");
						result.AddRange(AccessTools.GetDeclaredConstructors(type).Cast<MethodBase>());
						result.AddRange(AccessTools.GetDeclaredMethods(type).Cast<MethodBase>());
						var props = AccessTools.GetDeclaredProperties(type);
						result.AddRange(props.Select(prop => prop.GetGetMethod(true)).Where(method => method is not null).Cast<MethodBase>());
						result.AddRange(props.Select(prop => prop.GetSetMethod(true)).Where(method => method is not null).Cast<MethodBase>());
						break;
					case RustKernel.ClassTargetAction.Many:
						var targetMethods = RunMethod<HarmonyTargetMethods, IEnumerable<MethodBase>>(null, null);
						present = targetMethods is object;
						if (present)
						{
							result = [.. targetMethods];
							validate = result;
						}
						break;
					case RustKernel.ClassTargetAction.Single:
						var targetMethod = RunMethod<HarmonyTargetMethod, MethodBase>(null, null, method => method is null ? "null" : null);
						if (targetMethod is not null)
							result.Add(targetMethod);
						break;
					case RustKernel.ClassTargetAction.InvalidElements:
						if (auxilaryMethods.TryGetValue(typeof(HarmonyTargetMethods), out var auxiliaryMethod))
							throw new Exception($"Method {auxiliaryMethod.FullDescription()} returned an unexpected result: some element was null");
						throw new Exception("Some method returned an unexpected result: some element was null");
				}
				action = RustKernel.AdvanceClassTargets(action, present, validate);
			}
			return result;
		}

		void ReportException(Exception exception, MethodBase original)
		{
			if (exception is null) return;

			Logger.Log(Logger.LogChannel.Debug, () =>
			{
				_ = Harmony.VersionInfo(out var currentVersion);
				var sb = new StringBuilder();
				sb.AppendLine($"### Exception from user \"{instance.Id}\", Harmony v{currentVersion}");
				sb.AppendLine($"### Original: {original?.FullDescription() ?? "NULL"}");
				sb.AppendLine($"### Patch class: {containerType.FullDescription()}");
				var logException = exception;
				if (logException is HarmonyException hEx) logException = hEx.InnerException;
				var exStr = logException.ToString();
				while (exStr.Contains("\n\n"))
					exStr = exStr.Replace("\n\n", "\n");
				exStr = exStr.Split('\n').Join(line => $"### {line}", "\n");
				sb.AppendLine(exStr.Trim());
				return sb.ToString();
			});

			if (exception is HarmonyException) throw exception; // assume HarmonyException already wraps the actual exception
			throw new HarmonyException($"Patching exception in method {original.FullDescription()}", exception);
		}

		[SuppressMessage("Style", "IDE0300")]
		T RunMethod<S, T>(T defaultIfNotExisting, T defaultIfFailing, Func<T, string> failOnResult = null, params object[] parameters)
		{
			if (auxilaryMethods.TryGetValue(typeof(S), out var method))
			{
				var input = (parameters ?? []).Union(new object[] { instance }).ToArray();
				var actualParameters = AccessTools.ActualParameters(method, input);

				if (method.ReturnType != typeof(void) && typeof(T).IsAssignableFrom(method.ReturnType) is false)
					throw new Exception($"Method {method.FullDescription()} has wrong return type (should be assignable to {typeof(T).FullName})");

				var result = defaultIfFailing;
				try
				{
					if (method.ReturnType == typeof(void))
					{
						_ = method.Invoke(null, actualParameters);
						result = defaultIfNotExisting;
					}
					else
						result = (T)method.Invoke(null, actualParameters);

					if (failOnResult is not null)
					{
						var error = failOnResult(result);
						if (error is not null)
							throw new Exception($"Method {method.FullDescription()} returned an unexpected result: {error}");
					}
				}
				catch (Exception ex)
				{
					ReportException(ex, method);
				}
				return result;
			}

			return defaultIfNotExisting;
		}

		[SuppressMessage("Style", "IDE0300")]
		void RunMethod<S>(ref Exception exception, params object[] parameters)
		{
			if (auxilaryMethods.TryGetValue(typeof(S), out var method))
			{
				var input = (parameters ?? []).Union(new object[] { instance }).ToArray();
				var actualParameters = AccessTools.ActualParameters(method, input);
				try
				{
					var result = method.Invoke(null, actualParameters);
					if (method.ReturnType == typeof(Exception))
						exception = result as Exception;
				}
				catch (Exception ex)
				{
					ReportException(ex, method);
				}
			}
		}
	}
}
