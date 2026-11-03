using System.Reflection;
using System.Reflection.Emit;
using Object = UnityEngine.Object;

namespace SonsSdk.Attributes;

internal static class DynamicInitializerStore
{
    private static readonly Dictionary<Type, Func<IntPtr, Object>> Initializers = new();

    public static Func<IntPtr, Object> GetInitializer(Type t)
    {
        if (Initializers.TryGetValue(t, out var func))
        {
            return func;
        }

        func = Create(t);
        Initializers.Add(t, func);
        return func;
    }

    private static Func<IntPtr, Object> Create(Type type)
    {
        var dynamicMethod = new DynamicMethod("Initializer<" + type.AssemblyQualifiedName + ">", type, new[] { typeof(IntPtr) });
        dynamicMethod.DefineParameter(0, ParameterAttributes.None, "pointer");

        var ilGenerator = dynamicMethod.GetILGenerator();
        var pointerConstructor = type.GetConstructor(new[] { typeof(IntPtr) });

        if (pointerConstructor != null)
        {
            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Newobj, pointerConstructor);
        }

        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<Func<IntPtr, Object>>();
    }
}
