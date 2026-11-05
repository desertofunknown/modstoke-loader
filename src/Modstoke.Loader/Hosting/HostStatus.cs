// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Modstoke.Diagnostics;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    /// <summary>
    /// Keeps Runtime/host-status.json current for the hosting agent. The game thread only takes a small
    /// capture; the writer thread adds everything else and replaces the file atomically.
    /// </summary>
    internal static class HostStatus
    {
        private const int Schema = 1;
        private const int WriteIntervalMs = 2000;
        private const int ChangeIntervalMs = 250;
        private const int StalledMs = 3000;
        private const int SavingLimitMs = 60000;
        private const int IntegrityEntries = 50;

        private static readonly object Sync = new object();
        private static readonly object WriteGate = new object();
        private static readonly Lazy<string> Wine = new Lazy<string>(ReadWineVersion);
        private static Capture _pending;
        private static Capture _last;
        private static SaveRecord _lastSave;
        private static bool _writing;
        private static bool _active;
        private static bool _readySent;
        private static bool _failureLogged;
        private static long _sequence;
        private static long _written;
        private static long _nextCheck;
        private static long _nextWrite;
        private static long _savingSince;
        private static long _lastQueued;
        private static Timer _heartbeat;
        private static int _changed;
        private static int _crashed;
        private static string _stopping;
        private static string _baseState = "starting";
        private static string _lastState;
        private static int _maxPlayers;

        // Frame statistics, game thread only.
        private static long _windowStart;
        private static int _windowFrames;
        private static int _windowBoltFrame = -1;
        private static double _fps;
        private static double _frameTimeMs;
        private static double _tickRate;

        internal static string State
        {
            get
            {
                if (Volatile.Read(ref _crashed) != 0 || HostCrash.Crashed) return "crashed";
                if (Volatile.Read(ref _stopping) != null || HostSave.Stopping) return "stopping";
                if (Saving) return "saving";
                return Volatile.Read(ref _baseState);
            }
        }

        private static bool Saving
        {
            get
            {
                var since = Interlocked.Read(ref _savingSince);
                if (since != 0 && Environment.TickCount64 - since > SavingLimitMs)
                {
                    Interlocked.CompareExchange(ref _savingSince, 0, since);
                    since = 0;
                }
                return since != 0 || HostSave.InProgress;
            }
        }

        internal static void Initialize()
        {
            if (!Host.Enabled || _active)
                return;
            _active = true;
            _heartbeat = new Timer(_ => Heartbeat(), null, WriteIntervalMs, WriteIntervalMs);
        }

        internal static void Tick()
        {
            if (!_active)
                return;
            var now = Environment.TickCount64;
            SampleFrame(now);
            if (now < _nextCheck)
                return;
            _nextCheck = now + ChangeIntervalMs;

            ObserveState();
            var state = State;
            var changed = Interlocked.Exchange(ref _changed, 0) != 0 || state != _lastState;
            if (!changed && now < _nextWrite)
                return;
            _lastState = state;
            _nextWrite = now + WriteIntervalMs;
            var capture = CaptureNow();
            Volatile.Write(ref _last, capture);
            Queue(capture);

            if (capture.State == "ready" && !_readySent)
            {
                _readySent = true;
                var environment = GameRuntime.Environment;
                HostEvents.Emit("ready", new
                {
                    protocol = HostOutput.Protocol,
                    loader = LoaderVersion(environment),
                    game = environment.SonsGameVersion,
                    pid = Environment.ProcessId,
                    players = capture.Players,
                    maxPlayers = capture.MaxPlayers
                });
                MarkChanged();
            }
        }

        internal static void Shutdown()
        {
            if (!_active)
                return;
            SetStopping("application-quit");
            // The process is quitting, so this one write runs on the game thread.
            Capture capture;
            try { capture = CaptureNow(); }
            catch (Exception) { capture = Volatile.Read(ref _last); }
            if (capture != null)
                Write(capture);
            _active = false;
            _heartbeat?.Dispose();
            _heartbeat = null;
        }

        internal static void MarkChanged() => Volatile.Write(ref _changed, 1);

        internal static void SetSaving(bool saving)
        {
            Interlocked.Exchange(ref _savingSince, saving ? Math.Max(1, Environment.TickCount64) : 0);
            MarkChanged();
        }

        internal static void SetStopping(string reason)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "stop" : reason;
            if (Interlocked.CompareExchange(ref _stopping, reason, null) != null)
                return;
            if (Host.Enabled)
                HostEvents.Emit("stopping", new { reason });
            MarkChanged();
        }

        internal static void RecordSave(DateTimeOffset at, bool ok, string detail)
        {
            Volatile.Write(ref _lastSave, new SaveRecord(at, ok, detail));
            MarkChanged();
        }

        internal static void WriteCrashedNow()
        {
            Volatile.Write(ref _crashed, 1);
            WriteNow();
        }

        // Rewrites the last capture with the current time and state from the calling thread, for exits
        // that skip Shutdown.
        internal static void WriteNow()
        {
            if (!Host.Enabled)
                return;
            try
            {
                Write(Refreshed(Volatile.Read(ref _last) ?? new Capture(0, DateTimeOffset.UtcNow, State, 0, 0, 0, 0, 0, 0)));
            }
            catch (Exception)
            {
                // The process is going down; the crash report and log carry the details.
            }
        }

        internal static void RunStatus(ConsoleSubmission request)
        {
            Capture capture;
            string summary;
            try
            {
                capture = Host.IsMainThread ? CaptureNow() : Volatile.Read(ref _last);
                if (capture == null)
                {
                    HostCommands.Respond(request, false, "The status is not available yet.", error: "not-ready");
                    return;
                }
                summary = ConsoleHub.Read(long.MaxValue, 1).Summary;
            }
            catch (Exception error)
            {
                HostCommands.Respond(request, false, "Status failed: " + error.Message);
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    var human = (string.IsNullOrEmpty(summary) ? "Modstoke " + RuntimeEntryPoint.Version : summary)
                        + " · " + capture.State;
                    HostCommands.Respond(request, true, human, Build(capture));
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Status failed: " + error.Message);
                }
            });
        }

        // The assembly hash and Wine probe can touch the disk, so the reply is built off the game thread.
        internal static void RunVersion(ConsoleSubmission request)
        {
            Task.Run(() =>
            {
                try
                {
                    var environment = GameRuntime.Environment;
                    var game = Game(environment);
                    game.Remove("dedicatedServer");
                    game["assemblySha256"] = GameBuild.AssemblySha256;
                    var body = new JsonObject
                    {
                        ["protocol"] = HostOutput.Protocol,
                        ["loader"] = new JsonObject { ["version"] = LoaderVersion(environment) },
                        ["game"] = game,
                        ["runtime"] = Runtime(environment)
                    };
                    var wine = Wine.Value;
                    var human = "Modstoke " + LoaderVersion(environment)
                        + " · game " + (environment.SonsGameVersion ?? "unknown")
                        + " · .NET " + DotnetVersion(environment)
                        + (wine == null ? "" : " · Wine " + wine);
                    HostCommands.Respond(request, true, human, body);
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Version failed: " + error.Message);
                }
            });
        }

        private static void SampleFrame(long now)
        {
            if (_windowStart == 0)
            {
                _windowStart = now;
                _windowFrames = 0;
                _windowBoltFrame = BoltFrame();
                return;
            }
            _windowFrames++;
            var elapsed = now - _windowStart;
            if (elapsed < 1000)
                return;
            var boltFrame = BoltFrame();
            _fps = Math.Round(_windowFrames * 1000.0 / elapsed, 1);
            _frameTimeMs = _windowFrames == 0 ? 0 : Math.Round((double)elapsed / _windowFrames, 2);
            _tickRate = boltFrame >= 0 && _windowBoltFrame >= 0 && boltFrame >= _windowBoltFrame
                ? Math.Round((boltFrame - _windowBoltFrame) * 1000.0 / elapsed, 1) : 0;
            _windowStart = now;
            _windowFrames = 0;
            _windowBoltFrame = boltFrame;
        }

        private static int BoltFrame()
        {
            try { return BoltNetwork.isRunning ? BoltNetwork.frame : -1; }
            catch (Exception) { return -1; }
        }

        private static void ObserveState()
        {
            bool started = false, serverActive = false;
            try
            {
                started = RuntimeEntryPoint.StartupCompleted;
                serverActive = Host.IsServer && ServerWorld.IsServerActive;
            }
            catch (Exception)
            {
                // Treated as not ready until the next check.
            }
            Volatile.Write(ref _baseState, started && (!Host.IsServer || serverActive) ? "ready"
                : started ? "loading" : "starting");
        }

        private static Capture CaptureNow()
        {
            ObserveState();
            int players = 0;
            long tick = 0;
            try
            {
                var sessions = ServerSessions.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                    if (sessions[i].IsCurrent)
                        players++;
            }
            catch (Exception) { }
            try
            {
                if (ServerWorld.IsServerActive && ServerWorld.TryReadPopulation(out var population, out _))
                    _maxPlayers = population.RuntimeMaximumPlayers;
            }
            catch (Exception) { }
            try
            {
                if (BoltNetwork.isRunning)
                    tick = BoltNetwork.frame;
            }
            catch (Exception) { }
            return new Capture(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, State,
                _fps, _frameTimeMs, tick, _tickRate, players, _maxPlayers);
        }

        private static Capture Refreshed(Capture capture) =>
            capture with { Sequence = Interlocked.Increment(ref _sequence), At = DateTimeOffset.UtcNow, State = State };

        // Keeps updatedAt moving while the game thread is stuck in a long load or save.
        private static void Heartbeat()
        {
            try
            {
                var last = Volatile.Read(ref _last);
                if (_active && last != null && Environment.TickCount64 - Interlocked.Read(ref _lastQueued) > StalledMs)
                    Queue(Refreshed(last));
            }
            catch (Exception)
            {
                // The next tick writes again.
            }
        }

        private static void Queue(Capture capture)
        {
            Interlocked.Exchange(ref _lastQueued, Environment.TickCount64);
            lock (Sync)
            {
                _pending = capture;
                if (_writing)
                    return;
                _writing = true;
            }
            Task.Run(Drain);
        }

        private static void Drain()
        {
            while (true)
            {
                Capture capture;
                lock (Sync)
                {
                    capture = _pending;
                    _pending = null;
                    if (capture == null)
                    {
                        _writing = false;
                        return;
                    }
                }
                Write(capture);
            }
        }

        private static void Write(Capture capture)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Build(capture), HostOutput.Json);
                lock (WriteGate)
                {
                    // A slower writer must not replace a newer status, such as the final one at quit.
                    if (capture.Sequence < _written)
                        return;
                    HostOutput.WriteFileAtomic(LoaderPaths.HostStatusPath, bytes);
                    _written = capture.Sequence;
                    _failureLogged = false;
                }
            }
            catch (Exception error)
            {
                lock (WriteGate)
                {
                    if (_failureLogged)
                        return;
                    _failureLogged = true;
                }
                RuntimeEntryPoint.LogSource?.LogWarning("Host status write failed: " + error.Message);
            }
        }

        // JsonObject keeps explicit nulls, which the shared options would otherwise drop.
        private static JsonObject Build(Capture capture)
        {
            var environment = GameRuntime.Environment;
            var save = Volatile.Read(ref _lastSave);
            return new JsonObject
            {
                ["schema"] = Schema,
                ["protocol"] = HostOutput.Protocol,
                ["pid"] = Environment.ProcessId,
                ["updatedAt"] = HostOutput.Utc(capture.At),
                ["state"] = capture.State,
                ["startedAt"] = HostOutput.Utc(Host.StartedUtc),
                ["uptimeSeconds"] = Math.Max(0L, (long)(capture.At - Host.StartedUtc).TotalSeconds),
                ["loader"] = new JsonObject { ["version"] = LoaderVersion(environment) },
                ["game"] = Game(environment),
                ["runtime"] = Runtime(environment),
                ["performance"] = new JsonObject
                {
                    ["fps"] = capture.Fps,
                    ["frameTimeMs"] = capture.FrameTimeMs,
                    ["tick"] = capture.Tick,
                    ["tickRate"] = capture.TickRate
                },
                ["players"] = new JsonObject { ["count"] = capture.Players, ["max"] = capture.MaxPlayers },
                ["memory"] = new JsonObject
                {
                    ["managedBytes"] = GC.GetTotalMemory(false),
                    ["workingSetBytes"] = Environment.WorkingSet
                },
                ["mods"] = Read(Mods) ?? new JsonArray(),
                ["lastSaveAt"] = save == null ? null : HostOutput.Utc(save.At),
                ["lastSaveOk"] = save?.Ok,
                ["lastSaveDetail"] = save?.Detail,
                ["pendingChanges"] = Read(() => JsonSerializer.SerializeToNode(HostConfig.PendingChanges(), HostOutput.Json))
                    ?? new JsonArray(),
                ["pendingMods"] = Read(PendingMods) ?? new JsonArray(),
                ["integrity"] = Read(Integrity),
                ["configs"] = Read(Configs) ?? new JsonArray(),
                ["files"] = new JsonObject
                {
                    ["status"] = LoaderPaths.HostStatusPath,
                    ["events"] = LoaderPaths.HostEventsPath,
                    ["crashDirectory"] = LoaderPaths.RuntimeDirectory
                },
                ["tcp"] = Read(() => new JsonObject { ["port"] = HostTcp.Port, ["clients"] = HostTcp.ClientCount }),
                ["events"] = new JsonObject { ["dropped"] = HostEvents.Dropped, ["rateLimited"] = ServerHosting.RateLimited }
            };
        }

        private static JsonObject Game(EnvironmentSnapshot environment) => new JsonObject
        {
            ["version"] = environment.SonsGameVersion,
            ["unity"] = environment.UnityVersion,
            ["executable"] = environment.GameExecutableName,
            ["dedicatedServer"] = Host.IsServer,
            ["buildRecognized"] = BuildRecognized()
        };

        private static JsonObject Runtime(EnvironmentSnapshot environment) => new JsonObject
        {
            ["dotnet"] = DotnetVersion(environment),
            ["os"] = RuntimeInformation.OSDescription,
            ["wine"] = Wine.Value
        };

        private static JsonNode Mods()
        {
            var mods = new JsonArray();
            foreach (var mod in HostMods.LoadResults)
                mods.Add((JsonNode)new JsonObject
                {
                    ["id"] = mod.Id,
                    ["name"] = mod.Name,
                    ["version"] = mod.Version,
                    ["loader"] = mod.Loader,
                    ["status"] = mod.Status,
                    ["ok"] = mod.Ok,
                    ["loadMs"] = Math.Round(mod.LoadMs, 1),
                    ["error"] = mod.Error
                });
            return mods;
        }

        private static JsonNode Integrity()
        {
            var snapshot = FileIntegrity.Snapshot;
            var mismatches = new JsonArray();
            var issues = snapshot.IssueEntries;
            for (int i = 0; i < issues.Count && i < IntegrityEntries; i++)
            {
                var entry = issues[i];
                mismatches.Add((JsonNode)new JsonObject
                {
                    ["path"] = entry.Path,
                    ["status"] = entry.Status,
                    ["expectedSha256"] = entry.ExpectedSha256,
                    ["actualSha256"] = entry.ActualSha256,
                    ["expectedSize"] = entry.ExpectedSize,
                    ["actualSize"] = entry.ActualSize
                });
            }
            return new JsonObject
            {
                ["status"] = snapshot.Status,
                ["ready"] = snapshot.Ready,
                ["total"] = snapshot.TotalFileCount,
                ["missing"] = snapshot.MissingFileCount,
                ["sizeMismatch"] = snapshot.SizeMismatchFileCount,
                ["hashMismatch"] = snapshot.HashMismatchFileCount,
                ["mismatches"] = mismatches
            };
        }

        private static JsonNode Configs()
        {
            var configs = new JsonArray();
            foreach (var config in HostConfig.Known)
                configs.Add((JsonNode)new JsonObject
                {
                    ["id"] = config.Id,
                    ["path"] = config.Path,
                    ["format"] = config.Format,
                    ["reload"] = config.Reload
                });
            return configs;
        }

        // Mod installs and removals the native installer queued while the server ran; it applies them before the next
        // start. The Loader only reports them and never installs anything itself.
        private static JsonNode PendingMods()
        {
            var pending = new JsonArray();
            var directory = Path.Combine(LoaderPaths.RuntimeDirectory, "pending");
            if (!Directory.Exists(directory))
                return pending;
            var files = Directory.GetFiles(directory, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (pending.Count >= 64)
                    break;
                try
                {
                    if (new FileInfo(file).Length > 64 * 1024)
                        continue;
                    if (JsonNode.Parse(File.ReadAllBytes(file)) is not JsonObject request
                        || request["kind"]?.GetValue<string>() != "modstoke.pending-native-change")
                        continue;
                    var entry = new JsonObject();
                    foreach (var key in PendingModFields)
                    {
                        if (request[key] is JsonValue value)
                            entry[key] = value.DeepClone();
                    }
                    pending.Add((JsonNode)entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or JsonException or InvalidOperationException or FormatException)
                {
                    // A request still being written, or one that is not ours; the next write tries again.
                }
            }
            return pending;
        }

        private static readonly string[] PendingModFields =
            { "id", "action", "modId", "version", "archiveSha256", "queuedUtc", "requestedBy" };

        private static JsonNode Read(Func<JsonNode> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        // A client is checked against the pinned client build, a server against the server binary.
        private static bool? BuildRecognized()
        {
            try { return Host.IsServer ? GameBuild.IsDedicatedServer : GameBuild.IsRecognized; }
            catch (Exception) { return null; }
        }

        private static string LoaderVersion(EnvironmentSnapshot environment) =>
            environment.LoaderVersion ?? RuntimeEntryPoint.Version;

        private static string DotnetVersion(EnvironmentSnapshot environment) =>
            environment.DotnetRuntimeVersion ?? Environment.Version.ToString();

        // ntdll is already loaded in every process; under Wine it also exports its version.
        private static unsafe string ReadWineVersion()
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("ntdll.dll", out var ntdll))
                    return null;
                if (!NativeLibrary.TryGetExport(ntdll, "wine_get_version", out var export))
                    return null;
                var version = ((delegate* unmanaged<IntPtr>)export)();
                return version == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(version);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed record Capture(long Sequence, DateTimeOffset At, string State, double Fps, double FrameTimeMs,
            long Tick, double TickRate, int Players, int MaxPlayers);

        private sealed record SaveRecord(DateTimeOffset At, bool Ok, string Detail);
    }
}
