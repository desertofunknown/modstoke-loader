// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Modstoke.Bootstrap
{
    internal static class StateJson
    {
        internal static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return JsonEncodedText.Encode(value).ToString();
        }

        internal static void AppendProperty(StringBuilder builder, string name, string value, bool quote, int indent = 2, bool comma = true)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(Escape(name));
            builder.Append("\": ");
            if (quote)
            {
                builder.Append('"');
                builder.Append(Escape(value));
                builder.Append('"');
            }
            else
            {
                builder.Append(value);
            }

            if (comma)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        // Closes the document: the phase list is always the last property.
        internal static void AppendPhases(StringBuilder builder, IReadOnlyList<PhaseRecord> phases)
        {
            builder.AppendLine("  \"phases\": [");
            for (var i = 0; i < phases.Count; i++)
            {
                var phase = phases[i];
                builder.AppendLine("    {");
                AppendProperty(builder, "name", phase.Name, true, 6);
                AppendProperty(builder, "status", phase.Status, true, 6);
                AppendProperty(builder, "startedUtc", phase.StartedUtc, true, 6);
                AppendProperty(builder, "completedUtc", phase.CompletedUtc, true, 6);
                AppendProperty(builder, "exceptionType", phase.ExceptionType, true, 6);
                AppendProperty(builder, "detail", phase.Detail, true, 6, false);
                builder.Append("    }");
                if (i + 1 < phases.Count)
                {
                    builder.Append(",");
                }

                builder.AppendLine();
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");
        }
    }

    internal sealed class PhaseRecord
    {
        internal PhaseRecord(string name)
        {
            Name = name;
        }

        internal string Name { get; }
        internal string Status { get; set; } = "PENDING";
        internal string StartedUtc { get; set; } = "";
        internal string CompletedUtc { get; set; } = "";
        internal string ExceptionType { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
