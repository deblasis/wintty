namespace Ghostty.Core.Profiles;

/// <summary>
/// Where a profile launch is routed when the user clicks the
/// new-tab split button or invokes a profile from the command
/// palette (PR 5). Order is part of the contract: defensive test
/// in <c>Ghostty.Tests/Profiles/ProfileLaunchTargetTests.cs</c>
/// guards against accidental reorder.
/// </summary>
public enum ProfileLaunchTarget
{
    NewTab,
    NewPane,
    NewWindow,
}
