/*
 * vt_wrap: line-wrap edge cases for the oracle's `compare-transports`
 * mode. Writes fixed VT to std output only (no Console API). Assumes a
 * 120-column grid (the oracle runs it at 120x30).
 */
#include <windows.h>

static void emit(HANDLE h, const char *s, DWORD n) {
    DWORD written = 0;
    WriteFile(h, s, n, &written, NULL);
}
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);

    /* exactly 120 'a' then 1 'b': the 'b' must wrap to the next row and
     * the wrap state on the boundary row must match across transports */
    W(h,
      "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"   /* 50 */
      "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"   /* 100 */
      "aaaaaaaaaaaaaaaaaaaa"                                 /* 120 */
      "b\r\n");

    /* a wide (2-cell) CJK char straddling the last column: 119 'x' then a
     * wide char that cannot fit in the last cell -> spacer/reflow behavior */
    W(h,
      "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"  /* 50 */
      "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"  /* 100 */
      "xxxxxxxxxxxxxxxxxxx"                                  /* 119 */
      "\xe4\xbd\xa0"                                        /* wide char at col 120 */
      "\r\n");
    return 0;
}
