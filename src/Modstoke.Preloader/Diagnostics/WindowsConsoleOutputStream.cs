// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Modstoke.Preloader
{
    internal sealed class WindowsConsoleOutputStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private readonly Decoder _decoder;
        private readonly char[] _characters;
        private ExceptionDispatchInfo _writeFailure;
        private bool _pipeClosed;
        private bool _disposed;

        internal WindowsConsoleOutputStream(SafeFileHandle handle, Encoding encoding)
        {
            _handle = handle;
            if (GetConsoleMode(handle, out _))
            {
                _decoder = encoding.GetDecoder();
                _characters = new char[1024];
            }
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
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writeFailure?.Throw();
            if (buffer.IsEmpty || _pipeClosed)
                return;

            try
            {
                if (_decoder == null)
                    WriteBytes(buffer);
                else
                    WriteCharacters(buffer, flush: false);
            }
            catch (Exception error)
            {
                // A native failure may follow a partial write. Keep later flushes
                // from reporting success after output has already been lost.
                _writeFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writeFailure?.Throw();
            if (_decoder == null)
                return;
            try { WriteCharacters(ReadOnlySpan<byte>.Empty, flush: true); }
            catch (Exception error)
            {
                _writeFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }

        private void WriteCharacters(ReadOnlySpan<byte> buffer, bool flush)
        {
            bool completed;
            do
            {
                // Keep decoder state across writes, including split UTF-8 sequences.
                _decoder.Convert(buffer, _characters.AsSpan(), flush,
                    out var bytesUsed, out var charsUsed, out completed);
                WriteConsoleCharacters(_characters.AsSpan(0, charsUsed));
                buffer = buffer.Slice(bytesUsed);
            } while (!completed);
        }

        private unsafe void WriteConsoleCharacters(ReadOnlySpan<char> characters)
        {
            fixed (char* start = characters)
            {
                var offset = 0;
                while (offset < characters.Length)
                {
                    var count = characters.Length - offset;
                    if (!WriteConsole(_handle, (IntPtr)(start + offset), (uint)count,
                        out var written, IntPtr.Zero))
                        throw new IOException("Could not write console output.",
                            new Win32Exception(Marshal.GetLastWin32Error()));
                    if (written == 0 || written > count)
                        throw new IOException("The console handle reported an invalid write count.");
                    offset += (int)written;
                }
            }
        }

        private unsafe void WriteBytes(ReadOnlySpan<byte> buffer)
        {
            fixed (byte* start = buffer)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var count = buffer.Length - offset;
                    if (!WriteFile(_handle, (IntPtr)(start + offset), (uint)count,
                        out var written, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        // Match console streams when the receiving end of a pipe exits.
                        if (error == 109 || error == 232)
                        {
                            _pipeClosed = true;
                            return;
                        }
                        throw new IOException("Could not write standard output.", new Win32Exception(error));
                    }
                    if (written == 0 || written > count)
                        throw new IOException("The standard output handle reported an invalid write count.");
                    offset += (int)written;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && !_disposed && _writeFailure == null)
                    Flush();
            }
            finally
            {
                // The session owns the handle, potentially shared by both output writers.
                _disposed = true;
                base.Dispose(disposing);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteConsole(SafeFileHandle handle, IntPtr buffer, uint count,
            out uint written, IntPtr reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint count,
            out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
    }
}
