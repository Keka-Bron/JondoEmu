using System.Runtime.CompilerServices;

// The test project reaches a handful of internals — session caches, mostly — because that is what
// the checks are about: state that must not leak between the eight clients. Widening the public
// surface just so a test can see it would be worse; the leak this guards against is exactly the
// kind of thing that has no business being public.
[assembly: InternalsVisibleTo("Jondo.Unity.Tests")]
