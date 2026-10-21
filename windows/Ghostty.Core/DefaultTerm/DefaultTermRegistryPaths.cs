namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// The subkey paths default-terminal registration uses, either at their
/// real per-user locations or prefixed by a throwaway test root. The
/// registration engine writes nothing that does not come from here, so a
/// test that hands the engine <see cref="UnderTestRoot"/> can never touch
/// the real Classes namespace or the real Console delegation key.
/// </summary>
public sealed class DefaultTermRegistryPaths
{
    private const string StartupSuffix = @"Console\%%Startup";
    private const string ClsidPrefix = @"Software\Classes\CLSID";
    private const string InterfacePrefix = @"Software\Classes\Interface";

    /// <summary>Empty for the real locations, else the test root plus a separator.</summary>
    private readonly string _prefix;

    private DefaultTermRegistryPaths(string prefix)
    {
        _prefix = prefix;
    }

    /// <summary>The real per-user registration locations.</summary>
    public static DefaultTermRegistryPaths Production { get; } = new(string.Empty);

    /// <summary>
    /// Paths that carry every subkey under <paramref name="root"/>: the
    /// same layout the real locations have, grafted below a key nothing
    /// but the test that minted it knows.
    /// </summary>
    public static DefaultTermRegistryPaths UnderTestRoot(string root) =>
        new(root.TrimEnd('\\') + "\\");

    /// <summary>
    /// The Console delegation key holding the selected console and
    /// terminal pair.
    /// </summary>
    public string StartupSubkey => _prefix + StartupSuffix;

    /// <summary>The registration key of one handler class.</summary>
    public string ClsidSubkeyOf(string clsid) => _prefix + ClsidPrefix + "\\" + clsid;

    /// <summary>The proxy-stub registration key of one handoff interface.</summary>
    public string InterfaceSubkeyOf(string iid) => _prefix + InterfacePrefix + "\\" + iid;
}
