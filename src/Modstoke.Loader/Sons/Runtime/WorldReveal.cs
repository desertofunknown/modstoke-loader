// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Keeps a newly loaded world behind the loading screen until mods have done their one-time setup for it, so models,
    /// attachments and layouts are built in frames nobody sees rather than in the player's first seconds.
    /// </summary>
    public static class WorldReveal
    {
        /// <summary>How long a hold keeps a loaded world covered unless it asks for another limit, in seconds.</summary>
        public const float DefaultLimitSeconds = 10f;

        private static readonly List<Hold> Holds = new();

        /// <summary>
        /// True from the world's scenes being in until the game starts its fade in: the world and the player are loaded
        /// and the loading screen still covers them, so a long frame only pauses the loading bar. One-time setup may take a
        /// larger share of each frame while this holds.
        /// </summary>
        public static bool Covered { get; internal set; }

        /// <summary>
        /// Holds every world reveal until <paramref name="ready"/> returns true, or for at most
        /// <paramref name="limitSeconds"/> after the world has loaded. Dispose the result to stop holding.
        /// </summary>
        /// <remarks>
        /// Register once, on the game thread. <paramref name="ready"/> runs on the game thread every frame while
        /// <see cref="Covered"/> holds. Return true when this world needs nothing from the mod, so worlds without work are
        /// not held, and never wait on something that only happens once the player can see (the player in the world, the
        /// opening fade). A hold that runs out is logged with its owner; one that throws stops holding for that load. A
        /// dedicated server has no loading screen and never asks.
        /// </remarks>
        public static IDisposable HoldUntil(string owner, Func<bool> ready, float limitSeconds = DefaultLimitSeconds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);
            ArgumentNullException.ThrowIfNull(ready);
            if (!(limitSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(limitSeconds), "The limit must be positive.");
            var hold = new Hold(owner, ready, limitSeconds);
            Holds.Add(hold);
            return hold;
        }

        // A new load: holds that ran out or failed on the last one ask again.
        internal static void BeginLoad()
        {
            foreach (var hold in Holds) hold.Finished = false;
        }

        // The first owner still holding this far into the covered world, or null when every hold is ready.
        internal static string Holder(float coveredSeconds)
        {
            for (var index = 0; index < Holds.Count; index++)
            {
                var hold = Holds[index];
                if (hold.Finished) continue;
                bool ready;
                try
                {
                    ready = hold.Ready();
                }
                catch (Exception error)
                {
                    hold.Finished = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("World reveal stopped waiting for " + hold.Owner + ", whose check failed: " + error.Message);
                    continue;
                }
                if (ready) continue;
                if (coveredSeconds < hold.Limit) return hold.Owner;
                hold.Finished = true;
                RuntimeEntryPoint.LogSource?.LogWarning("World reveal stopped waiting for " + hold.Owner + " after its " + hold.Limit.ToString("0.#") + " s limit.");
                PerformanceCapture.Mark("reveal-hold-expired", hold.Owner);
            }
            return null;
        }

        private sealed class Hold : IDisposable
        {
            internal readonly string Owner;
            internal readonly Func<bool> Ready;
            internal readonly float Limit;
            internal bool Finished;

            internal Hold(string owner, Func<bool> ready, float limit)
            {
                Owner = owner;
                Ready = ready;
                Limit = limit;
            }

            public void Dispose() => Holds.Remove(this);
        }
    }
}
