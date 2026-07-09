/* Protected fields: DECSCA (ESC [ 1 " q protected / ESC [ 0 " q not) plus
 * selective erase DECSEL (ESC [ ? n K) and DECSED (ESC [ ? n J), which
 * erase only unprotected cells. Used by form-style TUIs. VT-only via std
 * output; explicit CRLF. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);
    W(h, "\x1b[2J\x1b[H");

    /* line 1: PROT (protected) then UNPROT (not protected) */
    W(h, "\x1b[1;1H\x1b[1\"qPROT\x1b[0\"qUNPROT");
    /* selective erase of the whole line: only UNPROT should clear */
    W(h, "\x1b[1;1H\x1b[?2K");

    /* line 3: mixed, then selective erase to end from mid-line */
    W(h, "\x1b[3;1H\x1b[1\"qAA\x1b[0\"qbb\x1b[1\"qCC\x1b[0\"qdd");
    W(h, "\x1b[3;1H\x1b[?0K");
    return 0;
}
