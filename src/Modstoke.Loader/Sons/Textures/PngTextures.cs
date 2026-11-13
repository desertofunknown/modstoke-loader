// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
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
        public sealed class Image
        {
            internal Image(int width, int height, int mips, byte[] data)
            {
                Width = width;
                Height = height;
                Mips = mips;
                Data = data;
            }

            public int Width { get; }
            public int Height { get; }
            public int Mips { get; }
            internal byte[] Data { get; }
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
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG image.");
            int width = 0, height = 0, channels = 0;
            using var compressed = new MemoryStream(png.Length);
            var offset = 8;
            while (offset + 8 <= png.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
                var type = png.AsSpan(offset + 4, 4);
                var start = offset + 8;
                if (length < 0 || start + length + 4 > png.Length) throw new InvalidDataException("A PNG chunk runs past the end of the file.");
                if (type.SequenceEqual("IHDR"u8))
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(start));
                    height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(start + 4));
                    if (png[start + 8] != 8 || png[start + 12] != 0) throw new NotSupportedException("Only 8-bit, non-interlaced PNG images are decoded here.");
                    channels = png[start + 9] switch { 0 => 1, 4 => 2, 2 => 3, 6 => 4, _ => throw new NotSupportedException("Palette PNG images are not decoded here.") };
                    if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new InvalidDataException("Unsupported PNG size.");
                }
                else if (type.SequenceEqual("IDAT"u8)) compressed.Write(png, start, length);
                else if (type.SequenceEqual("IEND"u8)) break;
                offset = start + length + 4;
            }
            if (channels == 0) throw new InvalidDataException("The PNG image has no header.");

            var mips = mipmaps ? 1 + (int)Math.Log2(Math.Max(width, height)) : 1;
            long total = 0;
            for (int level = 0, w = width, h = height; level < mips; level++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
                total += (long)w * h * 4;
            if (total > Array.MaxLength) throw new NotSupportedException("The PNG image is too large to decode here.");
            // Every byte is written below, so the array is not cleared first.
            var data = GC.AllocateUninitializedArray<byte>((int)total);
            compressed.Position = 0;
            using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
                Unfilter(inflate, width, height, channels, data);
            transform?.Invoke(data.AsSpan(0, width * height * 4));
            if (mips > 1) Downsample(data, width, height, mips);
            return new Image(width, height, mips, data);
        }

        /// <summary>Hands a decoded image to the GPU as a new texture; call on the game thread. The texture is not readable.</summary>
        public static Texture2D Create(Image image, string name, bool linear = false)
        {
            ArgumentNullException.ThrowIfNull(image);
            var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, image.Mips, linear) { name = name };
            try
            {
                var pin = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                try { texture.LoadRawTextureData(pin.AddrOfPinnedObject(), image.Data.Length); }
                finally { pin.Free(); }
                texture.Apply(false, true);
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
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
    }
}
