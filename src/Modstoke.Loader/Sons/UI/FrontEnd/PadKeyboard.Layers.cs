// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;

namespace Modstoke.Loader
{
    internal sealed partial class PadKeyboard
    {
        /// <summary>One page of the grid: five rows of eleven characters, and what the layer key shows to switch to it.</summary>
        private sealed class Layer
        {
            internal Layer(string name, params string[] rows)
            {
                Name = name;
                Rows = rows;
            }

            internal string Name { get; }
            internal string[] Rows { get; }
        }

        private const string Digits = "1234567890-", Symbols = "()/:;&+=#\"*";

        // The letters a language uses most beyond a-z, on the letters page's last row, ahead of the common symbols. The rest
        // are on the accents page.
        private static readonly Dictionary<string, string> OwnLetters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "äöüß",
            ["fr"] = "éèàçêâôîûùœ",
            ["es"] = "áéíóúñü",
            ["it"] = "àèéìòù",
            ["pt"] = "ãõçáéíóúêôà",
            ["pl"] = "ąćęłńóśźż",
            ["cs"] = "áčéěířšýžůú",
            ["tr"] = "çğıöşü",
            ["sv"] = "åäöé",
            ["fi"] = "äöå"
        };

        // Every accented letter the shipped languages write, in alphabetical order, and the marks Spanish and French quote with.
        private static readonly Layer Accents = new("àé", "àáâãäåæąçćč", "ďèéêëěęğìíî", "ïıłñńňòóôõö", "œřśšşßťùúûü", "ůýÿźżž¿¡«»€");

        private static readonly Layer Marks = new("#+=", "!@#$%^&*()?", "-_=+[]{}\\|/", ";:'\",.<>`~…", "€£¥¢°§©®™×÷", "¿¡«»„“”‘’·±");

        // ЙЦУКЕН, as a Russian keyboard has it.
        private static readonly Layer Cyrillic = new("абв", Digits, "йцукенгшщзх", "фывапролджэ", "ячсмитьбюъё", ",.?!'\"():;@");

        // German and Czech players know the letters as QWERTZ and French ones as AZERTY. Russian gets its own alphabet after
        // the Latin one; Chinese, Japanese and Korean stay on Latin letters, since their scripts need an input method.
        private static List<Layer> LayersFor(string language)
        {
            var code = Languages.BaseOf(language);
            var top = code switch { "de" or "cs" => "qwertzuiop'", "fr" => "azertyuiop'", _ => "qwertyuiop'" };
            var middle = code == "fr" ? "qsdfghjklm@" : "asdfghjkl@_";
            var bottom = code switch { "de" or "cs" => "yxcvbnm,.?!", "fr" => "wxcvbn_,.?!", _ => "zxcvbnm,.?!" };
            var own = OwnLetters.TryGetValue(code, out var letters) ? letters : "";
            var layers = new List<Layer> { new("abc", Digits, top, middle, bottom, (own + Symbols).Substring(0, Columns)) };
            if (code == "ru") layers.Add(Cyrillic);
            layers.Add(Accents);
            layers.Add(Marks);
            return layers;
        }
    }
}
