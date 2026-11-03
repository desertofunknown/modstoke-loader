// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Modstoke.Loader
{
    internal static class LoaderStatePersistence
    {
        private static readonly object Sync = new object();
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private static WriteRequest _pending;
        private static WriteRequest _failed;
        private static Task _writer = Task.CompletedTask;
        private static bool _running;
        private static long _completedWrites;
        private static long _failedWrites;
        private static string _lastError;
        private static TimeSpan _writeDuration;

        internal static void Queue(object state, string primaryPath, string mirrorPath)
        {
            // Evaluate every getter and enumerate every collection on the owner thread.
            // Only detached read-only containers and scalar values reach the writer.
            var request = new WriteRequest(Capture(state, 0), Path.GetFullPath(primaryPath), Path.GetFullPath(mirrorPath));
            lock (Sync)
            {
                _pending = request;
                StartWriterLocked();
            }
        }

        internal static void Flush()
        {
            var retried = false;
            while (true)
            {
                Task writer;
                lock (Sync)
                {
                    if (!_running)
                    {
                        if (_failed == null || retried)
                            return;
                        // A failed final publication remains available for one flush retry.
                        _pending = _failed;
                        retried = true;
                        StartWriterLocked();
                    }
                    writer = _writer;
                }
                writer.GetAwaiter().GetResult();
            }
        }

        internal static (long Completed, long Failed, string Error, TimeSpan Duration) TakeCompletion()
        {
            lock (Sync)
            {
                var result = (_completedWrites, _failedWrites, _lastError, _writeDuration);
                _completedWrites = 0;
                _failedWrites = 0;
                _lastError = null;
                _writeDuration = TimeSpan.Zero;
                return result;
            }
        }

        private static void StartWriterLocked()
        {
            if (_running)
                return;
            _writer = Task.Run(Drain);
            _running = true;
        }

        private static void Drain()
        {
            while (true)
            {
                WriteRequest request;
                lock (Sync)
                {
                    request = _pending;
                    _pending = null;
                    if (request == null)
                    {
                        _running = false;
                        return;
                    }
                }

                var stopwatch = Stopwatch.StartNew();
                string error = null;
                try
                {
                    var json = request.Contents ??= JsonSerializer.SerializeToUtf8Bytes(request.State, JsonOptions);
                    // Each mirror is attempted even if the other is unavailable.
                    try
                    {
                        if (!request.PrimaryWritten)
                        {
                            WriteMirror(request.PrimaryPath, json);
                            request.PrimaryWritten = true;
                        }
                    }
                    catch (Exception ex) { error = request.PrimaryPath + ": " + ex.Message; }
                    try
                    {
                        if (!request.MirrorWritten)
                        {
                            if (!request.PrimaryWritten || !string.Equals(request.PrimaryPath, request.MirrorPath,
                                StringComparison.OrdinalIgnoreCase))
                                WriteMirror(request.MirrorPath, json);
                            request.MirrorWritten = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        error = (error == null ? "" : error + "; ") + request.MirrorPath + ": " + ex.Message;
                    }
                }
                catch (Exception ex)
                {
                    error = "Serialization: " + ex.Message;
                }

                lock (Sync)
                {
                    _writeDuration += stopwatch.Elapsed;
                    if (error == null)
                    {
                        _completedWrites++;
                        _failed = null;
                    }
                    else
                    {
                        _failedWrites++;
                        _lastError = error;
                        _failed = request;
                    }
                }
            }
        }

        private static void WriteMirror(string path, byte[] contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception) { /* Preserve the original write failure. */ }
            }
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                MaxDepth = 64
            };
            options.MakeReadOnly();
            return options;
        }

        private static object Capture(object value, int depth)
        {
            if (value == null)
                return null;
            if (depth > JsonOptions.MaxDepth)
                throw new InvalidOperationException("Loader state exceeds the JSON depth limit.");

            var type = value.GetType();
            if (Type.GetTypeCode(type) != TypeCode.Object || value is Guid || value is DateTimeOffset
                || value is TimeSpan || value is Uri || value is Version)
                return value;
            if (value is JsonElement element)
                return element.Clone();

            var contract = JsonOptions.GetTypeInfo(type);
            if (contract.Kind == JsonTypeInfoKind.Dictionary && value is IDictionary dictionary)
            {
                var entries = new Dictionary<string, object>(dictionary.Count, StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                        throw new InvalidOperationException("Loader state dictionary keys must be strings.");
                    entries.Add(key, Capture(entry.Value, depth + 1));
                }
                return new ReadOnlyDictionary<string, object>(entries);
            }
            if (contract.Kind == JsonTypeInfoKind.Enumerable && value is IEnumerable sequence)
            {
                var entries = new List<object>();
                foreach (var entry in sequence)
                    entries.Add(Capture(entry, depth + 1));
                return entries.AsReadOnly();
            }
            if (contract.Kind != JsonTypeInfoKind.Object)
                throw new InvalidOperationException("Unsupported loader state value: " + type.FullName);

            var properties = new Dictionary<string, object>(contract.Properties.Count, StringComparer.Ordinal);
            foreach (var property in contract.Properties)
            {
                if (property.Get == null)
                    continue;
                var propertyValue = property.Get(value);
                if (property.ShouldSerialize == null || property.ShouldSerialize(value, propertyValue))
                    properties.Add(property.Name, Capture(propertyValue, depth + 1));
            }
            return new ReadOnlyDictionary<string, object>(properties);
        }

        private sealed record WriteRequest(object State, string PrimaryPath, string MirrorPath)
        {
            internal byte[] Contents;
            internal bool PrimaryWritten;
            internal bool MirrorWritten;
        }
    }
}
