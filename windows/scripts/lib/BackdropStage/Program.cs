using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BackdropStage;

/// <summary>
/// A window that stands in for "whatever is behind the terminal".
///
/// A contrast measurement taken in front of whatever the developer happened
/// to have open is a measurement of that developer's desktop. This process
/// gives a harness a backdrop it chooses, can move, and can repaint between
/// photographs, so the scene behind the chrome is a named input to the run.
///
/// It follows the test seam's shape on purpose: one named pipe, one JSON
/// line per request, one line back, the pipe named after a session token in
/// the environment (WINTTY_BACKDROP_STAGE, 32 hex characters),
/// CurrentUserOnly, and every ack sent AFTER the paint has landed, so a
/// driver that photographs on the ack photographs the scene it asked for.
/// Nothing here can reach the app under test.
///
/// It never activates (WS_EX_NOACTIVATE, ShowWithoutActivation): a harness
/// measuring an activated window must not have its subject deactivated by
/// its own scenery. TOPMOST, so it sits above every ordinary window; the
/// harness places the window under test TOPMOST afterwards, which lands it
/// above this one.
///
/// Protocol: {"op":"place","x","y","w","h"} in device pixels,
/// {"op":"solid","color":"#RRGGBB"}, {"op":"image","path","mode":"stretch|tile"},
/// {"op":"query"}, {"op":"quit"}. Every response carries ok, op, hwnd, pid,
/// the window rect and the scene.
///
/// Exit codes: 0 after quit, 2 on an internal failure, 120 when the token is
/// missing or malformed (incoda's "usage" code, so a wrapper can tell a
/// refusal from a crash).
/// </summary>
internal static class Program
{
    private const string EnvVar = "WINTTY_BACKDROP_STAGE";
    private const string PipeNamePrefix = "wintty-backdrop-stage-";
    private const int TokenLength = 32;
    private const int MaxRequestBytes = 16 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable(EnvVar);
        if (!IsSessionToken(token))
        {
            Console.Error.WriteLine(
                $"STAGE_REFUSED {EnvVar} must hold a {TokenLength}-character hex session token");
            return 120;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            var stage = new StageForm(
                ArgInt(args, "--x", 0), ArgInt(args, "--y", 0),
                ArgInt(args, "--w", 800), ArgInt(args, "--h", 600));
            var pipeName = PipeNamePrefix + token;
            var server = new Thread(() => Serve(stage, pipeName)) { IsBackground = true };
            stage.Shown += (_, _) =>
            {
                server.Start();
                // READY is the driver's cue that the HWND exists and the pipe
                // is about to be listening. Printed from the UI thread after
                // Shown so the handle in it is a real, visible window.
                Console.Out.WriteLine($"READY {stage.Handle.ToInt64()} {Environment.ProcessId}");
                Console.Out.Flush();
            };
            Application.Run(stage);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"STAGE_FAIL {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static bool IsSessionToken(string? value)
    {
        if (value is null || value.Length != TokenLength) return false;
        foreach (var c in value)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    private static void Serve(StageForm stage, string pipeName)
    {
        while (!stage.IsDisposed)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something already owns this token's name. Going quiet is the
                // seam's answer too: the driver's connect times out and says so.
                Console.Error.WriteLine($"STAGE_FAIL pipe name taken: {ex.Message}");
                return;
            }
            try
            {
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false))
                {
                    AutoFlush = true, NewLine = "\n",
                };
                while (pipe.IsConnected && !stage.IsDisposed)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    if (line.Length > MaxRequestBytes)
                    {
                        writer.WriteLine(Error("parse", "request too long"));
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var (response, quit) = Execute(stage, line);
                    writer.WriteLine(response);
                    if (quit)
                    {
                        stage.BeginInvoke(stage.Close);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The client hung up mid-line; accept the next one.
            }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                pipe.Dispose();
            }
        }
    }

    private static (string Response, bool Quit) Execute(StageForm stage, string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex) { return (Error("parse", ex.Message), false); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("op", out var opEl)
                || opEl.ValueKind != JsonValueKind.String)
                return (Error("parse", "request needs a string 'op'"), false);
            var op = opEl.GetString()!;

            try
            {
                // Every mutation runs on the UI thread and Refresh() is
                // synchronous, so by the time Invoke returns the pixels are on
                // screen and the ack below is not a promise.
                string? failure = null;
                stage.Invoke(() =>
                {
                    switch (op)
                    {
                        case "place":
                            stage.Place(ArgInt(root, "x"), ArgInt(root, "y"),
                                        ArgInt(root, "w"), ArgInt(root, "h"));
                            break;
                        case "solid":
                            stage.SetSolid(ArgString(root, "color") ?? "#000000");
                            break;
                        case "image":
                            stage.SetImage(ArgString(root, "path") ?? "",
                                           ArgString(root, "mode") ?? "stretch");
                            break;
                        case "query":
                        case "quit":
                            break;
                        default:
                            failure = $"unknown op '{op}'";
                            break;
                    }
                });
                if (failure is not null) return (Error(op, failure), false);
                return (Ok(stage, op), op == "quit");
            }
            catch (Exception ex)
            {
                return (Error(op, ex.InnerException?.Message ?? ex.Message), false);
            }
        }
    }

    private static string Ok(StageForm stage, string op)
    {
        var rect = stage.ScreenRect();
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("op", op);
            json.WriteNumber("hwnd", stage.Handle.ToInt64());
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteNumber("x", rect.Left);
            json.WriteNumber("y", rect.Top);
            json.WriteNumber("w", rect.Right - rect.Left);
            json.WriteNumber("h", rect.Bottom - rect.Top);
            json.WriteString("scene", stage.SceneDescription);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Error(string op, string message)
        => JsonSerializer.Serialize(new { ok = false, op, error = message });

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    private static int ArgInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : throw new ArgumentException($"'{name}' is required");

    private static string? ArgString(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>
/// The window itself: a borderless, non-activating, topmost surface that
/// paints one scene. Solid or image; the image is copied out of its file at
/// load so the file is never held open (a harness regenerates scenes in
/// place between runs).
/// </summary>
internal sealed class StageForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
    private const int WM_GETMINMAXINFO = 0x0024;

    private Color _solid = Color.Black;
    private Image? _image;
    private string _mode = "stretch";
    private readonly int _x, _y, _w, _h;

    public string SceneDescription { get; private set; } = "solid #000000";

    public StageForm(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;
        Text = "Wintty Backdrop Stage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // TopMost via SetWindowPos below rather than the property, so the
        // placement and the z-order land in one call with SWP_NOACTIVATE.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Place(_x, _y, _w, _h);
    }

    /// <summary>
    /// The stage must be able to be BIGGER than the monitor: a harness
    /// grows it past the window under test on every side so a translucent
    /// frame at the window's edge still has scene behind it, and a window
    /// under test that fills the screen needs a stage that overhangs it.
    /// Windows answers WM_GETMINMAXINFO with the monitor's size as the
    /// maximum tracking size and SetWindowPos honours it, so without this
    /// override a place larger than the screen is silently clamped, and the
    /// query would report a rect the caller never asked for.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GETMINMAXINFO)
        {
            base.WndProc(ref m);
            var info = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            info.ptMaxTrackSize = new POINT { X = 32000, Y = 32000 };
            info.ptMaxSize = info.ptMaxTrackSize;
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }
        base.WndProc(ref m);
    }

    public void Place(int x, int y, int w, int h)
    {
        if (w < 1 || h < 1) throw new ArgumentException("w and h must be positive");
        if (!SetWindowPos(Handle, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW))
            throw new InvalidOperationException(
                $"SetWindowPos failed ({Marshal.GetLastWin32Error()})");
        Refresh();
    }

    public RECT ScreenRect()
    {
        GetWindowRect(Handle, out var r);
        return r;
    }

    public void SetSolid(string hex)
    {
        _solid = ParseColor(hex);
        _image?.Dispose();
        _image = null;
        SceneDescription = $"solid {hex}";
        Refresh();
    }

    public void SetImage(string path, string mode)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("no such image", path);
        if (mode is not ("stretch" or "tile"))
            throw new ArgumentException($"unknown mode '{mode}'");
        // Copied out of the file so nothing holds it open afterwards.
        var bytes = File.ReadAllBytes(path);
        var loaded = Image.FromStream(new MemoryStream(bytes));
        _image?.Dispose();
        _image = loaded;
        _mode = mode;
        SceneDescription = $"image {mode} {path}";
        Refresh();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_solid);
        if (_image is null) return;
        if (_mode == "tile")
        {
            using var brush = new TextureBrush(_image);
            g.FillRectangle(brush, ClientRectangle);
            return;
        }
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(_image, ClientRectangle);
    }

    private static Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            throw new ArgumentException($"colour must be #RRGGBB, got '{hex}'");
        return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
