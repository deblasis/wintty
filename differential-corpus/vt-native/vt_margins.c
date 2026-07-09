/* Left/right margins: DECLRMM (ESC [ ? 69 h) enables them, DECSLRM
 * (ESC [ l ; r s) sets them. Text then wraps within [left,right]. Not all
 * hosts implement this (a likely divergence point). VT-only via std
 * output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    W(h, "\x1b[?69h");     /* enable left/right margin mode */
    W(h, "\x1b[20;60s");   /* left=20, right=60 */
    W(h, "\x1b[5;20H");    /* start inside the margins */
    /* 60 chars: should wrap at the right margin (col 60) back to left (20) */
    W(h, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    W(h, "\x1b[?69l");     /* disable margins */
    W(h, "\x1b[r");        /* reset (also clears margins) */
    W(h, "\x1b[10;1Hplain");
    return 0;
}
