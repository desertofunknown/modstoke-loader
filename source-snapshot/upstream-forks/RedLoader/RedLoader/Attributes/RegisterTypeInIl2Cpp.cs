using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;

namespace RedLoader
{
    [AttributeUsage(AttributeTargets.Class)]
    public class RegisterTypeInIl2Cpp : Attribute //Naming violation?
    {
        internal static List<Assembly> registrationQueue = new List<Assembly>();
        internal static bool ready;
        internal bool LogSuccess = true;
        private static readonly HashSet<Assembly> RegisteringAssemblies = new();

        public RegisterTypeInIl2Cpp() { }
        public RegisterTypeInIl2Cpp(bool logSuccess) { LogSuccess = logSuccess; }

        public static void RegisterAssembly(Assembly asm)
        {
            if (!ready)
            {
                registrationQueue.Add(asm);
                return;
            }

            if (!RegisteringAssemblies.Add(asm))
                return;

            try
            {
                foreach (var type in GetTypes(asm))
                {
                    if (type == null)
                        continue;

                    try
                    {
                        var attributes = type.GetCustomAttributes(typeof(RegisterTypeInIl2Cpp), false);
                        if (attributes.Length == 0 || attributes[0] is not RegisterTypeInIl2Cpp attribute)
                            continue;

                        ClassInjector.RegisterTypeInIl2Cpp(type, new RegisterTypeOptions { LogSuccess = attribute.LogSuccess });
                    }
                    catch (Exception exception)
                    {
                        ReportFailure("register type", asm, type, exception);
                    }
                }
            }
            finally
            {
                RegisteringAssemblies.Remove(asm);
            }
        }

        internal static void SetReady()
        {
            if (ready)
                return;

            // Native registration and its log callbacks may reenter this API.
            var pending = registrationQueue;
            registrationQueue = null;
            ready = true;

            if (pending == null)
                return;

            foreach (var asm in pending)
            {
                try
                {
                    RegisterAssembly(asm);
                }
                catch (Exception exception)
                {
                    ReportFailure("register assembly", asm, null, exception);
                }
            }
        }

        private static Type[] GetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                ReportFailure("load all types", assembly, null, exception);
                return exception.Types;
            }
            catch (Exception exception)
            {
                ReportFailure("load types", assembly, null, exception);
                return Array.Empty<Type>();
            }
        }

        private static void ReportFailure(string operation, Assembly assembly, Type type, Exception exception)
        {
            try
            {
                var target = type?.FullName ?? assembly?.FullName ?? "<unknown assembly>";
                RLog.Error($"Could not {operation} for IL2CPP: {target}", exception);
            }
            catch
            {
                // Logging callbacks must not interrupt the rest of the registration queue.
            }
        }
    }
}
