using System.Runtime.CompilerServices;

// Tests need to exercise the local process cache without exposing launcher-only state publicly.
[assembly: InternalsVisibleTo("Jondo.Unity.Tests")]
