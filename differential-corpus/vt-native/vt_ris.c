/* RIS hard reset (ESC c) — the strongest reset primitive, distinct from
 * the DECSTR soft reset. Set a pile of state, RIS, then verify a fresh
 * terminal. VT-only via std output; explicit CRLF. Deterministic.
 *
 * NB: "\x1b" "c" is split so the hex escape does not consume 'c' (a hex
 * digit) into the out-of-range escape 0x1BC. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* non-default state */
    W(h, "\x1b[4;9r\x1b[?6h\x1b[?7l\x1b[1;31;44m\x1b(0");
    W(h, "lqk");

    W(h, "\x1b" "c");     /* RIS hard reset */

    W(h, "\x1b[1;1Hafter-RIS-plain");
    W(h, "\x1b[20;1Hbottom");
    return 0;
}
