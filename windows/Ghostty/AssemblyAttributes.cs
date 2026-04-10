// Disable runtime marshalling for the entire assembly so the LibraryImport
// source generator can emit blittable stubs for structs like GhosttyRuntimeConfig,
// GhosttySurfaceConfig, and GhosttyInputKey. Without this attribute the generator
// refuses to handle non-primitive value types and falls back to SYSLIB1051 errors.
// All P/Invoke structs in this project are already blittable (no managed references,
// explicit layouts, fixed-size fields), so disabling runtime marshalling is safe.
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]
