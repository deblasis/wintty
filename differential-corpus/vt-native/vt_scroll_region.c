/* Scroll regions (DECSTBM) + LF/RI scrolling within a region. Tests
 * whether conhost's buffer scrolling produces the same cells as raw VT
 * applied to the ghostty-vt model. VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");        /* clear + home */
    W(h, "\x1b[3;8r");           /* scroll region rows 3..8 */
    W(h, "\x1b[3;1H");           /* into the region */
    /* 8 lines into a 6-row region -> scrolls up twice */
    W(h, "L1\nL2\nL3\nL4\nL5\nL6\nL7\nL8");
    W(h, "\x1b[3;1H\x1bM\x1bM");  /* reverse index at top -> scroll down */
    W(h, "TOP");
    W(h, "\x1b[r");              /* reset region */
    W(h, "\x1b[10;1Hafter-region");
    return 0;
}
