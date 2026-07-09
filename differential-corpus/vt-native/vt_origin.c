/* DECOM origin mode (ESC [ ? 6 h/l): cursor addressing becomes relative
 * to the scroll region, and moves are clamped to it. Full-screen apps
 * that use scroll regions rely on this. VT-only via std output; explicit
 * CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[5;10r");   /* scroll region rows 5..10 */
    W(h, "\x1b[?6h");     /* origin mode on */
    W(h, "\x1b[1;1HT");   /* relative 1,1 -> row 5 col 1 */
    W(h, "\x1b[3;3HM");   /* relative 3,3 -> row 7 col 3 */
    W(h, "\x1b[20;5HB");  /* relative row 20 clamps to region bottom (row 10) */

    W(h, "\x1b[?6l");     /* origin off */
    W(h, "\x1b[r");       /* reset region */
    W(h, "\x1b[1;1HA");   /* absolute 1,1 */
    return 0;
}
