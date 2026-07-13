using System.Runtime.CompilerServices;

// The track's internal bookkeeping (chunk positions, recycling) is not part of the public API —
// nothing outside Presentation has any business reading it. But it is exactly where the
// road-gap bug lived, so the test assembly is granted access to assert on it directly rather
// than forcing a public surface to exist purely for testing.
[assembly: InternalsVisibleTo("NeonRush.Tests.EditMode")]
