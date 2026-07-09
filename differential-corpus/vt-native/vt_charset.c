/* Character sets (SCS) + shift in/out — the DEC line-drawing building
 * block every pre-Unicode TUI (vim, htop, dialog, ncurses boxes) relies
 * on. Designate G0 = DEC Special Graphics (ESC ( 0), where lowercase
 * letters map to box chars (l=upper-left, q=horizontal, k=upper-right,
 * x=vertical, m=lower-left, j=lower-right). Also exercise G1 + SO/SI
 * locking shifts. VT-only via std output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* box via G0 special graphics, then back to ASCII for a label */
    W(h, "\x1b(0");          /* G0 = special graphics */
    W(h, "lqqk\r\n");        /* upper row of box */
    W(h, "x\x1b(Bhi\x1b(0x\r\n"); /* side, ASCII "hi", side */
    W(h, "mqqj\r\n");        /* lower row of box */
    W(h, "\x1b(B");          /* G0 = ASCII */

    /* a few named special-graphics glyphs: `=diamond a=checkerboard
     * f=degree g=plus/minus ~=bullet */
    W(h, "\x1b[6;1H\x1b(0`afg~\x1b(B");

    /* G1 + SO/SI locking shift: designate G1 special, SO invokes it,
     * SI returns to G0 (ASCII) */
    /* NB: split the literal after \x0f so the C hex escape does not
     * greedily swallow the 'A' of ASCII (A is a hex digit). */
    W(h, "\x1b[8;1H\x1b)0\x0elqk\x0f" "ASCII");
    return 0;
}
