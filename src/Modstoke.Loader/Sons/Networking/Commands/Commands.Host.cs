// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using TheForest.UI.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class Commands
    {
        private static readonly Dictionary<string, HostRegistration> HostRegistrations = new(StringComparer.OrdinalIgnoreCase);
        private static int _hostInstallationAttempted;
        private static int _hostOwnerThreadId;
        private static int _hostHookInstalled;

        /// <summary>Local chat hook registration, not evidence that a host command ran or its reply was visible.</summary>
        public static bool HostHooksInstalled => Volatile.Read(ref _hostHookInstalled) != 0;

        /// <summary>
        /// Registers an explicit listen-host handler, separate from remote session commands. Earlier chat-input
        /// prefixes may suppress it. Authorization is mandatory; remote Receiving and Completed events do not
        /// apply to this local input. Native input replies use the current chat box and plain-text GameChat limits.
        /// ClientCommands submissions receive a private bounded inline reply. Neither route sends network traffic.
        /// TrackReply is unsupported and suppresses the reply.
        /// Dispose removes this registration only; a captured callback may still finish.
        /// </summary>
        public static IDisposable RegisterHost(string name, Func<HostCommandContext, bool> authorize,
            Func<HostCommandContext, CommandResult> execute)
        {
            ValidateName(name);
            ArgumentNullException.ThrowIfNull(authorize);
            ArgumentNullException.ThrowIfNull(execute);
            CheckLegacyName(name);
            var registration = new HostRegistration(name, authorize, execute);
            lock (SyncRoot)
                if (!HostRegistrations.TryAdd(name, registration))
                    throw new InvalidOperationException("That host command name is already registered.");
            return registration;
        }

        internal static void InstallHostHarmonyPatch(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _hostInstallationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                addStartupWarning?.Invoke("Listen-host command input hook was not installed: "
                    + (support?.Detail ?? "Harmony support information is unavailable."));
                return;
            }
            _hostOwnerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                var target = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.SendLine), new[] { typeof(string) });
                if (target == null)
                    throw new MissingMethodException(typeof(ChatBox).FullName, "SendLine(string)");
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-host-commands").Patch(target,
                    prefix: new HarmonyMethod(typeof(Commands), nameof(BeforeHostChat)) { priority = Priority.Last });
                Volatile.Write(ref _hostHookInstalled, 1);
            }
            catch (Exception error)
            {
                addStartupWarning?.Invoke("Listen-host command input hook failed; native patch state is uncertain: " + error);
            }
        }

        private static bool BeforeHostChat(ChatBox __instance, string __0, bool __runOriginal)
        {
            if (!__runOriginal || !HostHooksInstalled)
                return true;
            var end = FindNameEnd(__0);
            if (end < 0)
                return true;
            HostRegistration registration;
            lock (SyncRoot)
                if (!HostRegistrations.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(__0.AsSpan(1, end - 1), out registration))
                    return true;

            // Never call Unity or Steam from an unexpected thread. Recognized input stays local on failure.
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _hostOwnerThreadId))
                return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer
                    || !GameServerManager.HasDedicatedState || GameServerManager.IsDedicatedServer)
                    return true;
                if (!IsCurrentHostChatBox(__instance) || !LocalHost.TryGetPlayer(out var player))
                    return false;

                CommandResult result;
                try
                {
                    result = ExecuteHost(__0, end, registration, player);
                }
                catch (Exception error)
                {
                    ReportFailure(error);
                    result = new CommandResult(CommandStatus.Failed, "The command could not be completed.");
                }

                if (!result.TrackReply && ServerChat.IsPlainTextValid(result.Reply)
                    && player.IsCurrent && IsCurrentHostChatBox(__instance))
                    __instance.AddLine(default, result.Reply, true);
            }
            catch (Exception error) { ReportFailure(error); }
            // A recognized host command must never be broadcast, including denied or failed attempts.
            return false;
        }

        private static CommandResult ExecuteHost(string message, int nameEnd,
            HostRegistration registration, HostPlayer player, WorldRecordContext world = null)
        {
            if (!IsInputValid(message))
                return new CommandResult(CommandStatus.InvalidArguments, "Command input is invalid.");
            if (!player.IsCurrent || (world != null && !world.IsCurrent))
                return Unavailable();
            var arguments = nameEnd == message.Length ? string.Empty : message.Substring(nameEnd + 1);
            var context = new HostCommandContext(registration.Name, arguments, player, DateTimeOffset.UtcNow);
            if (!registration.Authorize(context))
                return new CommandResult(CommandStatus.Denied, "You are not allowed to use this command.");
            if (!player.IsCurrent || (world != null && !world.IsCurrent))
                return Unavailable();
            return registration.Execute(context)
                ?? new CommandResult(CommandStatus.Failed, "The command could not be completed.");
        }

        // Uses the same explicit host authorization as native input, but never writes to the chat box.
        internal static ClientCommandOutcome DispatchHostClientCommand(string message,
            HostPlayer player, WorldRecordContext world,
            out string reply, out bool replyTruncated)
        {
            reply = null;
            replyTruncated = false;
            if (!HostHooksInstalled || Environment.CurrentManagedThreadId != Volatile.Read(ref _hostOwnerThreadId))
                return ClientCommandOutcome.Unavailable;
            if (!NoticeProtocol.IsCommandValid(message))
                return ClientCommandOutcome.InvalidArguments;
            try
            {
                if (player == null || world == null || !player.IsCurrent || !world.IsCurrent)
                    return ClientCommandOutcome.Unavailable;
                var end = FindNameEnd(message);
                HostRegistration registration;
                lock (SyncRoot)
                    if (!HostRegistrations.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(message.AsSpan(1, end - 1), out registration))
                        return ClientCommandOutcome.NotFound;
                var result = ExecuteHost(message, end, registration, player, world);
                // Tracked delivery is unavailable locally, just as it is for native host input.
                if (!result.TrackReply)
                    reply = NoticeProtocol.LimitCommandReply(result.Reply, out replyTruncated);
                return result.Status switch
                {
                    CommandStatus.Succeeded => ClientCommandOutcome.Succeeded,
                    CommandStatus.InvalidArguments => ClientCommandOutcome.InvalidArguments,
                    CommandStatus.Denied => ClientCommandOutcome.Denied,
                    CommandStatus.Unavailable => ClientCommandOutcome.Unavailable,
                    _ => ClientCommandOutcome.Failed
                };
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ClientCommandOutcome.Failed;
            }
        }

        private static bool IsCurrentHostChatBox(ChatBox chat)
        {
            if (chat is null || chat.WasCollected || !chat)
                return false;
            var hud = HudGui._instance;
            if (hud is null || hud.WasCollected || !hud)
                return false;
            var current = hud.Chatbox;
            return current is not null && !current.WasCollected && current && current.Pointer == chat.Pointer;
        }

        private sealed class HostRegistration : IDisposable
        {
            internal HostRegistration(string name, Func<HostCommandContext, bool> authorize,
                Func<HostCommandContext, CommandResult> execute)
            {
                Name = name;
                Authorize = authorize;
                Execute = execute;
            }

            internal string Name { get; }
            internal Func<HostCommandContext, bool> Authorize { get; }
            internal Func<HostCommandContext, CommandResult> Execute { get; }

            public void Dispose()
            {
                lock (SyncRoot)
                    if (HostRegistrations.TryGetValue(Name, out var current) && ReferenceEquals(current, this))
                        HostRegistrations.Remove(Name);
            }
        }
    }

    /// <summary>Local input bound to one captured listen-host player, never a fabricated remote session.</summary>
    public sealed class HostCommandContext
    {
        internal HostCommandContext(string name, string arguments, HostPlayer player, DateTimeOffset receivedAt)
        {
            Name = name;
            Arguments = arguments;
            Player = player;
            ReceivedAt = receivedAt;
        }

        public string Name { get; }
        /// <summary>Untrusted text after the first space or tab, without tokenization or quote interpretation.</summary>
        public string Arguments { get; }
        public HostPlayer Player { get; }
        public DateTimeOffset ReceivedAt { get; }
    }
}
