// Disable runtime marshalling for the entire Ghostty.Core assembly.
// Paired with IsAotCompatible=true in the csproj, this forbids any
// future hand-written P/Invoke from reintroducing runtime marshalling
// under NativeAOT. Without this guard, a hand-written [DllImport] added
// later would silently bring back the runtime marshaller.
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]

// #259 logging: Ghostty (WinUI shell) and Ghostty.Tests both consume
// internal logging types defined in this assembly (LogEvents constants,
// LoggingBootstrap, FilterState, CapturingLoggerProvider). Both
// consumers already reference Ghostty.Core via ProjectReference.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ghostty")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ghostty.Tests")]
