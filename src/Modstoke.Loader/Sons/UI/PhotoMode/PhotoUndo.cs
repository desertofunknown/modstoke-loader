// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Loader
{
    /// <summary>
    /// Everything a photo session changed in the game, each with the step that puts it back. The steps run newest
    /// first, each on its own, so one that fails does not keep the rest from running; running the log again does
    /// nothing, as every step is taken off before it runs.
    /// </summary>
    internal sealed class PhotoUndo
    {
        private readonly List<(string Name, Action Undo)> _steps = new();

        internal int Count => _steps.Count;

        internal void Add(string name, Action undo) => _steps.Add((name, undo));

        internal void Run()
        {
            for (var i = _steps.Count - 1; i >= 0; i--)
            {
                if (i >= _steps.Count) continue;
                var (name, undo) = _steps[i];
                _steps.RemoveAt(i);
                try
                {
                    undo();
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not put back " + name + ": " + error.Message);
                }
            }
        }
    }
}
