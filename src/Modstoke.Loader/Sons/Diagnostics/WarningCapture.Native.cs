// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class WarningCapture
    {
        [ThreadStatic] private static CallContext _context;
        private static NativeHook<InjectedQuery> _injectedQuery;
        private static NativeHook<PublicQuery> _publicQuery;
        private static NativeHook<ConvexSetter> _convexSetter;
        private static NativeHook<MeshSetter> _meshSetter;

        private struct CallContext
        {
            internal string Operation;
            internal nint Receiver, Point, Mesh;
            internal byte Convex;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InjectedQuery(nint collider, nint point, nint result, nint method);
        // The public wrapper uses Win64's hidden Vector3 result buffer as its first argument.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint PublicQuery(nint result, nint collider, nint point, nint method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ConvexSetter(nint collider, byte convex, nint method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MeshSetter(nint collider, nint mesh, nint method);

        private static void InstallNativeContexts()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || !GameBuild.IsRecognized)
            {
                Enqueue(new { kind = "native-coverage", status = "unavailable", reason = "Unsupported game build or platform" });
                return;
            }
            TryInstall("Collider.ClosestPoint_Injected", () => {
                _injectedQuery = new NativeHook<InjectedQuery>(typeof(Collider), "ClosestPoint_Injected", typeof(void),
                    new[] { typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType() }, 0x4595C30, 112,
                    "B4DCE98029CED24E27EDED954FF0C101DC2E21A576E48C3A9A2BBB4F266F0560", InvokeInjectedQuery);
                _injectedQuery.Install();
            });
            TryInstall("Collider.ClosestPoint", () => {
                _publicQuery = new NativeHook<PublicQuery>(typeof(Collider), "ClosestPoint", typeof(Vector3),
                    new[] { typeof(Vector3) }, 0x4595620, 128,
                    "D877AB2C86823589FCFCDA3508F89DE745C9CBC21F559DAECFA42CB687C7E48E", InvokePublicQuery);
                _publicQuery.Install();
            });
            TryInstall("MeshCollider.set_convex", () => {
                _convexSetter = new NativeHook<ConvexSetter>(typeof(MeshCollider), "set_convex", typeof(void),
                    new[] { typeof(bool) }, 0x4596620, 96,
                    "4A6D264CD7CC4BE5E7C6ACCD2E55472260A807FCB1A696E9A1E58154C6F6A194", InvokeConvexSetter);
                _convexSetter.Install();
            });
            TryInstall("MeshCollider.set_sharedMesh", () => {
                _meshSetter = new NativeHook<MeshSetter>(typeof(MeshCollider), "set_sharedMesh", typeof(void),
                    new[] { typeof(Mesh) }, 0x4596570, 96,
                    "5FACB2DD16D5834A16C6BE6A87F6FDC13C9CC90012AE6CAFA4BDA361F03ECC53", InvokeMeshSetter);
                _meshSetter.Install();
            });
            Enqueue(new { kind = "native-coverage-limit", note = "Physics.Query_ClosestPoint_Injected, inlined calls and engine deserialization are not hooked. Context identifies an active synchronous boundary, not asset provenance." });
        }

        private static void TryInstall(string name, Action install)
        {
            try { install(); Enqueue(new { kind = "native-hook", name, status = "installed" }); }
            catch (Exception error) { Enqueue(new { kind = "native-hook", name, status = "unavailable", error = error.ToString() }); }
        }

        private static void InvokeInjectedQuery(nint collider, nint point, nint result, nint method)
        {
            if (Volatile.Read(ref _active) == 0) { _injectedQuery.Original(collider, point, result, method); return; }
            var previous = _context;
            _context = new CallContext { Operation = "Collider.ClosestPoint_Injected", Receiver = collider, Point = point };
            try { _injectedQuery.Original(collider, point, result, method); }
            finally { _context = previous; }
        }

        private static nint InvokePublicQuery(nint result, nint collider, nint point, nint method)
        {
            if (Volatile.Read(ref _active) == 0) return _publicQuery.Original(result, collider, point, method);
            var previous = _context;
            _context = new CallContext { Operation = "Collider.ClosestPoint", Receiver = collider, Point = point };
            try { return _publicQuery.Original(result, collider, point, method); }
            finally { _context = previous; }
        }

        private static void InvokeConvexSetter(nint collider, byte convex, nint method)
        {
            if (Volatile.Read(ref _active) == 0) { _convexSetter.Original(collider, convex, method); return; }
            var previous = _context;
            _context = new CallContext { Operation = "MeshCollider.set_convex", Receiver = collider, Convex = convex };
            try { _convexSetter.Original(collider, convex, method); }
            finally { _context = previous; }
        }

        private static void InvokeMeshSetter(nint collider, nint mesh, nint method)
        {
            if (Volatile.Read(ref _active) == 0) { _meshSetter.Original(collider, mesh, method); return; }
            var previous = _context;
            _context = new CallContext { Operation = "MeshCollider.set_sharedMesh", Receiver = collider, Mesh = mesh };
            try { _meshSetter.Original(collider, mesh, method); }
            finally { _context = previous; }
        }

        private static object CaptureCurrentContext(string message, bool onMainThread)
        {
            var context = _context;
            bool query = message.IndexOf("ClosestPoint can only", StringComparison.OrdinalIgnoreCase) >= 0;
            bool cooking = message.IndexOf("create a Convex Mesh", StringComparison.OrdinalIgnoreCase) >= 0;
            bool matching = (query && context.Point != 0) || (cooking && context.Operation != null
                && context.Operation.StartsWith("MeshCollider.", StringComparison.Ordinal));
            if (!matching || context.Receiver == 0)
                return new { available = false, reason = "No matching synchronous instrumented call" };
            var snapshot = new Dictionary<string, object> {
                ["available"] = true, ["operation"] = context.Operation,
                ["receiver"] = "0x" + context.Receiver.ToInt64().ToString("X"),
                ["objectInspection"] = onMainThread ? "attempted" : "skipped: Unity main thread not established or different thread"
            };
            if (context.Operation == "MeshCollider.set_convex") snapshot["requestedConvexByte"] = context.Convex;
            if (context.Operation == "MeshCollider.set_sharedMesh") snapshot["requestedMesh"] = "0x" + context.Mesh.ToInt64().ToString("X");
            try
            {
                if (query)
                {
                    byte[] point = ReadMemory(context.Point, 12);
                    snapshot["queryPoint"] = new[] { BitConverter.ToSingle(point, 0), BitConverter.ToSingle(point, 4), BitConverter.ToSingle(point, 8) };
                }
                if (!onMainThread) return snapshot;
                var collider = new Collider(context.Receiver);
                if (!collider) { snapshot["objectInspection"] = "Unity object destroyed"; return snapshot; }
                snapshot["instanceId"] = collider.GetInstanceID();
                snapshot["type"] = collider.GetIl2CppType().FullName;
                snapshot["enabled"] = collider.enabled;
                snapshot["trigger"] = collider.isTrigger;
                snapshot["scene"] = collider.gameObject.scene.name;
                var names = new List<string>();
                var transform = collider.transform;
                snapshot["position"] = Coordinates(transform.position);
                snapshot["lossyScale"] = Coordinates(transform.lossyScale);
                for (int i = 0; transform && i < 64; i++, transform = transform.parent) names.Add(transform.name);
                names.Reverse();
                snapshot["hierarchy"] = string.Join("/", names);
                var body = collider.attachedRigidbody;
                if (body) snapshot["rigidbodyKinematic"] = body.isKinematic;
                var meshCollider = collider.TryCast<MeshCollider>();
                if (meshCollider)
                {
                    snapshot["observedConvex"] = meshCollider.convex;
                    snapshot["cookingOptions"] = meshCollider.cookingOptions.ToString();
                    snapshot["observedMesh"] = DescribeMesh(meshCollider.sharedMesh);
                    if (context.Operation == "MeshCollider.set_sharedMesh" && context.Mesh != 0)
                        snapshot["requestedMeshDetails"] = DescribeMesh(new Mesh(context.Mesh));
                }
                snapshot["objectInspection"] = "captured during warning; not pre-call state or asset provenance";
            }
            catch (Exception error) { snapshot["inspectionError"] = error.Message; }
            return snapshot;
        }

        private static float[] Coordinates(Vector3 value) => new[] { value.x, value.y, value.z };
        private static object DescribeMesh(Mesh mesh) => !mesh ? null : new {
            name = mesh.name, instanceId = mesh.GetInstanceID(), vertices = mesh.vertexCount,
            submeshes = mesh.subMeshCount, readable = mesh.isReadable
        };

        private sealed class NativeHook<T> where T : Delegate
        {
            private readonly nint _entry;
            private readonly int _length;
            private readonly string _hash;
            private readonly T _callback;
            private INativeDetour _detour;
            internal T Original;

            internal NativeHook(Type type, string name, Type returns, Type[] arguments, int rva, int length, string hash, T callback)
            {
                var method = AccessTools.Method(type, name, arguments);
                if (method == null || method.IsStatic || method.ReturnType != returns)
                    throw new MissingMethodException(type.Name, name);
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                    throw new InvalidOperationException("Missing IL2CPP method information.");
                _entry = (nint)BitConverter.ToInt64(ReadMemory(methodInfo, IntPtr.Size), 0);
                nint module = GetModuleHandle("GameAssembly.dll");
                if (module == 0 || _entry != module + rva)
                    throw new InvalidOperationException("Native entry does not match the verified game build.");
                _length = length;
                _hash = hash;
                _callback = callback;
            }

            internal void Install()
            {
                if (Convert.ToHexString(SHA256.HashData(ReadMemory(_entry, _length))) != _hash)
                    throw new InvalidOperationException("Native code changed or is already hooked; leaving it untouched.");
                _detour = INativeDetour.Create(_entry, _callback);
                Volatile.Write(ref Original, _detour.GenerateTrampoline<T>());
                _detour.Apply();
            }
        }

        private static byte[] ReadMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadProcessMemory(new nint(-1), address, bytes, (nuint)length, out nuint read) || read != (nuint)length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Diagnostic native memory read failed.");
            return bytes;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    }
}
