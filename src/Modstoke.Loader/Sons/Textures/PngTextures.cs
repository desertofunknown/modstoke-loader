// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// PNG images turned into textures without stalling the game. Decoding, the vertical flip Unity's texture rows
    /// need and the mip chain all run on a worker, and the game thread only hands the finished data to the GPU.
    /// Unity's ImageConversion.LoadImage does all of it on the game thread, a few hundred milliseconds for one 4K
    /// image. Covers 8-bit, non-interlaced grey, grey with alpha, RGB and RGBA images; others are refused with
    /// NotSupportedException so the caller can fall back to LoadImage.
    /// </summary>
    public static class PngTextures
    {
        private static ReadOnlySpan<byte> Signature => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>A decoded image as RGBA32 rows, bottom row first, with its mip chain after the full-size level.</summary>
        public sealed class Image : IDisposable
        {
            private readonly bool _pooled;
            private byte[] _data;

            internal Image(int width, int height, int mips, byte[] data, int length, bool pooled)
            {
                Width = width;
                Height = height;
                Mips = mips;
                _data = data;
                Length = length;
                _pooled = pooled;
            }

            public int Width { get; }
            public int Height { get; }
            public int Mips { get; }

            /// <summary>The RGBA32 pixels of every level, for callers that finish the texture themselves.</summary>
            /// <remarks>A pooled image's array is usually longer than <see cref="Length"/>.</remarks>
            public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(Image));

            /// <summary>Bytes of pixel data at the start of <see cref="Data"/>.</summary>
            public int Length { get; }

            /// <summary>Gives a pooled image's array back to the pool; other images are left as they are.</summary>
            public void Dispose()
            {
                if (!_pooled || _data == null) return;
                ArrayPool<byte>.Shared.Return(_data);
                _data = null;
            }
        }

        /// <summary>Rewrites full-size RGBA32 pixels in place before the mip chain is built, such as a packed normal map layout.</summary>
        public delegate void PixelTransform(Span<byte> rgba);

        /// <summary>Decodes on a worker thread; safe to call from the game thread.</summary>
        public static Task<Image> DecodeAsync(byte[] png, bool mipmaps = true) => Task.Run(() => Decode(png, mipmaps));

        /// <summary>Decodes on the calling thread, for code already running on a worker.</summary>
        public static Image Decode(byte[] png, bool mipmaps = true) => Decode(png, mipmaps, null);

        /// <summary>Decodes on the calling thread and applies <paramref name="transform"/> before the mips are built.</summary>
        public static Image Decode(byte[] png, bool mipmaps, PixelTransform transform)
        {
            ArgumentNullException.ThrowIfNull(png);
            using var stream = new MemoryStream(png, false);
            return Decode(stream, mipmaps, transform, false);
        }

        /// <summary>Decodes straight from a stream, such as a file or an embedded resource, on the calling thread.</summary>
        public static Image Decode(Stream png, bool mipmaps = true, PixelTransform transform = null) => Decode(png, mipmaps, transform, false);

        /// <summary>Decodes like <see cref="Decode(Stream, bool, PixelTransform)"/> into an array rented from the shared pool.</summary>
        /// <remarks>
        /// Dispose the image once its pixels are uploaded or compressed, and don't touch <see cref="Image.Data"/>
        /// after that. An image that is dropped instead is only collected, never handed out twice.
        /// </remarks>
        public static Image DecodePooled(Stream png, bool mipmaps = true, PixelTransform transform = null) => Decode(png, mipmaps, transform, true);

        /// <summary>Reads and decodes a PNG file into a pooled image, without loading the file into memory first.</summary>
        public static Image DecodePooled(string path, bool mipmaps = true, PixelTransform transform = null)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            return Decode(file, mipmaps, transform, true);
        }

        private static Image Decode(Stream png, bool mipmaps, PixelTransform transform, bool pooled)
        {
            ArgumentNullException.ThrowIfNull(png);
            // The signature, then IHDR, which PNG requires to come first.
            Span<byte> head = stackalloc byte[33];
            if (png.ReadAtLeast(head, head.Length, false) < head.Length || !head[..8].SequenceEqual(Signature))
                throw new InvalidDataException("Not a PNG image.");
            if (BinaryPrimitives.ReadInt32BigEndian(head[8..]) != 13 || !head.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The PNG image has no header.");
            var width = BinaryPrimitives.ReadInt32BigEndian(head[16..]);
            var height = BinaryPrimitives.ReadInt32BigEndian(head[20..]);
            if (head[24] != 8 || head[28] != 0) throw new NotSupportedException("Only 8-bit, non-interlaced PNG images are decoded here.");
            var channels = head[25] switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, _ => throw new NotSupportedException("Palette PNG images are not decoded here.") };
            if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new InvalidDataException("Unsupported PNG size.");

            var mips = mipmaps ? 1 + (int)Math.Log2(Math.Max(width, height)) : 1;
            long total = 0;
            for (int level = 0, w = width, h = height; level < mips; level++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
                total += (long)w * h * 4;
            if (total > Array.MaxLength) throw new NotSupportedException("The PNG image is too large to decode here.");
            // Every byte is written below, so the array is not cleared first.
            var data = pooled ? ArrayPool<byte>.Shared.Rent((int)total) : GC.AllocateUninitializedArray<byte>((int)total);
            try
            {
                using (var inflate = new ZLibStream(new ImageData(png), CompressionMode.Decompress))
                    Unfilter(inflate, width, height, channels, data);
                transform?.Invoke(data.AsSpan(0, width * height * 4));
                if (mips > 1) Downsample(data, width, height, mips);
                return new Image(width, height, mips, data, (int)total, pooled);
            }
            catch
            {
                if (pooled) ArrayPool<byte>.Shared.Return(data);
                throw;
            }
        }

        /// <summary>Hands a decoded image to the GPU as a new texture; call on the game thread. The texture is not readable.</summary>
        public static Texture2D Create(Image image, string name, bool linear = false)
        {
            var texture = CreateEmpty(image, name, linear);
            try
            {
                Load(texture, image);
                texture.Apply(false, true);
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        /// <summary>Create's first step on its own: an empty RGBA32 texture with the image's size and mip count.</summary>
        /// <remarks>
        /// For a 4K image Create costs over 20 ms in one call. Splitting it into this, <see cref="Load"/> and
        /// <c>Apply(false, true)</c> lets a caller spread those over frames. All three run on the game thread, and the
        /// caller destroys the texture if a later step fails.
        /// </remarks>
        public static Texture2D CreateEmpty(Image image, string name, bool linear = false)
        {
            ArgumentNullException.ThrowIfNull(image);
            return new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, image.Mips, linear) { name = name };
        }

        /// <summary>Copies the image into a texture from <see cref="CreateEmpty"/>; it stays readable until applied.</summary>
        /// <remarks>The texture keeps its own copy, so a pooled image can be disposed straight after.</remarks>
        public static void Load(Texture2D texture, Image image)
        {
            ArgumentNullException.ThrowIfNull(texture);
            ArgumentNullException.ThrowIfNull(image);
            var pin = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
            try { texture.LoadRawTextureData(pin.AddrOfPinnedObject(), image.Length); }
            finally { pin.Free(); }
        }

        // Reverses PNG's per-row filters one row at a time and writes RGBA, bottom row first as Unity's data runs.
        private static void Unfilter(Stream rows, int width, int height, int channels, byte[] rgba)
        {
            var stride = width * channels;
            // Index 0 holds the row's filter byte, so pixel bytes start at 1.
            var current = new byte[stride + 1];
            var previous = new byte[stride + 1];
            for (var y = 0; y < height; y++)
            {
                rows.ReadExactly(current);
                var row = current.AsSpan(1);
                var above = previous.AsSpan(1);
                switch (current[0])
                {
                    case 0:
                        break;
                    case 1:
                        for (var x = channels; x < stride; x++) row[x] += row[x - channels];
                        break;
                    case 2:
                        for (var x = 0; x < stride; x++) row[x] += above[x];
                        break;
                    case 3:
                        for (var x = 0; x < channels; x++) row[x] += (byte)(above[x] >> 1);
                        for (var x = channels; x < stride; x++) row[x] += (byte)((row[x - channels] + above[x]) >> 1);
                        break;
                    case 4:
                        for (var x = 0; x < channels; x++) row[x] += above[x];
                        for (var x = channels; x < stride; x++) row[x] += Paeth(row[x - channels], above[x], above[x - channels]);
                        break;
                    default:
                        throw new InvalidDataException("Unknown PNG row filter.");
                }
                var target = rgba.AsSpan((height - 1 - y) * width * 4, width * 4);
                if (channels == 4) row.CopyTo(target);
                else
                {
                    for (int x = 0, source = 0, to = 0; x < width; x++, source += channels, to += 4)
                    {
                        if (channels >= 3)
                        {
                            target[to] = row[source];
                            target[to + 1] = row[source + 1];
                            target[to + 2] = row[source + 2];
                            target[to + 3] = 255;
                        }
                        else
                        {
                            target[to] = target[to + 1] = target[to + 2] = row[source];
                            target[to + 3] = channels == 2 ? row[source + 1] : (byte)255;
                        }
                    }
                }
                (previous, current) = (current, previous);
            }
        }

        private static byte Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
        }

        // Each level averages 2x2 texels of the one above, as Unity's own mip generation does.
        private static void Downsample(byte[] data, int width, int height, int mips)
        {
            var from = 0;
            int w = width, h = height;
            for (var level = 1; level < mips; level++)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var to = from + w * h * 4;
                for (var y = 0; y < nh; y++)
                {
                    int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    for (var x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                        int a = from + (y0 * w + x0) * 4, b = from + (y0 * w + x1) * 4;
                        int c = from + (y1 * w + x0) * 4, d = from + (y1 * w + x1) * 4;
                        var target = to + (y * nw + x) * 4;
                        for (var channel = 0; channel < 4; channel++)
                            data[target + channel] = (byte)((data[a + channel] + data[b + channel] + data[c + channel] + data[d + channel] + 2) >> 2);
                    }
                }
                from = to;
                w = nw;
                h = nh;
            }
        }

        // The zlib stream split over the IDAT chunks after the header, read in place: chunks before the first IDAT
        // are skipped, CRCs are not checked, and the data ends at the first chunk after the IDAT run.
        private sealed class ImageData : Stream
        {
            private readonly Stream _png;
            private int _left;
            private bool _started, _ended;

            internal ImageData(Stream png) => _png = png;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (buffer.IsEmpty) return 0;
                while (_left == 0)
                    if (_ended || !NextChunk()) return 0;
                var read = _png.Read(buffer[..Math.Min(buffer.Length, _left)]);
                if (read == 0) throw new InvalidDataException("A PNG chunk runs past the end of the file.");
                _left -= read;
                return read;
            }

            // Moves to the next IDAT payload; false once the run has ended.
            private bool NextChunk()
            {
                Span<byte> header = stackalloc byte[8];
                // The CRC of the IDAT just finished.
                if (_started) Skip(4);
                while (true)
                {
                    if (_png.ReadAtLeast(header, header.Length, false) < header.Length)
                        throw new InvalidDataException("A PNG chunk runs past the end of the file.");
                    var length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length < 0) throw new InvalidDataException("A PNG chunk runs past the end of the file.");
                    if (header[4..].SequenceEqual("IDAT"u8))
                    {
                        _started = true;
                        _left = length;
                        return true;
                    }
                    if (_started || header[4..].SequenceEqual("IEND"u8))
                    {
                        _ended = true;
                        return false;
                    }
                    Skip(length + 4L);
                }
            }

            private void Skip(long count)
            {
                if (_png.CanSeek)
                {
                    _png.Seek(count, SeekOrigin.Current);
                    return;
                }
                Span<byte> discard = stackalloc byte[512];
                while (count > 0)
                {
                    var read = _png.Read(discard[..(int)Math.Min(count, discard.Length)]);
                    if (read == 0) throw new InvalidDataException("A PNG chunk runs past the end of the file.");
                    count -= read;
                }
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
