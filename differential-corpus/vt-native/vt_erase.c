/* Erase ops: EL (0/1/2) and ED (0), including erase-with-colored-bg which
 * fills cells with the current background -- a genuinely VISIBLE effect,
 * so any divergence here is a real one (not the invisible fg-on-blank
 * quirk). VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");
    W(h, "AAAAAAAAAA\r\nBBBBBBBBBB\r\nCCCCCCCCCC\r\nDDDDDDDDDD\r\n");

    W(h, "\x1b[1;5H\x1b[0K");     /* erase to end of line 1 */
    W(h, "\x1b[2;6H\x1b[1K");     /* erase to start of line 2 */

    /* erase whole line 3 with a red background (visible fill) */
    W(h, "\x1b[3;1H\x1b[41m\x1b[2K\x1b[0m");

    /* ED to end from mid-line 4 */
    W(h, "\x1b[6;1Hrow6\x1b[7;1Hrow7\x1b[6;3H\x1b[0J");
    return 0;
}
