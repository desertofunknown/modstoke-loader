using System;
using System.IO;

namespace Doorstop;

public static class Entrypoint
{
    public static void Start()
    {
        var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
        var gameRoot = string.IsNullOrWhiteSpace(processPath)
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(processPath);

        var logPath = Path.Combine(gameRoot ?? Environment.CurrentDirectory, "openacai-hostfxr-smoke.log");
        File.AppendAllText(logPath, $"HostFxr smoke reached managed code at {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }
}
