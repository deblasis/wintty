using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

/// <summary>
/// The program status action decoder, against real unmanaged memory laid
/// out the way the core's callback hands it over: every field copied
/// inside the callback, the surface's desktop-notifications setting
/// riding along, lifetime events decoded without a report, and anything
/// the core could not have sent refused rather than read.
/// </summary>
/// <remarks>
/// The payload struct and its report are borrowed for the callback only,
/// so the decode is the one place the app walks those pointers; these
/// rows are the guard that it copies out and refuses, never trusts.
/// </remarks>
public class ProgramStatusActionDecodeTests
{
    private sealed class Native : IDisposable
    {
        public readonly List<nint> _allocs = new();
        public nint Bytes(string s, out nuint len)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var p = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, p, b.Length);
            _allocs.Add(p);
            len = (nuint)b.Length;
            return p;
        }
        public nint Struct<T>(T value) where T : struct
        {
            var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, p, false);
            _allocs.Add(p);
            return p;
        }
        public void Dispose() { foreach (var p in _allocs) Marshal.FreeHGlobal(p); }
    }

    private static nint Report(Native n, int state, int kind, sbyte progress,
        string id, string app, string title, string message, nuint? messageLen = null, bool nullMessage = false, bool notify = true)
    {
        var r = new GhosttyActionProgramStatusReport
        {
            State = state,
            Kind = kind,
            Progress = progress,
            Id = n.Bytes(id, out var idLen), IdLen = idLen,
            App = n.Bytes(app, out var appLen), AppLen = appLen,
            Title = n.Bytes(title, out var titleLen), TitleLen = titleLen,
            Message = nullMessage ? 0 : n.Bytes(message, out _),
        };
        r.MessageLen = messageLen ?? (nuint)Encoding.UTF8.GetByteCount(message);
        return n.Struct(new GhosttyActionProgramStatus { Event = 0, DesktopNotifications = notify ? (byte)1 : (byte)0, Report = n.Struct(r) });
    }

    [Fact]
    public void Decode_AReport_CopiesEveryField()
    {
        using var n = new Native();
        var p = Report(n, (int)ProgramStatusState.Blocked, (int)ProgramStatusKind.Permission, 40,
            "a/b", "terraform", "Plan", "Apply 3 changes? é");
        Assert.True(ProgramStatusActionDecoder.TryDecode(p, out var e));
        Assert.Equal(ProgramStatusEventKind.Report, e.Kind);
        Assert.Equal(new ProgramStatusReport(ProgramStatusState.Blocked, ProgramStatusKind.Permission, 40,
            "a/b", "terraform", "Plan", "Apply 3 changes? é"), e.Report);
        Assert.True(e.DesktopNotifications);
    }

    [Fact]
    public void Decode_CarriesTheSurfacesDesktopNotificationsSetting()
    {
        using var n = new Native();
        Assert.True(ProgramStatusActionDecoder.TryDecode(
            Report(n, (int)ProgramStatusState.Blocked, 0, -1, "", "", "", "", notify: false), out var off));
        Assert.False(off.DesktopNotifications);
        Assert.True(ProgramStatusActionDecoder.TryDecode(
            Report(n, (int)ProgramStatusState.Blocked, 0, -1, "", "", "", "", notify: true), out var on));
        Assert.True(on.DesktopNotifications);
    }

    [Theory]
    [InlineData(1, ProgramStatusEventKind.Reset)]
    [InlineData(2, ProgramStatusEventKind.PromptStart)]
    public void Decode_LifetimeEvents_NeedNoReport(int raw, ProgramStatusEventKind kind)
    {
        using var n = new Native();
        var p = n.Struct(new GhosttyActionProgramStatus { Event = raw, Report = 0 });
        Assert.True(ProgramStatusActionDecoder.TryDecode(p, out var e));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public void Decode_RefusesWhatTheCoreCouldNotHaveSent()
    {
        using var n = new Native();
        Assert.False(ProgramStatusActionDecoder.TryDecode(0, out _));
        Assert.False(ProgramStatusActionDecoder.TryDecode(n.Struct(new GhosttyActionProgramStatus { Event = 3 }), out _));
        Assert.False(ProgramStatusActionDecoder.TryDecode(n.Struct(new GhosttyActionProgramStatus { Event = 0, Report = 0 }), out _));
        Assert.False(ProgramStatusActionDecoder.TryDecode(Report(n, 6, 0, -1, "", "", "", ""), out _));
        Assert.False(ProgramStatusActionDecoder.TryDecode(Report(n, 1, 4, -1, "", "", "", ""), out _));
        // A length past the limit is refused before anything is read.
        Assert.False(ProgramStatusActionDecoder.TryDecode(Report(n, 1, 0, -1, "", "", "", "x", messageLen: 2049), out _));
        // A length with no buffer behind it.
        Assert.False(ProgramStatusActionDecoder.TryDecode(Report(n, 1, 0, -1, "", "", "", "x", messageLen: 5, nullMessage: true), out _));
    }
}
