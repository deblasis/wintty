/* Line/char editing: IL/DL (insert/delete line), ICH/DCH (insert/delete
 * char), ECH (erase char). These shift cells around in conhost's buffer;
 * tests that the resulting grid matches raw VT applied to ghostty-vt.
 * VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");
    W(h, "line A\r\nline B\r\nline C\r\nline D\r\n");

    W(h, "\x1b[2;1H\x1b[2L");     /* insert 2 blank lines at row 2 */
    W(h, "\x1b[6;1H\x1b[1M");     /* delete 1 line at row 6 */

    W(h, "\x1b[1;3H\x1b[3@");     /* insert 3 blanks at row 1 col 3 */

    W(h, "\x1b[8;1Habcdefgh\x1b[8;2H\x1b[3P"); /* delete 3 chars at 8,2 */
    W(h, "\x1b[9;1Hzzzzzzzz\x1b[9;3H\x1b[2X");  /* erase 2 chars at 9,3 */
    return 0;
}
