// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader
{
    /// <summary>One full-screen themed screen for one owner, matching the mods menu. Main thread only.</summary>
    public static class ScreenHost
    {
        /// <summary>The UI surface ID is this prefix and the owner ID.</summary>
        public const string SurfacePrefix = "openacai.ui.screen:";
        private const int PendingFrameLimit = 10;
        private const float DefaultWaitSeconds = 120f;
        private const float MaxWaitSeconds = 1800f;
        private static int _ownerThread;
        private static bool _initialized;
        private static ScreenHostSession _session;
        private static ReadyWaiter _waiter;
        private static readonly UiEventSystem EventSystemLease = new UiEventSystem("Modstoke Screen Host Event System");
        private static volatile ScreenHostSnapshot _snapshot =
            ScreenHostSnapshot.Create(null, ScreenHostState.Closed, ScreenHostOrigin.OtherMod, "plain", "No screen is open.");

        public static event Action<ScreenHostSnapshot> Changed;

        /// <summary>True while a screen is open or waiting for input to be handed over.</summary>
        public static bool IsOpen => _session != null;

        public static ScreenHostSnapshot Snapshot() => _snapshot;

        internal static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            _ownerThread = Environment.CurrentManagedThreadId;
            GameRuntime.WorldChanged += OnWorldChanged;
            GameRuntime.SceneChanged += OnSceneChanged;
        }

        /// <summary>
        /// Returns a session that may be Pending while the mods menu or another lease hands off (a few frames at most).
        /// Fails at once if another owner's screen is open, or if input cannot be had in time. The same owner opening
        /// again replaces its own screen.
        /// </summary>
        public static bool TryOpen(ScreenHostRequest request, out ScreenHostSession session, out string status)
        {
            session = null;
            if (!OnOwnerThread(out status))
                return false;
            if (request == null || string.IsNullOrWhiteSpace(request.OwnerId))
            {
                status = "A screen needs an owner ID.";
                return false;
            }
            if (GameBuild.IsDedicatedServer)
            {
                status = "A dedicated server has no screen to show.";
                return false;
            }

            var ownerId = request.OwnerId.Trim();
            var current = _session;
            if (current != null && !string.Equals(current.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                status = "A screen is already open for " + current.OwnerId + ".";
                return false;
            }
            if (!request.AllowTitleScene && !GameRuntime.IsInWorld)
            {
                status = "This screen is available in the world only.";
                return false;
            }
            if (GameRuntime.IsLoadingScene)
            {
                status = "The game is loading.";
                return false;
            }
            // The menu holds no lease on the frame it is shown, so the lease check alone can miss it.
            if (request.Origin != ScreenHostOrigin.ModsMenu && MenuController.IsVisible)
            {
                status = "The mods menu is open.";
                return false;
            }

            var handedOff = current != null && current.HandedOff;
            var resumePage = current?.ResumePage;
            if (current != null)
            {
                Close(current, ScreenHostCloseReason.Replaced);
                // The replaced screen's Closed handler may have opened another owner's screen.
                if (_session != null)
                {
                    status = "A screen is already open for " + _session.OwnerId + ".";
                    return false;
                }
            }

            // In normal play the game has no active event system; the screen then borrows SUI's until it closes.
            if (EventSystemLease.Acquire() == null)
            {
                status = "The game's UI input system is not ready.";
                return false;
            }

            var created = new ScreenHostSession(request.Copy(), ownerId);
            try
            {
                ScreenHostView.Prepare(created);
            }
            catch (Exception error)
            {
                EventSystemLease.Release();
                ScreenHostView.Release();
                status = "The screen could not be created: " + error.Message;
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host for " + ownerId + " could not be created: " + error);
                return false;
            }

            created.Tier = ScreenHostView.Tier;
            var scene = SceneManager.GetActiveScene();
            created.SceneHandle = scene.handle;
            created.SceneName = scene.name;
            // The menu is already hidden for the screen this replaces, so its way back carries over.
            created.HandedOff = handedOff;
            created.ResumePage = resumePage;
            _session = created;
            session = created;
            if (created.Origin == ScreenHostOrigin.ModsMenu && MenuController.IsVisible)
            {
                // The command that opened this is still inside the menu, so the menu hides on the next tick.
                created.HandoffQueued = true;
                Publish(created, "Waiting for the mods menu to hand over input.");
                status = "pending";
                return true;
            }
            if (created.Origin == ScreenHostOrigin.Tablet)
            {
                // The tablet closes itself while a Loader surface is visible, which releases its input.
                ScreenHostInput.MarkSurface(created, "pending");
            }
            if (ScreenHostInput.TryAcquire(created))
            {
                Activate(created);
                status = "";
                return true;
            }
            if (created.Origin == ScreenHostOrigin.Tablet)
            {
                Publish(created, "Waiting for the tablet to hand over input.");
                status = "pending";
                return true;
            }

            _session = null;
            session = null;
            created.State = ScreenHostState.Closed;
            ScreenHostView.Hide();
            EventSystemLease.Release();
            status = "Input is held by another window.";
            return false;
        }

        /// <summary>
        /// Waits for: in world, not loading, pause menu closed, no InputBindings focus, live local player and
        /// <paramref name="ready"/>. Then calls TryOpen once and reports the session or the reason to
        /// <paramref name="completed"/>. For first-join flows; the "first join" decision stays with the caller.
        /// With <see cref="ScreenHostRequest.AllowTitleScene"/> the world and player are not waited for.
        /// </summary>
        public static bool TryOpenWhenReady(ScreenHostRequest request, Func<bool> ready, float timeoutSeconds,
            Action<ScreenHostSession, string> completed, out string status)
        {
            if (!OnOwnerThread(out status))
                return false;
            if (request == null || string.IsNullOrWhiteSpace(request.OwnerId))
            {
                status = "A screen needs an owner ID.";
                return false;
            }
            if (GameBuild.IsDedicatedServer)
            {
                status = "A dedicated server has no screen to show.";
                return false;
            }
            if (_waiter != null)
            {
                status = "A screen is already waiting to open for " + _waiter.Request.OwnerId.Trim() + ".";
                return false;
            }

            var timeout = float.IsFinite(timeoutSeconds) && timeoutSeconds > 0f ? Mathf.Min(timeoutSeconds, MaxWaitSeconds) : DefaultWaitSeconds;
            _waiter = new ReadyWaiter(request.Copy(), ready, completed, Time.unscaledTime + timeout);
            status = "waiting";
            return true;
        }

        internal static void Tick()
        {
            if (_ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread)
                return;
            TickWaiter();
            var session = _session;
            if (session == null)
                return;
            try
            {
                if (session.DisposeRequested)
                    Close(session, ScreenHostCloseReason.Requested);
                else if (!StillValid(session, out var reason))
                    Close(session, reason);
                else if (session.State == ScreenHostState.Pending)
                    TickPending(session);
                else
                    TickOpen(session);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host for " + session.OwnerId + " failed: " + error);
                Close(session, ScreenHostCloseReason.Failed);
            }
        }

        internal static void Shutdown()
        {
            var waiter = _waiter;
            _waiter = null;
            if (waiter != null)
                Complete(waiter, null, "The Loader is shutting down.");
            if (_session != null)
                Close(_session, ScreenHostCloseReason.SceneUnloaded);
        }

        internal static bool IsCurrent(ScreenHostSession session)
        {
            return session != null && ReferenceEquals(_session, session) && !session.Closing
                && Environment.CurrentManagedThreadId == _ownerThread;
        }

        internal static bool IsUsable(ScreenHostSession session) => IsCurrent(session);

        internal static void AttachStage(ScreenHostSession session, PreviewStage stage, bool disposeOnClose)
        {
            var previous = session.Stage;
            session.Stage = null;
            previous?.Detach(previous.DisposeOnClose && !ReferenceEquals(previous.Target, stage));
            if (stage != null && stage.IsAlive)
            {
                session.Stage = ScreenHostView.CreateStageView(stage, disposeOnClose);
                session.Stage.SetVisible(session.State == ScreenHostState.Open);
            }

            ScreenHostView.RefreshHints(session);
        }

        internal static void RequestClose(ScreenHostSession session)
        {
            if (Allows(session.CanClose, session))
                Close(session, ScreenHostCloseReason.Requested);
        }

        internal static void Dispose(ScreenHostSession session)
        {
            if (session == null || session.State == ScreenHostState.Closed || session.Closing)
                return;
            if (Environment.CurrentManagedThreadId != _ownerThread)
            {
                if (session.DisposeRequested)
                    return;
                session.DisposeRequested = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host for " + session.OwnerId
                    + " was disposed off the game thread; it closes on the next Loader tick.");
                return;
            }
            if (ReferenceEquals(_session, session))
                Close(session, ScreenHostCloseReason.Requested);
        }

        /// <summary>Back: leave a text field, then the caller's back stack, then the close guard, then close.</summary>
        internal static void Back(ScreenHostSession session)
        {
            if (ScreenHostInput.LeaveTextField())
                return;
            if (Handles(session.TryBack, session))
                return;
            if (Allows(session.CanClose, session) && IsCurrent(session))
                Close(session, ScreenHostCloseReason.Back);
        }

        private static void TickPending(ScreenHostSession session)
        {
            if (session.HandoffQueued)
            {
                session.HandoffQueued = false;
                session.HandedOff = MenuController.HandOffToScreen(out var page);
                session.ResumePage = page;
                // The menu gives its input back on its next update.
                return;
            }
            if (ScreenHostInput.TryAcquire(session))
            {
                Activate(session);
                return;
            }
            if (++session.PendingFrames >= PendingFrameLimit)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host for " + session.OwnerId + " could not get input within "
                    + PendingFrameLimit + " frames.");
                Close(session, ScreenHostCloseReason.InputLost);
            }
        }

        private static void TickOpen(ScreenHostSession session)
        {
            if (!ScreenHostInput.Hold(session))
            {
                Close(session, ScreenHostCloseReason.InputLost);
                return;
            }
            ScreenHostView.Tick();
            var stage = session.Stage;
            if (stage != null && !stage.Tick())
            {
                session.Stage = null;
                ScreenHostView.RefreshHints(session);
            }
            if (IsCurrent(session) && ScreenHostInput.BackPressed())
                Back(session);
        }

        private static void Activate(ScreenHostSession session)
        {
            session.State = ScreenHostState.Open;
            session.OpenedFrame = Time.frameCount;
            ScreenHostInput.Enter(session);
            ScreenHostView.Show(session);
            session.Stage?.SetVisible(true);
            Publish(session, "Screen is open.");
            RuntimeEntryPoint.LogSource?.LogInfo("Screen host opened for " + session.OwnerId + " from " + session.Origin
                + " · theme " + session.Tier + ".");
            session.RaiseOpened();
        }

        private static void Close(ScreenHostSession session, ScreenHostCloseReason reason)
        {
            if (session == null || session.Closing || session.State == ScreenHostState.Closed)
                return;
            session.Closing = true;
            var wasOpen = session.State == ScreenHostState.Open;
            string failure = null;
            void Step(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error.Message; }
            }

            // A slider nudged by key or wheel has called live but not yet commit.
            if (wasOpen)
                Step(() => ScreenHostView.Panel?.FlushPending());
            try
            {
                session.Closed?.Invoke(reason);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host Closed handler for " + session.OwnerId + " failed: " + error.Message);
            }

            var stage = session.Stage;
            session.Stage = null;
            if (stage != null)
                Step(() => stage.Detach(stage.DisposeOnClose));
            Step(ScreenHostView.Hide);
            Step(() => ScreenHostInput.Exit(session, wasOpen, reason));
            Step(EventSystemLease.Release);
            session.State = ScreenHostState.Closed;
            if (ReferenceEquals(_session, session))
                _session = null;
            Publish(session, "Closed: " + reason + ".");
            if (session.HandedOff && session.ResumeModsMenu && reason != ScreenHostCloseReason.Replaced
                && reason != ScreenHostCloseReason.SceneUnloaded && reason != ScreenHostCloseReason.WorldExited)
                Step(() => MenuController.ResumeAfterScreen(session.ResumePage));

            if (failure == null)
                RuntimeEntryPoint.LogSource?.LogInfo("Screen host for " + session.OwnerId + " closed (" + reason + ").");
            else
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host for " + session.OwnerId + " closed (" + reason
                    + ") with cleanup errors: " + failure);
        }

        private static bool StillValid(ScreenHostSession session, out ScreenHostCloseReason reason)
        {
            reason = ScreenHostCloseReason.Requested;
            if (!session.AllowTitleScene && !GameRuntime.IsInWorld)
            {
                reason = ScreenHostCloseReason.WorldExited;
                return false;
            }
            if (SceneManager.GetActiveScene().handle != session.SceneHandle || !ScreenHostView.IsAlive)
            {
                reason = ScreenHostCloseReason.SceneUnloaded;
                return false;
            }
            var system = EventSystem.current;
            if (system == null || !system.isActiveAndEnabled)
            {
                reason = ScreenHostCloseReason.InputLost;
                return false;
            }
            return true;
        }

        private static void TickWaiter()
        {
            var waiter = _waiter;
            if (waiter == null)
                return;
            string failure = null;
            var waiting = !IsReady(waiter, ref failure, out var reason);
            if (failure == null && waiting)
            {
                if (Time.unscaledTime < waiter.Deadline)
                    return;
                failure = "Timed out waiting to open: " + reason + ".";
            }

            _waiter = null;
            ScreenHostSession session = null;
            var status = failure;
            if (failure == null)
                TryOpen(waiter.Request, out session, out status);
            Complete(waiter, session, status);
        }

        private static bool IsReady(ReadyWaiter waiter, ref string failure, out string reason)
        {
            var request = waiter.Request;
            var state = GameRuntime.State;
            waiter.SawWorld |= state.IsInWorld;
            if (!request.AllowTitleScene && waiter.SawWorld && !state.IsInWorld)
            {
                failure = "The world was exited before the screen could open.";
                reason = "world exited";
                return false;
            }

            reason = !request.AllowTitleScene && !state.IsInWorld ? "the world is not loaded"
                : GameRuntime.IsLoadingScene ? "the game is loading"
                : state.PauseMenuOpen ? "the pause menu is open"
                : IsOpen ? "another screen is open"
                : MenuController.IsVisible ? "the mods menu is open"
                : InputBindings.HasFocus ? "another window holds input"
                : !request.AllowTitleScene && !LocalPlayerLive() ? "the local player is not ready"
                : null;
            if (reason != null)
                return false;
            try
            {
                if (waiter.Ready != null && !waiter.Ready())
                {
                    reason = "the caller is not ready";
                    return false;
                }
            }
            catch (Exception error)
            {
                failure = "The ready check failed: " + error.Message;
                reason = "ready check failed";
                return false;
            }
            return true;
        }

        private static bool LocalPlayerLive()
        {
            try
            {
                var player = LocalPlayer.GameObject;
                return LocalPlayer.IsInWorld && player != null && player;
            }
            catch
            {
                return false;
            }
        }

        private static void Complete(ReadyWaiter waiter, ScreenHostSession session, string status)
        {
            try
            {
                waiter.Completed?.Invoke(session, status);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host ready handler for " + waiter.Request.OwnerId + " failed: " + error.Message);
            }
        }

        private static bool Handles(Func<bool> callback, ScreenHostSession session)
        {
            try
            {
                return callback != null && callback();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host TryBack handler for " + session.OwnerId + " failed: " + error.Message);
                return false;
            }
        }

        // A guard that throws would otherwise trap the player in the screen.
        private static bool Allows(Func<bool> callback, ScreenHostSession session)
        {
            try
            {
                return callback == null || callback();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Screen host CanClose handler for " + session.OwnerId + " failed: " + error.Message);
                return true;
            }
        }

        private static void Publish(ScreenHostSession session, string detail)
        {
            var snapshot = ScreenHostSnapshot.Create(session.OwnerId, session.State, session.Origin, session.Tier, detail);
            _snapshot = snapshot;
            var handlers = Changed;
            if (handlers == null)
                return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(snapshot);
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Screen host Changed subscriber failed: " + error.Message);
                }
            }
        }

        private static bool OnOwnerThread(out string status)
        {
            if (_ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread)
            {
                status = "The screen host runs on the game thread only.";
                return false;
            }
            status = "";
            return true;
        }

        private static void OnWorldChanged(WorldEvent world)
        {
            var session = _session;
            if (session != null && !session.AllowTitleScene && !world.IsInWorld
                && Environment.CurrentManagedThreadId == _ownerThread)
                Close(session, ScreenHostCloseReason.WorldExited);
        }

        private static void OnSceneChanged(Modstoke.Sons.SceneEvent scene)
        {
            var session = _session;
            if (session != null && string.Equals(scene.Phase, "Unloaded", StringComparison.OrdinalIgnoreCase)
                && string.Equals(scene.SceneName, session.SceneName, StringComparison.OrdinalIgnoreCase)
                && Environment.CurrentManagedThreadId == _ownerThread)
                Close(session, ScreenHostCloseReason.SceneUnloaded);
        }

        private sealed class ReadyWaiter
        {
            internal ReadyWaiter(ScreenHostRequest request, Func<bool> ready, Action<ScreenHostSession, string> completed, float deadline)
            {
                Request = request;
                Ready = ready;
                Completed = completed;
                Deadline = deadline;
            }

            internal ScreenHostRequest Request { get; }
            internal Func<bool> Ready { get; }
            internal Action<ScreenHostSession, string> Completed { get; }
            internal float Deadline { get; }
            internal bool SawWorld { get; set; }
        }
    }
}
