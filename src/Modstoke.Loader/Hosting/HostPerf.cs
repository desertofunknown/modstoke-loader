// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Diagnostics;
using UnityEngine;
using static System.FormattableString;

namespace Modstoke.Loader
{
    /// <summary>
    /// Rolling frame statistics for the status file, and the <c>perf</c> command, which times each mod's per-frame
    /// methods for a few seconds. Timing patches exist only while a perf session runs.
    /// </summary>
    internal static class HostPerf
    {
        private const string HarmonyId = "modstoke.loader.perf";
        private const double WindowSeconds = 10;
        private const int RingSize = 16384;
        private const int AllocationSamples = 12;
        private const int DefaultSeconds = 10;
        private const int MaxSeconds = 60;
        private const int MaxMethods = 2000;
        private const int TopMethods = 3;
        private const int SummaryMods = 5;
        private const int WatchdogSlackSeconds = 120;
        private const int MaxLabelLength = 200;

        private const string NotCovered = "Only Update, LateUpdate, FixedUpdate and OnGUI on mod MonoBehaviours, native "
            + "mod frame callbacks and RedLoader/SonsSdk per-frame event handlers are timed, including everything they "
            + "call. Not measured: coroutines, Harmony patch bodies on game methods, IL2CPP-side work a mod causes "
            + "outside those methods (physics, rendering, game systems), async tasks and other threads, a mod's "
            + "secondary assemblies, and handlers or components registered after the session started.";

        // Scanning, patching and unpatching are spread over frames so a session never stalls the server for long.
        private static readonly long StepTicks = Stopwatch.Frequency * 4 / 1000;
        private static readonly string[] Messages = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
        private static readonly string[] NativeHooks = { "update", "lateupdate", "fixedupdate", "gui" };

        // Frame times in seconds and one allocation sample per second; the game thread writes, the status writer reads.
        private static readonly object Sync = new object();
        private static readonly object ImpactSync = new object();
        private static readonly float[] Ring = new float[RingSize];
        private static readonly long[] AllocatedAt = new long[AllocationSamples];
        private static readonly long[] Allocated = new long[AllocationSamples];
        private static int _ringNext;
        private static int _ringCount;
        private static int _allocationNext;
        private static int _allocationCount;
        private static long _nextAllocationSample;
        private static JsonObject _lastImpact;

        // Game thread only. The timing patches read the last three on every call.
        private static Session _session;
        private static Dictionary<IntPtr, Slot> _slots;
        private static bool _recording;
        private static int _gameThread;

        private enum Phase { Scanning, Patching, Measuring, Unpatching }

        /// <summary>Records the last frame time and advances a running perf session. Game thread, once per frame.</summary>
        internal static void Frame()
        {
            var delta = Time.unscaledDeltaTime;
            var now = Stopwatch.GetTimestamp();
            var allocated = -1L;
            if (now >= _nextAllocationSample)
            {
                allocated = GC.GetTotalAllocatedBytes(false);
                _nextAllocationSample = now + Stopwatch.Frequency;
            }
            lock (Sync)
            {
                Ring[_ringNext] = delta;
                _ringNext = (_ringNext + 1) % RingSize;
                if (_ringCount < RingSize)
                    _ringCount++;
                if (allocated >= 0)
                {
                    AllocatedAt[_allocationNext] = now;
                    Allocated[_allocationNext] = allocated;
                    _allocationNext = (_allocationNext + 1) % AllocationSamples;
                    if (_allocationCount < AllocationSamples)
                        _allocationCount++;
                }
            }
            var session = _session;
            if (session != null)
                Advance(session, now);
        }

        /// <summary>Frame and managed GC statistics over the last 10 s. Safe from any thread.</summary>
        internal static JsonObject FrameStats()
        {
            float[] frames;
            long sampledAt = 0, sampledBytes = 0;
            var now = Stopwatch.GetTimestamp();
            var allocated = GC.GetTotalAllocatedBytes(false);
            lock (Sync)
            {
                // Newest first until the window is full; the newest frame always counts, however long it was.
                int count = 0;
                double total = 0;
                while (count < _ringCount)
                {
                    var delta = Ring[(_ringNext - 1 - count + RingSize) % RingSize];
                    if (count > 0 && total + delta > WindowSeconds)
                        break;
                    total += delta;
                    count++;
                }
                frames = new float[count];
                for (int i = 0; i < count; i++)
                    frames[i] = Ring[(_ringNext - count + i + RingSize) % RingSize] * 1000f;
                for (int i = _allocationCount; i > 0; i--)
                {
                    var index = (_allocationNext - i + AllocationSamples) % AllocationSamples;
                    if (now - AllocatedAt[index] <= (WindowSeconds + 1) * Stopwatch.Frequency)
                    {
                        sampledAt = AllocatedAt[index];
                        sampledBytes = Allocated[index];
                        break;
                    }
                }
            }

            double totalMs = 0;
            foreach (var ms in frames)
                totalMs += ms;
            Array.Sort(frames);
            // A frame lags when it takes more than twice the window's median, or 100 ms if that is longer: a steady
            // server only counts real hitches, and one that is slow throughout only counts spikes above its norm.
            var threshold = Math.Max(2 * Percentile(frames, 0.5), 100);
            var lagging = 0;
            for (int i = frames.Length - 1; i >= 0 && frames[i] > threshold; i--)
                lagging++;
            var elapsed = now - sampledAt;
            var rate = sampledAt == 0 || elapsed < Stopwatch.Frequency / 2 ? 0
                : (long)(Math.Max(0, allocated - sampledBytes) * (double)Stopwatch.Frequency / elapsed);

            // The .NET runtime's GC, which runs the Loader and managed mods; the game's IL2CPP heap is separate.
            return new JsonObject
            {
                ["windowSeconds"] = Math.Round(totalMs / 1000, 2),
                ["frames"] = frames.Length,
                ["fps"] = totalMs > 0 ? Math.Round(frames.Length * 1000 / totalMs, 1) : 0,
                ["frameMs"] = FrameMs(frames.Length == 0 ? 0 : totalMs / frames.Length, frames,
                    frames.Length == 0 ? 0 : frames[^1]),
                ["tickLag"] = lagging,
                ["gc"] = new JsonObject
                {
                    ["gen0"] = GC.CollectionCount(0),
                    ["gen1"] = GC.CollectionCount(1),
                    ["gen2"] = GC.CollectionCount(2),
                    ["pauseMs"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
                    ["allocatedBytesPerSecond"] = rate,
                    ["heapBytes"] = GC.GetTotalMemory(false)
                }
            };
        }

        /// <summary>The last finished perf result with <c>at</c>, or null before the first. Safe from any thread.</summary>
        internal static JsonObject LastImpact()
        {
            lock (ImpactSync)
                return _lastImpact?.DeepClone().AsObject();
        }

        /// <summary><c>perf [--seconds N]</c>. Replies once, when the session has finished and its patches are gone.</summary>
        internal static void RunPerf(ConsoleSubmission request)
        {
            if (!Host.IsMainThread)
            {
                if (Host.Initialized)
                    Host.Post(() => RunPerf(request));
                else
                    HostCommands.Respond(request, false, "The game thread is not running yet.", error: "not-ready");
                return;
            }
            if (!TryReadSeconds(request.Arguments, out var seconds))
            {
                HostCommands.Respond(request, false, "Usage: perf [--seconds <1-" + MaxSeconds + ">]",
                    error: "invalid-arguments");
                return;
            }
            if (_session != null)
            {
                HostCommands.Respond(request, false, "A perf session is already running.", error: "busy");
                return;
            }
            if (HarmonySupport.CreateState()?.SafeForHarmonyPatches != true)
            {
                HostCommands.Respond(request, false, "Harmony patching is not available here, so mods cannot be timed.",
                    error: "unavailable");
                return;
            }
            try
            {
                _session = Begin(request, seconds);
            }
            catch (Exception error)
            {
                HostCommands.Respond(request, false, "Perf could not start: " + error.Message);
            }
        }

        /// <summary>Removes any timing patches and answers a running session. Call at quit, on the game thread.</summary>
        internal static void Shutdown()
        {
            var session = _session;
            if (session != null)
                Abort(session, "The perf session was stopped because the server is shutting down.", "unavailable");
        }

        private static Session Begin(ConsoleSubmission request, int seconds)
        {
            _gameThread = Environment.CurrentManagedThreadId;
            var session = new Session(request, seconds);
            MapAssemblies(session);
            Collect(session, "native mod callbacks", AddNativeCallbacks);
            Collect(session, "RedLoader event handlers", AddRedLoaderHandlers);
            Collect(session, "SonsSdk event handlers", AddSonsSdkHandlers);
            session.Scan = session.Assemblies.ToArray();
            session.Watchdog = new Timer(_ => Host.Post(() => Abort(session,
                "The perf session did not finish in time and was stopped.", "timeout")),
                null, (seconds + WatchdogSlackSeconds) * 1000L, Timeout.Infinite);
            return session;
        }

        private static void Advance(Session session, long now)
        {
            try
            {
                var stop = Stopwatch.GetTimestamp() + StepTicks;
                switch (session.Phase)
                {
                    case Phase.Scanning:
                        while (session.Cursor < session.Scan.Length)
                        {
                            var (assembly, mod) = session.Scan[session.Cursor++];
                            AddMessages(session, assembly, mod);
                            if (Stopwatch.GetTimestamp() >= stop)
                                return;
                        }
                        LinkOverrides(session);
                        session.Cursor = 0;
                        session.Phase = Phase.Patching;
                        return;

                    case Phase.Patching:
                        while (session.Cursor < session.Order.Count && Stopwatch.GetTimestamp() < stop)
                            Patch(session, session.Order[session.Cursor++]);
                        if (session.Cursor < session.Order.Count)
                            return;
                        session.Phase = Phase.Measuring;
                        return;

                    case Phase.Measuring:
                        // The first frame after patching only opens the window, so patch work is never measured.
                        if (session.MeasureStart == 0)
                        {
                            session.MeasureStart = session.LastFrame = now;
                            _slots = session.Slots;
                            _recording = true;
                            return;
                        }
                        CloseFrame(session, now);
                        if (now - session.MeasureStart < session.Seconds * Stopwatch.Frequency)
                            return;
                        _recording = false;
                        _slots = null;
                        session.MeasureEnd = now;
                        session.EndedUtc = DateTimeOffset.UtcNow;
                        session.Cursor = 0;
                        session.Phase = Phase.Unpatching;
                        return;

                    case Phase.Unpatching:
                        while (session.Cursor < session.Order.Count && Stopwatch.GetTimestamp() < stop)
                        {
                            var slot = session.Order[session.Cursor++];
                            if (!slot.Patched)
                                continue;
                            try { session.Harmony.Unpatch(slot.Method, HarmonyPatchType.All, HarmonyId); }
                            catch (Exception) { }
                        }
                        if (session.Cursor < session.Order.Count)
                            return;
                        // A last sweep in case a method could not be unpatched on its own.
                        Unpatch(session.Harmony);
                        session.Watchdog?.Dispose();
                        _session = null;
                        Task.Run(() => Report(session));
                        return;
                }
            }
            catch (Exception error)
            {
                Abort(session, "Perf failed: " + error.Message, "failed");
            }
        }

        private static void CloseFrame(Session session, long now)
        {
            var ms = Ms(now - session.LastFrame);
            session.LastFrame = now;
            if (session.Frames < session.FrameMs.Length)
                session.FrameMs[session.Frames] = (float)ms;
            session.Frames++;
            if (ms > session.MaxFrameMs)
                session.MaxFrameMs = ms;
            foreach (var mod in session.Mods)
            {
                if (mod.FrameTicks > mod.MaxFrameTicks)
                    mod.MaxFrameTicks = mod.FrameTicks;
                mod.FrameTicks = 0;
            }
        }

        private static void Abort(Session session, string human, string error)
        {
            if (_session != session)
                return;
            _recording = false;
            _slots = null;
            _session = null;
            session.Watchdog?.Dispose();
            Unpatch(session.Harmony);
            HostCommands.Respond(session.Request, false, human, error: error);
        }

        private static void Unpatch(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception error)
            {
                // The patches stay, but they return at once while no session is recording.
                RuntimeEntryPoint.LogSource?.LogWarning("Perf timing patches could not all be removed: " + error.Message);
            }
        }

        private static void Patch(Session session, Slot slot)
        {
            try
            {
                session.Harmony.Patch(slot.Method,
                    prefix: slot.Overrides == null ? session.Prefix : session.OverridablePrefix,
                    postfix: session.Postfix);
                slot.Patched = true;
                session.Timed++;
            }
            catch (Exception)
            {
                session.Skipped++;
            }
        }

        // Only the main assembly of each discovered mod is timed. The Loader and its compatibility layers never are.
        private static void MapAssemblies(Session session)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var load in HostMods.LoadResults)
                AddFile(files, load.AssemblyPath);
            foreach (var entry in LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>())
                AddFile(files, entry?.AssemblyPath);

            var owned = new HashSet<Assembly>
            {
                typeof(HostPerf).Assembly, typeof(ConsoleSubmission).Assembly,
                typeof(global::RedLoader.GlobalEvents).Assembly, typeof(global::SonsSdk.SdkEvents).Assembly,
                typeof(BepInEx.Paths).Assembly, typeof(BepInEx.Unity.IL2CPP.BasePlugin).Assembly
            };
            var loaderDirectory = DirectoryOf(typeof(HostPerf).Assembly.Location);
            var mods = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || assembly.IsCollectible || owned.Contains(assembly))
                        continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location) || !files.Contains(Path.GetFileName(location))
                        || string.Equals(DirectoryOf(location), loaderDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var load = HostMods.FindByAssembly(location);
                    if (load == null)
                        continue;
                    var key = !string.IsNullOrEmpty(load.Id) ? load.Id
                        : !string.IsNullOrEmpty(load.Name) ? load.Name : assembly.GetName().Name;
                    if (!mods.TryGetValue(key, out var mod))
                    {
                        mod = new Mod(key, string.IsNullOrEmpty(load.Name) ? key : load.Name, load.Loader);
                        mods.Add(key, mod);
                        session.Mods.Add(mod);
                    }
                    session.Assemblies[assembly] = mod;
                }
                catch (Exception)
                {
                    // An assembly that cannot describe itself is left alone.
                }
            }
        }

        private static void Collect(Session session, string what, Action<Session> collect)
        {
            try
            {
                collect(session);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Perf could not read " + what + ": " + error.Message);
            }
        }

        // Native mods name their frame callbacks in the manifest; they are public static methods on the entry type.
        private static void AddNativeCallbacks(Session session)
        {
            var candidates = ModHost.Candidates;
            foreach (var entry in ModRuntime.State.Mods)
            {
                if (!entry.Active)
                    continue;
                Type type = null;
                foreach (var hook in entry.Hooks)
                {
                    if (Array.IndexOf(NativeHooks, hook.Name) < 0)
                        continue;
                    type ??= EntryType(session, entry, candidates);
                    if (type == null)
                        break;
                    Add(session, type.GetMethod(hook.MethodName, BindingFlags.Public | BindingFlags.Static
                        | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null), false);
                }
            }
        }

        private static Type EntryType(Session session, ModRuntimeEntry entry, IReadOnlyList<ModCandidateSnapshot> candidates)
        {
            var candidate = candidates.FirstOrDefault(item => item != null
                && SamePath(item.ManifestPath, entry.ManifestPath) && SamePath(item.AssemblyPath, entry.AssemblyPath));
            if (string.IsNullOrEmpty(candidate?.MatchedEntrypointTypeName))
                return null;
            foreach (var assembly in session.Assemblies.Keys)
            {
                if (SamePath(assembly.Location, entry.AssemblyPath))
                    return assembly.GetType(candidate.MatchedEntrypointTypeName, false);
            }
            return null;
        }

        // Handlers are timed by patching the subscribed methods themselves, so the compatibility layer is untouched.
        private static void AddRedLoaderHandlers(Session session)
        {
            AddHandlers(session, global::RedLoader.GlobalEvents.OnUpdate.GetSubscribers());
            AddHandlers(session, global::RedLoader.GlobalEvents.OnLateUpdate.GetSubscribers());
            AddHandlers(session, global::RedLoader.GlobalEvents.OnFixedUpdate.GetSubscribers());
            AddHandlers(session, global::RedLoader.GlobalEvents.OnGUI.GetSubscribers());
        }

        private static void AddSonsSdkHandlers(Session session) =>
            AddHandlers(session, global::SonsSdk.SdkEvents.OnInWorldUpdate.GetSubscribers());

        private static void AddHandlers(Session session,
            global::RedLoader.MelonEventBase<global::RedLoader.LemonAction>.MelonEventSubscriber[] subscribers)
        {
            if (subscribers == null)
                return;
            foreach (var subscriber in subscribers)
            {
                if (subscriber?.del != null)
                    Add(session, subscriber.del.Method, false);
            }
        }

        // Unity messages on MonoBehaviours the mod registered with ClassInjector. Abstract bases are included because
        // their messages run for registered subclasses.
        private static void AddMessages(Session session, Assembly assembly, Mod mod)
        {
            foreach (var type in Types(assembly))
            {
                try
                {
                    if (type == null || type.IsInterface || !typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;
                    if (!type.IsAbstract && !type.ContainsGenericParameters && !ClassInjector.IsTypeRegisteredInIl2Cpp(type))
                        continue;
                    foreach (var name in Messages)
                    {
                        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (method != null)
                            Add(session, method, true, mod);
                    }
                }
                catch (Exception)
                {
                    // A type that cannot be inspected cannot be running either.
                }
            }
        }

        private static void Add(Session session, MethodInfo method, bool message, Mod mod = null)
        {
            var type = method?.DeclaringType;
            if (type == null || (mod == null && !session.Assemblies.TryGetValue(type.Assembly, out mod)))
                return;
            var handle = method.MethodHandle.Value;
            if (session.Slots.ContainsKey(handle))
                return;
            if (method.IsAbstract || method.IsGenericMethod || type.IsGenericType || method.GetMethodBody() == null)
            {
                session.Skipped++;
                return;
            }
            if (session.Order.Count >= MaxMethods)
            {
                session.Skipped++;
                session.Capped = true;
                return;
            }
            var slot = new Slot(mod, method, message);
            session.Slots.Add(handle, slot);
            session.Order.Add(slot);
            if (message)
                session.MessageSlots[(type, method.Name)] = slot;
        }

        // An override that calls base.Update() would otherwise be counted twice. Each timed message learns which
        // timed subclasses declare the same message, and skips calls made on their instances.
        private static void LinkOverrides(Session session)
        {
            foreach (var slot in session.Order)
            {
                if (!slot.Message)
                    continue;
                for (var type = slot.Type.BaseType; type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                {
                    if (session.MessageSlots.TryGetValue((type, slot.Method.Name), out var baseSlot))
                        baseSlot.AddOverride(slot.Type);
                }
            }
        }

        private static void Before(MethodBase __originalMethod, out Probe __state)
        {
            __state = default;
            if (!_recording || Environment.CurrentManagedThreadId != _gameThread)
                return;
            var slots = _slots;
            if (slots != null && slots.TryGetValue(__originalMethod.MethodHandle.Value, out var slot))
                __state = new Probe(slot, Stopwatch.GetTimestamp());
        }

        private static void BeforeOverridable(object __instance, MethodBase __originalMethod, out Probe __state)
        {
            __state = default;
            if (!_recording || Environment.CurrentManagedThreadId != _gameThread)
                return;
            var slots = _slots;
            if (slots == null || !slots.TryGetValue(__originalMethod.MethodHandle.Value, out var slot))
                return;
            for (var type = __instance?.GetType(); type != null && type != slot.Type; type = type.BaseType)
            {
                if (Array.IndexOf(slot.Overrides, type) >= 0)
                    return;
            }
            __state = new Probe(slot, Stopwatch.GetTimestamp());
        }

        // A method that throws skips the postfix and loses that one sample; nothing else depends on it.
        private static void After(Probe __state) => __state.Slot?.Add(Stopwatch.GetTimestamp() - __state.Started);

        private static void Report(Session session)
        {
            try
            {
                var body = BuildReport(session, out var human);
                var stored = body.DeepClone().AsObject();
                stored["at"] = HostOutput.Utc(session.EndedUtc);
                lock (ImpactSync)
                    _lastImpact = stored;
                HostCommands.Respond(session.Request, true, human, body);
            }
            catch (Exception error)
            {
                HostCommands.Respond(session.Request, false, "Perf failed: " + error.Message);
            }
        }

        private static JsonObject BuildReport(Session session, out string human)
        {
            var frames = session.Frames;
            var sampled = new float[Math.Min(frames, session.FrameMs.Length)];
            Array.Copy(session.FrameMs, sampled, sampled.Length);
            Array.Sort(sampled);
            var elapsed = session.MeasureEnd - session.MeasureStart;
            var average = frames == 0 ? 0 : Ms(elapsed) / frames;
            var hooks = CountHooks(session);
            var timed = session.Order.Where(slot => slot.Patched).ToLookup(slot => slot.Mod);

            var rows = new List<(Mod Mod, double MsPerFrame, JsonObject Json)>();
            foreach (var mod in session.Mods)
            {
                var slots = timed[mod].ToArray();
                hooks.TryGetValue(mod, out var hookCount);
                if (slots.Length == 0 && hookCount == 0)
                    continue;
                long ticks = 0, calls = 0;
                foreach (var slot in slots)
                {
                    ticks += slot.Ticks;
                    calls += slot.Calls;
                }
                var top = new JsonArray();
                foreach (var slot in slots.Where(slot => slot.Calls > 0).OrderByDescending(slot => slot.Ticks).Take(TopMethods))
                    top.Add((JsonNode)new JsonObject
                    {
                        ["method"] = slot.Label,
                        ["msPerFrame"] = Math.Round(PerFrame(slot.Ticks, frames), 3)
                    });
                var msPerFrame = PerFrame(ticks, frames);
                rows.Add((mod, msPerFrame, new JsonObject
                {
                    ["id"] = mod.Id,
                    ["name"] = mod.Name,
                    ["loader"] = mod.Loader,
                    ["msPerFrame"] = Math.Round(msPerFrame, 3),
                    ["maxFrameMs"] = Math.Round(Ms(mod.MaxFrameTicks), 3),
                    ["calls"] = calls,
                    ["share"] = elapsed > 0 ? Math.Round((double)ticks / elapsed, 4) : 0,
                    ["methods"] = slots.Length,
                    ["hooks"] = hookCount,
                    ["top"] = top
                }));
            }
            rows.Sort((a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));
            var mods = new JsonArray();
            foreach (var row in rows)
                mods.Add((JsonNode)row.Json);

            var p95 = Percentile(sampled, 0.95);
            var text = new StringBuilder(Invariant(
                $"Perf over {Ms(elapsed) / 1000:0.0} s, {frames} frames (avg {average:0.00} ms, p95 {p95:0.00} ms, max {session.MaxFrameMs:0.00} ms)."));
            var busiest = rows.Where(row => row.MsPerFrame > 0).Take(SummaryMods).ToArray();
            text.Append(busiest.Length == 0 ? " No timed mod method ran."
                : " " + string.Join(", ", busiest.Select(row => Invariant($"{row.Mod.Name} {row.MsPerFrame:0.000} ms/frame"))) + ".");
            text.Append(Invariant($" {session.Timed} methods timed, {session.Skipped} skipped"));
            text.Append(session.Capped ? Invariant($" (limit {MaxMethods}).") : ".");
            human = text.ToString();

            return new JsonObject
            {
                ["windowSeconds"] = Math.Round(Ms(elapsed) / 1000, 2),
                ["frames"] = frames,
                ["frameMs"] = FrameMs(average, sampled, session.MaxFrameMs),
                ["patched"] = session.Timed,
                ["skipped"] = session.Skipped,
                ["mods"] = mods,
                ["notCovered"] = NotCovered
            };
        }

        // Game methods each mod patches with its own Harmony patches, grouped by the assembly declaring the patch.
        private static Dictionary<Mod, int> CountHooks(Session session)
        {
            var patched = new Dictionary<Mod, HashSet<MethodBase>>();
            foreach (var original in HarmonyLib.Harmony.GetAllPatchedMethods().ToArray())
            {
                try
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(original);
                    if (info == null)
                        continue;
                    foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers)
                        .Concat(info.Finalizers).Concat(info.ILManipulators))
                    {
                        if (patch == null || patch.owner == HarmonyId)
                            continue;
                        var assembly = patch.PatchMethod?.DeclaringType?.Assembly;
                        if (assembly == null || !session.Assemblies.TryGetValue(assembly, out var mod))
                            continue;
                        if (!patched.TryGetValue(mod, out var methods))
                            patched.Add(mod, methods = new HashSet<MethodBase>());
                        methods.Add(original);
                    }
                }
                catch (Exception)
                {
                    // A patch whose method no longer resolves is not counted.
                }
            }
            return patched.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        }

        private static JsonObject FrameMs(double average, float[] sorted, double max) => new JsonObject
        {
            ["avg"] = Math.Round(average, 2),
            ["p95"] = Math.Round(Percentile(sorted, 0.95), 2),
            ["max"] = Math.Round(max, 2)
        };

        // Nearest-rank percentile of an ascending array.
        private static double Percentile(float[] sorted, double fraction) => sorted.Length == 0 ? 0
            : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static double PerFrame(long ticks, int frames) => frames == 0 ? 0 : Ms(ticks) / frames;

        private static bool TryReadSeconds(string arguments, out int seconds)
        {
            seconds = DefaultSeconds;
            var tokens = (arguments ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return true;
            return tokens.Length == 2 && tokens[0] == "--seconds"
                && int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                && seconds is >= 1 and <= MaxSeconds;
        }

        private static Type[] Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { return error.Types; }
            catch (Exception) { return Array.Empty<Type>(); }
        }

        private static void AddFile(HashSet<string> files, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try { files.Add(Path.GetFileName(path)); }
            catch (ArgumentException) { }
        }

        private static string DirectoryOf(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch (Exception) { return null; }
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        private static string Label(MethodInfo method)
        {
            var label = (method.DeclaringType.FullName ?? method.DeclaringType.Name) + "." + method.Name;
            return label.Length > MaxLabelLength ? label.Substring(0, MaxLabelLength) : label;
        }

        private readonly struct Probe
        {
            internal Probe(Slot slot, long started)
            {
                Slot = slot;
                Started = started;
            }

            internal readonly Slot Slot;
            internal readonly long Started;
        }

        private sealed class Mod
        {
            internal Mod(string id, string name, string loader)
            {
                Id = id;
                Name = name;
                Loader = loader;
            }

            internal readonly string Id;
            internal readonly string Name;
            internal readonly string Loader;
            internal long FrameTicks;
            internal long MaxFrameTicks;
        }

        private sealed class Slot
        {
            internal Slot(Mod mod, MethodInfo method, bool message)
            {
                Mod = mod;
                Method = method;
                Type = method.DeclaringType;
                Message = message;
                Label = HostPerf.Label(method);
            }

            internal readonly Mod Mod;
            internal readonly MethodInfo Method;
            internal readonly Type Type;
            internal readonly bool Message;
            internal readonly string Label;
            internal Type[] Overrides;
            internal bool Patched;
            internal long Ticks;
            internal long Calls;

            internal void Add(long ticks)
            {
                Ticks += ticks;
                Calls++;
                Mod.FrameTicks += ticks;
            }

            internal void AddOverride(Type type)
            {
                var overrides = Overrides ?? Array.Empty<Type>();
                Array.Resize(ref overrides, overrides.Length + 1);
                overrides[^1] = type;
                Overrides = overrides;
            }
        }

        private sealed class Session
        {
            internal Session(ConsoleSubmission request, int seconds)
            {
                Request = request;
                Seconds = seconds;
                FrameMs = new float[seconds * 1000];
            }

            internal readonly ConsoleSubmission Request;
            internal readonly int Seconds;
            internal readonly float[] FrameMs;
            internal readonly HarmonyLib.Harmony Harmony = new HarmonyLib.Harmony(HarmonyId);
            internal readonly HarmonyMethod Prefix = new HarmonyMethod(typeof(HostPerf), nameof(Before)) { priority = Priority.First };
            internal readonly HarmonyMethod OverridablePrefix =
                new HarmonyMethod(typeof(HostPerf), nameof(BeforeOverridable)) { priority = Priority.First };
            internal readonly HarmonyMethod Postfix = new HarmonyMethod(typeof(HostPerf), nameof(After)) { priority = Priority.Last };
            internal readonly Dictionary<Assembly, Mod> Assemblies = new Dictionary<Assembly, Mod>();
            internal readonly List<Mod> Mods = new List<Mod>();
            internal readonly Dictionary<IntPtr, Slot> Slots = new Dictionary<IntPtr, Slot>();
            internal readonly Dictionary<(Type, string), Slot> MessageSlots = new Dictionary<(Type, string), Slot>();
            internal readonly List<Slot> Order = new List<Slot>();
            internal KeyValuePair<Assembly, Mod>[] Scan = Array.Empty<KeyValuePair<Assembly, Mod>>();
            internal Phase Phase;
            internal int Cursor;
            internal int Timed;
            internal int Skipped;
            internal bool Capped;
            internal int Frames;
            internal double MaxFrameMs;
            internal long MeasureStart;
            internal long MeasureEnd;
            internal long LastFrame;
            internal DateTimeOffset EndedUtc;
            internal Timer Watchdog;
        }
    }
}
