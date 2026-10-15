// SPDX-License-Identifier: GPL-3.0-or-later
// Scan semantics and list locking adapted from Il2CppInterop Pass16ScanMethodRefs and CollectionEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors.
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.ExceptionServices;
using Il2CppInterop.Common.XrefScans;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropMethodScanner
    {
        internal static unsafe void Scan(InteropRewriteContext context, InteropGenerationOptions options, InteropScanState scanState)
        {
            if (string.IsNullOrEmpty(options.GameAssemblyPath))
            {
                context.ScanState.HasObfuscatedMethods = false;
                return;
            }

            MemoryMappedFile mapping = null;
            MemoryMappedViewAccessor view = null;
            byte* pointer = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                mapping = MemoryMappedFile.CreateFromFile(options.GameAssemblyPath, FileMode.Open, null, 0,
                    MemoryMappedFileAccess.Read);
                view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                if (pointer == null)
                    throw new InvalidDataException("The native assembly mapping returned a null pointer.");

                var baseAddress = (nint)pointer + checked((nint)view.PointerOffset);
                context.HasGcWbarrierFieldWrite = ContainsGcWriteBarrier(baseAddress, view.Capacity);
                if (context.ScanState.HasObfuscatedMethods)
                    ScanMethods(context, options, scanState, baseAddress, view.Capacity);
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }

            // ForAll joins its synchronous workers before ScanMethods returns or throws.
            if (pointer != null)
                InteropConversion.Cleanup(ref failure, "release interop native-file pointer",
                    () => view.SafeMemoryMappedViewHandle.ReleasePointer());
            InteropConversion.Cleanup(ref failure, "dispose interop native-file view", () => view?.Dispose());
            InteropConversion.Cleanup(ref failure, "dispose interop native-file mapping", () => mapping?.Dispose());
            failure?.Throw();
        }

        private static void ScanMethods(InteropRewriteContext context, InteropGenerationOptions options,
            InteropScanState scanState, nint baseAddress, long fileLength)
        {
            var methodStarts = context.MethodStartAddresses;
            methodStarts.Sort();
            var methods = context.Assemblies.SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
                .Where(method => method.FileOffset != 0)
                .Select(method => (Method: method, Length: GetScanLength(method.FileOffset, fileLength, methodStarts)))
                .ToArray();
            var callers = new ConcurrentDictionary<long, List<XrefInstance>>();
            var callees = new ConcurrentDictionary<long, List<nint>>();
            InteropMetadataScanner metadataScanner = null;

            if (methods.Length != 0)
            {
                if (!options.NoXrefCache)
                    metadataScanner = new InteropMetadataScanner(context, baseAddress, fileLength);

                methods.AsParallel().ForAll(entry =>
                {
                    var method = entry.Method;
                    if (metadataScanner != null)
                    {
                        var metadata = metadataScanner.Read(method);
                        method.MetadataInitFlagRva = metadata.FlagRva;
                        method.MetadataInitTokenRva = metadata.TokenRva;
                    }

                    var relativeBase = baseAddress + (nint)method.FileOffset - (nint)method.Rva;
                    var decoder = InteropXrefScanner.CreateDecoder(baseAddress + (nint)method.FileOffset, entry.Length);
                    foreach (var reference in InteropXrefScanner.Scan(decoder))
                    {
                        var relative = new XrefInstance(reference.Type, reference.Pointer - relativeBase, reference.FoundAt - relativeBase);
                        if (relative.Type == XrefType.Method)
                        {
                            AddLocked(callers.GetOrAdd(relative.Pointer.ToInt64(), _ => new List<XrefInstance>()),
                                new XrefInstance(XrefType.Method, (nint)method.Rva, relative.FoundAt));
                            AddLocked(callees.GetOrAdd(method.Rva, _ => new List<nint>()), relative.Pointer);
                        }
                        if (!options.NoXrefCache)
                            method.XrefScanResults.Add(relative);
                    }
                });
            }

            var liveMethods = new HashSet<long>();
            var pending = new Stack<long>();
            foreach (var entry in methods)
            {
                var method = entry.Method;
                if (method.OriginalMethod.Name.IsObfuscated(options) && !method.OriginalMethod.IsVirtual)
                    continue;
                pending.Push(method.Rva);
                while (pending.TryPop(out var address))
                {
                    if (!liveMethods.Add(address) || !callees.TryGetValue(address, out var targets))
                        continue;
                    for (var i = targets.Count - 1; i >= 0; i--)
                        pending.Push(targets[i].ToInt64());
                }
            }
            scanState.Publish(callers, liveMethods, metadataScanner?.MetadataInitRva ?? 0);
        }

        private static void AddLocked<T>(List<T> list, T value)
        {
            lock (list)
                list.Add(value);
        }

        private static int GetScanLength(long offset, long fileLength, List<long> methodStarts)
        {
            if (offset < 0 || offset >= fileLength)
                throw new InvalidDataException("A method offset is outside the native assembly: " + offset);
            var next = methodStarts.BinarySearch(offset + 1);
            if (next < 0)
                next = ~next;
            var length = next < methodStarts.Count ? methodStarts[next] - offset : 1024 * 1024;
            length = Math.Min(length, fileLength - offset);
            if (length <= 0 || length > int.MaxValue)
                throw new InvalidDataException("A native method scan has an unsupported length: " + length);
            return (int)length;
        }

        private static unsafe bool ContainsGcWriteBarrier(nint baseAddress, long length)
        {
            ReadOnlySpan<byte> marker = "il2cpp_gc_wbarrier_set_field"u8;
            long offset = 0;
            while (length - offset >= marker.Length)
            {
                var count = (int)Math.Min(int.MaxValue, length - offset);
                if (new ReadOnlySpan<byte>((void*)(baseAddress + (nint)offset), count).IndexOf(marker) >= 0)
                    return true;
                // Overlap windows so a marker crossing the span-size boundary is still found.
                offset += count - marker.Length + 1;
            }
            return false;
        }

    }
}
