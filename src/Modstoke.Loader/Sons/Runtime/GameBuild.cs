// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;

namespace Modstoke.Loader
{
    internal static class GameBuild
    {
        private static readonly Lazy<string> AssemblyHash = new Lazy<string>(() =>
            Hash(Path.Combine(Paths.GameRootPath, "GameAssembly.dll")));

        private static readonly Lazy<bool> Recognized = new Lazy<bool>(() =>
            AssemblyHash.Value == "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B"
            && Hash(MetadataPath("SonsOfTheForest_Data")) == "6A1A92A2E30BD55FCC86C24DFFA95D8AFC58BEC76E6A8729FD8D57B81E4D26C7");

        // Same release (Steam build 20228410), but a separately compiled server binary whose native
        // bodies differ from the client's. Only hooks reviewed against this binary may accept it.
        private static readonly Lazy<bool> DedicatedServer = new Lazy<bool>(() =>
            AssemblyHash.Value == "DEADBEF72344C5D19E2572E607F0C1ECBD0B85200EC9B98D5939753CDE5929A2"
            && Hash(MetadataPath("SonsOfTheForestDS_Data")) == "D9DF15CE6470003DCFFE1149A2D11625FA6B3FEB7DB509BCD65E3B268BFE448C");

        // The client build every pinned native body was qualified on.
        internal static bool IsRecognized => Recognized.Value;
        internal static bool IsDedicatedServer => DedicatedServer.Value;
        // Server-core hooks rely on managed signatures and runtime layout checks, not pinned client bodies.
        internal static bool SupportsServerCore => IsRecognized || IsDedicatedServer;

        private static string MetadataPath(string dataFolder) =>
            Path.Combine(Paths.GameRootPath, dataFolder, "il2cpp_data", "Metadata", "global-metadata.dat");

        // A missing or unreadable file is another build, never an exception: Lazy would rethrow it from every gate.
        private static string Hash(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
