/* Isolates the newline-processing divergence found on 2026-07-08.
 *
 * conhost applies ENABLE_PROCESSED_OUTPUT, so a bare LF (\n, no CR) acts as
 * a full newline (return to column 1 + line feed). A raw VT stream to
 * ghostty-vt treats \n as pure line feed (down one row, SAME column), so
 * writing words separated by bare \n produces a staircase. This program is
 * expected to be NOT-IDENTICAL: conhost puts each word at column 1, raw
 * pipe indents them.
 *
 * Consequence for a raw-pipe transport: to be cell-identical to ConPTY it
 * must reproduce console LF->newline processing (translate LF->CRLF, or
 * treat LF as newline), or restrict to programs that emit explicit CRLF.
 *
 * VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");
    W(h, "alpha\nbravo\ncharlie\ndelta"); /* bare LF between words */
    return 0;
}
