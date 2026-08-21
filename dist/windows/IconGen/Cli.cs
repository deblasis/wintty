namespace Ghostty.IconGen;

internal enum Channel
{
    Stable,
    Nightly,
}

internal sealed record Options(Channel Channel, string OutputDir, Edition Edition);

internal static class Cli
{
    public static Options Parse(string[] args)
    {
        Channel? channel = null;
        string? outputDir = null;
        // Optional, and defaulting to the unmarked mark: this repo builds
        // one product, and the editions are selected by the tier repo that
        // packages them. A missing --edition has to keep producing exactly
        // what it produced before editions existed.
        var edition = Edition.None;

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
                case "--edition":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--edition requires a value");
                    edition = args[++i].ToLowerInvariant() switch
                    {
                        "none" => Edition.None,
                        "pro" => Edition.Pro,
                        "enterprise" => Edition.Enterprise,
                        "legacy" => Edition.Legacy,
                        "oss" => Edition.Oss,
                        var other => throw new ArgumentException(
                            $"Unknown edition '{other}'. Expected 'none', 'pro', "
                            + "'enterprise', 'legacy' or 'oss'."),
                    };
                    break;
            }
        }

        if (channel is null)
            throw new ArgumentException("--channel is required");
        if (outputDir is null)
            throw new ArgumentException("--out is required");

        return new Options(channel.Value, outputDir, edition);
    }
}
