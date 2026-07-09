/* Explicit scroll: SU (ESC [ n S) and SD (ESC [ n T), exercised WITHIN a
 * scroll region so neither side touches scrollback -- this isolates the
 * SU/SD mechanics (which are faithful) from a separate finding:
 *
 *   Full-screen SU pushes the scrolled-off lines into ghostty-vt's
 *   scrollback, but conhost (re-serialized through ConPTY) does not expose
 *   them, so a scrollback-inclusive dump diverges by exactly those lines
 *   while the visible grid stays identical. That is a scrollback-
 *   accumulation difference, not an SU/SD mechanics divergence.
 *
 * VT-only via std output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[5;12r");   /* scroll region rows 5..12 */
    W(h, "\x1b[5;1HL1\r\nL2\r\nL3\r\nL4\r\nL5");
    W(h, "\x1b[2S");      /* SU 2 within the region */
    W(h, "\x1b[9;1HX\r\nY");
    W(h, "\x1b[1T");      /* SD 1 within the region */
    W(h, "\x1b[r");       /* reset region */
    W(h, "\x1b[15;1Hafter-region");
    return 0;
}
