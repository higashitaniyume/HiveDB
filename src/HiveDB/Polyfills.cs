// Polyfill for netstandard2.1: IsExternalInit is required for init-only properties and record types.

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices;

internal sealed class IsExternalInit { }
#endif
