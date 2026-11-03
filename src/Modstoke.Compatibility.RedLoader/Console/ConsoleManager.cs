using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using RedLoader.Unix;
using MonoMod.Utils;
using RedLoader;
using RedLoader.Utils;

namespace RedLoader;

public static class ConsoleManager
{
    public enum ConsoleOutRedirectType
    {
        [Description("Auto")]
        Auto = 0,

        [Description("Console Out")]
        ConsoleOut,

        [Description("Standard Out")]
        StandardOut
    }

    private const uint SHIFT_JIS_CP = 932;

    private const string ENABLE_CONSOLE_ARG = "--enable-console";

    public static readonly bool ConfigConsoleEnabled = true;

    public static readonly bool ConfigPreventClose = false;

    public static readonly bool ConfigConsoleShiftJis = false;

    public static readonly ConsoleOutRedirectType ConfigConsoleOutRedirectType = ConsoleOutRedirectType.Auto;

    private static readonly bool? EnableConsoleArgOverride;

    static ConsoleManager()
    {
        // Command-line access must not prevent the console manager from initializing.
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var res = args[i];
                if (res == ENABLE_CONSOLE_ARG && i + 1 < args.Length && bool.TryParse(args[i + 1], out var enable))
                    EnableConsoleArgOverride = enable;
            }
        }
        catch (Exception)
        {
            // Keep the configured console setting if command-line access fails.
        }
    }

    public static bool ConsoleEnabled => EnableConsoleArgOverride ?? CorePreferences.ShowConsole.Value || LoaderEnvironment.IsDedicatedServer;

    internal static IConsoleDriver Driver { get; set; }

    /// <summary>
    ///     True if an external console has been started, false otherwise.
    /// </summary>
    public static bool ConsoleActive => Driver?.ConsoleActive ?? false;

    /// <summary>
    ///     The process's standard-output writer, or null until initialized.
    /// </summary>
    public static TextWriter StandardOutStream => Driver?.StandardOut;

    /// <summary>
    ///     The stream that writes to an external console. Null if no such console exists
    /// </summary>
    public static TextWriter ConsoleStream => Driver?.ConsoleOut;


    public static void Initialize(bool alreadyActive, bool useManagedEncoder)
    {
        if (PlatformHelper.Is(Platform.Unix))
            Driver = new LinuxConsoleDriver();
        else if (PlatformHelper.Is(Platform.Windows))
            Driver = new WindowsConsoleDriver();
        else
            throw new PlatformNotSupportedException("Was unable to determine console driver for platform " +
                                                    PlatformHelper.Current);

        Driver.Initialize(alreadyActive, useManagedEncoder);
    }

    private static void DriverCheck()
    {
        if (Driver == null)
            throw new InvalidOperationException("Driver has not been initialized");
    }

    public static void CreateConsole()
    {
        if (ConsoleActive)
            return;

        DriverCheck();

        // Pass code-page IDs to the driver because some Mono versions do not support Encoding.GetEncoding.
        var codepage = ConfigConsoleShiftJis ? SHIFT_JIS_CP : (uint) Encoding.UTF8.CodePage;

        Driver.CreateConsole(codepage);

        if (ConfigPreventClose)
            Driver.PreventClose();
    }

    public static void DetachConsole()
    {
        if (!ConsoleActive)
            return;

        DriverCheck();

        Driver.DetachConsole();
    }

    public static void SetConsoleTitle(string title)
    {
        DriverCheck();

        Driver.SetConsoleTitle(title);
    }

    public static void SetConsoleColor(ConsoleColor color)
    {
        DriverCheck();

        Driver.SetConsoleColor(color);
    }
    
    internal static void SetConsoleRect(CorePreferences.FConsoleRect rect)
    {
        DriverCheck();
        
        Driver.SetConsoleRect(rect);
    }
    
    internal static CorePreferences.FConsoleRect GetConsoleRect()
    {
        DriverCheck();

        var rect = Driver.GetConsoleRect();
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
