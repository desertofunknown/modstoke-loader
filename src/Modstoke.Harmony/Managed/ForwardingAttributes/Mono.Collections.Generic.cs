#if NETFRAMEWORK || NETSTANDARD || MODSTOKE_HARMONY
using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(Mono.Collections.Generic.Collection<>))]
#endif
