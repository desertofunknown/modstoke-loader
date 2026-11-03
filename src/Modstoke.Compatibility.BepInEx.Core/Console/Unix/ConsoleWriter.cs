using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BepInEx.Unix;

internal static class ConsoleWriter
{
    private static Func<Stream, Encoding, bool, StreamWriter> cStreamWriterConstructor;

    private static Func<Stream, Encoding, bool, StreamWriter> CStreamWriterConstructor
    {
        get
        {
            if (cStreamWriterConstructor != null)
                return cStreamWriterConstructor;

            var cStreamWriter = AccessTools.TypeByName("System.IO.CStreamWriter");

            Func<Stream, Encoding, bool, StreamWriter> GetCtor(int[] parameterOrder)
            {
                var parameters = new[] { typeof(Stream), typeof(Encoding), typeof(bool) };
                var ctor = AccessTools.Constructor(cStreamWriter, parameterOrder.Select(i => parameters[i]).ToArray());
                if (ctor != null)
                    return (stream, encoding, leaveOpen) =>
                    {
                        var arguments = new object[] { stream, encoding, leaveOpen };
                        return (StreamWriter) ctor.Invoke(parameterOrder.Select(i => arguments[i]).ToArray());
                    };
                return null;
            }

            var constructorParameterOrders = new[]
            {
                new[] { 0, 1, 2 }, // Unity 5.x and up
                new[] { 0, 1 }     // Unity 4.7 and older
            };

            cStreamWriterConstructor = constructorParameterOrders.Select(GetCtor).FirstOrDefault(ctor => ctor != null);
            if (cStreamWriterConstructor == null)
                throw new AmbiguousMatchException("Failed to find suitable constructor for CStreamWriter");
            return cStreamWriterConstructor;
        }
    }

    public static TextWriter CreateConsoleStreamWriter(Stream stream, Encoding encoding, bool leaveOpen)
    {
        var writer = CStreamWriterConstructor(stream, encoding, leaveOpen);
        writer.AutoFlush = true;
        return writer;
    }
}
