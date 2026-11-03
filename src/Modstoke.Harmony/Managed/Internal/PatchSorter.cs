using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib.Tools;

namespace HarmonyLib
{
	internal class PatchSorter
	{
		List<PatchSortingWrapper> patches;
		internal Patch[] sortedPatchArray;
		readonly bool debug;

		/// <summary>Creates a patch sorter</summary>
		/// <param name="patches">Array of patches that will be sorted</param>
		/// <param name="debug">Use debugging</param>
		internal PatchSorter(Patch[] patches, bool debug = false)
		{
			// Build the list of all patches first to be able to create dependency relationships.
			this.patches = [.. patches.Select(x => new PatchSortingWrapper(x))];
			this.debug = debug;

			// For each node find and bidirectionally register all it's dependencies.
			foreach (var node in this.patches)
			{
				node.AddBeforeDependency(this.patches.Where(x => node.innerPatch.before.Contains(x.innerPatch.owner)));
				node.AddAfterDependency(this.patches.Where(x => node.innerPatch.after.Contains(x.innerPatch.owner)));
			}

			// Sort list based on priority/index. This order will be maintain throughout the rest of the sorting process.
			SortByPriority();
		}

		void SortByPriority()
		{
			if (patches.Count < 2) return;
			// Patch subclasses can hide the fields read by the reflection-based comparer.
			// Keep their comparer calls and exception behavior on the managed sort path.
			foreach (var node in patches)
				if (node.innerPatch.GetType() != typeof(Patch))
				{
					patches.Sort();
					return;
				}

			var keys = new RustKernel.PatchPriorityKey[patches.Count];
			for (var i = 0; i < patches.Count; i++)
			{
				var patch = patches[i].innerPatch;
				keys[i] = new RustKernel.PatchPriorityKey { Index = patch.index, Priority = patch.priority };
			}
			var order = RustKernel.SortPatchPriorities(keys);
			var unsorted = patches.ToArray();
			for (var i = 0; i < order.Length; i++) patches[i] = unsorted[(int)order[i]];
		}

		/// <summary>Sorts internal PatchSortingWrapper collection and caches the results.
		/// After first run the result is provided from the cache.</summary>
		/// <param name="original">The original method</param>
		/// <returns>The sorted patch methods</returns>
		internal List<MethodInfo> Sort(MethodBase original) =>
			SortAsPatches(original).Select(x => x.GetMethod(original)).ToList();

		/// <summary>Sorts internal PatchSortingWrapper collection and caches the results.
		/// After first run the result is provided from the cache.</summary>
		/// <param name="original">The original method</param>
		/// <returns>The sorted patch methods as <see cref="Patch"/> instance</returns>
		internal Patch[] SortAsPatches(MethodBase original)
		{
			if (sortedPatchArray is not null) return sortedPatchArray;
			var identities = new Dictionary<MethodInfo, uint>();
			for (var i = 0; i < patches.Count; i++) patches[i].ordinal = (uint)i;
			var nodes = new RustKernel.Node[patches.Count];
			var edges = new List<uint>();
			for (var i = 0; i < patches.Count; i++)
			{
				var patch = patches[i];
				var method = patch.innerPatch.PatchMethod;
				if (!identities.TryGetValue(method, out var identity))
				{
					identity = (uint)identities.Count;
					identities.Add(method, identity);
				}
				nodes[i].Method = identity;
				nodes[i].BeforeStart = (uint)edges.Count;
				nodes[i].BeforeCount = (uint)patch.before.Count;
				foreach (var edge in patch.before) edges.Add(edge.ordinal);
				nodes[i].AfterStart = (uint)edges.Count;
				nodes[i].AfterCount = (uint)patch.after.Count;
				foreach (var edge in patch.after) edges.Add(edge.ordinal);
			}
			var plan = RustKernel.Sort(nodes, edges.ToArray());
			for (var i = 0; i < plan.Cycles.Length; i += 2)
			{
				var dependencyNode = patches[(int)plan.Cycles[i]];
				var dependentNode = patches[(int)plan.Cycles[i + 1]];
				// A log subscriber can throw. Retain each cull before invoking it so a retry
				// sees the same remaining graph as the managed dependency traversal.
				dependentNode.RemoveAfterDependency(dependencyNode);
				var dependency = dependencyNode.innerPatch;
				var dependent = dependentNode.innerPatch;
				Logger.Log(Logger.LogChannel.Debug, () =>
					$"Breaking dependence between {dependency.PatchMethod.FullDescription()} and {dependent.PatchMethod.FullDescription()}", debug);
			}
			sortedPatchArray = new Patch[plan.Order.Length];
			for (var i = 0; i < plan.Order.Length; i++)
				sortedPatchArray[i] = patches[(int)plan.Order[i]].innerPatch;
			patches = null;
			return sortedPatchArray;
		}
		/// <summary>Checks if the sorter was created with the same patch list and as a result can be reused to
		/// get the sorted order of the patches.</summary>
		/// <param name="patches">List of patches to check against</param>
		/// <returns>true if equal</returns>
		internal bool ComparePatchLists(Patch[] patches)
		{
			if (sortedPatchArray is null) _ = Sort(null);
			return patches is not null && sortedPatchArray.Length == patches.Length
				&& sortedPatchArray.All(x => patches.Contains(x, new PatchDetailedComparer()));
		}

		/// <summary>Wrapper used over the Patch object to allow faster dependency access and
		/// dependency removal in case of cyclic dependencies</summary>
		class PatchSortingWrapper : IComparable
		{
			internal readonly HashSet<PatchSortingWrapper> after;
			internal readonly HashSet<PatchSortingWrapper> before;
			internal readonly Patch innerPatch;
			internal uint ordinal;

			/// <summary>Create patch wrapper object used for sorting</summary>
			/// <param name="patch">Patch to wrap</param>
			internal PatchSortingWrapper(Patch patch)
			{
				innerPatch = patch;
				before = [];
				after = [];
			}

			/// <summary>Determines how patches sort</summary>
			/// <param name="obj">The other patch</param>
			/// <returns>integer to define sort order (-1, 0, 1)</returns>
			public int CompareTo(object obj)
			{
				var p = obj as PatchSortingWrapper;
				return PatchInfoSerialization.PriorityComparer(p?.innerPatch, innerPatch.index, innerPatch.priority);
			}

			/// <summary>Determines whether patches are equal</summary>
			/// <param name="obj">The other patch</param>
			/// <returns>true if equal</returns>
			public override bool Equals(object obj) => obj is PatchSortingWrapper wrapper && innerPatch.PatchMethod == wrapper.innerPatch.PatchMethod;

			/// <summary>Hash function</summary>
			/// <returns>A hash code</returns>
			public override int GetHashCode() => innerPatch.PatchMethod.GetHashCode();

			/// <summary>Bidirectionally registers Patches as after dependencies</summary>
			/// <param name="dependencies">List of dependencies to register</param>
			internal void AddBeforeDependency(IEnumerable<PatchSortingWrapper> dependencies)
			{
				foreach (var i in dependencies)
				{
					_ = before.Add(i);
					_ = i.after.Add(this);
				}
			}

			/// <summary>Bidirectionally registers Patches as before dependencies</summary>
			/// <param name="dependencies">List of dependencies to register</param>
			internal void AddAfterDependency(IEnumerable<PatchSortingWrapper> dependencies)
			{
				foreach (var i in dependencies)
				{
					_ = after.Add(i);
					_ = i.before.Add(this);
				}
			}

			/// <summary>Bidirectionally removes Patch from after dependencies</summary>
			/// <param name="afterNode">Patch to remove</param>
			internal void RemoveAfterDependency(PatchSortingWrapper afterNode)
			{
				_ = after.Remove(afterNode);
				_ = afterNode.before.Remove(this);
			}

			/// <summary>Bidirectionally removes Patch from before dependencies</summary>
			/// <param name="beforeNode">Patch to remove</param>
			internal void RemoveBeforeDependency(PatchSortingWrapper beforeNode)
			{
				_ = before.Remove(beforeNode);
				_ = beforeNode.after.Remove(this);
			}
		}

		internal class PatchDetailedComparer : IEqualityComparer<Patch>
		{
			public bool Equals(Patch x, Patch y)
			{
				return y is not null && x is not null && x.owner == y.owner && x.PatchMethod == y.PatchMethod && x.index == y.index
					&& x.priority == y.priority
					&& x.before.Length == y.before.Length && x.after.Length == y.after.Length
					&& x.before.All(y.before.Contains) && x.after.All(y.after.Contains);
			}

			public int GetHashCode(Patch obj) => obj.GetHashCode();
		}
	}
}
