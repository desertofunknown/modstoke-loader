// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Modstoke.Diagnostics;

/// <summary>Local console transport. Background work never invokes game or mod callbacks.</summary>
public static class ConsoleHost
{
    public const string EndpointFileName = "console-endpoint.json";
    private static readonly object Sync = new();
    private static CancellationTokenSource? _stop;
    private static string? _gameRoot, _pipeName, _endpointPath;
    private static Process? _desktop;
    private static bool _stopped;

    public static void Start(string gameRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Sync)
        {
            if (_stop != null || _stopped) return;
            var root = Path.GetFullPath(gameRoot);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            string name = "modstoke-console-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            // Create the first endpoint synchronously so startup can fall back if local IPC is unavailable.
            var first = CreatePipe(name);
            _gameRoot = root;
            _pipeName = name;
            PublishEndpoint(root, name);
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _ = ServeAsync(name, first, token);
            _ = Task.Run(() => ServeAsync(name, null, token));
            _ = Task.Run(() => TailDiagnosticsAsync(root, token));
        }
    }

    public static bool TryOpenDesktop(out string detail)
    {
        lock (Sync)
        {
            if (_stop == null || _stop.IsCancellationRequested || _gameRoot == null || _pipeName == null)
            {
                detail = "The desktop console transport is unavailable.";
                return false;
            }
            try
            {
                if (_desktop is not null && !_desktop.HasExited)
                {
                    detail = "The desktop console is already open.";
                    return true;
                }
                string directory = Path.GetDirectoryName(typeof(ConsoleHost).Assembly.Location)!;
                string executable = Path.Combine(directory, "Modstoke.Console.exe");
                if (!File.Exists(executable))
                {
                    detail = "The desktop console executable is missing from the Loader runtime.";
                    return false;
                }
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = directory,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                start.ArgumentList.Add("--pipe");
                start.ArgumentList.Add(_pipeName);
                string runtime = Path.Combine(_gameRoot, "dotnet");
                start.Environment["DOTNET_ROOT"] = runtime;
                start.Environment["DOTNET_ROOT_X64"] = runtime;
                start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                _desktop?.Dispose();
                _desktop = Process.Start(start);
                detail = _desktop == null ? "The desktop console did not start." : "Desktop console opened.";
                return _desktop != null;
            }
            catch (Exception error)
            {
                detail = "Could not open the desktop console: " + error.Message;
                return false;
            }
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            if (_stopped) return;
            _stopped = true;
            _stop?.Cancel();
            RetractEndpoint();
            // Keep the dashboard alive so startup failures and final logs can be read or exported.
            _desktop?.Dispose();
            _desktop = null;
        }
    }

    // Lets the Manager or a console window attach. The pipe already admits only this Windows user.
    private static void PublishEndpoint(string root, string name)
    {
        try
        {
            var directory = ResolveUserData(root);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, EndpointFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new ConsoleEndpoint(1, name, Environment.ProcessId, DateTimeOffset.UtcNow)));
            File.Move(temporary, path, true);
            _endpointPath = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ConsoleHub.Append("Warning", "Console", "The console endpoint could not be published: " + error.Message);
        }
    }

    // Same rule as Bootstrap: a UserData folder it could not move out of OpenACAI stays in use.
    private static string ResolveUserData(string root)
    {
        var current = Path.Combine(root, "Modstoke", "UserData");
        if (Directory.Exists(current)) return current;
        var legacy = Path.Combine(root, "OpenACAI", "UserData");
        return Directory.Exists(legacy) ? legacy : current;
    }

    private static void RetractEndpoint()
    {
        var path = _endpointPath;
        _endpointPath = null;
        if (path == null || _pipeName == null) return;
        try
        {
            // A later process may have replaced the file; only remove our own endpoint.
            if (File.Exists(path) && File.ReadAllText(path).Contains(_pipeName, StringComparison.Ordinal))
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static NamedPipeServerStream CreatePipe(string name)
        => new(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 65536);

    private static async Task ServeAsync(string name, NamedPipeServerStream? first, CancellationToken token)
    {
        NamedPipeServerStream? next = first;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var pipe = next ?? CreatePipe(name);
                next = null;
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var request = await ConsoleWire.ReadAsync<ConsoleRequest>(pipe, ConsoleWire.MaximumRequestBytes,
                        deadline.Token).ConfigureAwait(false);
                    ConsoleSnapshot response;
                    if (request.Operation == "read")
                        response = ConsoleHub.Read(Math.Max(0, request.After), request.Limit);
                    else if (request.Operation == "submit")
                    {
                        bool accepted = ConsoleHub.TrySubmit(request.Command, request.Arguments, request.Confirmed,
                            ConsoleOrigin.DesktopConsole, out var detail);
                        response = ConsoleHub.Read(Math.Max(0, request.After), request.Limit) with
                            { Accepted = accepted, Detail = detail };
                    }
                    else
                        response = ConsoleHub.Read(Math.Max(0, request.After), 1) with
                            { Detail = "Unknown console operation." };
                    await ConsoleWire.WriteResponseAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
                    or System.Text.Json.JsonException)
                {
                    // A disconnected or malformed client does not affect the history or other view.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                ConsoleHub.Append("Error", "Console", "The desktop console endpoint stopped: " + error.Message);
        }
        finally { next?.Dispose(); }
    }

    private static async Task TailDiagnosticsAsync(string root, CancellationToken token)
    {
        DateTimeOffset started;
        using (var process = Process.GetCurrentProcess()) started = process.StartTime.ToUniversalTime();
        var bootstrap = new DiagnosticTail(Path.Combine(root, "openacai-bootstrap.log"), "Bootstrap", "Debug", started, true);
        var native = new DiagnosticTail(Path.Combine(root, "BepInEx", "ErrorLog.log"), "Native stderr", "Error", started, false);
        try
        {
            while (!token.IsCancellationRequested)
            {
                bootstrap.Poll();
                native.Poll();
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ConsoleHub.Append("Warning", "Console", "Supplemental diagnostic capture stopped: " + error.Message);
        }
    }
}
