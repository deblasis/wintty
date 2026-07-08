/* Scroll regions (DECSTBM) + CR/LF and RI scrolling within a region.
 * Uses explicit CR+LF (not bare LF) so it tests scroll-region behavior in
 * isolation, without the console's LF->newline processing (that divergence
 * is isolated in vt_newline.c). VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");        /* clear + home */
    W(h, "\x1b[3;8r");           /* scroll region rows 3..8 */
    W(h, "\x1b[3;1H");           /* into the region */
    /* 8 lines into a 6-row region -> scrolls up twice (CRLF each) */
    W(h, "L1\r\nL2\r\nL3\r\nL4\r\nL5\r\nL6\r\nL7\r\nL8");
    W(h, "\x1b[3;1H\x1bM\x1bM");  /* reverse index at top -> scroll down */
    W(h, "TOP");
    W(h, "\x1b[r");              /* reset region */
    W(h, "\x1b[10;1Hafter-region");
    return 0;
}
