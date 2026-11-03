using System;

namespace SUI;

internal static class MenuButtonRegistrationPolicy
{
    private static Func<string, bool> filter;

    internal static bool HasFilter => filter != null;

    internal static void SetFilter(Func<string, bool> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(filter, value))
            return;
        if (filter != null)
            throw new InvalidOperationException("Menu button registration ownership has already been selected.");

        filter = value;
    }

    internal static bool Allows(string id)
    {
        return filter == null || filter(id);
    }
}
