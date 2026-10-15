// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class WindowsConsoleOutputStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private readonly bool _console;
        private bool _disposed;

        internal WindowsConsoleOutputStream(SafeFileHandle handle)
        {
            _handle = handle;
            _console = GetConsoleMode(handle, out _);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count == 0)
                return;

            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                while (count > 0)
                {
                    if (!WriteFile(_handle, IntPtr.Add(pinned.AddrOfPinnedObject(), offset),
                        (uint)count, out var written, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        // Match console streams when the receiving end of a pipe exits.
                        if (error == 109 || error == 232)
                            return;
                        throw new IOException("Could not write console output.", new Win32Exception(error));
                    }

                    // Console hosts do not consistently report a byte count for encoded text.
                    // Only redirected files/pipes use the result to continue partial writes.
                    if (_console)
                        return;
                    if (written == 0 || written > count)
                        throw new IOException("The standard output handle reported an invalid write count.");
                    offset += (int)written;
                    count -= (int)written;
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The session owns the handle, potentially shared by both output writers.
            _disposed = true;
            base.Dispose(disposing);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint count,
            out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
    }
}
