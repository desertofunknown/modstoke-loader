// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Inventory;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed record CartSaveRecord(string Name, int ItemId, int State, bool RuntimeCreated,
            Vector3 Position, Vector3 Rotation, int DataLength, string Version, int BaseTypeId,
            int CartTypeId, float Power);

        private static CartSaveRecord ReadCartSaveRecord(WorldItemState state)
        {
            RequireExactSaveType<WorldItemState>(state);
            Require(!string.IsNullOrEmpty(state.ObjectNameId) && state.State != 1,
                "Unnamed or destroyed save records cannot be reserved.");
            var data = state.ItemStateData;
            Require(data == null || !data.WasCollected, "Save data is unavailable.");
            int length = data == null ? -1 : data.Length;
            Require(length >= -1 && length <= 1,
                "Only absent, empty or single cart-power save data is supported; opaque entries cannot be dropped.");
            string version = null;
            int baseType = 0, cartType = 0;
            float power = 0;
            if (length == 1)
            {
                var item = data[0];
                RequireExactSaveType<GolfCartWorldItemStateData>(item);
                var cart = item.Cast<GolfCartWorldItemStateData>();
                version = cart.Version;
                baseType = item.StateDataTypeId;
                cartType = cart.StateDataTypeId;
                power = Finite(cart.Power);
            }
            var position = state.Position;
            var rotation = state.Rotation;
            return new(state.ObjectNameId, state.ItemId, state.State, state.RuntimeCreated,
                new(Finite(position.x), Finite(position.y), Finite(position.z)),
                new(Finite(rotation.x), Finite(rotation.y), Finite(rotation.z)),
                length, version, baseType, cartType, power);
        }

        private static WorldItemState CopyCartSaveRecord(WorldItemState source)
        {
            var snapshot = ReadCartSaveRecord(source);
            var copy = MaterializeCartSaveRecord(snapshot);
            Require(ReadCartSaveRecord(source) == snapshot, "Save data changed while copying; no reservation was made.");
            return copy;
        }

        private static WorldItemState MaterializeCartSaveRecord(CartSaveRecord snapshot)
        {
            var copy = new WorldItemState
            {
                ObjectNameId = snapshot.Name,
                ItemId = snapshot.ItemId,
                State = snapshot.State,
                RuntimeCreated = snapshot.RuntimeCreated,
                Position = U(snapshot.Position),
                Rotation = U(snapshot.Rotation)
            };
            if (snapshot.DataLength >= 0)
            {
                var data = new Il2CppReferenceArray<WorldItemStateData>(size: snapshot.DataLength);
                if (snapshot.DataLength == 1)
                {
                    var cart = new GolfCartWorldItemStateData
                    {
                        Version = snapshot.Version,
                        StateDataTypeId = snapshot.CartTypeId,
                        Power = snapshot.Power
                    };
                    // The derived class hides a separate base StateDataTypeId field.
                    ((WorldItemStateData)cart).StateDataTypeId = snapshot.BaseTypeId;
                    data[0] = cart;
                }
                copy.ItemStateData = data;
            }
            else copy.ItemStateData = null;
            Require(ReadCartSaveRecord(copy) == snapshot, "Independent save-record materialization differs.");
            return copy;
        }

        private static void RequireExactSaveType<T>(Il2CppObjectBase value) where T : Il2CppObjectBase
        {
            RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
            Require(value != null && !value.WasCollected
                && IL2CPP.il2cpp_object_get_class(value.Pointer) == Il2CppClassPointerStore<T>.NativeClassPtr,
                "Unsupported or unavailable native save/controller class.");
        }
    }
}
