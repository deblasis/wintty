/* Soft reset (DECSTR, ESC [ ! p) — the primitive programs use to return
 * the terminal to a known state. Set a pile of non-default state (scroll
 * region, origin mode, autowrap off, SGR bold+color, special-graphics
 * charset), soft-reset, then verify subsequent output behaves as a fresh
 * terminal (full-screen region, absolute addressing, default attrs, ASCII,
 * autowrap on). VT-only via std output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* pile on non-default state */
    W(h, "\x1b[4;9r");   /* scroll region 4..9 */
    W(h, "\x1b[?6h");    /* origin mode on */
    W(h, "\x1b[?7l");    /* autowrap off */
    W(h, "\x1b[1;31;44m"); /* bold, red fg, blue bg */
    W(h, "\x1b(0");      /* G0 special graphics */
    W(h, "lqk");         /* draw something in that state */

    /* soft reset */
    W(h, "\x1b[!p");

    /* now: absolute 1;1 addressing, default attrs, ASCII, autowrap on */
    W(h, "\x1b[1;1Hplain-abc");
    W(h, "\x1b[20;1Hafter-reset");
    /* if region were still 4..9, this absolute move would be clamped;
     * if origin mode were still on, 1;1 would land in the old region */
    return 0;
}
