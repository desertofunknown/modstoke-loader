// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using Il2CppInterop.Runtime;
using BepInExCollections = BepInEx.Unity.IL2CPP.Utils.Collections;
using RedLoaderCollections = RedLoader.Unity.IL2CPP.Utils.Collections;
using Il2CppIEnumerator = Il2CppSystem.Collections.IEnumerator;
using Il2CppObject = Il2CppSystem.Object;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAIManagedEnumerator :
        RedLoaderCollections.IManagedEnumeratorDriver,
        BepInExCollections.IManagedEnumeratorDriver
    {
        private static readonly Func<IEnumerator, RedLoaderCollections.IManagedEnumeratorDriver> RedLoaderFactory =
            routine => new OpenACAIManagedEnumerator(routine, child => new RedLoaderCollections.Il2CppManagedEnumerator(child));
        private static readonly Func<IEnumerator, BepInExCollections.IManagedEnumeratorDriver> BepInExFactory =
            routine => new OpenACAIManagedEnumerator(routine, child => new BepInExCollections.Il2CppManagedEnumerator(child));

        private readonly IEnumerator _routine;
        private readonly Func<IEnumerator, Il2CppObject> _wrapChild;
        private Il2CppObject _current;
        private bool _hasCurrent;

        private OpenACAIManagedEnumerator(IEnumerator routine, Func<IEnumerator, Il2CppObject> wrapChild)
        {
            _routine = routine;
            _wrapChild = wrapChild;
        }

        internal static void InstallRedLoader()
        {
            RedLoaderCollections.ManagedEnumeratorDriverFactory.SetFactory(RedLoaderFactory);
        }

        internal static void InstallBepInEx()
        {
            BepInExCollections.ManagedEnumeratorDriverFactory.SetFactory(BepInExFactory);
        }

        public Il2CppObject Current
        {
            get
            {
                if (!_hasCurrent)
                {
                    _current = ConvertYield(_routine.Current);
                    _hasCurrent = true;
                }

                return _current;
            }
        }

        public bool MoveNext()
        {
            _current = null;
            _hasCurrent = false;
            return _routine.MoveNext();
        }

        public void Reset()
        {
            _current = null;
            _hasCurrent = false;
            _routine.Reset();
        }

        private Il2CppObject ConvertYield(object value)
        {
            return value switch
            {
                Il2CppIEnumerator native => native.Cast<Il2CppObject>(),
                IEnumerator child => _wrapChild(child),
                Il2CppObject native => native,
                string text => new Il2CppObject(IL2CPP.ManagedStringToIl2Cpp(text)),
                bool primitive => Box(primitive),
                char primitive => Box(primitive),
                sbyte primitive => Box(primitive),
                byte primitive => Box(primitive),
                short primitive => Box(primitive),
                ushort primitive => Box(primitive),
                int primitive => Box(primitive),
                uint primitive => Box(primitive),
                long primitive => Box(primitive),
                ulong primitive => Box(primitive),
                float primitive => Box(primitive),
                double primitive => Box(primitive),
                IntPtr primitive => Box(primitive),
                UIntPtr primitive => Box(primitive),
                null => null,
                _ => throw new NotSupportedException($"Type {value.GetType()} cannot be converted directly to an Il2Cpp object")
            };
        }

        private static unsafe Il2CppObject Box<T>(T value) where T : unmanaged
        {
            // Keep the value's storage valid through the native call, as the original adapter did.
            var pointer = IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, (IntPtr)(&value));
            return new Il2CppObject(pointer);
        }
    }
}
