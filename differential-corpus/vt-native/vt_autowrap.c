/* Autowrap (DECAWM) and the deferred / "pending wrap" behavior at the
 * last column — the classic terminal-compliance minefield. With DECAWM
 * on, writing the last column leaves the cursor "stuck" there with a
 * pending-wrap flag; the NEXT printable wraps to the next row first. With
 * DECAWM off, further writes overwrite the last column. The oracle
 * captures the pending-wrap flag in its cursor line, so this pins that
 * state too. VT-only via std output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* DECAWM on (default): A at 119, B at 120 (last col -> pending),
     * C wraps to row 2 col 1 */
    W(h, "\x1b[?7h");
    W(h, "\x1b[1;119HABC");

    /* deferred wrap is cancelled by a cursor move: fill last col, then
     * move explicitly — no wrap should occur */
    W(h, "\x1b[3;120HX\x1b[3;1HY"); /* X at last col, explicit move to col1, Y */

    /* DECAWM off: X at 119, Y at 120, Z overwrites col 120 (no wrap) */
    W(h, "\x1b[?7l");
    W(h, "\x1b[5;119HXYZ");
    W(h, "\x1b[?7h"); /* restore default */

    /* leave the cursor in a pending-wrap state to compare that flag */
    W(h, "\x1b[7;1H");
    for (int i = 0; i < 6; i++) W(h, "12345678901234567890"); /* 120 cols -> pending */
    return 0;
}
