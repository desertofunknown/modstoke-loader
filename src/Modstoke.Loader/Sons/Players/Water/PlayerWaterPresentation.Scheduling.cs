// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerWaterPresentation
    {
        private static bool _sourceHooksAttempted, _sourceHooksReady;
        private static int _sourceHookThread;
        private static PlayerWaterPresentation _scheduledSource;
        private PlayerClientMovement _sourceMovement;
        private FirstPersonCharacter _sourceCharacter;
        private PlayerAnimatorControl _sourceAnimation;
        private camFollowHead _sourceCamera;
        private int _sourceCharacterId, _sourceAnimationId, _sourceCameraId, _consumerDepth;

        internal bool IsSourceSchedulePrepared => !_bridge.IsServer && !IsDisposed
            && FailureReason == null && _sourceHooksReady && _sourceMovement != null
            && !_sourceMovement.IsDisposed && ReferenceEquals(_scheduledSource, this);

        internal bool TryBindSourceMovement(PlayerBridge bridge,
            PlayerClientMovement movement, FirstPersonCharacter character,
            PlayerAnimatorControl animation, Rigidbody body, GameObject root, out string detail)
        {
            detail = "Bind water to the exact prepared source movement rig before activating its bridge.";
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || IsDisposed || FailureReason != null || _active || _bridge.IsServer
                || !ReferenceEquals(bridge, _bridge) || _bridge.IsGameplayActive
                || _sourceMovement != null || _scheduledSource != null || movement == null
                || !movement.IsPrepared) return false;
            try
            {
                RequireCurrent();
                if (!SameSourceObject(character, LocalPlayer.FpCharacter)
                    || !SameSourceObject(animation, LocalPlayer.AnimControl)
                    || !SameSourceObject(body, LocalPlayer.Rigidbody)
                    || !SameSourceObject(root, LocalPlayer.GameObject)
                    || !Live(LocalPlayer.CamFollowHead)) return false;
                if (!TryInstallSourceHooks(out detail)) return false;
                _sourceCharacter = character; _sourceCharacterId = character.GetInstanceID();
                _sourceAnimation = animation; _sourceAnimationId = animation.GetInstanceID();
                _sourceCamera = LocalPlayer.CamFollowHead; _sourceCameraId = _sourceCamera.GetInstanceID();
                _sourceMovement = movement;
                _scheduledSource = this;
                detail = "Exact original water consumers bound while the rig is held; no callbacks, physics or input were resumed.";
                return true;
            }
            catch (Exception error)
            { detail = "Source water scheduling preparation refused: " + error.Message; return false; }
        }

        private static bool TryInstallSourceHooks(out string detail)
        {
            detail = "Recognized original water-consumer hooks are unavailable.";
            if (_sourceHooksAttempted)
                return _sourceHooksReady && Environment.CurrentManagedThreadId == _sourceHookThread;
            _sourceHooksAttempted = true;
            if (HarmonySupport.CreateState()?.SafeForHarmonyPatches != true
                || !GameBuild.IsRecognized) return false;
            HarmonyLib.Harmony harmony = null;
            try
            {
                // Existing native lifecycle and waterline evidence qualifies these bodies.
                // Direct PAC water reads also cover calls outside its Unity Update methods.
                var methods = new[]
                {
                    Q(typeof(FirstPersonCharacter), "Update", 0x2F7E640),
                    Q(typeof(FirstPersonCharacter), "FixedUpdate", 0x2F7FF20),
                    Q(typeof(PlayerAnimatorControl), "Update", 0x35A2D90),
                    Q(typeof(PlayerAnimatorControl), "FixedUpdate", 0x35A44D0),
                    Q(typeof(PlayerAnimatorControl), "LateUpdate", 0x35A4CF0),
                    Q(typeof(PlayerAnimatorControl), "OnAnimatorMove", 0x35A5B50),
                    Q(typeof(PlayerAnimatorControl), "UpdateInWaterControl", 0x35B9DB0),
                    Q(typeof(PlayerAnimatorControl), "GetWaterLevel", 0x35BC120,
                        typeof(float).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType()),
                    Q(typeof(camFollowHead), "UpdateCamPosition", 0x3595FD0),
                    Q(typeof(camFollowHead), "DoCamFollow", 0x35962A0),
                    Q(typeof(SimpleMouseRotator), "Update", 0x2F911A0)
                };
                _sourceHookThread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-source-water-consumers");
                foreach (var method in methods)
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(PlayerWaterPresentation), nameof(BeforeSourceConsumer))
                            { priority = Priority.First + 100 },
                        finalizer: new HarmonyMethod(typeof(PlayerWaterPresentation), nameof(FinishSourceConsumer)));
                InstallNativeSwimming(harmony);
                _sourceHooksReady = true;
                return true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                detail = "Source water-consumer hooks refused: " + error.Message;
                return false;
            }

            static System.Reflection.MethodInfo Q(Type type, string name, int rva, params Type[] arguments)
                => PlayerLifecycle.QualifyNative(type, name, rva, false, typeof(void), arguments);
        }

        private void RequireSourceScheduleCurrent()
        {
            if (!IsSourceSchedulePrepared || Environment.CurrentManagedThreadId != _sourceHookThread
                || !_sourceMovement.WaterBindingCurrent(this)
                || !SourceIdentity(_sourceCharacter, _sourceCharacterId, LocalPlayer.FpCharacter)
                || !SourceIdentity(_sourceAnimation, _sourceAnimationId, LocalPlayer.AnimControl)
                || !SourceIdentity(_sourceCamera, _sourceCameraId, LocalPlayer.CamFollowHead))
                throw new InvalidOperationException("The exact original source water-consumer schedule retired.");
        }

        private static void BeforeSourceConsumer(Component __instance, bool __runOriginal,
            out PlayerWaterPresentation __state)
        {
            __state = null;
            var source = _scheduledSource;
            if (!__runOriginal || !_sourceHooksReady || source == null || source.IsDisposed
                || Environment.CurrentManagedThreadId != _sourceHookThread) return;
            try
            {
                if (!source.OwnsSourceConsumer(__instance)) return;
                // Keep one absolute observation through nested native consumers. Neither
                // a prefix nor its finalizer skips or replays the original callback.
                if (source._consumerDepth == 0 && source.IsActive)
                {
                    if (source._sourceMovement.HasSourceInputHold) source._sourceMovement.PumpSourceInputHold(out _);
                    else source.TryPump(out _);
                }
                source._consumerDepth++;
                __state = source;
            }
            catch (Exception error) { source.Fail("Source water preconsumer scheduling failed: " + error.Message); }
        }

        private static void FinishSourceConsumer(PlayerWaterPresentation __state)
        {
            if (__state != null) __state._consumerDepth--;
        }

        private bool OwnsSourceConsumer(Component component)
            => SourceIdentity(_sourceCharacter, _sourceCharacterId, component)
                || SourceIdentity(_sourceAnimation, _sourceAnimationId, component)
                || SourceIdentity(_sourceCamera, _sourceCameraId, component)
                || (_sourceMovement != null && _sourceMovement.OwnsWaterViewConsumer(component));

        private static bool SourceIdentity(Component retained, int id, Component current)
            => SameSourceObject(retained, current) && retained.GetInstanceID() == id;

        private static bool SameSourceObject(UnityEngine.Object retained, UnityEngine.Object current)
            => Live(retained) && Live(current) && retained.Pointer == current.Pointer;

        private void ReleaseSourceSchedule()
        {
            if (ReferenceEquals(_scheduledSource, this)) _scheduledSource = null;
            _sourceMovement = null;
        }
    }
}
