using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Preloader.Core;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using MonoMod.Utils;

// ReSharper disable once CheckNamespace
namespace Doorstop;

public static class Entrypoint
{
    private static void TraceBootstrap(string message)
    {
        try
        {
            var processPath = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
            var root = string.IsNullOrWhiteSpace(processPath)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(processPath);
            var path = Path.Combine(root ?? Environment.CurrentDirectory, "openacai-bepinex-net10-bootstrap.log");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // This diagnostic must never become the reason bootstrap fails.
        }
    }

    /// <summary>
    ///     The main entrypoint of BepInEx, called from Doorstop.
    /// </summary>
    public static void Start()
    {
        TraceBootstrap("Doorstop.Entrypoint.Start entered");

        // We set it to the current directory first as a fallback, but try to use the same location as the .exe file.
        var silentExceptionLog = Environment.GetEnvironmentVariable("BEPINEX_PRELOADER_LOG") ??
                                 $"preloader_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log";
        Mutex mutex = null;

        try
        {
            TraceBootstrap("Loading Doorstop environment variables");
            EnvVars.LoadVars();
            TraceBootstrap("Doorstop environment variables loaded");

            silentExceptionLog =
                Path.Combine(Path.GetDirectoryName(EnvVars.DOORSTOP_PROCESS_PATH), silentExceptionLog);

            TraceBootstrap("Computing preloader mutex id");
            var mutexId = Utility.HashStrings(Process.GetCurrentProcess().ProcessName, EnvVars.DOORSTOP_PROCESS_PATH,
                                              typeof(Entrypoint).FullName);

            TraceBootstrap("Creating preloader mutex");
            mutex = new Mutex(false, $"Global\\{mutexId}");
            TraceBootstrap("Waiting on preloader mutex");
            mutex.WaitOne();
            TraceBootstrap("Starting UnityPreloaderRunner.PreloaderMain");

            UnityPreloaderRunner.PreloaderMain();
            TraceBootstrap("UnityPreloaderRunner.PreloaderMain returned");
        }
        catch (Exception ex)
        {
            TraceBootstrap($"Managed bootstrap exception: {ex}");
            File.WriteAllText(silentExceptionLog, ex.ToString());

            try
            {
                if (PlatformHelper.Is(Platform.Windows))
                {
                    MessageBox.Show("Failed to start " + OpenACAIBranding.LoaderName, OpenACAIBranding.LoaderName);
                }
                else if (NotifySend.IsSupported)
                {
                    NotifySend.Send("Failed to start " + OpenACAIBranding.LoaderName, "Check logs for details");
                }
                else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BEPINEX_FAIL_FAST")))
                {
                    // Don't exit the game if we have no way of signaling to the user that a crash happened
                    return;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            Environment.Exit(1);
        }
        finally
        {
            TraceBootstrap("Doorstop.Entrypoint.Start finally");
            mutex?.ReleaseMutex();
        }
    }
}
