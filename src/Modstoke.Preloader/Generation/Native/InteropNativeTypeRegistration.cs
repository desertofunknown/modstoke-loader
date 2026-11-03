// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    internal static class InteropNativeTypeRegistration
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int BootstrapWindow(uint abi);

        internal sealed record Publication(string Status, string Detail);

        internal sealed record Report(Publication NavMesh, Publication Video, Publication EventDebugger, Publication StyleEnum,
            Publication AudioType, Publication MovieTexture, Publication Www)
        {
            internal static readonly Report NotAttempted = new(
                new Publication("not-attempted", "Native NavMesh publication has not been attempted."),
                new Publication("not-attempted", "Native video publication has not been attempted."),
                new Publication("not-attempted", "Native EventDebugger publication has not been attempted."),
                new Publication("not-attempted", "Native style enum publication has not been attempted."),
                new Publication("not-attempted", "Native audio type publication has not been attempted."),
                new Publication("not-attempted", "Native MovieTexture publication has not been attempted."),
                new Publication("not-attempted", "Native WWW publication has not been attempted."));

            internal string Describe() => $"Native type publication: NavMesh {NavMesh.Status}; video {Video.Status}; EventDebugger {EventDebugger.Status}; style enum {StyleEnum.Status}; audio type {AudioType.Status}; MovieTexture {MovieTexture.Status}; WWW {Www.Status}.";
        }

        internal static Report TryPublish(string gameAssemblyPath)
        {
            // Publish the enum before another prepared graph or wrapper can cache its native class pointer.
            var audioType = Publish(gameAssemblyPath, "NativeAudioTypeRegistration", "audio type");
            var movieTexture = Publish(gameAssemblyPath, "NativeMovieTextureRegistration", "MovieTexture");
            var www = Publish(gameAssemblyPath, "NativeWwwRegistration", "WWW");
            return new Report(
                Publish(gameAssemblyPath, "NativeNavMeshRegistration", "NavMesh"),
                Publish(gameAssemblyPath, "NativeVideoRegistration", "video"),
                Publish(gameAssemblyPath, "NativeEventDebuggerRegistration", "EventDebugger"),
                Publish(gameAssemblyPath, "NativeStyleEnumRegistration", "style enum"), audioType, movieTexture, www);
        }

        private static Publication Publish(string gameAssemblyPath, string registrationName, string label)
        {
            try
            {
                // Older runtime/native-host pairs remain supported without early publication.
                var type = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection." + registrationName);
                var method = type?.GetMethod("PublishDuringBootstrap", BindingFlags.Public | BindingFlags.Static,
                    binder: null, types: new[] { typeof(IntPtr) }, modifiers: null);
                if (method == null)
                    return new Publication("unavailable", $"The loaded runtime has no native {label} publication contract.");
                if (method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                    throw new NotSupportedException($"The native {label} publication contract has an unsupported signature.");
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new NotSupportedException("Early native type publication requires Windows x64.");

                var directory = Path.GetDirectoryName(Path.GetFullPath(gameAssemblyPath));
                IntPtr query = IntPtr.Zero;
                using (var process = Process.GetCurrentProcess())
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        using (module)
                        {
                            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(module.FileName)), directory,
                                StringComparison.OrdinalIgnoreCase) ||
                                !NativeLibrary.TryGetExport(module.BaseAddress, "OpenACAI_IsIl2CppBootstrapWindow", out var candidate))
                                continue;
                            if (query != IntPtr.Zero)
                                throw new NotSupportedException("More than one native bootstrap host is loaded.");
                            query = candidate;
                        }
                    }
                }

                if (query == IntPtr.Zero || Marshal.GetDelegateForFunctionPointer<BootstrapWindow>(query)(1) != 1)
                    throw new InvalidOperationException("The native host does not expose an active initial bootstrap interval.");
                InteropClassInitResolver.TryInstall(gameAssemblyPath);
                method.Invoke(null, new object[] { query });
                InteropGenerationDiagnostics.Log(LogLevel.Info,
                    $"Published the native {label} declaration graph before MonoManager initialization.");
                return new Publication("published", $"Native {label} publication completed during the initial bootstrap interval.");
            }
            catch (Exception error)
            {
                if (error is TargetInvocationException invocation && invocation.InnerException != null)
                    error = invocation.InnerException;
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    $"Early {label} publication did not complete; no late engine-cache repair was attempted.", error);
                return new Publication("failed", InteropGenerationDiagnostics.Message(error));
            }
        }
    }
}
