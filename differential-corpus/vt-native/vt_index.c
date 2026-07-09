/* Line primitives distinct from the bare-LF byte: IND (ESC D, index =
 * down + scroll, column unchanged), RI (ESC M, reverse index = up +
 * scroll), NEL (ESC E, next line = CR + LF), and the C0 controls VT (0x0B)
 * and FF (0x0C), which xterm treats as index. These are the scroll/line
 * building blocks; conhost's handling of VT/FF in particular is worth
 * pinning. VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* IND: down, same column */
    W(h, "\x1b[3;5HA\x1bDB");   /* A at 3,5; IND -> 4,6; B */

    /* RI: up, same column */
    W(h, "\x1b[6;5HC\x1bMD");   /* C at 6,5; RI -> 5,6; D */

    /* NEL: CR + LF -> next row, col 1 */
    W(h, "\x1b[9;10HE\x1bEF");  /* E at 9,10; NEL -> 10,1; F */

    /* VT and FF as index-like line feeds (down, same column in xterm) */
    W(h, "\x1b[13;5HG\x0bH");   /* VT after G */
    W(h, "\x1b[16;5HI\x0cJ");   /* FF after I */

    /* RI at top of the screen scrolls the screen down */
    W(h, "\x1b[1;1Htop\x1bM\x1bMrolled");
    return 0;
}
