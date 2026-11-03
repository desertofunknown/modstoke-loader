// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Reflection;

namespace SonsSdk;

internal interface ISdkInitializationDriver
{
    bool InitializationFailed { get; }
    void InitTitleScene();
    void InitDedicatedServer();
    void CompleteEventSetup();
    void ReportInitializationFailure(Exception exception);
}

internal class MainInitializer
{
    private static readonly Lazy<ISdkInitializationDriver> InitializationFallback = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkInitializationFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkInitializationDriver>)method.CreateDelegate(typeof(Func<ISdkInitializationDriver>)))();
    });

    private static ISdkInitializationDriver _initializationDriver;
    internal static bool _initializationFailed;

    internal static bool InitializationFailed => _initializationDriver?.InitializationFailed ?? _initializationFailed;

    internal static void SetInitializationDriver(ISdkInitializationDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (ReferenceEquals(_initializationDriver, driver))
            return;
        if (_initializationDriver != null || _initializationStarted || _eventSetupCompleted ||
            _initializationFinished || _initializationFailed)
            throw new InvalidOperationException("SDK initialization ownership must be selected before startup.");

        _initializationDriver = driver;
    }

    internal static void InitTitleScene()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.InitTitleScene();
            return;
        }

        InitializationFallback.Value.InitTitleScene();
    }

    internal static void InitDedicatedServer()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.InitDedicatedServer();
            return;
        }

        InitializationFallback.Value.InitDedicatedServer();
    }

    internal static void CompleteEventSetup()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.CompleteEventSetup();
            return;
        }

        InitializationFallback.Value.CompleteEventSetup();
    }

    internal static void ReportInitializationFailure(Exception exception)
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.ReportInitializationFailure(exception);
            return;
        }

        InitializationFallback.Value.ReportInitializationFailure(exception);
    }

    internal static bool _initializationStarted;
    internal static bool _initializationFinished;
    internal static bool _eventSetupCompleted;
}
