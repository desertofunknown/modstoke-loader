// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Modstoke.Diagnostics.Desktop;

internal static class Native
{
    internal const uint Child = 0x40000000, Visible = 0x10000000, TabStop = 0x10000;
    internal const uint SetFont = 0x30, Command = 0x111, Timer = 0x113, DrawItem = 0x2b;
    internal const uint LbAddString = 0x180, LbResetContent = 0x184;
    internal const uint LbSetCurSel = 0x186, LbGetCurSel = 0x188, LbGetTopIndex = 0x18e, LbSetTopIndex = 0x197;
    internal const uint LbSetItemHeight = 0x1a0;
    internal const uint CbAddString = 0x143, CbResetContent = 0x14b, CbGetCurSel = 0x147, CbSetCurSel = 0x14e;
    internal const uint TextLeft = 0, TextCenter = 1, TextVCenter = 4, TextSingleLine = 0x20, TextNoPrefix = 0x800, TextEllipsis = 0x8000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? Menu;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal Rect(int left, int top, int right, int bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Paint
    {
        internal nint Device;
        internal int Erase;
        internal Rect Area;
        internal int Restore, Update;
        internal fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawItemData
    {
        internal uint Type, ControlId, ItemId, Action, State;
        internal nint Window, Device;
        internal Rect Bounds;
        internal nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MeasureItemData
    {
        internal uint Type, ControlId, ItemId, ItemWidth, ItemHeight;
        internal nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        internal Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OpenFileName
    {
        internal uint Size;
        internal nint Owner, Instance;
        internal string Filter;
        internal nint CustomFilter;
        internal uint MaxCustomFilter, FilterIndex;
        internal StringBuilder File;
        internal uint MaxFile;
        internal nint FileTitle;
        internal uint MaxFileTitle;
        internal string? InitialDirectory;
        internal string Title;
        internal uint Flags;
        internal ushort FileOffset, FileExtension;
        internal string DefaultExtension;
        internal nint CustomData, Hook;
        internal string? Template;
        internal nint Reserved;
        internal uint ReservedValue, FlagsEx;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SystemParametersInfo(uint action, uint parameter, out Rect value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool IsDialogMessage(nint window, ref Message message);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(nint window, bool enable);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] internal static extern nint SendText(nint window, uint message, nuint wParam, string text);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll")] internal static extern int FillRect(nint device, ref Rect rect, nint brush);
    [DllImport("user32.dll")] internal static extern int FrameRect(nint device, ref Rect rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(nint device, string text, int length, ref Rect rect, uint format);
    [DllImport("user32.dll")] internal static extern bool DrawFocusRect(nint device, ref Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(nint owner, string text, string caption, uint type);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint device, nint handle);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint device, uint color);
    [DllImport("gdi32.dll")] internal static extern uint SetBkColor(nint device, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint device, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] internal static extern int SetWindowTheme(nint window, string application, string? idList);
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetSaveFileName(ref OpenFileName file);
    [DllImport("comdlg32.dll")] internal static extern uint CommDlgExtendedError();
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
}
