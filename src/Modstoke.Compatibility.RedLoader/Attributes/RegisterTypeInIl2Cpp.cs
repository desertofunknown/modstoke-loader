using System;
using System.Collections.Generic;
using System.Reflection;

namespace RedLoader
{
    [AttributeUsage(AttributeTargets.Class)]
    public class RegisterTypeInIl2Cpp : Attribute //Naming violation?
    {
        internal static List<Assembly> registrationQueue = new List<Assembly>();
        internal static bool ready;
        internal bool LogSuccess = true;

        public RegisterTypeInIl2Cpp() { }
        public RegisterTypeInIl2Cpp(bool logSuccess) { LogSuccess = logSuccess; }

        public static void RegisterAssembly(Assembly asm) => TypeRegistrationDriver.RegisterAssembly(asm);

        internal static void SetReady() => TypeRegistrationDriver.SetReady();
    }
}
