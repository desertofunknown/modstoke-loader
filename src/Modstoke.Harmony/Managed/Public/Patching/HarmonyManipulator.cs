using HarmonyLib.Internal.Patching;
using HarmonyLib.Internal.RuntimeFixes;
using HarmonyLib.Internal.Util;
using HarmonyLib.Tools;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HarmonyLib.Public.Patching;

/// <summary>
///    IL manipulator to create Harmony-style patches.
/// </summary>
public class HarmonyManipulator
{
	private static readonly MethodInfo GetMethodFromHandle1 =
		typeof(MethodBase).GetMethod("GetMethodFromHandle", new[] {typeof(RuntimeMethodHandle)});

	private static readonly MethodInfo GetMethodFromHandle2 = typeof(MethodBase).GetMethod("GetMethodFromHandle",
		new[] {typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle)});

	// ReSharper disable ConvertToConstant.Local
	private static readonly string RunOriginalParam = "__runOriginal";
	private static readonly string ResultVar = "__result";
	private static readonly string ResultRefVar = "__resultRef";
	private static readonly string ArgsArrayVar = "__args";
	private static readonly string StateVar = "__state";
	private static readonly string ExceptionVar = "__exception";
	private static readonly string ParamIndexPrefix = "__";
	private static readonly string InstanceFieldPrefix = "___";
	// ReSharper restore ConvertToConstant.Local


	private static readonly MethodInfo LogPatchExceptionMethod =
		AccessTools.Method(typeof(HarmonyManipulator), nameof(LogPatchException));

	private readonly bool debug;
	private readonly string[] dumpPaths;
	private readonly PatchInfo patchInfo;

	private readonly Dictionary<string, VariableDefinition> variables = new();

	private ILContext ctx;

	private List<PatchContext> finalizers;
	private ILEmitter il;
	private List<PatchContext> ilManipulators;
	private MethodBase original;
	private List<PatchContext> postfixes;
	private List<PatchContext> prefixes;
	private List<PatchContext> transpilers;

	private Type _returnType;
	private Type ReturnType => _returnType ??= AccessTools.GetReturnedType(original);

	/// <summary>
	///    Initializes a new instance of the <see cref="HarmonyManipulator" /> class.
	/// </summary>
	/// <param name="patchInfo">Patches to apply to the body.</param>
	public HarmonyManipulator(PatchInfo patchInfo)
	{
		this.patchInfo = patchInfo;
		debug = patchInfo.Debugging;
		dumpPaths = patchInfo.DebugEmitPaths;
	}

	/// <summary>
	///    Manipulates a <see cref="Mono.Cecil.Cil.MethodBody" /> by applying Harmony patches to it.
	/// </summary>
	/// <param name="original">
	///    Reference to the method that should be considered as original. Used to reference parameter and
	///    return types.
	/// </param>
	/// <param name="patchInfo">Collection of Harmony patches to apply.</param>
	/// <param name="ctx">Method body to manipulate as <see cref="ILContext" /> instance. Should contain instructions to patch.</param>
	/// <remarks>
	///    In most cases you will want to use <see cref="PatchManager.ToPatchInfo" /> to create or obtain global
	///    patch info for the method that contains aggregated info of all Harmony instances.
	/// </remarks>
	public static void Manipulate(MethodBase original, PatchInfo patchInfo, ILContext ctx)
	{
		var writer = new HarmonyManipulator(patchInfo);
		writer.Process(ctx, original);
	}

	/// <summary>
	///    Manipulates IL into a replacement method with Harmony patches applied to it.
	/// </summary>
	/// <param name="original">Original method that contains <see cref="PatchInfo" /> and other metadata.</param>
	/// <param name="ctx">IL to rewrite.</param>
	public static void Manipulate(MethodBase original, ILContext ctx)
	{
		Manipulate(original, original.GetPatchInfo(), ctx);
	}

	private static void LogPatchException(object errorObject, string patch)
	{
		Logger.LogText(Logger.LogChannel.Error, $"Error while running {patch}. Error: {errorObject}");
	}

	/// <summary>
	///    Convert the IL code into a Harmony patch.
	/// </summary>
	/// <param name="ilContext">IL body of the method to rewrite into a Harmony patch.</param>
	/// <param name="originalMethod">Original method to use for context.</param>
	public void Process(ILContext ilContext, MethodBase originalMethod)
	{
		ctx = ilContext;
		original = originalMethod;
		il = new ILEmitter(ctx.IL);
		SortPatches();

		Logger.Log(Logger.LogChannel.Info, () =>
		{
			var sb = new StringBuilder();

			sb.AppendLine(
				$"Patching {original.FullDescription()} with {prefixes.Count} prefixes, {postfixes.Count} postfixes, {transpilers.Count} transpilers, {finalizers.Count} finalizers");

			void Print(ICollection<PatchContext> list, string type)
			{
				if (list.Count == 0)
					return;
				sb.AppendLine($"{list.Count} {type}:");
				foreach (var fix in list)
					sb.AppendLine($"* {fix.method.FullDescription()}");
			}

			Print(prefixes, "prefixes");
			Print(postfixes, "postfixes");
			Print(transpilers, "transpilers");
			Print(finalizers, "finalizers");
			Print(ilManipulators, "ilmanipulators");

			return sb.ToString();
		}, debug);

		WriteImpl();

		if (dumpPaths.Length > 0)
			CecilEmitter.Dump(ctx.Method, dumpPaths, original);
	}

	private bool WritePrefixes(ILEmitter.Label returnLabel)
	{
		// Prefix layout:
		// Make return value (if needed) into a variable
		// Call prefixes
		// If method returns a value, add additional logic to allow skipping original method

		if (prefixes.Count == 0)
			return false;

		Logger.Log(Logger.LogChannel.Info, () => "Writing prefixes", debug);

		// Start emitting at the start
		il.emitBefore = il.IL.Body.Instructions[0];

		var returnType = ReturnType;
		if (!variables.TryGetValue(ResultVar, out var returnValueVar))
			returnValueVar = variables[ResultVar] = returnType == typeof(void) ? null : il.DeclareVariable(returnType);

		// Alloc __result if by ref
		if (returnValueVar is not null && returnType.IsByRef)
		{
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Newarr, returnType.GetElementType());
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Ldelema, returnType.GetElementType());
			il.Emit(OpCodes.Stloc, returnValueVar);
		}

		// A prefix that can modify control flow has one of the following:
		// * It returns a boolean
		// * It declares bool __runOriginal
		// Keep this CLR signature query short-circuiting, including custom MethodInfo implementations.
		var canModifyControlFlow = prefixes.Any(p => p.method.ReturnType == typeof(bool) ||
			p.method.GetParameters().Any(pp => pp.Name == RunOriginalParam &&
				pp.ParameterType.OpenRefType() == typeof(bool)));
		var plan = PlanPatchCalls(RustKernel.ManipulatorStage.Prefix, prefixes, returnValueVar,
			canModifyControlFlow: canModifyControlFlow);

		// Flag to check if the orignal method should be run (or was run)
		// Always present so other patchers can access it
		var runOriginal = variables[RunOriginalParam] = il.DeclareVariable(typeof(bool));
		// Init runOriginal to true
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Stloc, runOriginal);

		// If runOriginal flag exists, we need to add more logic to the method end
		var postProcessTarget = returnValueVar != null ? il.DeclareLabel() : returnLabel;

		EmitPatchCalls(plan, RustKernel.ManipulatorStage.Prefix, prefixes, returnValueVar, runOriginal);

		if (!canModifyControlFlow)
			return false;

		// If runOriginal is false, branch automatically to the end
		il.Emit(OpCodes.Ldloc, runOriginal);
		il.Emit(OpCodes.Brfalse, postProcessTarget);

		if (returnValueVar == null)
			return true;

		// Finally, load return value onto stack at the end
		il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];
		il.MarkLabel(postProcessTarget);
		il.Emit(OpCodes.Ldloc, returnValueVar);

		return true;
	}

	private static RustKernel.ManipulatorPlan PlanPatchCalls(RustKernel.ManipulatorStage stage,
		List<PatchContext> patches, VariableDefinition result, bool suppressExceptions = false,
		bool canModifyControlFlow = false)
	{
		var methods = new RustKernel.ManipulatorMethod[patches.Count];
		for (var i = 0; i < methods.Length; i++)
		{
			var patch = patches[i];
			// Only private context flags are snapshotted. Signatures and attributes remain live.
			methods[i] = new RustKernel.ManipulatorMethod
			{
				Flags = (patch.hasArgsArrayArg ? 1u : 0u) | (patch.wrapTryCatch ? 2u : 0u)
			};
		}
		return RustKernel.PlanManipulator(stage, methods, result != null, suppressExceptions, canModifyControlFlow);
	}

	private void EmitPatchCalls(RustKernel.ManipulatorPlan plan, RustKernel.ManipulatorStage stage,
		List<PatchContext> patches, VariableDefinition result, VariableDefinition runOriginal = null)
	{
		// Rust supplies ordered emission commands; CLR objects never leave this adapter.
		ILEmitter.Label start = null;
		VariableDefinition instanceBox = null, resultBox = null;
		MethodInfo resultRefInvoke = null;
		List<ArgumentBoxInfo> argumentBoxes = null;
		for (var instruction = 0; instruction < plan.Steps.Length; instruction++)
		{
			var step = plan.Steps[instruction];
			var method = step.Method == uint.MaxValue ? null : patches[checked((int)step.Method)].method;
			switch (step.Action)
			{
				case RustKernel.ManipulatorAction.Begin:
					start = il.DeclareLabel();
					il.MarkLabel(start);
					break;
				case RustKernel.ManipulatorAction.Call:
					EmitCallParameter(method, stage == RustKernel.ManipulatorStage.Postfix,
						out instanceBox, out resultBox, out resultRefInvoke, out argumentBoxes);
					il.Emit(OpCodes.Call, method);
					break;
				case RustKernel.ManipulatorAction.AssignArgs:
					EmitAssignRefsFromArgsArray(variables[ArgsArrayVar]);
					break;
				case RustKernel.ManipulatorAction.RestoreInstance:
					EmitInstanceUnbox(instanceBox, original.DeclaringType);
					break;
				case RustKernel.ManipulatorAction.InvokeResultRef:
					EmitResultRefInvoke(resultRefInvoke);
					break;
				case RustKernel.ManipulatorAction.RestoreResult:
					EmitResultUnbox(resultBox, result);
					break;
				case RustKernel.ManipulatorAction.RestoreArguments:
					EmitArgUnbox(argumentBoxes);
					break;
				case RustKernel.ManipulatorAction.InvalidPrefix:
					throw new InvalidHarmonyPatchArgumentException(
						$"Prefix patch {method.FullDescription()} has return type {method.ReturnType}, but only `bool` or `void` are permitted",
						original, method);
				case RustKernel.ManipulatorAction.MergeRunOriginal:
					il.Emit(OpCodes.Ldloc, runOriginal);
					il.Emit(OpCodes.And);
					il.Emit(OpCodes.Stloc, runOriginal);
					break;
				case RustKernel.ManipulatorAction.Wrap:
					EmitTryCatchWrapper(method, start);
					break;
				case RustKernel.ManipulatorAction.LoadResult:
					il.Emit(OpCodes.Ldloc, result);
					break;
				case RustKernel.ManipulatorAction.StoreResult:
					il.Emit(OpCodes.Stloc, result);
					break;
				case RustKernel.ManipulatorAction.ValidatePassthrough:
					var firstParam = method.GetParameters().FirstOrDefault();
					if (firstParam == null || method.ReturnType != firstParam.ParameterType)
					{
						if (firstParam != null)
							throw new InvalidHarmonyPatchArgumentException(
								$"Return type of pass through postfix {method.FullDescription()} does not match type of its first parameter",
								original, method);
						throw new InvalidHarmonyPatchArgumentException(
							$"Postfix patch {method.FullDescription()} must have `void` as return type", original, method);
					}
					break;
				case RustKernel.ManipulatorAction.StoreException:
					il.Emit(OpCodes.Stloc, variables[ExceptionVar]);
					break;
				case RustKernel.ManipulatorAction.IfVoid:
					if (method.ReturnType == typeof(void)) instruction = checked((int)step.Target) - 1;
					break;
				case RustKernel.ManipulatorAction.IfNonVoid:
					if (method.ReturnType != typeof(void)) instruction = checked((int)step.Target) - 1;
					break;
				case RustKernel.ManipulatorAction.IfBool:
					if (method.ReturnType == typeof(bool)) instruction = checked((int)step.Target) - 1;
					break;
				case RustKernel.ManipulatorAction.DisableRethrow:
					plan.CanRethrow = false;
					break;
			}
		}
	}

	private void EmitTryCatchWrapper(MethodBase target, ILEmitter.Label start)
	{
		var exBlock = il.BeginExceptionBlock(start);
		il.BeginHandler(exBlock, ExceptionHandlerType.Catch, typeof(object));
		il.Emit(OpCodes.Ldstr, target.FullDescription());
		il.Emit(OpCodes.Call, LogPatchExceptionMethod);
		il.EndExceptionBlock(exBlock);
		// Force proper closure in case nothing is emitted after the catch
		il.Emit(OpCodes.Nop);
	}

	private void EmitArgUnbox(List<ArgumentBoxInfo> boxInfo)
	{
		if (boxInfo == null || boxInfo.Count == 0)
			return;
		var plan = RustKernel.PlanManipulatorTransfer(RustKernel.ManipulatorTransfer.RestoreBoxes);
		var argument = 0;
		ArgumentBoxInfo info = null;
		for (var instruction = 0; instruction < plan.Length; instruction++)
		{
			var step = plan[instruction];
			var jump = false;
			switch (step.Action)
			{
				case RustKernel.ManipulatorTransferAction.BeginArguments:
					argument = 0;
					info = boxInfo[argument];
					break;
				case RustKernel.ManipulatorTransferAction.NextArgument:
					jump = ++argument < boxInfo.Count;
					if (jump) info = boxInfo[argument];
					break;
				case RustKernel.ManipulatorTransferAction.IfNotBoxByRef:
					jump = !info.isByRef;
					break;
				case RustKernel.ManipulatorTransferAction.Jump:
					jump = true;
					break;
				case RustKernel.ManipulatorTransferAction.LoadArgument:
					il.Emit(OpCodes.Ldarg, info.index);
					break;
				case RustKernel.ManipulatorTransferAction.LoadBox:
					il.Emit(OpCodes.Ldloc, info.tmpVar);
					break;
				case RustKernel.ManipulatorTransferAction.Unbox:
					il.Emit(OpCodes.Unbox_Any, info.type);
					break;
				case RustKernel.ManipulatorTransferAction.StoreObject:
					il.Emit(OpCodes.Stobj, info.type);
					break;
				case RustKernel.ManipulatorTransferAction.StoreArgument:
					il.Emit(OpCodes.Starg, info.index);
					break;
				default:
					throw new RustBackendException("Modstoke Harmony received an invalid boxed argument transfer.");
			}
			if (jump) instruction = checked((int)step.Target) - 1;
		}
	}

	private void EmitResultUnbox(
		VariableDefinition tmp,
		VariableDefinition result)
	{
		if (tmp == null)
			return;
		il.Emit(OpCodes.Ldloc, tmp);
		il.Emit(OpCodes.Unbox_Any, ReturnType);
		il.Emit(OpCodes.Stloc, result);
	}

	private void EmitResultRefInvoke(MethodInfo resultRefInvoke)
	{
		if(resultRefInvoke is null)
			return;

		var returnRefVar = variables[ResultRefVar];
		var label = il.DeclareLabel();
		il.Emit(OpCodes.Ldloc, returnRefVar);
		il.Emit(OpCodes.Brfalse_S, label);

		il.Emit(OpCodes.Ldloc, returnRefVar);
		il.Emit(OpCodes.Callvirt, resultRefInvoke);
		il.Emit(OpCodes.Stloc, variables[ResultVar]);
		il.Emit(OpCodes.Ldnull);
		il.Emit(OpCodes.Stloc, returnRefVar);

		il.MarkLabel(label);
		il.Emit(OpCodes.Nop);
	}

	private void EmitInstanceUnbox(
		VariableDefinition box,
		Type declaringType)
	{
		if (box == null)
			return;
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldloc, box);
		il.Emit(OpCodes.Unbox_Any, declaringType);
		il.Emit(OpCodes.Stobj, declaringType);
	}

	private void WriteTranspilers()
	{
		if (transpilers.Count == 0)
			return;

		Logger.Log(Logger.LogChannel.Info, () => $"Transpiling {original.FullDescription()}", debug);

		// Create a high-level manipulator for the method
		var manipulator = new ILManipulator(ctx.Body, debug);

		// Add in all transpilers
		foreach (var transpiler in transpilers)
			manipulator.AddTranspiler(transpiler.method);

		// Write new manipulated code to our body
		manipulator.WriteTo(ctx.Body, original);
	}

	private (ILEmitter.Label, bool) MakeReturnLabel()
	{
		// Ensure body is not empty
		if (ctx.IL.Body.Instructions.Count == 0)
			il.Emit(OpCodes.Nop);

		// We replace all `ret`s with a simple branch to force potential execution of post-original code

		// Create a helper label as well
		// We mark the label as not emitted so that potential postfix code can mark it
		var resultLabel = il.DeclareLabel();
		resultLabel.emitted = false;

		var hasRet = false;
		foreach (var ins in il.IL.Body.Instructions.Where(ins => ins.MatchRet()))
		{
			hasRet = true;
			ins.OpCode = OpCodes.Br;
			ins.Operand = resultLabel.instruction;
			resultLabel.targets.Add(ins);
		}

		resultLabel.instruction = Instruction.Create(OpCodes.Ret);

		// Already append ending label for other code to use as emitBefore point
		il.IL.Append(resultLabel.instruction);

		return (resultLabel, hasRet);
	}

	private void WriteImpl()
	{
		try
		{
			if (original == null)
				throw new ArgumentException(nameof(original));

			Logger.Log(Logger.LogChannel.Info, () => $"Running ILHook manipulator on {original.FullDescription()}",
				debug);

			WriteTranspilers();

			// If no need to wrap anything, we're basically done!
			if (prefixes.Count + postfixes.Count + finalizers.Count + ilManipulators.Count == 0)
			{
				Logger.Log(Logger.LogChannel.IL,
					() => $"Generated patch ({ctx.Method.FullName}):\n{ctx.Body.ToILDasmString()}", debug);
				return;
			}

			var (returnLabel, originalHasReturn) = MakeReturnLabel();
			var auxMethods = prefixes.Union(postfixes).Union(finalizers);

			// Collect state and arg variables
			foreach (var nfix in auxMethods)
			{
				var parametersToRealNames = ParametersWithRealNames(nfix.method);
				var argsParameterWithRealName = parametersToRealNames.FirstOrDefault(p => p.realName == ArgsArrayVar);

				if (argsParameterWithRealName.param != null)
				{
					if (argsParameterWithRealName.param.ParameterType != typeof(object[]))
						throw new InvalidHarmonyPatchArgumentException(
							$"Patch {nfix.method.FullDescription()} defines __args list, but only type `object[]` is permitted",
							original, nfix.method);
					variables[ArgsArrayVar] = il.DeclareVariable(typeof(object[]));
				}

				if (nfix.method.DeclaringType?.FullName != null &&
				    !variables.ContainsKey(nfix.method.DeclaringType.FullName))
					foreach (var patchParamWithRealName in parametersToRealNames
						         .Where(paramToRealName => paramToRealName.realName == StateVar))
						variables[nfix.method.DeclaringType.FullName] =
							il.DeclareVariable(patchParamWithRealName.param.ParameterType.OpenRefType()); // Fix possible reftype
			}

			var canModifyControlFlow = WritePrefixes(returnLabel);
			WritePostfixes(returnLabel, originalHasReturn || canModifyControlFlow);
			WriteFinalizers(returnLabel, originalHasReturn);

			// remove ret for dead-end methods
			if(!originalHasReturn && !canModifyControlFlow && postfixes.Count == 0 && finalizers.Count == 0 )
				ctx.Instrs.RemoveAt(ctx.Instrs.Count - 1);

			il.MarkLabel(returnLabel);
			il.SetOpenLabelsTo(ctx.Instrs[ctx.Instrs.Count - 1]);

			// If we have the args array, we need to initialize it right at start
			// TODO: Come up with a better place for initializer code
			if (variables.TryGetValue(ArgsArrayVar, out var argsArrayVariable))
			{
				il.emitBefore = ctx.Instrs[0];
				EmitInitArgsArray();
				il.Emit(OpCodes.Stloc, argsArrayVariable);
			}

			ApplyManipulators(ctx, original, ilManipulators.Select(m => m.method).ToList(), returnLabel);

			StackTraceFixes.FixStackTrace(ctx);

			Logger.Log(Logger.LogChannel.IL,
				() => $"Generated patch ({ctx.Method.FullName}):\n{ctx.Body.ToILDasmString()}", debug);
		}
		catch (Exception e)
		{
			Logger.Log(Logger.LogChannel.Error, () => $"Failed to patch {original.FullDescription()}: {e}", debug);
			throw HarmonyException.Create(e, ctx.Body);
		}
	}

	internal static void ApplyManipulators(
		ILContext ctx,
		MethodBase original,
		List<MethodInfo> ilManipulators,
		ILEmitter.Label retLabel)
	{
		// Define a label to branch to, if not passed a label create one to the last instruction
		var retIlLabel = ctx.DefineLabel(retLabel?.instruction) ?? ctx.DefineLabel(ctx.Body.Instructions.Last());

		foreach (var method in ilManipulators)
		{
			var manipulatorParameters = new List<object>();
			foreach (var type in method.GetParameters().Select(p => p.ParameterType))
			{
				if (type.IsAssignableFrom(typeof(ILContext)))
					manipulatorParameters.Add(ctx);
				if (type.IsAssignableFrom(typeof(MethodBase)))
					manipulatorParameters.Add(original);
				if (type.IsAssignableFrom(typeof(ILLabel)))
					manipulatorParameters.Add(retIlLabel);
			}

			method.Invoke(null, manipulatorParameters.ToArray());
		}
	}

	private void EmitOutValue(Type t)
	{
		var (def, load, store) = t switch
		{
			_ when t == typeof(float) => (0F, OpCodes.Ldc_R4, OpCodes.Stind_R4),
			_ when t == typeof(double) => (0D, OpCodes.Ldc_R8, OpCodes.Stind_R8),
			_ when t == typeof(long) => (0L, OpCodes.Ldc_I8, OpCodes.Stind_I8),
			_ => (0, OpCodes.Ldc_I4, t.GetCecilStoreOpCode())
		};
		il.EmitUnsafe(load, def);
		il.Emit(store);
	}

	private void EmitInitArgsArray()
		=> EmitArgsArrayTransfer(RustKernel.ManipulatorTransfer.InitializeArray, null);

	private bool WriteFinalizers(
		ILEmitter.Label returnLabel, bool originalHasReturn)
	{
		// Finalizer layout:
		// Create __exception variable to store exception info and a skip flag
		// Wrap the whole method into a try/catch
		// Call finalizers at the end of method (simulate `finally`)
		// If __exception got set, throw it
		// Begin catch block
		// Store exception into __exception
		// If skip flag is set, skip finalizers
		// Call finalizers
		// If __exception is still set, rethrow (if new exception set, otherwise throw the new exception)
		// End catch block

		if (finalizers.Count == 0)
			return false;

		Logger.Log(Logger.LogChannel.Info, () => "Writing finalizers", debug);

		// Create variables to hold custom exception
		variables[ExceptionVar] = il.DeclareVariable(typeof(Exception));

		// Create a flag to signify that finalizers have been run
		// Cecil DMD fix: initialize it to false
		var skipFinalizersVar = il.DeclareVariable(typeof(bool));
		il.emitBefore = il.IL.Body.Instructions[0];
		il.Emit(OpCodes.Ldc_I4_0);
		il.Emit(OpCodes.Stloc, skipFinalizersVar);

		il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];

		// Mark the original method return label here if it hasn't been yet
		il.MarkLabel(returnLabel);

		if (!variables.TryGetValue(ResultVar, out var returnValueVar))
		{
			var retVal = ReturnType;
			returnValueVar = variables[ResultVar] = retVal == typeof(void) ? null : il.DeclareVariable(retVal);
		}

		// Start main exception block
		var mainBlock = il.BeginExceptionBlock(il.DeclareLabelFor(il.IL.Body.Instructions[0]));

		bool WriteFinalizerCalls(bool suppressExceptions)
		{
			var plan = PlanPatchCalls(RustKernel.ManipulatorStage.Finalizer, finalizers, returnValueVar, suppressExceptions);
			EmitPatchCalls(plan, RustKernel.ManipulatorStage.Finalizer, finalizers, returnValueVar);
			return plan.CanRethrow;
		}

		// First, store potential result into a variable and empty the stack
		if (returnValueVar != null && originalHasReturn)
			il.Emit(OpCodes.Stloc, returnValueVar);

		// Write finalizers inside the `try`
		WriteFinalizerCalls(false);

		// Mark finalizers as skipped so they won't rerun
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Stloc, skipFinalizersVar);

		// If __exception is not null, throw
		var skipLabel = il.DeclareLabel();
		il.Emit(OpCodes.Ldloc, variables[ExceptionVar]);
		il.Emit(OpCodes.Brfalse, skipLabel);
		il.Emit(OpCodes.Ldloc, variables[ExceptionVar]);
		il.Emit(OpCodes.Throw);
		il.MarkLabel(skipLabel);

		// Begin a generic `catch(Exception o)` here and capture exception into __exception
		il.BeginHandler(mainBlock, ExceptionHandlerType.Catch, typeof(Exception));
		il.Emit(OpCodes.Stloc, variables[ExceptionVar]);

		// Call finalizers or skip them if needed
		il.Emit(OpCodes.Ldloc, skipFinalizersVar);
		var postFinalizersLabel = il.DeclareLabel();
		il.Emit(OpCodes.Brtrue, postFinalizersLabel);

		var rethrowPossible = WriteFinalizerCalls(true);

		il.MarkLabel(postFinalizersLabel);

		// Possibly rethrow if __exception is still not null (i.e. suppressed)
		skipLabel = il.DeclareLabel();
		il.Emit(OpCodes.Ldloc, variables[ExceptionVar]);
		il.Emit(OpCodes.Brfalse, skipLabel);
		if (rethrowPossible)
			il.Emit(OpCodes.Rethrow);
		else
		{
			il.Emit(OpCodes.Ldloc, variables[ExceptionVar]);
			il.Emit(OpCodes.Throw);
		}

		il.MarkLabel(skipLabel);
		il.EndExceptionBlock(mainBlock);

		if (returnValueVar != null)
			il.Emit(OpCodes.Ldloc, returnValueVar);

		return true;
	}

	private void WritePostfixes(
		ILEmitter.Label returnLabel, bool emitResultStore)
	{
		// Postfix layout:
		// Make return value (if needed) into a variable
		// If method has return value, store the current stack value into it (since the value on the stack is the return value)
		// Call postfixes that modify return values by __return
		// Call postfixes that modify return values by chaining

		if (postfixes.Count == 0)
			return;

		Logger.Log(Logger.LogChannel.Info, () => "Writing postfixes", debug);

		// Get the last instruction (expected to be `ret`)
		il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];

		// Mark the original method return label here
		il.MarkLabel(returnLabel);

		if (!variables.TryGetValue(ResultVar, out var returnValueVar))
		{
			var retVal = ReturnType;
			returnValueVar = variables[ResultVar] = retVal == typeof(void) ? null : il.DeclareVariable(retVal);
		}

		if (returnValueVar != null && emitResultStore)
			il.Emit(OpCodes.Stloc, returnValueVar);

		if (!variables.ContainsKey(RunOriginalParam))
		{
			// If __runOriginal wasn't defined in prefixes, create it and mark as true (since original was run)
			var runOriginalVar = variables[RunOriginalParam] = il.DeclareVariable(typeof(bool));
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Stloc, runOriginalVar);
		}

		var plan = PlanPatchCalls(RustKernel.ManipulatorStage.Postfix, postfixes, returnValueVar);
		EmitPatchCalls(plan, RustKernel.ManipulatorStage.Postfix, postfixes, returnValueVar);
	}

	private void SortPatches()
	{
		Patch[] prefixesArr, postfixesArr, transpilersArr, finalizersArr, ilmanipulatorsArr;

		// Lock to ensure no more patches are added while we're sorting
		lock (patchInfo)
		{
			prefixesArr = patchInfo.prefixes.ToArray();
			postfixesArr = patchInfo.postfixes.ToArray();
			transpilersArr = patchInfo.transpilers.ToArray();
			finalizersArr = patchInfo.finalizers.ToArray();
			ilmanipulatorsArr = patchInfo.ilmanipulators.ToArray();
		}

		static List<PatchContext> Sort(MethodBase original, Patch[] patches, bool debug)
		{
			return PatchFunctions.GetSortedPatchMethodsAsPatches(original, patches, debug)
				.Select(p => new {method = p.GetMethod(original), origP = p})
				.Select(p => new PatchContext
				{
					method = p.method,
					wrapTryCatch = p.origP.wrapTryCatch,
					hasArgsArrayArg = p.method.GetParameters().Any(par => par.Name == ArgsArrayVar)
				})
				.ToList();
		}

		// debug is useless; debug logs passed on-demand
		prefixes = Sort(original, prefixesArr, debug);
		postfixes = Sort(original, postfixesArr, debug);
		transpilers = Sort(original, transpilersArr, debug);
		finalizers = Sort(original, finalizersArr, debug);
		ilManipulators = Sort(original, ilmanipulatorsArr, debug);
	}

	private bool EmitOriginalBaseMethod()
	{
		if (original is MethodInfo method)
			il.Emit(OpCodes.Ldtoken, method);
		else if (original is ConstructorInfo constructor)
			il.Emit(OpCodes.Ldtoken, constructor);
		else return false;

		var type = original.ReflectedType;
		if (type == null) return false;
		if (type.IsGenericType) il.Emit(OpCodes.Ldtoken, type);
		il.Emit(OpCodes.Call, type.IsGenericType ? GetMethodFromHandle2 : GetMethodFromHandle1);
		return true;
	}

	private void EmitAssignRefsFromArgsArray(
		VariableDefinition argsArrayVariable)
		=> EmitArgsArrayTransfer(RustKernel.ManipulatorTransfer.AssignArray, argsArrayVariable);

	private void EmitArgsArrayTransfer(RustKernel.ManipulatorTransfer kind, VariableDefinition argsArrayVariable)
	{
		var parameters = original.GetParameters();
		var startOffset = original.IsStatic ? 0 : 1;
		var plan = RustKernel.PlanManipulatorTransfer(kind);
		Type paramType = null, openedType = null;
		ParameterInfo outParameter = null;
		var argument = 0;
		for (var instruction = 0; instruction < plan.Length; instruction++)
		{
			var step = plan[instruction];
			var jump = false;
			// Keep metadata reads at their emission points; custom reflection types remain live.
			switch (step.Action)
			{
				case RustKernel.ManipulatorTransferAction.BeginArguments:
					argument = 0;
					jump = parameters.Length == 0;
					break;
				case RustKernel.ManipulatorTransferAction.NextArgument:
					jump = ++argument < parameters.Length;
					break;
				case RustKernel.ManipulatorTransferAction.IfNotOut:
					outParameter = parameters[argument];
					jump = !(outParameter.IsOut || outParameter.IsRetval);
					break;
				case RustKernel.ManipulatorTransferAction.PrepareOutType:
					openedType = outParameter.ParameterType.OpenRefType().OpenRefType();
					break;
				case RustKernel.ManipulatorTransferAction.NewArray:
					il.Emit(OpCodes.Ldc_I4, parameters.Length);
					il.Emit(OpCodes.Newarr, typeof(object));
					break;
				case RustKernel.ManipulatorTransferAction.ReadType:
					openedType = paramType = parameters[argument].ParameterType;
					break;
				case RustKernel.ManipulatorTransferAction.OpenType:
					openedType = paramType.OpenRefType();
					break;
				case RustKernel.ManipulatorTransferAction.Duplicate:
					il.Emit(OpCodes.Dup);
					break;
				case RustKernel.ManipulatorTransferAction.ElementIndex:
					il.Emit(OpCodes.Ldc_I4, argument);
					break;
				case RustKernel.ManipulatorTransferAction.LoadArgument:
					il.Emit(OpCodes.Ldarg, argument + startOffset);
					break;
				case RustKernel.ManipulatorTransferAction.IfNotByRef:
					jump = !paramType.IsByRef;
					break;
				case RustKernel.ManipulatorTransferAction.IfNotStruct:
					jump = !AccessTools.IsStruct(openedType);
					break;
				case RustKernel.ManipulatorTransferAction.LoadObject:
					il.Emit(OpCodes.Ldobj, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.LoadIndirect:
					il.Emit(openedType.GetCecilLoadOpCode());
					break;
				case RustKernel.ManipulatorTransferAction.Jump:
					jump = true;
					break;
				case RustKernel.ManipulatorTransferAction.IfNotValue:
					jump = !openedType.IsValueType;
					break;
				case RustKernel.ManipulatorTransferAction.Box:
					il.Emit(OpCodes.Box, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.StoreElement:
					il.Emit(OpCodes.Stelem_Ref);
					break;
				case RustKernel.ManipulatorTransferAction.LoadArray:
					il.Emit(OpCodes.Ldloc, argsArrayVariable);
					break;
				case RustKernel.ManipulatorTransferAction.LoadElement:
					il.Emit(OpCodes.Ldelem_Ref);
					break;
				case RustKernel.ManipulatorTransferAction.Unbox:
					il.Emit(OpCodes.Unbox_Any, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.Cast:
					il.Emit(OpCodes.Castclass, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.StoreObject:
					il.Emit(OpCodes.Stobj, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.StoreIndirect:
					il.Emit(openedType.GetCecilStoreOpCode());
					break;
				case RustKernel.ManipulatorTransferAction.StoreRef:
					il.Emit(OpCodes.Stind_Ref);
					break;
				case RustKernel.ManipulatorTransferAction.StoreArgument:
					il.Emit(OpCodes.Starg, argument + startOffset);
					break;
				case RustKernel.ManipulatorTransferAction.IfNotPrimitive:
					jump = !AccessTools.IsValue(openedType);
					break;
				case RustKernel.ManipulatorTransferAction.InitializeValue:
					EmitOutValue(openedType);
					break;
				case RustKernel.ManipulatorTransferAction.InitializeObject:
					il.Emit(OpCodes.Initobj, openedType);
					break;
				case RustKernel.ManipulatorTransferAction.LoadNull:
					il.Emit(OpCodes.Ldnull);
					break;
				default:
					throw new RustBackendException("Modstoke Harmony received an invalid argument array transfer.");
			}
			if (jump) instruction = checked((int)step.Target) - 1;
		}
	}

	private void EmitInstanceParameter(ParameterInfo patchParam, Type originalType, ref VariableDefinition temporary)
	{
		var plan = RustKernel.PlanManipulatorTransfer(RustKernel.ManipulatorTransfer.LoadInstance);
		Type paramType = null;
		var parameterIsRef = false;
		for (var instruction = 0; instruction < plan.Length; instruction++)
		{
			var step = plan[instruction];
			var jump = false;
			switch (step.Action)
			{
				case RustKernel.ManipulatorTransferAction.IfInstanceNotStatic:
					jump = !original.IsStatic;
					break;
				case RustKernel.ManipulatorTransferAction.LoadNull:
					il.Emit(OpCodes.Ldnull);
					break;
				case RustKernel.ManipulatorTransferAction.ReadInstanceType:
					paramType = patchParam.ParameterType;
					parameterIsRef = paramType.IsByRef;
					break;
				case RustKernel.ManipulatorTransferAction.IfInstanceStruct:
					jump = originalType is not null && AccessTools.IsStruct(originalType);
					break;
				case RustKernel.ManipulatorTransferAction.IfNotParameterByRef:
					jump = !parameterIsRef;
					break;
				case RustKernel.ManipulatorTransferAction.IfTypedInstance:
					jump = paramType != typeof(object) && paramType != typeof(object).MakeByRefType();
					break;
				case RustKernel.ManipulatorTransferAction.LoadInstance:
					il.Emit(OpCodes.Ldarg_0);
					break;
				case RustKernel.ManipulatorTransferAction.InstanceAddress:
					il.Emit(OpCodes.Ldarga, 0);
					break;
				case RustKernel.ManipulatorTransferAction.LoadObject:
					il.Emit(OpCodes.Ldobj, originalType);
					break;
				case RustKernel.ManipulatorTransferAction.Box:
					il.Emit(OpCodes.Box, originalType);
					break;
				case RustKernel.ManipulatorTransferAction.DeclareInstanceBox:
					temporary = il.DeclareVariable(typeof(object));
					break;
				case RustKernel.ManipulatorTransferAction.StoreInstanceBox:
					il.Emit(OpCodes.Stloc, temporary);
					break;
				case RustKernel.ManipulatorTransferAction.LoadInstanceBoxAddress:
					il.Emit(OpCodes.Ldloca, temporary);
					break;
				case RustKernel.ManipulatorTransferAction.Jump:
					jump = true;
					break;
				default:
					throw new RustBackendException("Modstoke Harmony received an invalid instance argument recipe.");
			}
			if (jump) instruction = checked((int)step.Target) - 1;
		}
	}

	private void EmitCallParameter(MethodInfo patch,
		bool allowFirsParamPassthrough,
		out VariableDefinition tmpInstanceBoxingVar,
		out VariableDefinition tmpObjectVar,
		out MethodInfo resultRefInvoke,
		out List<ArgumentBoxInfo> tmpBoxVars)
	{
		tmpInstanceBoxingVar = null;
		tmpObjectVar = null;
		resultRefInvoke = null;
		tmpBoxVars = new List<ArgumentBoxInfo>();
		var isInstance = original.IsStatic is false;
		var originalParameters = original.GetParameters();
		var originalParameterNames = originalParameters.Select(p => p.Name).ToArray();
		var parametersWithRealNames = ParametersWithRealNames(patch);
		var originalType = original.DeclaringType;

		// check for passthrough using first parameter (which must have same type as return type)
		if (allowFirsParamPassthrough && patch.ReturnType != typeof(void) && parametersWithRealNames.Count > 0 &&
		    parametersWithRealNames[0].param.ParameterType == patch.ReturnType)
			parametersWithRealNames.RemoveRange(0, 1);

		var argumentKinds = RustKernel.ClassifyManipulatorArguments(parametersWithRealNames.Select(p => p.realName).ToArray());
		for (var parameterIndex = 0; parameterIndex < parametersWithRealNames.Count; parameterIndex++)
		{
			var patchParamWithRealName = parametersWithRealNames[parameterIndex];
			var realParamName = patchParamWithRealName.realName;
			var patchParam = patchParamWithRealName.param;
			var kind = argumentKinds[parameterIndex];

			if (kind == RustKernel.ManipulatorArgument.OriginalMethod)
			{
				if (EmitOriginalBaseMethod())
					continue;

				il.Emit(OpCodes.Ldnull);
				continue;
			}

			if (kind == RustKernel.ManipulatorArgument.Instance)
			{
				EmitInstanceParameter(patchParam, originalType, ref tmpInstanceBoxingVar);
				continue;
			}

			if (kind == RustKernel.ManipulatorArgument.Args)
			{
				if (variables.TryGetValue(ArgsArrayVar, out var argsArrayVar))
					il.Emit(OpCodes.Ldloc, argsArrayVar);
				else
					il.Emit(OpCodes.Ldnull);
				continue;
			}

			// Preserve the null-name failure point after preceding arguments have emitted.
			if (kind == RustKernel.ManipulatorArgument.NullName)
				throw new NullReferenceException();

			if (kind == RustKernel.ManipulatorArgument.Field)
			{
				var fieldName = realParamName.Substring(InstanceFieldPrefix.Length);
				FieldInfo fieldInfo;
				if (fieldName.All(char.IsDigit))
				{
					// field access by index only works for declared fields
					fieldInfo = AccessTools.DeclaredField(originalType, int.Parse(fieldName));
					if (fieldInfo is null)
						throw new ArgumentException(
							$"No field found at given index in class {originalType.FullName}", fieldName);
				}
				else
				{
					fieldInfo = AccessTools.Field(originalType, fieldName);
					if (fieldInfo is null)
						throw new ArgumentException($"No such field defined in class {originalType.FullName}",
							fieldName);
				}

				if (fieldInfo.IsStatic)
					il.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldsflda : OpCodes.Ldsfld, fieldInfo);
				else
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldflda : OpCodes.Ldfld, fieldInfo);
				}

				continue;
			}

			// state is special too since each patch has its own local var
			if (kind == RustKernel.ManipulatorArgument.State)
			{
				var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
				if (variables.TryGetValue(patch.DeclaringType.FullName, out var stateVar))
					il.Emit(ldlocCode, stateVar);
				else
					il.Emit(OpCodes.Ldnull);
				continue;
			}

			// treat __result var special
			if (kind == RustKernel.ManipulatorArgument.Result)
			{
				var returnType = ReturnType;
				if (returnType == typeof(void))
					throw new Exception($"Cannot get result from void method {original.FullDescription()}");
				var resultType = patchParam.ParameterType;
				if (resultType.IsByRef && !returnType.IsByRef)
					resultType = resultType.GetElementType();
				if (resultType.IsAssignableFrom(returnType) is false)
					throw new Exception(
						$"Cannot assign method return type {returnType.FullName} to {ResultVar} type {resultType.FullName} for method {original.FullDescription()}");
				var ldlocCode = patchParam.ParameterType.IsByRef && !returnType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
				if (returnType.IsValueType && patchParam.ParameterType == typeof(object).MakeByRefType())
					ldlocCode = OpCodes.Ldloc;
				il.Emit(ldlocCode, variables[ResultVar]);
				if (returnType.IsValueType)
				{
					if (patchParam.ParameterType == typeof(object))
						il.Emit(OpCodes.Box, returnType);
					else if (patchParam.ParameterType == typeof(object).MakeByRefType())
					{
						il.Emit(OpCodes.Box, returnType);
						tmpObjectVar = il.DeclareVariable(typeof(object));
						il.Emit(OpCodes.Stloc, tmpObjectVar);
						il.Emit(OpCodes.Ldloca, tmpObjectVar);
					}
				}

				continue;
			}

			// treat __resultRef delegate special
			if (kind == RustKernel.ManipulatorArgument.ResultRef)
			{
				var returnType = ReturnType;
				if (!returnType.IsByRef)
					throw new Exception(
						$"Cannot use {ResultRefVar} with non-ref return type {returnType.FullName} of method {original.FullDescription()}");

				var resultType = patchParam.ParameterType;
				var expectedType = typeof(RefResult<>).MakeGenericType(returnType.GetElementType());
				var expectedTypeRef = expectedType.MakeByRefType();
				if (resultType != expectedTypeRef)
					throw new Exception(
						$"Wrong type of {ResultRefVar} for method {original.FullDescription()}. Expected {expectedTypeRef.FullName}, got {resultType.FullName}");

				if (!variables.TryGetValue(ResultRefVar, out var returnRefVar))
					returnRefVar = variables[ResultRefVar] = il.DeclareVariable(expectedTypeRef);
				il.Emit(OpCodes.Ldloca, returnRefVar);

				resultRefInvoke = AccessTools.Method(expectedType, "Invoke");
				continue;
			}

			// any other declared variables
			if (variables.TryGetValue(realParamName, out var localBuilder))
			{
				var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
				il.Emit(ldlocCode, localBuilder);
				continue;
			}

			int idx;
			if (kind == RustKernel.ManipulatorArgument.Indexed)
			{
				var val = realParamName.Substring(ParamIndexPrefix.Length);
				if (!int.TryParse(val, out idx))
					throw new Exception($"Parameter {realParamName} does not contain a valid index");
				if (idx < 0 || idx >= originalParameters.Length)
					throw new Exception($"No parameter found at index {idx}");
			}
			else
			{
				idx = patch.GetArgumentIndex(originalParameterNames, patchParam);
				if (idx == -1)
				{
					var harmonyMethod = HarmonyMethodExtensions.GetMergedFromType(patchParam.ParameterType);
					if (harmonyMethod.methodType is null) // MethodType default is Normal
						harmonyMethod.methodType = MethodType.Normal;
					var delegateOriginal = harmonyMethod.GetOriginalMethod();
					if (delegateOriginal is MethodInfo methodInfo)
					{
						var delegateConstructor =
							patchParam.ParameterType.GetConstructor(new[] {typeof(object), typeof(IntPtr)});
						if (delegateConstructor != null)
						{
							if (methodInfo.IsStatic)
								il.Emit(OpCodes.Ldnull);
							else
							{
								il.Emit(OpCodes.Ldarg_0);
								if (originalType.IsValueType)
								{
									il.Emit(OpCodes.Ldobj, originalType);
									il.Emit(OpCodes.Box, originalType);
								}
							}

							if (methodInfo.IsStatic is false && harmonyMethod.nonVirtualDelegate is false)
							{
								il.Emit(OpCodes.Dup);
								il.Emit(OpCodes.Ldvirtftn, methodInfo);
							}
							else
								il.Emit(OpCodes.Ldftn, methodInfo);

							il.Emit(OpCodes.Newobj, delegateConstructor);
							continue;
						}
					}

					throw new Exception(
						$"Parameter \"{patchParam.Name}\" not found in method {original.FullDescription()}");
				}
			}

			//   original -> patch     opcode
			// --------------------------------------
			// 1 normal   -> normal  : LDARG
			// 2 normal   -> ref/out : LDARGA
			// 3 ref/out  -> normal  : LDARG, LDIND_x
			// 4 ref/out  -> ref/out : LDARG
			//
			var originalParamType = originalParameters[idx].ParameterType;
			var originalParamElementType =
				originalParamType.IsByRef ? originalParamType.GetElementType() : originalParamType;
			var patchParamType = patchParam.ParameterType;
			var patchParamElementType = patchParamType.IsByRef ? patchParamType.GetElementType() : patchParamType;
			var originalIsNormal = originalParameters[idx].IsOut is false && originalParamType.IsByRef is false;
			var patchIsNormal = patchParam.IsOut is false && patchParamType.IsByRef is false;
			var action = RustKernel.Argument(originalIsNormal, patchIsNormal,
				originalParamElementType.IsValueType, patchParamElementType.IsValueType);
			var patchArgIndex = idx + (isInstance ? 1 : 0);
			switch (action)
			{
				case RustKernel.ArgumentAction.LoadArgument:
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					break;
				case RustKernel.ArgumentAction.Box:
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					il.Emit(OpCodes.Box, originalParamElementType);
					break;
				case RustKernel.ArgumentAction.Address:
					il.Emit(OpCodes.Ldarga, patchArgIndex);
					break;
				case RustKernel.ArgumentAction.BoxTemporary:
				case RustKernel.ArgumentAction.IndirectBoxTemporary:
				{
					var byRef = action == RustKernel.ArgumentAction.IndirectBoxTemporary;
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					if (byRef) il.Emit(OpCodes.Ldobj, originalParamElementType);
					il.Emit(OpCodes.Box, originalParamElementType);
					var temporary = il.DeclareVariable(patchParamElementType);
					il.Emit(OpCodes.Stloc, temporary);
					il.Emit(OpCodes.Ldloca_S, temporary);
					tmpBoxVars.Add(new ArgumentBoxInfo
					{
						index = patchArgIndex, type = originalParamElementType, tmpVar = temporary, isByRef = byRef
					});
					break;
				}
				case RustKernel.ArgumentAction.Indirect:
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					if (originalParamElementType.IsValueType)
						il.Emit(OpCodes.Ldobj, originalParamElementType);
					else
						il.Emit(originalParameters[idx].ParameterType.GetCecilLoadOpCode());
					break;
				case RustKernel.ArgumentAction.IndirectBox:
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					il.Emit(OpCodes.Ldobj, originalParamElementType);
					il.Emit(OpCodes.Box, originalParamElementType);
					break;
			}
		}
	}

	private static List<(ParameterInfo param, string realName)> ParametersWithRealNames(MethodInfo method)
	{
		var baseArgs = method.GetArgumentAttributes();
		if (method.DeclaringType is not null)
			baseArgs = baseArgs.Union(method.DeclaringType.GetArgumentAttributes());

		return method.GetParameters().Select(p =>
		{
			var arg = p.GetArgumentAttribute();
			if (arg != null)
				return (p, arg.OriginalName ?? p.Name);
			return (p, baseArgs.GetRealName(p.Name, null) ?? p.Name);
		}).ToList();
	}

	private class PatchContext
	{
		public bool hasArgsArrayArg;
		public MethodInfo method;
		public bool wrapTryCatch;
	}

	private class ArgumentBoxInfo
	{
		public int index;
		public bool isByRef;
		public VariableDefinition tmpVar;
		public Type type;
	}
}
