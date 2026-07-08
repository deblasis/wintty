/*
 * vt_smoke: a purpose-built VT-native program for the oracle's
 * `compare-transports` mode. It writes a FIXED VT stream to its standard
 * output handle via WriteFile only -- no Console API, no read-back, no
 * PID/handle/time -- so it produces byte-identical output whether stdout
 * is a ConPTY or a raw pipe, and the resulting cell grids can be compared.
 *
 * Stays within a 120x30 grid; never queries the console. Deterministic.
 */
#include <windows.h>

static void emit(HANDLE h, const char *s, DWORD n) {
    DWORD written = 0;
    WriteFile(h, s, n, &written, NULL);
}
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);

    /* Match production wintty (UTF-8 active codepage, #301): without this
     * ConPTY interprets the UTF-8 bytes below per the system ANSI codepage
     * and mangles them. Harmless over a raw pipe (WriteFile is unaffected
     * by console CP). */
    SetConsoleOutputCP(65001);

    /* plain text */
    W(h, "plain text line\r\n");

    /* SGR: bold, italic, underline, then reset */
    W(h, "\x1b[1mbold\x1b[0m \x1b[3mitalic\x1b[0m \x1b[4munderline\x1b[0m\r\n");

    /* 16-color fg/bg */
    W(h, "\x1b[31mred\x1b[0m \x1b[42mgreenbg\x1b[0m\r\n");

    /* 256-color and truecolor */
    W(h, "\x1b[38;5;208m256-orange\x1b[0m \x1b[38;2;100;149;237mtruecolor\x1b[0m\r\n");

    /* absolute cursor position then text (row 8, col 20) */
    W(h, "\x1b[8;20HAT-8-20");

    /* relative cursor moves: down 2, back to col 1 via CR */
    W(h, "\x1b[2B\rrelmove\r\n");

    /* UTF-8 CJK wide characters */
    W(h, "wide: \xe4\xbd\xa0\xe5\xa5\xbd (ni hao)\r\n");

    /* combining: e + combining acute */
    W(h, "combine: e\xcc\x81\r\n");

    /* leave the cursor somewhere deterministic */
    W(h, "\x1b[15;1Hend");
    return 0;
}
