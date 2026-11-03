// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Console manager initialization for early IL2CPP preloader
// logging. This preserves the BepInEx ConsoleManager compatibility surface
// without calling its Initialize method.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Modstoke.Bootstrap
{
    internal static class ConsoleManagerInitializer
    {
        private const bool AlreadyActive = false;
        private const bool UseManagedEncoder = true;
        private const string OwnedDriverAssembly = "Modstoke.Preloader";
        private const string OwnedDriverType = "Modstoke.Preloader.WindowsConsoleDriver";

        internal static ConsoleManagerInitializationState Initialize(
            Func<string, string, Type> loadRequiredType,
            Action<string> trace)
        {
            if (loadRequiredType == null)
            {
                throw new ArgumentNullException(nameof(loadRequiredType));
            }

            var state = new ConsoleManagerInitializationState
            {
                OpenAcaiOwned = true,
                BepInExConsoleManagerInitializeUsed = false,
                AlreadyActiveRequested = AlreadyActive,
                UseManagedEncoder = UseManagedEncoder,
                Platform = GetPlatformName()
            };

            var consoleManagerType = loadRequiredType("BepInEx.Core", "BepInEx.ConsoleManager");
            state.ConsoleManagerType = consoleManagerType.FullName ?? "";
            state.ConsoleEnabled = TryGetStaticBool(consoleManagerType, "ConsoleEnabled");

            var driverProperty = consoleManagerType.GetProperty(
                "Driver",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (driverProperty == null)
            {
                throw new MissingMemberException(consoleManagerType.FullName, "Driver");
            }

            var existingDriver = driverProperty.GetValue(null);
            state.ExistingDriverType = existingDriver?.GetType().FullName ?? "";

            object driver;
            if (existingDriver != null)
            {
                driver = existingDriver;
                state.DriverReused = true;
                state.DriverInitialized = true;
                state.DriverBound = true;
            }
            else
            {
                var driverType = ResolveDriverType(loadRequiredType, state.Platform);
                state.DriverType = driverType.FullName ?? "";
                // Validate publication before initializing a driver that can change native handles.
                var driverSetter = driverProperty.GetSetMethod(true);
                if (driverSetter == null || !driverSetter.IsStatic)
                    throw new MissingMethodException(consoleManagerType.FullName, "set_Driver");
                if (!driverProperty.PropertyType.IsAssignableFrom(driverType))
                    throw new InvalidOperationException("Console driver " + driverType.AssemblyQualifiedName
                        + " is not assignable to " + driverProperty.PropertyType.AssemblyQualifiedName + ".");

                driver = CreateDriver(driverType);
                var initializeMethod = driver.GetType().GetMethod(
                    "Initialize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool), typeof(bool) },
                    null);
                if (initializeMethod == null)
                {
                    throw new MissingMethodException(driver.GetType().FullName, "Initialize");
                }

                initializeMethod.Invoke(driver, new object[] { AlreadyActive, UseManagedEncoder });
                state.DriverInitialized = true;

                driverSetter.Invoke(null, new[] { driver });
                state.DriverBound = ReferenceEquals(driverProperty.GetValue(null), driver);
                if (!state.DriverBound)
                    throw new InvalidOperationException("ConsoleManager.Driver did not retain the initialized console driver.");
            }

            if (string.IsNullOrWhiteSpace(state.DriverType))
            {
                state.DriverType = driver.GetType().FullName ?? "";
            }

            state.DriverOpenAcaiOwned = driver.GetType().FullName == OwnedDriverType
                && driver.GetType().Assembly.GetName().Name == OwnedDriverAssembly;
            state.ConsoleActive = TryGetStaticBool(consoleManagerType, "ConsoleActive");
            state.StandardOutAvailable = TryGetStaticTextWriter(consoleManagerType, "StandardOutStream") != null;
            state.ConsoleOutAvailable = TryGetStaticTextWriter(consoleManagerType, "ConsoleStream") != null;
            state.Detail = state.DriverReused
                ? "Modstoke reused the existing ConsoleManager compatibility driver without calling BepInEx ConsoleManager.Initialize."
                : "Modstoke created, initialized, and bound the ConsoleManager compatibility driver without calling BepInEx ConsoleManager.Initialize.";

            trace?.Invoke("Modstoke console manager initialized: " + state.Detail);
            return state;
        }

        private static Type ResolveDriverType(Func<string, string, Type> loadRequiredType, string platform)
        {
            if (string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return loadRequiredType(OwnedDriverAssembly, OwnedDriverType);
            }

            if (string.Equals(platform, "linux", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase))
            {
                return loadRequiredType("BepInEx.Core", "BepInEx.Unix.LinuxConsoleDriver");
            }

            throw new PlatformNotSupportedException("Modstoke console manager could not select a compatibility console driver for platform " + platform + ".");
        }

        private static object CreateDriver(Type driverType)
        {
            var constructor = driverType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (constructor == null)
            {
                throw new MissingMethodException(driverType.FullName, ".ctor");
            }

            return constructor.Invoke(Array.Empty<object>());
        }

        private static bool TryGetStaticBool(Type type, string propertyName)
        {
            try
            {
                var value = GetStaticProperty(type, propertyName);
                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        private static TextWriter TryGetStaticTextWriter(Type type, string propertyName)
        {
            try
            {
                return GetStaticProperty(type, propertyName) as TextWriter;
            }
            catch
            {
                return null;
            }
        }

        private static object GetStaticProperty(Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property == null)
            {
                throw new MissingMemberException(type.FullName, propertyName);
            }

            return property.GetValue(null);
        }

        private static string GetPlatformName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }

            return RuntimeInformation.OSDescription;
        }
    }

    internal sealed class ConsoleManagerInitializationState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExConsoleManagerInitializeUsed { get; set; }
        internal bool AlreadyActiveRequested { get; set; }
        internal bool UseManagedEncoder { get; set; }
        internal bool ConsoleEnabled { get; set; }
        internal bool ConsoleActive { get; set; }
        internal bool DriverInitialized { get; set; }
        internal bool DriverBound { get; set; }
        internal bool DriverReused { get; set; }
        internal bool DriverOpenAcaiOwned { get; set; }
        internal bool StandardOutAvailable { get; set; }
        internal bool ConsoleOutAvailable { get; set; }
        internal string Platform { get; set; } = "";
        internal string ConsoleManagerType { get; set; } = "";
        internal string ExistingDriverType { get; set; } = "";
        internal string DriverType { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
