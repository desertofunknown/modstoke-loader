// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Modstoke.Diagnostics;

public static class ConsoleWire
{
    // Include the bounded catalog and mod metadata, even with JSON's six-byte Unicode escaping.
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int MaximumRequestBytes = 16 * 1024;

    /// <summary>One bounded local exchange; failed submissions must not be automatically resent.</summary>
    public static async Task<ConsoleSnapshot> ExchangeAsync(string pipeName, ConsoleRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 160
            || !pipeName.StartsWith("modstoke-console-", StringComparison.Ordinal)
            || pipeName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("Invalid local console pipe name.", nameof(pipeName));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await WriteAsync(pipe, request, MaximumRequestBytes, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<ConsoleSnapshot>(pipe, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes) throw new InvalidDataException("Console message exceeds its size limit.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Console message is empty.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > maximumBytes) throw new InvalidDataException("Console response exceeds its size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static Task WriteResponseAsync(Stream stream, ConsoleSnapshot snapshot, CancellationToken token)
        => WriteAsync(stream, snapshot, MaximumResponseBytes, token);
}
