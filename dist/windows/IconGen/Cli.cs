namespace Ghostty.IconGen;

internal enum Channel
{
    Stable,
    Nightly,
}

internal sealed record Options(Channel Channel, string OutputDir);

internal static class Cli
{
    public static Options Parse(string[] args)
    {
        Channel? channel = null;
        string? outputDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--channel":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--channel requires a value");
                    channel = args[++i].ToLowerInvariant() switch
                    {
                        "stable" => Channel.Stable,
                        "nightly" => Channel.Nightly,
                        var other => throw new ArgumentException(
                            $"Unknown channel '{other}'. Expected 'stable' or 'nightly'."),
                    };
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--out requires a value");
                    outputDir = args[++i];
                    break;
            }
        }

        if (channel is null)
            throw new ArgumentException("--channel is required");
        if (outputDir is null)
            throw new ArgumentException("--out is required");

        return new Options(channel.Value, outputDir);
    }
}
