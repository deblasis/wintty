namespace Ghostty.Core.Tabs;

/// <summary>
/// OSC 9;4 progress state mapped 1:1 onto the discriminated cases
/// libghostty surfaces report. The percent value is meaningless for
/// <see cref="Kind.None"/> and <see cref="Kind.Indeterminate"/>.
///
/// Pure-logic record (no WinUI types): consumed by the per-tab inline
/// indicator in <c>TabHost</c> and by the <c>TaskbarProgressCoordinator</c>
/// introduced in plan 4.
/// </summary>
internal readonly record struct TabProgressState(TabProgressState.Kind State, int Percent)
{
    public enum Kind
    {
        None,
        Indeterminate,
        Normal,
        Paused,
        Error,
    }

    public static TabProgressState None => new(Kind.None, 0);
    public static TabProgressState Indeterminate => new(Kind.Indeterminate, 0);
    public static TabProgressState Normal(int p) => new(Kind.Normal, p);
    public static TabProgressState Paused(int p) => new(Kind.Paused, p);
    public static TabProgressState Error(int p) => new(Kind.Error, p);
}
