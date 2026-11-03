// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.EventSystems;
using Sui = SUI.SUI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Lends SUI's spare event system to one Loader UI while the game has none active, as in normal play. Each lease
    /// owns an SUI enabler, so SUI's own count of users decides when the spare one turns off again.
    /// </summary>
    internal sealed class UiEventSystem
    {
        private readonly string _name;
        private GameObject _enabler;

        internal UiEventSystem(string name) => _name = name;

        /// <summary>Returns the active event system, turning on SUI's when no other is active. Main thread only.</summary>
        internal EventSystem Acquire()
        {
            var enabled = false;
            if (!IsActive(EventSystem.current))
            {
                if (_enabler == null)
                {
                    _enabler = new GameObject(_name);
                    _enabler.SetActive(false);
                    Object.DontDestroyOnLoad(_enabler);
                    _enabler.AddComponent<Sui.EventSystemEnabler>();
                }
                enabled = !_enabler.activeSelf;
                _enabler.SetActive(true);
            }
            var current = EventSystem.current;
            if (IsActive(current))
                return current;
            // A failed acquire must not leave SUI counting this enabler as a user.
            if (enabled)
                _enabler.SetActive(false);
            return null;
        }

        internal void Release()
        {
            if (_enabler != null && _enabler.activeSelf)
                _enabler.SetActive(false);
        }

        private static bool IsActive(EventSystem system) => system != null && system.isActiveAndEnabled;
    }
}
