// SPDX-License-Identifier: GPL-3.0-or-later
// Implements the BepInEx IConsoleDriver compatibility contract.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using BepInEx;
using Microsoft.Win32.SafeHandles;

namespace OpenACAI.Endnight.Loader.Preloader
{
    [SupportedOSPlatform("windows")]
    internal sealed class OpenACAIWindowsConsoleDriver : IConsoleDriver
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private WindowsConsoleSession _session;
        private TextWriter _initialOutput;
        private TextWriter _ownedStandardOutput;
        private TextWriter _ownedConsoleOutput;
        private bool _useManagedEncoder;

        public TextWriter StandardOut { get; private set; }
        public TextWriter ConsoleOut { get; private set; }
        public bool ConsoleActive { get; private set; }
        public bool ConsoleIsExternal => true;

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

                    _ownedStandardOutput = output == null ? TextWriter.Null : CreateWriter(output, Utf8);
                    _ownedConsoleOutput = CreateWriter(_session.ConsoleHandle, encoding);
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
                if (ConsoleActive)
                    WindowsConsoleSession.PreventClose();
        }

        public void SetConsoleColor(ConsoleColor color)
        {
            lock (_gate)
            {
                Console.ForegroundColor = color;
                if (ConsoleActive)
                    _session?.SetForegroundColor(color);
            }
        }

        public void SetConsoleTitle(string title)
        {
            lock (_gate)
                if (ConsoleActive)
                    WindowsConsoleSession.SetTitle(title);
        }

        private static TextWriter CreateWriter(SafeFileHandle handle, Encoding encoding)
        {
            var stream = new WindowsConsoleOutputStream(handle);
            return TextWriter.Synchronized(new StreamWriter(stream, encoding) { AutoFlush = true });
        }

        private void ReleaseConsole()
        {
            ConsoleActive = false;
            StandardOut = _initialOutput;
            ConsoleOut = null;
            var errors = new List<Exception>();
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
