/* IRM insert/replace mode (ESC [ 4 h/l). In insert mode, printing a char
 * shifts the rest of the line right; in replace mode it overwrites. Line
 * editors depend on this. VT-only via std output; explicit CRLF.
 * Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[1;1HABCDEF");
    W(h, "\x1b[1;3H");   /* cursor on 'C' */
    W(h, "\x1b[4h");     /* IRM insert on */
    W(h, "XY");          /* -> AB XY CDEF (C..F shift right) */
    W(h, "\x1b[4l");     /* IRM off */

    W(h, "\x1b[3;1H123456");
    W(h, "\x1b[3;3H");   /* cursor on '3' */
    W(h, "Z");           /* replace mode: Z overwrites -> 12Z456 */
    return 0;
}
