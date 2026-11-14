// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Modstoke.Loader
{
    public sealed class ModDependency
    {
        internal ModDependency(string id, string version)
        {
            Id = id;
            Version = version;
        }

        public string Id { get; }
        public string Version { get; }
    }

    internal sealed class ModDependencyDeclaration
    {
        internal ModDependencyDeclaration(
            IReadOnlyList<ModDependency> requirements, string issue, bool declared)
        {
            Requirements = Array.AsReadOnly(requirements.ToArray());
            Issue = issue ?? "";
            Declared = declared;
        }

        public IReadOnlyList<ModDependency> Requirements { get; }
        public string Issue { get; }
        public bool Declared { get; }
    }

    internal sealed class ModDependencyPlan
    {
        internal ModDependencyPlan(
            IReadOnlyList<ModCandidateSnapshot> ordered,
            IDictionary<ModCandidateSnapshot, string> issues,
            IDictionary<ModCandidateSnapshot, IReadOnlyList<ModCandidateSnapshot>> required)
        {
            Ordered = Array.AsReadOnly(ordered.ToArray());
            Issues = new ReadOnlyDictionary<ModCandidateSnapshot, string>(
                new Dictionary<ModCandidateSnapshot, string>(issues));
            var copy = new Dictionary<ModCandidateSnapshot, IReadOnlyList<ModCandidateSnapshot>>();
            foreach (var pair in required)
                copy.Add(pair.Key, Array.AsReadOnly(pair.Value.ToArray()));
            Required = new ReadOnlyDictionary<ModCandidateSnapshot, IReadOnlyList<ModCandidateSnapshot>>(copy);
        }

        public IReadOnlyList<ModCandidateSnapshot> Ordered { get; }
        public IReadOnlyDictionary<ModCandidateSnapshot, string> Issues { get; }
        public IReadOnlyDictionary<ModCandidateSnapshot, IReadOnlyList<ModCandidateSnapshot>> Required { get; }
    }

    internal static class ModDependencies
    {
        private const int MaxRequirements = 64;
        private const int MaxTextLength = 256;

        internal static ModDependencyDeclaration Read(JsonElement root, string modId)
        {
            try
            {
                return ReadDeclaration(root, modId);
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException)
            {
                return Invalid("The native dependency declaration contains invalid JSON text.");
            }
        }

        private static ModDependencyDeclaration ReadDeclaration(JsonElement root, string modId)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Invalid("The native manifest must be a JSON object.", false);

            var found = false;
            var declaration = default(JsonElement);
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals("modstokeDependencies", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found)
                    return Invalid("Duplicate modstokeDependencies declaration.");
                found = true;
                declaration = property.Value;
            }
            if (!found)
                return new ModDependencyDeclaration(Array.Empty<ModDependency>(), "", false);
            if (!IsValidText(modId, ModHost.MaxModIdLength))
                return Invalid("A native mod declaring dependencies must have an unmodified, nonblank id of at most 96 characters, without control characters or surrounding whitespace.");
            if (declaration.ValueKind != JsonValueKind.Object)
                return Invalid("modstokeDependencies must be an object.");

            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var required = default(JsonElement);
            foreach (var property in declaration.EnumerateObject())
            {
                if (!members.Add(property.Name))
                    return Invalid("Duplicate member in modstokeDependencies.");
                if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var schema) || schema != 1)
                        return Invalid("modstokeDependencies.schemaVersion must be the integer 1.");
                }
                else if (property.Name.Equals("required", StringComparison.OrdinalIgnoreCase))
                    required = property.Value;
                else
                    return Invalid("Unknown member in modstokeDependencies.");
            }
            if (!members.Contains("schemaVersion"))
                return Invalid("modstokeDependencies.schemaVersion is required.");
            if (required.ValueKind != JsonValueKind.Array)
                return Invalid("modstokeDependencies.required must be an array.");
            if (required.GetArrayLength() > MaxRequirements)
                return Invalid("modstokeDependencies.required may contain at most 64 entries.");

            var requirements = new List<ModDependency>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in required.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    return Invalid("Each required native dependency must be an object.");
                members.Clear();
                string id = null;
                string version = null;
                foreach (var property in entry.EnumerateObject())
                {
                    if (!members.Add(property.Name))
                        return Invalid("Duplicate member in a required native dependency.");
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryReadText(property.Value, ModHost.MaxModIdLength, out id))
                            return Invalid("A required native dependency id must be nonblank text of at most 96 characters, without control characters or surrounding whitespace.");
                    }
                    else if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryReadText(property.Value, MaxTextLength, out version))
                            return Invalid("A required native dependency version must be nonblank text of at most 256 characters, without control characters or surrounding whitespace.");
                        if (LoaderVersionRequirement.IsMinimum(version) && !LoaderVersionRequirement.IsValidMinimum(version))
                            return Invalid("A minimum dependency version must be \">=\" and a version like 0.3.0.");
                    }
                    else
                        return Invalid("Unknown member in a required native dependency.");
                }
                if (id == null || version == null)
                    return Invalid("Each required native dependency must declare id and version.");
                if (!ids.Add(id))
                    return Invalid("Duplicate required native dependency id.");
                if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase))
                    return Invalid("A native mod cannot require itself.");
                requirements.Add(new ModDependency(id, version));
            }
            return new ModDependencyDeclaration(requirements, "", true);
        }

        private static bool TryReadText(JsonElement value, int maxLength, out string text)
        {
            text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return IsValidText(text, maxLength);
        }

        private static bool IsValidText(string text, int maxLength)
        {
            return !string.IsNullOrWhiteSpace(text) && text.Length <= maxLength
                && text.Equals(text.Trim(), StringComparison.Ordinal) && !text.Any(char.IsControl);
        }

        private static ModDependencyDeclaration Invalid(string issue, bool declared = true)
        {
            return new ModDependencyDeclaration(Array.Empty<ModDependency>(), issue, declared);
        }

        internal static ModDependencyPlan Plan(IReadOnlyList<ModCandidateSnapshot> candidates)
        {
            var issues = new Dictionary<ModCandidateSnapshot, string>();
            var required = new Dictionary<ModCandidateSnapshot, IReadOnlyList<ModCandidateSnapshot>>();
            var byId = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var byAssembly = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var dependencies = new List<int>[candidates.Count];
            var consumers = new List<int>[candidates.Count];

            for (var i = 0; i < candidates.Count; i++)
            {
                dependencies[i] = new List<int>();
                consumers[i] = new List<int>();
                var candidate = candidates[i];
                if (!IsExecutable(candidate))
                    continue;
                AddIndex(byId, candidate.ModId, i);
                if (!string.IsNullOrEmpty(candidate.DependencyIssue))
                    issues[candidate] = candidate.DependencyIssue;
                else if (!candidate.Hostable)
                    issues[candidate] = string.IsNullOrWhiteSpace(candidate.Detail)
                        ? "The native hosting contract is invalid or unsupported." : candidate.Detail;
                if (!candidate.HasAssembly)
                    continue;
                try
                {
                    AddIndex(byAssembly, Path.GetFullPath(candidate.AssemblyPath), i);
                }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException)
                {
                    issues[candidate] = "The native assembly path is invalid.";
                }
            }
            foreach (var group in byId.Values.Concat(byAssembly.Values))
            {
                if (group.Count > 1)
                    foreach (var index in group)
                        issues[candidates[index]] = "More than one enabled native manifest identifies this mod ID or assembly.";
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var targets = new List<ModCandidateSnapshot>();
                required.Add(candidate, targets);
                if (!IsExecutable(candidate))
                    continue;
                foreach (var dependency in candidate.Dependencies)
                {
                    string issue = null;
                    if (!byId.TryGetValue(dependency.Id, out var matches))
                        issue = "Required native mod '" + dependency.Id + "' has no enabled executable candidate.";
                    else if (matches.Count != 1)
                        issue = "Required native mod '" + dependency.Id + "' has duplicate enabled candidates.";
                    else
                    {
                        var targetIndex = matches[0];
                        var target = candidates[targetIndex];
                        if (!target.DependencyIdentityValid)
                            issue = "Required native mod '" + dependency.Id + "' has an invalid or truncated manifest identity.";
                        else if (!LoaderVersionRequirement.Satisfies(target.Version, dependency.Version))
                            issue = "Required native mod '" + dependency.Id + "' is version " + target.Version + "; this mod needs "
                                + (LoaderVersionRequirement.IsMinimum(dependency.Version) ? dependency.Version[2..] + " or newer." : "exactly " + dependency.Version + ".");
                        else
                        {
                            targets.Add(target);
                            dependencies[i].Add(targetIndex);
                            consumers[targetIndex].Add(i);
                        }
                    }
                    // A missing prerequisite usually also leaves its assembly unresolved; the dependency says why.
                    if (issue != null && (!issues.ContainsKey(candidate)
                        || !candidate.Hostable && candidate.IssueCode == "unresolved-assembly-references"
                            && issues[candidate] == candidate.Detail))
                        issues[candidate] = issue;
                }
            }

            // Propagate known failures before sorting so an invalid target can never satisfy a requirement.
            var blocked = new Queue<int>();
            for (var i = 0; i < candidates.Count; i++)
                if (issues.ContainsKey(candidates[i]))
                    blocked.Enqueue(i);
            while (blocked.Count > 0)
            {
                var targetIndex = blocked.Dequeue();
                foreach (var consumer in consumers[targetIndex])
                {
                    if (issues.ContainsKey(candidates[consumer]))
                        continue;
                    issues[candidates[consumer]] = "Required native mod '" + candidates[targetIndex].ModId
                        + "' is blocked by an invalid or unsatisfied hosting or dependency contract.";
                    blocked.Enqueue(consumer);
                }
            }

            var indegrees = new int[candidates.Count];
            var ready = new SortedSet<int>();
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!issues.ContainsKey(candidates[i]))
                    indegrees[i] = dependencies[i].Count;
                if (indegrees[i] == 0)
                    ready.Add(i);
            }
            var ordered = new List<ModCandidateSnapshot>(candidates.Count);
            var emitted = new bool[candidates.Count];
            while (ready.Count > 0)
            {
                var index = ready.Min;
                ready.Remove(index);
                emitted[index] = true;
                ordered.Add(candidates[index]);
                foreach (var consumer in consumers[index])
                {
                    if (!issues.ContainsKey(candidates[consumer]) && --indegrees[consumer] == 0)
                        ready.Add(consumer);
                }
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                if (emitted[i])
                    continue;
                issues[candidates[i]] = "The native dependency graph contains a cycle or this mod depends on a cycle.";
                ordered.Add(candidates[i]);
            }
            return new ModDependencyPlan(ordered, issues, required);
        }

        private static bool IsExecutable(ModCandidateSnapshot candidate)
        {
            return candidate.Enabled && !candidate.MetadataOnly;
        }

        private static void AddIndex(Dictionary<string, List<int>> index, string key, int candidateIndex)
        {
            if (!index.TryGetValue(key, out var matches))
            {
                matches = new List<int>();
                index.Add(key, matches);
            }
            matches.Add(candidateIndex);
        }
    }
}
