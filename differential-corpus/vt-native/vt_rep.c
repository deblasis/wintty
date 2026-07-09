/* REP (ESC [ n b): repeat the preceding graphic character n times. Used
 * by output optimizers (e.g. terminfo `rep`). VT-only via std output;
 * explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[1;1HX\x1b[5b");    /* X then repeat 5 -> XXXXXX */
    W(h, "\x1b[2;1HAB\x1b[3b");   /* AB then repeat last (B) 3 -> ABBBB */
    W(h, "\x1b[3;1H-\x1b[9b");    /* a short rule of dashes */
    return 0;
}
