/*
 * vt_resize: a resize-aware child for the oracle's `compare-resize` mode.
 *
 * THE QUESTION. A raw-pipe transport has no console, so it cannot
 * ResizePseudoConsole. The injection-free substitute is in-band resize
 * (DECSET 2048): the emulator emits `CSI 48;rows;cols;hpix;wpix t` on the
 * child's stdin and a 2048-aware child redraws to the new size. Under
 * ConPTY, by contrast, the child is attached to a real console and learns
 * resize the classic way: a WINDOW_BUFFER_SIZE_EVENT from ReadConsoleInput.
 *
 * So this program detects resize through BOTH mechanisms and drives the
 * SAME redraw from either. The oracle runs it under ConPTY (triggering
 * resize via ResizePseudoConsole) and over a raw pipe (triggering resize by
 * emitting the 2048 report), then compares the resulting cell grids. If they
 * are identical, the raw-pipe 2048 path is a faithful substitute for
 * ConPTY's console-API resize -- the fidelity claim for resize.
 *
 * Transport is discriminated exactly as a production transport would: if
 * stdin is a console (GetConsoleMode succeeds) we are under ConPTY and use
 * ReadConsoleInput; otherwise stdin is a pipe and we scan it for a 2048
 * report. The redraw encodes the learned size into the grid (a rule exactly
 * `cols` wide + a marker on the last row) so a wrong or missing size is
 * visible as a divergent grid, not a silent pass.
 *
 * Deterministic: no PID/handle/time, fixed redraw for a given size. VT out
 * via WriteFile only; UTF-8 CP set to match production wintty.
 */
#include <windows.h>

#define ENABLE_VIRTUAL_TERMINAL_INPUT_ 0x0200

static void emit(HANDLE h, const char *s, DWORD n) {
    DWORD w;
    WriteFile(h, s, n, &w, NULL);
}
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

/* Redraw to (cols, rows). The grid encodes both dimensions so a wrong or
 * absent resize shows up as a divergent dump rather than passing silently:
 *  - "SIZE=<cols>x<rows>" on the home row,
 *  - a rule of exactly `cols` '#' on row 2 (encodes width in wrap position),
 *  - "BOTTOM" anchored to the last row (encodes height). */
static void redraw(HANDLE o, unsigned cols, unsigned rows) {
    char b[256];
    int n;

    W(o, "\x1b[2J\x1b[H");

    n = wsprintfA(b, "SIZE=%ux%u\r\n", cols, rows);
    emit(o, b, (DWORD)n);

    /* Clamp the rule so a bogus size can't make us write megabytes. */
    unsigned rule = cols;
    if (rule > 240) rule = 240;
    for (unsigned i = 0; i < rule; i++) emit(o, "#", 1);

    n = wsprintfA(b, "\x1b[%u;1HBOTTOM", rows);
    emit(o, b, (DWORD)n);
}

/* Scan a byte buffer for `CSI 48 ; rows ; cols ; ...t` (an in-band size
 * report). On success fill cols/rows and return 1. Minimal parser: finds
 * ESC '[' '4' '8' ';', reads the row and col decimal fields, ignores the
 * pixel fields up to the final 't'. */
static int parse_2048(const char *buf, DWORD len, unsigned *cols, unsigned *rows) {
    for (DWORD i = 0; i + 5 < len; i++) {
        if (buf[i] == 0x1b && buf[i + 1] == '[' &&
            buf[i + 2] == '4' && buf[i + 3] == '8' && buf[i + 4] == ';') {
            DWORD j = i + 5;
            unsigned r = 0, c = 0;
            while (j < len && buf[j] >= '0' && buf[j] <= '9') r = r * 10 + (buf[j++] - '0');
            if (j >= len || buf[j] != ';') continue;
            j++;
            while (j < len && buf[j] >= '0' && buf[j] <= '9') c = c * 10 + (buf[j++] - '0');
            if (r == 0 || c == 0) continue;
            *rows = r;
            *cols = c;
            return 1;
        }
    }
    return 0;
}

int main(void) {
    HANDLE o = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    SetConsoleOutputCP(65001);

    DWORD inmode = 0;
    BOOL in_is_console = GetConsoleMode(in, &inmode);

    /* Enable in-band resize (mode 2048) and announce readiness. On the raw
     * pipe this is what tells the emulator to send us size reports; under
     * ConPTY conhost ignores it (measured) and we fall back to console
     * input events below. Either way, "READY" makes the initial drain
     * deterministic across transports. */
    W(o, "\x1b[?2048h");
    W(o, "READY\r\n");

    if (in_is_console) {
        /* ConPTY path: wait for a WINDOW_BUFFER_SIZE_EVENT. */
        for (int tries = 0; tries < 60; tries++) {
            if (WaitForSingleObject(in, 100) != WAIT_OBJECT_0) continue;
            INPUT_RECORD rec[16];
            DWORD nread = 0;
            if (!ReadConsoleInputW(in, rec, 16, &nread)) break;
            for (DWORD k = 0; k < nread; k++) {
                if (rec[k].EventType == WINDOW_BUFFER_SIZE_EVENT) {
                    /* dwSize is the SCREEN BUFFER size; its height is sticky
                     * on shrink (the buffer keeps its scrollback rows), so it
                     * reports the OLD row count when the viewport shrinks
                     * (measured: 120x30->80x24 delivers dwSize 80x30). The
                     * true visible size is the viewport rectangle srWindow --
                     * what a correct TUI uses. The raw-pipe 2048 report
                     * carries the exact viewport size with no such ambiguity. */
                    CONSOLE_SCREEN_BUFFER_INFO csbi;
                    unsigned cols, rows;
                    if (GetConsoleScreenBufferInfo(o, &csbi)) {
                        cols = (unsigned)(csbi.srWindow.Right - csbi.srWindow.Left + 1);
                        rows = (unsigned)(csbi.srWindow.Bottom - csbi.srWindow.Top + 1);
                    } else {
                        COORD sz = rec[k].Event.WindowBufferSizeEvent.dwSize;
                        cols = (unsigned)sz.X;
                        rows = (unsigned)sz.Y;
                    }
                    redraw(o, cols, rows);
                    return 0;
                }
            }
        }
        /* No resize event arrived: record that, so the grid is non-empty
         * and the divergence (if any) is legible rather than blank. */
        W(o, "\x1b[3;1HNO-RESIZE-EVENT");
        return 0;
    }

    /* Raw-pipe path: scan stdin for an in-band 2048 report. */
    char buf[1024];
    DWORD held = 0;
    for (int tries = 0; tries < 60; tries++) {
        DWORD avail = 0;
        if (PeekNamedPipe(in, NULL, 0, NULL, &avail, NULL) && avail > 0) {
            DWORD n = 0;
            DWORD want = sizeof(buf) - held;
            if (avail < want) want = avail;
            if (ReadFile(in, buf + held, want, &n, NULL) && n > 0) {
                held += n;
                unsigned cols = 0, rows = 0;
                if (parse_2048(buf, held, &cols, &rows)) {
                    redraw(o, cols, rows);
                    return 0;
                }
                if (held == sizeof(buf)) held = 0; /* avoid overflow; drop */
            }
        } else {
            Sleep(100);
        }
    }
    W(o, "\x1b[3;1HNO-2048-REPORT");
    return 0;
}
