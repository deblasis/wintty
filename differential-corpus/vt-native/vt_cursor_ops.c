/* Cursor movement primitives: CUU/CUD/CUF/CUB (A/B/C/D), CHA (G, column),
 * VPA (d, row), CNL/CPL (E/F), edge clamping, and DECSC/DECRC (ESC 7 /
 * ESC 8) save+restore. Everything that positions output depends on these.
 * VT-only via std output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[5;5HO");         /* CUP 5,5 -> O (cursor now 5,6) */
    W(h, "\x1b[2AU");           /* CUU 2 -> row 3, col 6 -> U */
    W(h, "\x1b[3;10H\x1b[4CR"); /* CUP 3,10; CUF 4 -> col 14 -> R */
    W(h, "\x1b[10;10H\x1b[3DL");/* CUP 10,10; CUB 3 -> col 7 -> L */
    W(h, "\x1b[15GG");          /* CHA col 15 (row = current) -> G */
    W(h, "\x1b[20dV");          /* VPA row 20 (col = current) -> V */
    W(h, "\x1b[6;40H\x1b[2EN"); /* CUP 6,40; CNL 2 -> row 8 col 1 -> N */
    W(h, "\x1b[18;40H\x1b[2FP");/* CUP 18,40; CPL 2 -> row 16 col 1 -> P */

    /* edge clamping: at row 1, CUU 5 stays at row 1; at col 1, CUB 5 stays */
    W(h, "\x1b[1;1H\x1b[5A^");
    W(h, "\x1b[12;1H\x1b[5D<");

    /* DECSC / DECRC: save at 8,8, move away and write, restore, write */
    W(h, "\x1b[8;8H\x1b" "7");  /* DECSC */
    W(h, "\x1b[1;1Hmoved");
    W(h, "\x1b" "8" "S");       /* DECRC -> back to 8,8 -> S */
    return 0;
}
