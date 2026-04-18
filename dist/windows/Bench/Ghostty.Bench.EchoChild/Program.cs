// Copies every byte arriving on stdin to stdout.
// Spawned by Ghostty.Bench to measure transport cost independent of any real shell.
try
{
    using var stdin = Console.OpenStandardInput();
    using var stdout = Console.OpenStandardOutput();
    stdin.CopyTo(stdout);
}
catch (IOException)
{
    // Parent closed the pipe. Normal shutdown.
}
