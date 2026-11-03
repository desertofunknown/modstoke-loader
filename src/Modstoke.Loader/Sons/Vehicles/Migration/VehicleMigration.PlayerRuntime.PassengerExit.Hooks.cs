// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Call before installing cart writers, occupant writers or client mode hooks.
        // Their shared methods must be hashed while the native entries are still pristine.
        internal static void QualifyCartPassengerExitHooks() => CartPassengerExitHooks.Qualify();

        private static class CartPassengerExitHooks
        {
            private static bool _attempted, _installed;
            private static string _failure;
            private static MethodInfo[] _methods;
            internal static LocalPassengerExit Client;

            internal static void Qualify()
            {
                if (_methods != null) return;
                Require(GameBuild.IsRecognized, "Passenger native qualification requires the recognized game build.");
                var methods = new[]
                {
                        Q(typeof(GolfCartController), "DisconnectSlot", 0x33D4B40, 672,
                            "3A53B4A367038E6726C693D4C0BF572B2D925D3E4261A552CC25886EDB4D70CD", typeof(GolfCartConnectSlotEvent)),
                        Q(typeof(GolfCartController), "DisconnectPassenger", 0x33CC4A0, 320,
                            "BED09FD94C5CBFC4124307665D39B45863D85D3C68EA3AA5CFE2663F0F603120", typeof(int), typeof(bool), typeof(bool)),
                        Q(typeof(PlayerGolfCartAction), "Disconnect", 0x33DE450, 912,
                            "7FCE9075A8EA2FF906F701813FEA36ADCEC9AD32FC0E455DAD9264000191A4C0", typeof(GolfCartController), typeof(bool)),
                        Q(typeof(PlayerGolfCartPassengerAction), "DisconnectPlayerHook", 0x33E2170, 2640,
                            "DE6CD8C1A03CE7B515EEEBA7BD1FF93AA365A7DE5000282D212859E59C2153AA", typeof(GolfCartController), typeof(bool)),
                        Q(typeof(PlayerGolfCartPassengerAction), "ClearInputs", 0x33E1FA0, 336,
                            "5A67BFD383D0FDBA76E08B842AE324B06DB406D70FC628CF2AB4694695105736"),
                        Q(typeof(GolfCartController), "DisconnectLocalDriver", 0x33CBEB0, 656,
                            "B691F5A4B51F177BB4C9D39E1B683B3A2B8B622F4CBA7340651AB38E45152E28", typeof(bool), typeof(bool)),
                        Q(typeof(PlayerGolfCartDriverAction), "DisconnectPlayerHook", 0x33DFE80, 2704,
                            "26728A10CFF21B4CF60698C06F119635FB2B02916A0B72CEFB031F025D841DCD", typeof(GolfCartController), typeof(bool)),
                        Q(typeof(PlayerGolfCartDriverAction), "ClearInputs", 0x33DFD30, 336,
                            "E72ADD715A604D784C7EE3F4047CBD5BFB8D09216F41822468DFC3FB4312411D"),
                        Q(typeof(PlayerGolfCartDriverAction), "DisconnectIk", 0x33E0A40, 240,
                            "498B1C080B2337B6DB3B8E3F16F9ECE6E7066A0735244DB23DD0FC2F250543F8", typeof(GolfCartController))
                };
                _ = Q(typeof(GolfCartController), "OnPassengerSlotChanged", 0x33CF950, 1104,
                    "39AF3000306B20B4311A272601A1EB5762EEBE86101AEA2C6795095AC90AD693", typeof(BoltEntity), typeof(int));
                _methods = methods;
            }

            internal static void Arm()
            {
                Require(OnSelectedThread() && CartWriterHooks.Installed && GameBuild.IsRecognized
                    && _methods != null, "Passenger exit requires early native qualification and the selected cart lifecycle thread.");
                if (!_attempted)
                {
                    _attempted = true;
                    HarmonyLib.Harmony harmony = null;
                    try
                    {
                        harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-cart-passenger-exit");
                        Patch(_methods[0], nameof(BeforeSlot), nameof(AfterSlot));
                        Patch(_methods[1], nameof(BeforeLocal), nameof(AfterLocal));
                        Patch(_methods[2], nameof(BeforeAction), nameof(AfterLocal));
                        Patch(_methods[3], nameof(BeforeAction), nameof(AfterLocal));
                        Patch(_methods[4], nameof(BeforeClear), nameof(AfterLocal));
                        Patch(_methods[5], nameof(BeforeLocalDriver), nameof(AfterLocal));
                        Patch(_methods[6], nameof(BeforeAction), nameof(AfterLocal));
                        Patch(_methods[7], nameof(BeforeClear), nameof(AfterLocal));
                        Patch(_methods[8], nameof(BeforeDriverIk), nameof(AfterLocal));
                        _installed = true;

                        void Patch(MethodInfo method, string before, string after)
                            => harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(CartPassengerExitHooks), before) { priority = Priority.Last },
                                finalizer: new HarmonyMethod(typeof(CartPassengerExitHooks), after) { priority = Priority.Last });
                    }
                    catch (Exception error)
                    {
                        _failure = error.Message;
                        try { harmony?.UnpatchSelf(); } catch (Exception) { }
                    }
                }
                Require(_installed, _failure ?? "Qualified passenger exit hooks are unavailable.");
            }

            private static MethodInfo Q(Type type, string name, int rva, int length, string hash, params Type[] args)
                => CartWriterHooks.Qualify(type, name, typeof(void), rva, length, hash, args);

            private static bool BeforeSlot(GolfCartController __instance, GolfCartConnectSlotEvent __0,
                bool __runOriginal, out object __state)
            {
                __state = null;
                if (!__runOriginal) return false;
                var dispatch = CartPlayerPassengerAuthority.DispatchFor(__instance);
                if (dispatch != null)
                {
                    if (!dispatch.AcceptDispatch(__0)) { dispatch.Quarantine(); return false; }
                    __state = dispatch;
                    return true;
                }
                if (__0 == null) return !CartPlayerPassengerAuthority.IsAcquired(__instance);
                var driver = CartDriverAuthorityHooks.ForMigration(__instance);
                if (driver?.RequiresOrderedExit == true && __0.SlotIndex == 0)
                {
                    if (!driver.AcceptExitDispatch(__0))
                    { driver.Quarantine("An unordered native driver disconnect crossed its retained authority."); return false; }
                    __state = driver;
                    return true;
                }
                return CartPlayerPassengerAuthority.ForSeat(__instance, __0.SlotIndex) == null;
            }

            private static Exception AfterSlot(object __state, bool __runOriginal, Exception __exception)
            {
                if (__state is CartPlayerPassengerAuthority passenger)
                {
                    if (!__runOriginal || __exception != null) passenger.Quarantine();
                    else passenger._exitDispatchCompleted = true;
                }
                else if (__state is CartDriverAuthority driver)
                {
                    if (!__runOriginal || __exception != null) driver.Quarantine("Original driver slot disconnect failed or was suppressed.");
                    else driver._exitDispatchCompleted = true;
                }
                return __exception;
            }

            private static bool BeforeLocal(GolfCartController __instance, int __0, bool __1, bool __2,
                bool __runOriginal, out int __state)
            {
                __state = 0;
                var receipt = Client;
                if (receipt == null || receipt.Cart.Pointer != __instance?.Pointer) return __runOriginal;
                return BeginLocal(1, __runOriginal && __0 == receipt.Slot && !__1 && __2 == receipt.SkipReequip, out __state);
            }

            private static bool BeforeAction(PlayerGolfCartAction __instance, GolfCartController __0, bool __1,
                MethodBase __originalMethod, bool __runOriginal, out int __state)
            {
                __state = 0;
                var receipt = Client;
                if (receipt == null || receipt.Action.Pointer != __instance?.Pointer) return __runOriginal;
                int flag = __originalMethod.Name == "Disconnect" ? 2 : 4;
                return BeginLocal(flag, __runOriginal && __0?.Pointer == receipt.Cart.Pointer && __1 == receipt.SkipReequip
                    && (flag == 2 ? (receipt.Entered & 1) != 0 : (receipt.Completed & 8) != 0), out __state);
            }

            private static bool BeforeLocalDriver(GolfCartController __instance, bool __0, bool __1,
                bool __runOriginal, out int __state)
            {
                __state = 0;
                var receipt = Client;
                if (receipt == null || receipt.Cart.Pointer != __instance?.Pointer) return __runOriginal;
                return BeginLocal(1, __runOriginal && receipt.Slot == 0 && !__0 && __1 == receipt.SkipReequip, out __state);
            }

            private static bool BeforeDriverIk(PlayerGolfCartDriverAction __instance, GolfCartController __0,
                bool __runOriginal, out int __state)
            {
                __state = 0;
                var receipt = Client;
                if (receipt == null || receipt.Action.Pointer != __instance?.Pointer) return __runOriginal;
                return BeginLocal(16, __runOriginal && receipt.Slot == 0 && __0?.Pointer == receipt.Cart.Pointer
                    && (receipt.Completed & 4) != 0, out __state);
            }

            private static bool BeforeClear(PlayerGolfCartAction __instance, bool __runOriginal, out int __state)
            {
                __state = 0;
                var receipt = Client;
                if (receipt == null || receipt.Action.Pointer != __instance?.Pointer) return __runOriginal;
                return BeginLocal(8, __runOriginal && (receipt.Entered & 2) != 0, out __state);
            }

            private static bool BeginLocal(int flag, bool exact, out int state)
            {
                state = 0;
                var receipt = Client;
                if (!OnSelectedThread() || !exact || receipt.Failure != null || (receipt.Entered & flag) != 0)
                { receipt.Failure ??= "Native passenger exit was suppressed, reentered or changed its receiver/arguments."; return false; }
                receipt.Entered |= flag; state = flag;
                return true;
            }

            private static Exception AfterLocal(int __state, bool __runOriginal, Exception __exception)
            {
                if (__state == 0) return __exception;
                var receipt = Client;
                if (receipt == null) return __exception;
                if (!OnSelectedThread() || !__runOriginal || __exception != null)
                    receipt.Failure ??= "Original native passenger exit failed or was suppressed.";
                else receipt.Completed |= __state;
                return __exception;
            }
        }
    }
}
