// Disable runtime marshalling for the entire Ghostty.Core assembly.
// Paired with IsAotCompatible=true in the csproj, this forbids any
// future hand-written P/Invoke from reintroducing runtime marshalling
// under NativeAOT. Without this guard, a hand-written [DllImport] added
// later would silently bring back the runtime marshaller.
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]
