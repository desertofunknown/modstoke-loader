#if NETFRAMEWORK || NETSTANDARD || MODSTOKE_HARMONY
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(Mono.Cecil.Pdb.NativePdbReader))]
[assembly: TypeForwardedTo(typeof(Mono.Cecil.Pdb.NativePdbWriter))]
#endif
