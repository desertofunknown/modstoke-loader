// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Animation;
using global::Sons.Ai.Vail;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Gameplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Opt-in qualification/installation only. All receivers pass through until a deferred
        /// participant acquires its exact action/ghost hold, on a later game frame.
        /// Includes exact retained Vail, seated local player and IK writers, but not general locomotion, arbitrary
        /// action coroutines or mod writers. Installation alone never certifies Ready.
        /// </summary>
        public static bool TryArmOccupiedCartWriterHooks(out string detail)
        {
            detail = "Occupant writer arming requires the selected game thread without reentry.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                CartOccupantWriterHooks.Arm();
                detail = "Qualified cart action, ghost, Vail, seated local player and IK entries armed passively. Acquire exact occupant holds on a later frame; remote player rigs and native continuations remain separate.";
                return true;
            }
            catch (Exception error) { detail = Failure("Occupant writer arming rejected", error); return false; }
            finally { _busy = false; }
        }

        private static partial class CartOccupantWriterHooks
        {
            private readonly record struct Entry(IntPtr Pointer, CartOccupantWriterHold Hold);
            private static Entry[] _entries = Array.Empty<Entry>();
            private static bool _attempted;
            private static volatile bool _installed;
            private static int _thread, _frame, _depth, _wrongThread;
            private static string _failure;

            internal static void Arm()
            {
                Require(CartWriterHooks.Installed && GameBuild.IsRecognized && OnSelectedThread(),
                    "Qualified cart lifecycle support must already be installed.");
                if (!_attempted)
                {
                    _attempted = true;
                    HarmonyLib.Harmony harmony = null;
                    try
                    {
                        // Matched GameAssembly 9BEED267... / metadata 6A1A92A2.... All entries
                        // below are unique except Ghost.Update/UpdateDebugRenderers (same type,
                        // same void ABI). Never patch the shared empty 0x180597160 or registration
                        // thunk 0x182DA8800. Death/burning/cutscene thunks dispatch to gated overrides.
                        var methods = new List<MethodInfo>
                        {
                            Base("Update", 0x33DD660, 64, "FAAC19A88AA0198FF8B602FBEC9D67B0F741AF584BCF5FAA26919A4DC9FBCC96"),
                            Base("Initialize", 0x33DD6C0, 800, "4E6590E5C335016446E8E11AF1F420D9CE91C011B790CD33B351138CCBEF7B08", typeof(GolfCartController), typeof(int)),
                            Base("InitializeConnectedTransform", 0x33DD9E0, 128, "84869EC93929659AE21D3B683A8CF99F8285700BE9E73C155B75CB0B17DA1AF8"),
                            Base("SetupInputs", 0x33DDC60, 640, "4FA06FEFAC3FD0C6BF3E587E9591153FBB271CE6A19E8CAD25F1B75266EC5A56"),
                            Base("ClearInputs", 0x33DDEE0, 496, "AE6DA589AAD7E0D637C323B38EE677D61937512D584001024AAEEBD486BB9E7A"),
                            Base("OnDisconnectAction", 0x33DE0D0, 544, "D863110BCF408BC43978373F1BA9E4F1F0FF98D4745B5C38935F4C3A408582C3", typeof(InputAction.CallbackContext)),
                            Base("Disconnect", 0x33DE450, 912, "7FCE9075A8EA2FF906F701813FEA36ADCEC9AD32FC0E455DAD9264000191A4C0", typeof(GolfCartController), typeof(bool)),
                            Base("OnDestroy", 0x33DE7E0, 96, "AD2195E469C419D1DBFCFCB08367FCC26788BE42028A31059E171B7AB650CD27"),
                            Base("SetNetworkProxyConnectedTransform", 0x33DE840, 128, "B8992310387C85AFD46AD6BE80C9E14F0DA6CF279CBD9005D56879D2419519E6", typeof(Vector3), typeof(Quaternion)),
                            Passenger("LastUpdate", 0x33E11B0, 736, "E9B9B9BD73C73027DD02FD27D31CEE1C8D68BDD627D479A899046BF48E9B8495"),
                            Passenger("PostInitialize", 0x33E1490, 2144, "D5EEF93C1651A60232F64843FA572D0F5C08E945A1A79C12982F25C88F23DBA1", typeof(GolfCartController)),
                            Passenger("SetupInputs", 0x33E1CF0, 688, "028CDCF40C2066E4AA5A1299E42D6214C111155832B791710B96428E01EDBF4E"),
                            Passenger("ClearInputs", 0x33E1FA0, 336, "5A67BFD383D0FDBA76E08B842AE324B06DB406D70FC628CF2AB4694695105736"),
                            Passenger("DoZoomMapAction", 0x33E20F0, 128, "4835683C2E043796174F57E6BA4C10FDD1F63DEFA1BD792A4D36148397FB9162", typeof(InputAction.CallbackContext)),
                            Passenger("DisconnectPlayerHook", 0x33E2170, 2640, "DE6CD8C1A03CE7B515EEEBA7BD1FF93AA365A7DE5000282D212859E59C2153AA", typeof(GolfCartController), typeof(bool)),
                            Passenger("TriggerDisconnect", 0x33E2BC0, 96, "2B83FE9C125CBEE2CC67A76A793E7216BBE52D5E3DBEF12DDAF5339B4ECA6086", typeof(bool)),
                            Driver("UpdateInput", 0x33DE9B0, 784, "F064581288D039083FC724C034FFD708A60EA859790729C3BDFE7B028C1BB814"),
                            Driver("LateUpdate", 0x33DECC0, 1152, "A62B6EDE41AF4CCEFC3109B4D7AB9B65E6E3136CBA15A69CCAE91DCDDFE635A5"),
                            Driver("PostInitialize", 0x33DF140, 2240, "C7C791A4EAC8332ECEB4CC0307134141D3D25221AF7F14CA447CBA4B80A315F3", typeof(GolfCartController)),
                            Driver("SetupInputs", 0x33DFA00, 688, "F350234B3ACA3350D16F2550759DA629B116ACBA8E6858FF2DDD2D84D6ABABF1"),
                            Driver("DoZoomMapAction", 0x33DFCB0, 128, "6AA6448A4EC53CB858F0D2D042A88C687CCBC54C31482E7D1354B55EDF3094CE", typeof(InputAction.CallbackContext)),
                            Driver("ClearInputs", 0x33DFD30, 336, "E72ADD715A604D784C7EE3F4047CBD5BFB8D09216F41822468DFC3FB4312411D"),
                            Driver("DisconnectPlayerHook", 0x33DFE80, 2704, "26728A10CFF21B4CF60698C06F119635FB2B02916A0B72CEFB031F025D841DCD", typeof(GolfCartController), typeof(bool)),
                            Driver("TriggerDisconnect", 0x33E0910, 80, "7EAEF93B718F908F39BB0CFDEE908D999963AFE9AE04D8907BD5848B397F0249", typeof(bool)),
                            Driver("ConnectIk", 0x33E0960, 224, "3C9098F0C2D5EB959264750FD58327D978BAF4EEE2B5D1DA1DCB01F58F9E18B0", typeof(GolfCartController), typeof(float), typeof(IkTargetsController)),
                            Driver("DisconnectIk", 0x33E0A40, 240, "498B1C080B2337B6DB3B8E3F16F9ECE6E7066A0735244DB23DD0FC2F250543F8", typeof(GolfCartController)),
                            Driver("OnTurnPerformed", 0x33E0B30, 128, "667F4A5C3D6196A787280CC90AB9AA32C375D3BEA1AA53459B3883BCCF7251AF", typeof(InputAction.CallbackContext)),
                            Driver("OnReversePerformed", 0x33E0BB0, 112, "1AF9C05B0D3F6AA41A6B9D6451A57F5CAB73F971414DB26948B08F861F2538A0", typeof(InputAction.CallbackContext)),
                            Driver("OnThrottleAction", 0x33E0C20, 112, "716D6C07C3EC79511F5385D8D50936AF49C3D79B9D15AEF65BC1C75CF8FF33C8", typeof(InputAction.CallbackContext)),
                            Ghost("Update", 0x33D7880, 96, "166DDA1415D8D1D1AFB60ACFB6A24AE6881FE38C3FD9A489BC8E0F87E6337D9A"),
                            Ghost("Attached", 0x33D78E0, 1152, "E98246CC7DF7F4A7B2131AFF4F0298E7CCE7E6BDBA3E9BB635EF8FB06CC92351"),
                            Ghost("NonOwnerOnlyAttach", 0x33D7D60, 240, "E37A6C342F98664D424EDA3B5D523853093A96869B1C38FEFC493C316FE90523"),
                            Ghost("OnSteeringWheelRotationChanged", 0x33D7E50, 464, "5592D23002F03231D732F96220ABD39DC12C891EECC5B2FA0441E62BE6926181"),
                            Ghost("SetSteeringWheelRotation", 0x33D8020, 336, "57EE97FD7531D639EDC6DA3D27D2E2C070331E628C802BEA9C360A6685943C33", typeof(float)),
                            Ghost("UpdateTransforms", 0x33D8170, 1024, "F9B8737FC4F3F9A3CF5FE191771C1469CC62A256D8EC10AC5F385A4A120CC9EA", typeof(Transform), typeof(Il2CppSystem.Collections.Generic.List<Transform>)),
                            Ghost("SetDriverEntity", 0x33D8770, 320, "762D43A6CD8C0D03F2198927A79A475E4B77D2EDC19BDF0C74462D6CBEE9A558", typeof(BoltEntity)),
                            Ghost("UpdatePower", 0x33D88B0, 288, "232E9546DA69281EA258933949EBF5E650F542E325139A68D28E907C52093D49", typeof(float)),
                            Ghost("UpdateVelocity", 0x33D89D0, 304, "5747710FAD7E3E89B998F5E250D75183EFA42EDB420D92DAA8BA6B931C4DC3CA", typeof(Vector3)),
                            Q(typeof(Entity), "Render", 0x947B30, 80, "8AF6D857DA27C5BD0BD6CC5978CB5DEC95F1F19BBCBD42F7A9F68E6B93525B9F"),
                            Q(typeof(Entity), "Simulate", 0x948CA0, 2208, "C85DF49A3FFB90998C56A3B7265DAEA2866864A9C876BCE1C9F507F1FDC403B0")
                        };
                        var actorFirst = new List<MethodInfo>();
                        var actorSecond = new List<MethodInfo>();
                        var actorPairs = new List<MethodInfo>();
                        QualifyIndependentWriters(methods, actorFirst, actorSecond, actorPairs);
                        QualifyPlayerRigWriters(methods);
                        _thread = Environment.CurrentManagedThreadId;
                        harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-cart-occupant-writers");
                        Patch(methods, nameof(Before));
                        Patch(actorFirst, nameof(BeforeActorFirst));
                        Patch(actorSecond, nameof(BeforeActorSecond));
                        Patch(actorPairs, nameof(BeforeActorPair));
                        _frame = Time.frameCount;
                        _installed = true;

                        void Patch(List<MethodInfo> entries, string prefix)
                        {
                            foreach (var method in entries)
                                harmony.Patch(method,
                                    prefix: new HarmonyMethod(typeof(CartOccupantWriterHooks), prefix) { priority = HarmonyLib.Priority.Last },
                                    finalizer: new HarmonyMethod(typeof(CartOccupantWriterHooks), nameof(After)) { priority = HarmonyLib.Priority.Last });
                        }
                    }
                    catch (Exception error)
                    {
                        _failure = error.Message;
                        try { harmony?.UnpatchSelf(); } catch (Exception) { }
                        // Partial installation remains passive; no receiver can acquire a hold.
                    }
                }
                Require(_installed, _failure ?? "Occupant writer hooks are unavailable.");
            }

            private static MethodInfo Q(Type type, string name, int rva, int length, string hash, params Type[] parameters)
                => CartWriterHooks.Qualify(type, name, typeof(void), rva, length, hash, parameters);
            private static MethodInfo Base(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(PlayerGolfCartAction), name, rva, length, hash, parameters);
            private static MethodInfo Passenger(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(PlayerGolfCartPassengerAction), name, rva, length, hash, parameters);
            private static MethodInfo Driver(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(PlayerGolfCartDriverAction), name, rva, length, hash, parameters);
            private static MethodInfo Ghost(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(GolfCartGhostController), name, rva, length, hash, parameters);

            internal static void RequireReady()
            {
                Arm();
                Require(Environment.CurrentManagedThreadId == _thread && _depth == 0
                    && Volatile.Read(ref _wrongThread) == 0 && Time.frameCount != _frame,
                    "Acquire occupant holds outside native callbacks on a later game frame.");
            }

            private static CartOccupantWriterHold Find(IntPtr pointer)
            {
                foreach (var entry in Volatile.Read(ref _entries)) if (entry.Pointer == pointer) return entry.Hold;
                return null;
            }

            private static bool Before(Il2CppObjectBase __instance, MethodBase __originalMethod, bool __runOriginal, out bool __state)
                => BeforePointer(__instance?.Pointer ?? IntPtr.Zero, IntPtr.Zero, __originalMethod, __runOriginal, out __state);

            private static bool BeforeActorFirst(VailActor __0, MethodBase __originalMethod, bool __runOriginal, out bool __state)
                => BeforePointer(__0?.Pointer ?? IntPtr.Zero, IntPtr.Zero, __originalMethod, __runOriginal, out __state);

            private static bool BeforeActorSecond(GolfCartStimuli __instance, int __0, VailActor __1,
                MethodBase __originalMethod, bool __runOriginal, out bool __state)
            {
                // Also protect a held seat from being overwritten by an approaching actor.
                // No manager or unrelated stimulus is held globally.
                IntPtr reserved = IntPtr.Zero;
                if (_installed && __runOriginal && Environment.CurrentManagedThreadId == _thread
                    && Live(__instance) && __instance._seats != null && __0 >= 0 && __0 < __instance._seats.Length)
                    reserved = __instance._seats[__0]?._actor?.Pointer ?? IntPtr.Zero;
                return BeforePointer(__1?.Pointer ?? IntPtr.Zero, reserved, __originalMethod, __runOriginal, out __state);
            }

            private static bool BeforeActorPair(VailActor __instance, VailActor __0, MethodBase __originalMethod, bool __runOriginal, out bool __state)
                => BeforePointer(__instance?.Pointer ?? IntPtr.Zero, __0?.Pointer ?? IntPtr.Zero, __originalMethod, __runOriginal, out __state);

            private static bool BeforePointer(IntPtr pointer, IntPtr other, MethodBase method, bool runOriginal, out bool state)
            {
                state = false;
                if (!_installed || !runOriginal) return true;
                var hold = Find(pointer) ?? (other == IntPtr.Zero ? null : Find(other));
                if (Environment.CurrentManagedThreadId != _thread)
                {
                    Interlocked.Exchange(ref _wrongThread, 1);
                    if (hold != null) Interlocked.CompareExchange(ref hold.Conflict, "Held occupant writer left the game thread.", null);
                    return hold == null;
                }
                if (hold != null)
                {
                    string name = method.Name;
                    // Ordinary sampling/render ticks are suspended, not queued for replay. An
                    // input, disconnect, network callback or lifecycle mutation invalidates Ready.
                    if (!IsSuspendedTick(name))
                        Interlocked.CompareExchange(ref hold.Conflict, "Native occupant " + name + " arrived while held.", null);
                    if (name != "OnDestroy" && name != "OnDisable") return false;
                    Interlocked.CompareExchange(ref hold.TerminalConflict, "A retained occupant was disabled or destroyed.", null);
                }
                ++_depth; state = true;
                return true;
            }

            private static Exception After(Il2CppObjectBase __instance, bool __state, bool __runOriginal, Exception __exception)
            {
                if (__state)
                {
                    --_depth;
                    if (__runOriginal && __exception != null)
                    {
                        var hold = Find(__instance.Pointer);
                        if (hold != null) Interlocked.CompareExchange(ref hold.Conflict, "An occupant lifecycle callback failed.", null);
                    }
                }
                return __exception;
            }

            internal static void Acquire(CartOccupantWriterHold hold)
            {
                RequireReady();
                foreach (var pointer in hold.Pointers) Require(Find(pointer) == null, "An occupant already has a writer hold.");
                var old = Volatile.Read(ref _entries);
                var next = new Entry[old.Length + hold.Pointers.Length];
                Array.Copy(old, next, old.Length);
                for (int i = 0; i < hold.Pointers.Length; i++) next[old.Length + i] = new(hold.Pointers[i], hold);
                Volatile.Write(ref _entries, next);
            }

            internal static void RequireRegistered(CartOccupantWriterHold hold)
            {
                Require(_installed && Environment.CurrentManagedThreadId == _thread && _depth == 0,
                    "Occupant completion must run outside native callbacks on the selected thread.");
                foreach (var pointer in hold.Pointers) Require(ReferenceEquals(Find(pointer), hold), "Occupant writer barrier changed.");
            }

            internal static void RequireHeld(CartOccupantWriterHold hold)
            {
                RequireRegistered(hold);
                Require(hold.Conflict == null && hold.TerminalConflict == null, hold.TerminalConflict ?? hold.Conflict);
            }

            internal static void Release(CartOccupantWriterHold hold)
            {
                RequireRegistered(hold);
                var old = Volatile.Read(ref _entries);
                var next = new List<Entry>(old.Length);
                foreach (var entry in old) if (!ReferenceEquals(entry.Hold, hold)) next.Add(entry);
                Volatile.Write(ref _entries, next.ToArray());
            }
        }

        private sealed partial class CartOccupantWriterHold
        {
            internal readonly IntPtr[] Pointers;
            internal string Conflict;
            internal string TerminalConflict;
            private readonly CartOccupants _occupants;
            private readonly PlayerGolfCartAction _player;
            private readonly BoltEntity _ghost;
            private readonly GolfCartGhostController _controller;
            private readonly IntPtr _ghostBacking, _ghostState;
            private readonly ulong _ghostId;
            private readonly bool _ghostOwner, _playerEnabled, _controllerEnabled;
            private bool _held, _used;
            internal bool IsHeld => _held;

            internal CartOccupantWriterHold(CartOccupants occupants)
            {
                CartOccupantWriterHooks.RequireReady();
                _occupants = occupants;
                var pointers = new HashSet<IntPtr>();
                foreach (var seat in occupants.RetainedOccupants())
                {
                    if (seat.LocalPlayerAction is not null)
                    {
                        Require(_player is null, "Only one exact local cart action may be held.");
                        _player = seat.LocalPlayerAction; _playerEnabled = _player.enabled;
                        Require(Live(_player) && !_player.IsInvoking(), "Local cart action has a pending invoke.");
                        pointers.Add(_player.Pointer);
                    }
                    if (seat.DriverGhost is not null)
                    {
                        _ghost = seat.DriverGhost;
                        _controller = _ghost.GetComponent<GolfCartGhostController>();
                        RequireExactSaveType<GolfCartGhostController>(_controller);
                        Require(Live(_controller) && !_controller.IsInvoking(), "Ghost writer lifecycle is unavailable.");
                        _ghostId = _ghost.networkId.PackedValue; _ghostBacking = _ghost._entity.Pointer;
                        _ghostState = _ghost._entity.Serializer.Pointer; _ghostOwner = _ghost.isOwner;
                        _controllerEnabled = _controller.enabled;
                        foreach (var body in _ghost.GetComponentsInChildren<Rigidbody>(true))
                            Require(body.isKinematic, "A retained ghost has independent dynamic physics.");
                        pointers.Add(_controller.Pointer); pointers.Add(_ghostBacking);
                    }
                }
                CaptureIndependentWriters(occupants, pointers);
                CapturePlayerRigWriters(occupants, pointers);
                Pointers = new IntPtr[pointers.Count]; pointers.CopyTo(Pointers);
                RequireCurrent();
            }

            private void RequireCurrent()
            {
                if (_player is not null)
                    Require(Live(_player) && _player.enabled == _playerEnabled && !_player.IsInvoking(), "Retained action lifecycle changed.");
                if (_ghost is not null)
                    Require(Live(_ghost) && _ghost.isAttached && _ghost.networkId.PackedValue == _ghostId
                        && _ghost._entity?.Pointer == _ghostBacking && _ghost._entity.Serializer?.Pointer == _ghostState
                        && _ghost.isOwner == _ghostOwner && Live(_controller) && _controller.enabled == _controllerEnabled
                        && SameEntity(_controller.entity, _ghost) && !_controller.IsInvoking(), "Retained ghost lifetime/authority changed.");
                RequireIndependentWriters();
                RequirePlayerRig();
            }

            internal void Acquire()
            {
                Require(!_used, "Occupant hold is single-use.");
                RequireCurrent();
                CartOccupantWriterHooks.Acquire(this); _held = true; _used = true;
                // Publish all gates before the first native write. On partial failure the
                // caller still owns this object and must cancel or keep it quarantined.
                PauseActorAnimation();
                PausePlayerRig();
                RequireHeld();
            }

            internal void RequireHeld()
            {
                Require(_held, "Occupant writer hold is absent.");
                CartOccupantWriterHooks.RequireHeld(this); RequireCurrent();
                foreach (var animation in _animations) animation.RequirePaused();
                RequirePlayerRigHeld();
            }

            internal void Release()
            {
                RequireHeld();
                Require(_occupants.Target != null && _occupants.MatchesRestoredReferences(_occupants.Target),
                    "Independent writers require the exact repaired replacement before release.");
                ResumeAndReleaseWriters();
            }

            internal void Cancel()
            {
                if (!_held) return;
                CartOccupantWriterHooks.RequireRegistered(this);
                Require(TerminalConflict == null, TerminalConflict);
                Require(_occupants.Source.Matches() && !_occupants.IsParked
                    && _occupants.MatchesRestoredReferences(_occupants.Source),
                    "Restore the intact original cart and parked bindings before cancelling occupant holds.");
                RequireCurrent();
                // A suppressed input/action invalidates Ready, not cancellation of an intact
                // original. Never replay it, clear subscriptions, or abandon another hold.
                ResumeAndReleaseWriters();
            }

            private void ResumeAndReleaseWriters()
            {
                RestoreActorAnimation();
                RestorePlayerRig();
                try { CartOccupantWriterHooks.Release(this); _held = false; }
                catch
                {
                    // A failed release still owns the published gates and every resumed clock.
                    try { ReholdPlayerRig(); }
                    catch (Exception) { TerminalConflict ??= "Original seated player could not be reheld after writer release failed."; }
                    foreach (var animation in _animations)
                    {
                        try { animation.Pause(); }
                        catch (Exception) { TerminalConflict ??= "Occupied animation could not be reheld after writer release failed."; }
                    }
                    throw;
                }
            }
        }
    }
}
