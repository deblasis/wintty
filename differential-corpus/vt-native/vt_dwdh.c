/* Double-width / double-height line attributes: DECDWL (ESC # 6),
 * DECDHL top/bottom halves (ESC # 3 / ESC # 4), DECSWL single width
 * (ESC # 5). These are per-line attributes that change cell width; a
 * classic point of terminal divergence. VT-only via std output; explicit
 * CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[1;1H\x1b#6" "double-wide");     /* DECDWL */
    W(h, "\x1b[3;1H\x1b#3" "big-top");         /* DECDHL top half */
    W(h, "\x1b[4;1H\x1b#4" "big-bottom");      /* DECDHL bottom half */
    W(h, "\x1b[6;1H\x1b#5" "single-wide");     /* DECSWL */
    return 0;
}
