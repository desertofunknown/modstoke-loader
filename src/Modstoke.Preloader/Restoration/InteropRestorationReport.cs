// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsmResolver.DotNet;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed class InteropRestorationReport
    {
        private readonly List<InteropRestorationEntry> methods = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        internal bool Started { get; set; }
        internal bool Completed { get; set; }

        internal InteropRestorationEntry Add(MethodDefinition method)
        {
            var entry = new InteropRestorationEntry
            {
                Assembly = Describe(() => method.DeclaringModule.Assembly.Name),
                Method = Describe(() => method.FullName),
                Token = method.MetadataToken.ToInt32(),
                Nested = method.DeclaringType.DeclaringType != null,
                Outcome = "pending"
            };
            methods.Add(entry);
            return entry;
        }

        internal void Write(InteropGenerationOptions options, Exception generationError)
        {
            var path = options.RestorationReportPath ?? Path.Combine(options.OutputDir, "openacai-restoration-report.json");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var counts = methods.CountBy(method => method.Outcome).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                var report = new
                {
                    Schema = 1,
                    GeneratedAtUtc = DateTimeOffset.UtcNow,
                    options.InputHash,
                    options.UnityVersion,
                    GeneratorBuild = typeof(InteropRestorationReport).Assembly.ManifestModule.ModuleVersionId,
                    Scope = "Unity base-library method restoration, including nested types; not all game APIs or runtime callability.",
                    RestorationStarted = Started,
                    RestorationCompleted = Completed,
                    GenerationCompleted = generationError == null,
                    GenerationError = generationError == null ? null : InteropGenerationDiagnostics.Describe(generationError),
                    ConsideredMethods = methods.Count,
                    NestedMethods = methods.Count(method => method.Nested),
                    Counts = counts,
                    Methods = methods
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, report, JsonOptions);
                File.Move(temporary, path, overwrite: true);
                var restorationStatus = !Started ? "restoration not started" :
                    Completed ? "restoration completed" : "restoration incomplete";
                var generationStatus = generationError == null ? "generation completed" : "generation failed";
                var outcomes = counts.Count == 0 ? "" : "; " +
                    string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value));
                InteropGenerationDiagnostics.Log(generationError == null ? LogLevel.Info : LogLevel.Warning,
                    "Unity method restoration report: " + path + " (" + generationStatus + "; " +
                    restorationStatus + "; " + methods.Count + " considered" + outcomes + ").");
            }
            catch (Exception error)
            {
                // Reporting must not turn usable output into a failed conversion or hide its original error.
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not write Unity restoration report: " + path, error);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error)
                {
                    InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not remove restoration report temporary file: " + temporary, error);
                }
            }
        }

        internal static string Describe(Func<string> value)
        {
            try { return value() ?? ""; }
            catch { return "<unavailable metadata>"; }
        }
    }

    internal sealed class InteropRestorationEntry
    {
        public string Assembly { get; internal set; }
        public string Method { get; internal set; }
        public int Token { get; internal set; }
        public bool Nested { get; internal set; }
        public string Outcome { get; internal set; }
        public string Representation { get; internal set; }
        public string Reason { get; internal set; }
        public string Detail { get; internal set; }
        public InteropTranslationFailure Failure { get; internal set; }

        internal void Set(string outcome, string reason = null, string detail = null)
        {
            Outcome = outcome;
            Reason = reason;
            Detail = detail;
        }
    }
}
