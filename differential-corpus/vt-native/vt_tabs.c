/* Tabs — HT with default 8-column stops, forward/back tab (CHT/CBT), and
 * setting/clearing stops (HTS / TBC). Alignment via tabs is a fundamental
 * primitive (shells, `ls -l`, many CLIs). VT-only via std output; explicit
 * CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* default stops every 8: a@1 b@9 c@17 d@25 */
    W(h, "a\tb\tc\td\r\n");

    /* CHT: forward 3 tabs from col 1 -> col 25 */
    W(h, "\x1b[2;1H\x1b[3IP\r\n");

    /* CBT: back 2 tabs from col 30 -> stops at 25,17 -> col 17 */
    W(h, "\x1b[3;30H\x1b[2ZQ\r\n");

    /* HTS / TBC: clear all stops, set one at col 5, HT from col 1 -> col 5;
     * a second HT past the only stop goes to the last column */
    W(h, "\x1b[3g");            /* clear ALL tab stops */
    W(h, "\x1b[5;5H\x1bH");     /* HTS: set a stop at col 5 */
    W(h, "\x1b[5;1H\tR");       /* HT from col1 -> col5, R */
    return 0;
}
