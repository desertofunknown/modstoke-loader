// SPDX-License-Identifier: GPL-3.0-or-later
// Implements the BepInEx IConsoleDriver compatibility contract.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using BepInEx;
using Microsoft.Win32.SafeHandles;
using Modstoke.Diagnostics;

namespace Modstoke.Preloader
{
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsConsoleDriver : IConsoleDriver
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private WindowsConsoleSession _session;
        private TextWriter _initialOutput;
        private TextWriter _ownedStandardOutput;
        private TextWriter _ownedConsoleOutput;
        private bool _useManagedEncoder;
        private bool _desktopActive;

        public TextWriter StandardOut { get; private set; }
        public TextWriter ConsoleOut { get; private set; }
        public bool ConsoleActive { get; private set; }
        public bool ConsoleIsExternal => true;
        internal bool DesktopActive => Volatile.Read(ref _desktopActive);

        public void Initialize(bool alreadyActive, bool useManagedEncoder)
        {
            lock (_gate)
            {
                if (_initialOutput != null)
                    throw new InvalidOperationException("The console driver is already initialized.");
                _initialOutput = Console.Out;
                _useManagedEncoder = useManagedEncoder;
                StandardOut = _initialOutput;
                if (alreadyActive)
                {
                    ConsoleOut = _initialOutput;
                    _ownedStandardOutput = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput()));
                    StandardOut = _ownedStandardOutput;
                }
                ConsoleActive = alreadyActive;
            }
        }

        public void CreateConsole(uint codepage)
        {
            lock (_gate)
            {
                if (ConsoleActive)
                    return;
                if (_initialOutput == null)
                    throw new InvalidOperationException("The console driver has not been initialized.");
                if (_session != null)
                    ReleaseConsole();

                if (ConsoleLogging.DesktopRequested)
                {
                    string detail;
                    try
                    {
                        ConsoleHost.Start(Paths.GameRootPath);
                        if (ConsoleHost.TryOpenDesktop(out detail))
                        {
                            _ownedConsoleOutput = new ConsoleLogging.OutputWriter();
                            _ownedStandardOutput = _ownedConsoleOutput;
                            ConsoleOut = _ownedConsoleOutput;
                            StandardOut = _ownedStandardOutput;
                            Volatile.Write(ref _desktopActive, true);
                            ConsoleActive = true;
                            return;
                        }
                    }
                    catch (Exception error)
                    {
                        detail = error.ToString();
                    }
                    ConsoleLogging.Append("Warning", "Console",
                        "Could not open the desktop console; using the Windows console. " + detail);
                }

                _session = new WindowsConsoleSession();
                try
                {
                    _session.Attach();
                    var encoding = _useManagedEncoder ? Utf8 : _session.SetOutputEncoding(codepage);
                    var output = ConsoleManager.ConfigConsoleOutRedirectType.Value switch
                    {
                        ConsoleManager.ConsoleOutRedirectType.ConsoleOut => _session.ConsoleHandle,
                        ConsoleManager.ConsoleOutRedirectType.StandardOut => _session.OriginalOutput,
                        _ => _session.OriginalOutput ?? _session.ConsoleHandle
                    };

                    _ownedConsoleOutput = CreateWriter(_session.ConsoleHandle, encoding);
                    _ownedStandardOutput = output == null ? TextWriter.Null
                        : ReferenceEquals(output, _session.ConsoleHandle) && encoding.CodePage == Utf8.CodePage
                            ? _ownedConsoleOutput
                            : CreateWriter(output, Utf8);
                    StandardOut = _ownedStandardOutput;
                    ConsoleOut = _ownedConsoleOutput;
                    ConsoleActive = true;
                }
                catch (Exception failure)
                {
                    try { ReleaseConsole(); }
                    catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                    throw;
                }
            }
        }

        public void DetachConsole()
        {
            lock (_gate)
                ReleaseConsole();
        }

        public void PreventClose()
        {
            lock (_gate)
                if (ConsoleActive && !_desktopActive)
                    WindowsConsoleSession.PreventClose();
        }

        public void SetConsoleColor(ConsoleColor color)
        {
            if ((uint)color > 15)
                throw new ArgumentOutOfRangeException(nameof(color));
            lock (_gate)
            {
                if (!ConsoleActive || _desktopActive)
                    return;
                if (_session != null)
                    _session.SetForegroundColor(color);
                else
                    Console.ForegroundColor = color;
            }
        }

        public void SetConsoleTitle(string title)
        {
            lock (_gate)
                if (ConsoleActive && !_desktopActive)
                    WindowsConsoleSession.SetTitle(title);
        }

        private static TextWriter CreateWriter(SafeFileHandle handle, Encoding encoding)
        {
            var stream = new WindowsConsoleOutputStream(handle, encoding);
            return TextWriter.Synchronized(new StreamWriter(stream, encoding) { AutoFlush = true });
        }

        private void ReleaseConsole()
        {
            // Detaching the compatibility writer must not stop shared capture or close the desktop application.
            Volatile.Write(ref _desktopActive, false);
            ConsoleActive = false;
            StandardOut = _initialOutput;
            ConsoleOut = null;
            var errors = new List<Exception>();
            if (ReferenceEquals(_ownedConsoleOutput, _ownedStandardOutput))
                _ownedStandardOutput = null;
            DisposeWriter(ref _ownedConsoleOutput, errors);
            DisposeWriter(ref _ownedStandardOutput, errors);

            try
            {
                _session?.Dispose();
                _session = null;
            }
            catch (Exception error)
            {
                // Retain the session if native restoration failed; its handles may
                // still be installed in the process standard-handle table.
                errors.Add(error);
            }

            if (errors.Count == 1)
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Could not fully release console output.", errors);
        }

        private static void DisposeWriter(ref TextWriter writer, List<Exception> errors)
        {
            var owned = writer;
            writer = null;
            try { owned?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
    }
}
