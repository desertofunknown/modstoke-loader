// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Modstoke.Diagnostics;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    /// <summary>
    /// The host "save" and "stop" commands, and the "saved" and "save-failed" events for every world save.
    /// A save is answered once the game's save has finished and SaveData.zip was rewritten. Stop saves,
    /// waits for its replies to be written and quits, and a watchdog ends the process if the quit hangs.
    /// </summary>
    internal static class HostSave
    {
        internal const int SavedExitCode = 0;
        internal const int SaveFailedExitCode = 2;
        internal const int WatchdogExitCode = 3;
        private const int SaveTimeoutMs = 30000;
        private const int UnhookedCheckMs = 2000;
        private const int UnhookedRetryMs = 500;
        private const int StopLimitMs = 50000;
        private const int QuitLimitMs = 15000;
        private const int FlushLimitMs = 2000;
        private const string ArchiveName = "SaveData.zip";
        private const string RefusedDetail = "The save was refused so Modstoke world records are not lost. See the log.";

        private static readonly object Sync = new object();
        private static Save _save;
        private static long _beforeTicks;
        private static DateTimeOffset _beforeAt;
        private static string _directory;
        private static Stop _stop;
        private static bool _subscribed;
        private static long _stopDeadline;
        private static long _deadline;
        private static LastRecord _last;

        internal static bool InProgress
        {
            get { lock (Sync) return _save != null; }
        }

        /// <summary>True while a host save waits for its result or the game is inside a save.</summary>
        internal static bool Saving
        {
            get
            {
                lock (Sync)
                    return _save != null || (_beforeTicks != 0 && Environment.TickCount64 - _beforeTicks < SaveTimeoutMs);
            }
        }

        internal static bool Stopping => Volatile.Read(ref _stop) != null;

        /// <summary>The last finished or failed world save: at (UTC ms), slot, ok, durationMs, sizeBytes. Safe from any thread.</summary>
        internal static JsonObject LastSave()
        {
            var last = Volatile.Read(ref _last);
            return last == null ? null : new JsonObject
            {
                ["at"] = last.At.ToUnixTimeMilliseconds(),
                ["slot"] = last.Slot,
                ["ok"] = last.Ok,
                ["durationMs"] = last.DurationMs,
                ["sizeBytes"] = last.SizeBytes
            };
        }

        internal static void Initialize()
        {
            if (_subscribed)
                return;
            _subscribed = true;
            SaveLifecycle.Lifecycle += OnLifecycle;
        }

        internal static void Tick()
        {
            Save save;
            Outcome outcome;
            lock (Sync)
            {
                save = _save;
                outcome = save?.Outcome;
            }
            if (save != null)
                Progress(save, outcome);
            else
                CheckAbandoned();

            var stop = _stop;
            if (stop != null && stop.ExitCode >= 0 && !stop.Quit)
                TryQuit(stop);
        }

        internal static void OnQuit()
        {
            if (_stop != null)
                return;
            Save save;
            lock (Sync)
            {
                save = _save;
                _save = null;
            }
            if (save?.Request != null)
                HostCommands.Respond(save.Request, false, "The server is shutting down.", error: "failed");
        }

        internal static void RunSave(ConsoleSubmission request)
        {
            if (InProgress || Stopping)
            {
                HostCommands.Respond(request, false, "A save or stop is already running.", error: "busy");
                return;
            }
            if (!WorldActive())
            {
                HostCommands.Respond(request, false, "No world is running.", error: "not-ready");
                return;
            }
            Begin(new Save(request, "command"));
        }

        internal static void RunStop(ConsoleSubmission request)
        {
            if (Stopping)
            {
                HostCommands.Respond(request, false, "The server is already stopping.", error: "busy");
                return;
            }
            var stop = new Stop(request);
            Volatile.Write(ref _stop, stop);
            HostStatus.SetStopping("command");
            StartWatchdog();

            if (request.Arguments == "--no-save")
            {
                EndStop(stop, null, "Skipped with --no-save.");
                return;
            }
            if (!WorldActive())
            {
                EndStop(stop, null, "No world was running.");
                return;
            }
            Save running;
            lock (Sync)
                running = _save;
            // A save already in flight finishes the stop too.
            stop.Save = running ?? new Save(null, "stop");
            if (running == null)
                Begin(stop.Save);
        }

        private static void Begin(Save save)
        {
            lock (Sync)
                _save = save;
            var status = ServerSaves.SaveNow();
            if (status != ServerSaveRequestStatus.Queued)
            {
                var notReady = status is ServerSaveRequestStatus.NotReady or ServerSaveRequestStatus.NotDedicatedServer
                    or ServerSaveRequestStatus.SaveManagerUnavailable or ServerSaveRequestStatus.AutosaveUnavailable;
                Fail(save, notReady ? "not-ready" : "failed", Describe(status), !notReady);
                return;
            }
            if (Refused(save))
                Fail(save, "refused", RefusedDetail, true);
        }

        // WorldRecords refuses inside the save prefix, so a refused save never reaches the lifecycle events.
        // A refusal left over from an earlier save is not trusted; that save runs into the timeout instead.
        private static bool Refused(Save save) =>
            !save.RefusedAtStart
            && SaveLifecycle.State.AfterSaveCount == save.AfterCount
            && WorldRecords.LastSaveStatus == WorldRecordSaveStatus.Refused;

        private static void Progress(Save save, Outcome outcome)
        {
            if (outcome != null)
            {
                Finish(save, outcome, outcome.Ok ? null : "failed");
                return;
            }
            if (!save.Verifying && Refused(save))
            {
                Fail(save, "refused", RefusedDetail, true);
                return;
            }
            var elapsed = Environment.TickCount64 - save.StartedTicks;
            if (elapsed >= SaveTimeoutMs)
            {
                HostStatus.SetSaving(false);
                string last;
                lock (Sync)
                {
                    last = save.LastDetail;
                    // A game save left open belongs to this one, which reports it.
                    _beforeTicks = 0;
                }
                Fail(save, "timeout", "The save did not finish within " + SaveTimeoutMs / 1000 + " seconds."
                    + (last == null ? "" : " " + last), true);
                return;
            }
            // Without the lifecycle hooks no save event arrives; check the archive where the last save went.
            if (elapsed < UnhookedCheckMs || SaveLifecycle.State.HarmonyPatchesInstalled)
                return;
            var now = Environment.TickCount64;
            lock (Sync)
            {
                if (save.Verifying || now < save.NextCheckTicks)
                    return;
                save.Verifying = true;
                save.NextCheckTicks = now + UnhookedRetryMs;
            }
            var directory = KnownDirectory();
            var at = DateTimeOffset.UtcNow;
            var day = HostWorld.Day;
            Task.Run(() => Verify(save, save.Source, directory, save.StartedAt, at, elapsed, day));
        }

        // The game's save runs inside one call, so a Before still open after the save timeout means it threw.
        private static void CheckAbandoned()
        {
            string directory;
            long elapsed;
            lock (Sync)
            {
                elapsed = Environment.TickCount64 - _beforeTicks;
                if (_beforeTicks == 0 || elapsed < SaveTimeoutMs)
                    return;
                _beforeTicks = 0;
                directory = _directory;
            }
            HostStatus.SetSaving(false);
            Record("autosave", new Outcome(false, DateTimeOffset.UtcNow, directory, elapsed,
                "The game's save did not finish. See the log."), "failed");
        }

        // Runs inside the game's save, so it only copies values and queues the archive check.
        private static void OnLifecycle(SaveLifecycleEvent evt)
        {
            if (evt == null || evt.Operation != "Save" || evt.SavePlayerOnly == true)
                return;
            if (evt.Phase == "Before")
            {
                lock (Sync)
                {
                    _beforeTicks = Environment.TickCount64;
                    _beforeAt = evt.TimestampUtc;
                    _directory = evt.Directory ?? _directory;
                }
                HostStatus.SetSaving(true);
                return;
            }
            if (evt.Phase != "After")
                return;

            HostStatus.SetSaving(false);
            Save save;
            string directory;
            DateTimeOffset since;
            long durationMs;
            lock (Sync)
            {
                save = _save != null && !_save.Verifying ? _save : null;
                if (save != null)
                    save.Verifying = true;
                directory = _directory = evt.Directory ?? _directory;
                since = _beforeTicks != 0 ? _beforeAt : save?.StartedAt ?? evt.TimestampUtc;
                durationMs = _beforeTicks != 0 ? Environment.TickCount64 - _beforeTicks : 0;
                _beforeTicks = 0;
            }
            var source = save?.Source ?? "autosave";
            var at = evt.TimestampUtc;
            var day = HostWorld.Day;
            Task.Run(() => Verify(save, source, directory, since, at, durationMs, day));
        }

        private static void Verify(Save save, string source, string directory, DateTimeOffset since,
            DateTimeOffset at, long durationMs, int? day)
        {
            var outcome = Check(directory, since, at, durationMs) with { Day = day };
            if (save != null && !SaveLifecycle.State.HarmonyPatchesInstalled)
            {
                // Without the hooks the game may still be writing the archive, so it has to pass twice
                // without changing. Until then Progress checks again; the timeout reports a save that never does.
                var stamp = outcome.Ok ? Stamp(directory) : null;
                lock (Sync)
                {
                    var settled = stamp != null && stamp == save.Stamp;
                    save.Stamp = stamp;
                    save.LastDetail = outcome.Detail;
                    if (!settled)
                    {
                        save.Verifying = false;
                        return;
                    }
                }
            }
            if (outcome.Ok)
                outcome = outcome with { Slot = HostWorld.SlotOf(directory), SizeBytes = HostWorld.DirectorySize(directory) };
            Record(source, outcome, "failed");
            if (save != null)
                lock (Sync)
                    save.Outcome = outcome;
        }

        private static string Stamp(string directory)
        {
            try
            {
                var archive = new FileInfo(Path.Combine(directory, ArchiveName));
                return archive.Exists ? archive.Length + ":" + archive.LastWriteTimeUtc.Ticks : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Outcome Check(string directory, DateTimeOffset since, DateTimeOffset at, long durationMs)
        {
            var ok = false;
            string detail;
            try
            {
                var archive = string.IsNullOrEmpty(directory) ? null : new FileInfo(Path.Combine(directory, ArchiveName));
                if (archive == null)
                    detail = "The save directory is not known.";
                else if (!archive.Exists)
                    detail = ArchiveName + " was not found in " + directory + ".";
                else if (archive.Length == 0)
                    detail = ArchiveName + " is empty.";
                else if (archive.LastWriteTimeUtc < since.UtcDateTime.AddSeconds(-2))
                    detail = ArchiveName + " was not rewritten by this save.";
                else
                {
                    ok = true;
                    detail = "Saved to " + directory + ".";
                }
            }
            catch (Exception error)
            {
                detail = "The save could not be checked: " + error.Message;
            }
            return new Outcome(ok, at, directory, durationMs, detail);
        }

        // source is the event's reason: autosave, command or stop. Safe from any thread.
        private static void Record(string source, Outcome outcome, string error)
        {
            var slot = outcome.Slot ?? HostWorld.SlotOf(outcome.Directory ?? KnownDirectory()) ?? HostWorld.ActiveSlot;
            HostStatus.RecordSave(outcome.At, outcome.Ok, outcome.Detail);
            Volatile.Write(ref _last, new LastRecord(outcome.At, slot, outcome.Ok, outcome.DurationMs, outcome.SizeBytes));
            // JsonObject keeps the nulls, so every field is always present.
            if (outcome.Ok)
                HostEvents.Emit("saved", new JsonObject
                {
                    ["slot"] = slot,
                    ["day"] = outcome.Day,
                    ["sizeBytes"] = outcome.SizeBytes,
                    ["durationMs"] = outcome.DurationMs,
                    ["reason"] = source,
                    ["path"] = HostWorld.GamePath(outcome.Directory)
                });
            else
                HostEvents.Emit("save-failed", new JsonObject
                {
                    ["slot"] = slot,
                    ["reason"] = source,
                    ["error"] = error ?? "failed",
                    ["detail"] = outcome.Detail
                });
        }

        private static void Fail(Save save, string error, string detail, bool record)
        {
            var outcome = new Outcome(false, DateTimeOffset.UtcNow, null,
                Environment.TickCount64 - save.StartedTicks, detail);
            if (record)
                Record(save.Source, outcome, error);
            Finish(save, outcome, error);
        }

        private static void Finish(Save save, Outcome outcome, string error)
        {
            lock (Sync)
            {
                if (_save != save)
                    return;
                _save = null;
            }
            if (save.Request != null)
            {
                if (outcome.Ok)
                {
                    var path = HostWorld.GamePath(outcome.Directory);
                    HostCommands.Respond(save.Request, true,
                        "World saved to " + path + " in " + outcome.DurationMs + " ms.",
                        new JsonObject
                        {
                            ["saved"] = true,
                            ["at"] = HostOutput.Utc(outcome.At),
                            ["slot"] = outcome.Slot,
                            ["day"] = outcome.Day,
                            ["path"] = path,
                            ["directory"] = outcome.Directory,
                            ["sizeBytes"] = outcome.SizeBytes,
                            ["durationMs"] = outcome.DurationMs
                        });
                }
                else
                    HostCommands.Respond(save.Request, false, outcome.Detail, error: error);
            }
            var stop = _stop;
            if (stop != null && stop.Save == save && stop.ExitCode < 0)
                EndStop(stop, outcome.Ok, outcome.Detail);
        }

        private static void EndStop(Stop stop, bool? saved, string detail)
        {
            stop.ExitCode = saved == false ? SaveFailedExitCode : SavedExitCode;
            stop.FinishedTicks = Environment.TickCount64;
            var human = saved switch
            {
                true => "World saved. Stopping the server.",
                false => "The save failed: " + detail + " Stopping the server anyway.",
                _ => detail + " Stopping the server."
            };
            // JsonObject keeps "saved": null, which the serializer options would drop.
            HostCommands.Respond(stop.Request, true, human, new JsonObject
            {
                ["saved"] = saved,
                ["exitCode"] = stop.ExitCode,
                ["saveDetail"] = detail
            });
            HostEvents.Emit("stop", new JsonObject { ["exitCode"] = stop.ExitCode, ["saved"] = saved });
        }

        // Gives the writers a moment to put the reply and events out before Unity starts tearing down.
        private static void TryQuit(Stop stop)
        {
            var flushed = HostOutput.PendingCount == 0 && HostTcp.PendingCount == 0 && HostEvents.Flush(0);
            if (!flushed && Environment.TickCount64 - stop.FinishedTicks < FlushLimitMs)
                return;
            stop.Quit = true;
            var quitDeadline = Environment.TickCount64 + QuitLimitMs;
            Interlocked.Exchange(ref _deadline, Math.Min(Interlocked.Read(ref _stopDeadline), quitDeadline));
            RuntimeEntryPoint.LogSource?.LogMessage("Quitting with exit code " + stop.ExitCode + ".");
            UnityEngine.Application.Quit(stop.ExitCode);
        }

        private static void StartWatchdog()
        {
            var deadline = Environment.TickCount64 + StopLimitMs;
            Interlocked.Exchange(ref _stopDeadline, deadline);
            Interlocked.Exchange(ref _deadline, deadline);
            new Thread(Watch) { IsBackground = true, Name = "Modstoke stop watchdog" }.Start();
        }

        private static void Watch()
        {
            while (Environment.TickCount64 < Interlocked.Read(ref _deadline))
                Thread.Sleep(250);
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server did not stop in time. Ending the process.");
            }
            catch (Exception) { }
            HostEvents.Emit("stop", new JsonObject
            {
                ["exitCode"] = WatchdogExitCode,
                ["saved"] = null,
                ["watchdog"] = true
            });
            HostEvents.Flush(500);
            HostStatus.WriteNow();
            HostOutput.Flush(500);
            HostCrash.Terminate(WatchdogExitCode);
        }

        private static bool WorldActive()
        {
            try
            {
                return ServerWorld.IsServerActive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string KnownDirectory()
        {
            lock (Sync)
            {
                if (_directory != null)
                    return _directory;
            }
            return SaveLifecycle.State.LastDirectory;
        }

        private static string Describe(ServerSaveRequestStatus status) => status switch
        {
            ServerSaveRequestStatus.NotDedicatedServer => "The dedicated server is not hosting a world.",
            ServerSaveRequestStatus.SaveManagerUnavailable => "The game's save manager is not available.",
            ServerSaveRequestStatus.AutosaveUnavailable => "The server's autosave controller is not available.",
            ServerSaveRequestStatus.NotReady => "The world is not ready to save yet.",
            ServerSaveRequestStatus.RequestUncertain => "The game's save failed part way. See the log.",
            _ => "The save could not be started (" + status + ")."
        };

        private sealed class Save
        {
            internal Save(ConsoleSubmission request, string source)
            {
                Request = request;
                Source = source;
                AfterCount = SaveLifecycle.State.AfterSaveCount;
                RefusedAtStart = WorldRecords.LastSaveStatus == WorldRecordSaveStatus.Refused;
                StartedTicks = Environment.TickCount64;
                StartedAt = DateTimeOffset.UtcNow;
            }

            // Null for the save a stop starts itself.
            internal ConsoleSubmission Request { get; }
            internal string Source { get; }
            internal int AfterCount { get; }
            internal bool RefusedAtStart { get; }
            internal long StartedTicks { get; }
            internal DateTimeOffset StartedAt { get; }
            internal bool Verifying { get; set; }
            internal long NextCheckTicks { get; set; }
            internal string Stamp { get; set; }
            internal string LastDetail { get; set; }
            internal Outcome Outcome { get; set; }
        }

        // Slot and SizeBytes are filled in for a verified save, Day where the clock was read.
        private sealed record Outcome(bool Ok, DateTimeOffset At, string Directory, long DurationMs, string Detail,
            string Slot = null, int? Day = null, long? SizeBytes = null);

        private sealed record LastRecord(DateTimeOffset At, string Slot, bool Ok, long DurationMs, long? SizeBytes);

        private sealed class Stop
        {
            internal Stop(ConsoleSubmission request) => Request = request;

            internal ConsoleSubmission Request { get; }
            internal Save Save { get; set; }
            internal int ExitCode { get; set; } = -1;
            internal long FinishedTicks { get; set; }
            internal bool Quit { get; set; }
        }
    }
}
