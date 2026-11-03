#if NETFRAMEWORK || NETSTANDARD || MODSTOKE_HARMONY
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(MonoMod.Utils.Cil.CecilILGenerator))]
[assembly: TypeForwardedTo(typeof(MonoMod.Utils.Cil.ILGeneratorShim))]
#endif
