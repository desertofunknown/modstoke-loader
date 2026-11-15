// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Modstoke.Sons
{
    /// <summary>
    /// Streams 8-bit RGB PNG files from RGBA pixels, for photos far larger than one buffer should hold twice.
    /// Rows are filtered one at a time and compressed straight into IDAT chunks, so the only extra memory is a few
    /// rows and the compressor. Plain .NET throughout, safe on any thread.
    /// </summary>
    internal static class PngWriter
    {
        // Readers accept IDAT split anywhere, so the compressed stream goes out in pieces of this size.
        private const int IdatSize = 1 << 20;
        private static readonly uint[] CrcTable = CreateCrcTable();

        /// <summary>
        /// Writes <paramref name="crop"/> of a <paramref name="width"/> × <paramref name="height"/> RGBA image as an
        /// sRGB PNG, alpha dropped. The crop is measured from the picture's top-left corner; <paramref name="bottomUp"/>
        /// says the rows arrive bottom row first, as Unity's texture data does. Each text entry becomes a tEXt chunk,
        /// or iTXt when it asks for UTF-8 or will not fit Latin-1.
        /// </summary>
        internal static void WriteRgb(Stream output, ReadOnlySpan<byte> rgba, int width, int height,
            (int X, int Y, int Width, int Height) crop, bool bottomUp, IReadOnlyList<(string Key, string Text, bool Utf8)> text)
        {
            ArgumentNullException.ThrowIfNull(output);
            if (width < 1 || height < 1 || rgba.Length < (long)width * height * 4)
                throw new ArgumentException("The pixels do not cover the image size.", nameof(rgba));
            if (crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > width || crop.Y + crop.Height > height)
                throw new ArgumentOutOfRangeException(nameof(crop), "The crop must lie inside the image.");

            output.Write(stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, crop.Width);
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(4), crop.Height);
            header[8] = 8;
            header[9] = 2;
            WriteChunk(output, "IHDR"u8, header);
            // Perceptual intent; gAMA is the fallback the PNG spec asks sRGB writers to add for older readers.
            WriteChunk(output, "sRGB"u8, stackalloc byte[] { 0 });
            Span<byte> gamma = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(gamma, 45455);
            WriteChunk(output, "gAMA"u8, gamma);
            if (text != null)
                foreach (var entry in text)
                    WriteText(output, entry.Key, entry.Text, entry.Utf8);

            using (var idat = new IdatStream(output))
            {
                using (var zlib = new ZLibStream(idat, new ZLibCompressionOptions { CompressionLevel = 4, CompressionStrategy = ZLibCompressionStrategy.Filtered }, true))
                    WriteRows(zlib, rgba, width, height, crop, bottomUp);
                idat.Finish();
            }
            WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        // Each row gets Up or Paeth, whichever leaves the smaller sum of signed bytes: the usual cheap estimate of
        // what deflate will make of it. Photos nearly always pick Paeth; skies and flat walls go to Up.
        private static void WriteRows(Stream zlib, ReadOnlySpan<byte> rgba, int width, int height,
            (int X, int Y, int Width, int Height) crop, bool bottomUp)
        {
            var stride = crop.Width * 3;
            var current = new byte[stride];
            var previous = new byte[stride];
            // Index 0 is the filter type byte, written with the row.
            var up = new byte[stride + 1];
            var paeth = new byte[stride + 1];
            up[0] = 2;
            paeth[0] = 4;
            for (var y = 0; y < crop.Height; y++)
            {
                var row = crop.Y + y;
                var source = rgba.Slice(((bottomUp ? height - 1 - row : row) * width + crop.X) * 4, crop.Width * 4);
                for (int from = 0, to = 0; to < stride; from += 4, to += 3)
                {
                    current[to] = source[from];
                    current[to + 1] = source[from + 1];
                    current[to + 2] = source[from + 2];
                }

                long upCost = 0, paethCost = 0;
                for (var x = 0; x < stride; x++)
                {
                    var above = previous[x];
                    var u = (byte)(current[x] - above);
                    up[x + 1] = u;
                    upCost += u < 128 ? u : 256 - u;
                    var p = (byte)(current[x] - (x < 3 ? above : Paeth(current[x - 3], above, previous[x - 3])));
                    paeth[x + 1] = p;
                    paethCost += p < 128 ? p : 256 - p;
                }
                zlib.Write(paethCost <= upCost ? paeth : up);
                (previous, current) = (current, previous);
            }
        }

        private static byte Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
        }

        private static void WriteText(Stream output, string key, string text, bool utf8)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 79 || key[0] == ' ' || key[^1] == ' ')
                throw new ArgumentException("A PNG text keyword is 1-79 characters without outer spaces.", nameof(key));
            foreach (var c in key)
                if (c < 32 || c > 255 || c is > '~' and < '¡')
                    throw new ArgumentException("A PNG text keyword is printable Latin-1.", nameof(key));
            text = (text ?? "").Replace("\0", "");
            if (!utf8)
                foreach (var c in text)
                    if (c > 255) { utf8 = true; break; }

            var keyword = Encoding.Latin1.GetBytes(key);
            using var data = new MemoryStream(keyword.Length + text.Length + 8);
            data.Write(keyword);
            data.WriteByte(0);
            if (utf8)
            {
                // Uncompressed, with no language tag or translated keyword.
                data.Write(stackalloc byte[] { 0, 0, 0, 0 });
                data.Write(Encoding.UTF8.GetBytes(text));
                WriteChunk(output, "iTXt"u8, data.GetBuffer().AsSpan(0, (int)data.Length));
            }
            else
            {
                data.Write(Encoding.Latin1.GetBytes(text));
                WriteChunk(output, "tEXt"u8, data.GetBuffer().AsSpan(0, (int)data.Length));
            }
        }

        private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
            output.Write(word);
            output.Write(type);
            output.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc(Crc(0xFFFFFFFFu, type), data));
            output.Write(word);
        }

        private static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            var table = CrcTable;
            foreach (var value in data)
                crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        // Collects the compressor's output and writes it out as IDAT chunks of up to IdatSize bytes.
        private sealed class IdatStream : Stream
        {
            private readonly Stream _output;
            private readonly byte[] _buffer = new byte[IdatSize];
            private int _used;

            internal IdatStream(Stream output) => _output = output;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> data)
            {
                while (!data.IsEmpty)
                {
                    var take = Math.Min(data.Length, _buffer.Length - _used);
                    data.Slice(0, take).CopyTo(_buffer.AsSpan(_used));
                    _used += take;
                    data = data.Slice(take);
                    if (_used == _buffer.Length) Emit();
                }
            }

            /// <summary>Writes what is left; call once the compressor is closed.</summary>
            internal void Finish()
            {
                if (_used > 0) Emit();
            }

            // The compressor flushes as it closes; chunks only go out when full or on Finish.
            public override void Flush()
            {
            }

            private void Emit()
            {
                WriteChunk(_output, "IDAT"u8, _buffer.AsSpan(0, _used));
                _used = 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
