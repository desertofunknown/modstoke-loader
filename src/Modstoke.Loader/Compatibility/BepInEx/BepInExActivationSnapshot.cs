// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Loader
{
    internal sealed record BepInExActivationSnapshot
    {
        internal BepInExActivationSnapshot(string id, string version, bool registered,
            bool direct, bool versionNormalized)
        {
            Id = id;
            Version = version;
            Registered = registered;
            Direct = direct;
            VersionNormalized = versionNormalized;
        }

        internal string Id { get; }
        internal string Version { get; }
        internal bool Registered { get; }
        internal bool Direct { get; }
        internal bool VersionNormalized { get; }
    }
}
