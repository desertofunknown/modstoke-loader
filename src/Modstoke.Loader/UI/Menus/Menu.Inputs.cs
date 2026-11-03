// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Loader
{
    /// <summary>Transient form fields. Providers retain ownership of drafts and their lifetime.</summary>
    public interface IMenuInputProvider : IMenuProvider
    {
        MenuInputSnapshot GetInputs();
        bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result);
    }

    public sealed class MenuInputSnapshot
    {
        public MenuInputSnapshot(Guid scope, IReadOnlyList<MenuInputFieldSnapshot> fields)
        {
            if (scope == Guid.Empty)
                throw new ArgumentException("A form scope is required.", nameof(scope));
            if (fields == null || fields.Count > 8)
                throw new ArgumentException("A form supports up to eight fields.", nameof(fields));
            var copy = fields.ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (copy.Any(field => field == null || !ids.Add(field.Id)))
                throw new ArgumentException("Form field IDs must be unique.", nameof(fields));
            Scope = scope;
            Fields = Array.AsReadOnly(copy);
        }

        public Guid Scope { get; }
        public IReadOnlyList<MenuInputFieldSnapshot> Fields { get; }
    }

    public sealed class MenuInputFieldSnapshot
    {
        public MenuInputFieldSnapshot(string id, string displayName, string value,
            string description, int maximumLength, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
                throw new ArgumentException("A field ID of up to 64 characters is required.", nameof(id));
            if (maximumLength < 1 || maximumLength > 1024)
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            if (value != null && value.Length > maximumLength)
                throw new ArgumentException("The field value exceeds its character limit.", nameof(value));
            Id = id;
            DisplayName = displayName ?? id;
            Value = value ?? "";
            Description = description ?? "";
            MaximumLength = maximumLength;
            Enabled = enabled;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Value { get; }
        public string Description { get; }
        public int MaximumLength { get; }
        public bool Enabled { get; }
    }

    public static partial class MenuRegistry
    {
        public static bool TryGetProviderInputs(string providerId, out MenuInputSnapshot inputs)
        {
            inputs = null;
            if (string.IsNullOrWhiteSpace(providerId))
                return false;
            if (!TryGetRegistration(providerId, out var provider, out var registered))
                return false;
            if (!(provider is IMenuInputProvider inputProvider))
                return false;
            try
            {
                var created = inputProvider.GetInputs();
                if (!IsCurrentRegistration(registered, provider))
                    return false;
                inputs = created;
                return created != null;
            }
            catch
            {
                // Form values may contain private locations or notes. Never log provider exceptions here.
                return false;
            }
        }

        public static bool TrySetProviderInput(string providerId, Guid scope, string fieldId, string value,
            out CommandResultSnapshot result)
        {
            result = CommandResultSnapshot.Failure("This form is no longer available.");
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(fieldId) || value == null || scope == Guid.Empty)
                return false;
            if (!TryGetRegistration(providerId, out var provider, out var registered))
                return false;
            if (!(provider is IMenuInputProvider inputProvider))
                return false;
            try
            {
                var inputs = inputProvider.GetInputs();
                if (!provider.Enabled || inputs == null || inputs.Scope != scope || !IsCurrentRegistration(registered, provider))
                    return false;
                var field = inputs.Fields.FirstOrDefault(item => string.Equals(item.Id, fieldId, StringComparison.Ordinal));
                if (field == null || !field.Enabled || value.Length > field.MaximumLength)
                    return false;
                // The provider must recheck the scope under its own lock before changing the draft.
                var applied = inputProvider.TrySetInput(scope, fieldId, value, out result);
                if (result == null || (!applied && result.Succeeded))
                    result = applied ? CommandResultSnapshot.Success("Draft updated.")
                        : CommandResultSnapshot.Failure("The draft was not changed.");
                return applied && result.Succeeded;
            }
            catch
            {
                result = CommandResultSnapshot.Failure("The draft could not be updated.");
                return false;
            }
        }
    }
}
