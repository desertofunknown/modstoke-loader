// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper.

namespace Modstoke.Preloader
{
    internal sealed class InteropTranslationFailure
    {
        public string Reason { get; }
        public string Detail { get; }
        public int? Offset { get; }
        public string OpCode { get; }
        public string Operand { get; }

        internal InteropTranslationFailure(string reason, string detail, int? offset, string opCode, string operand)
        {
            Reason = reason ?? string.Empty;
            Detail = detail ?? string.Empty;
            Offset = offset;
            OpCode = opCode ?? string.Empty;
            Operand = operand ?? string.Empty;
        }
    }
}
