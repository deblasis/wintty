/* Explicit scroll: SU (ESC [ n S, scroll up) and SD (ESC [ n T, scroll
 * down), full screen and within a scroll region. VT-only via std output;
 * explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[1;1HL1\r\nL2\r\nL3\r\nL4\r\nL5");
    W(h, "\x1b[2S");     /* scroll whole screen up 2 */
    W(h, "\x1b[12;1Hlow");
    W(h, "\x1b[1T");     /* scroll whole screen down 1 */

    /* within a scroll region */
    W(h, "\x1b[15;20r");
    W(h, "\x1b[15;1HA\r\nB\r\nC\r\nD");
    W(h, "\x1b[1S");     /* scroll region up 1 */
    W(h, "\x1b[r");
    return 0;
}
