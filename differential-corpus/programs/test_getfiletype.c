#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_console_handle_type(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD type = GetFileType(hOut);
    // Our hook should return FILE_TYPE_CHAR for console handles
    if (type == FILE_TYPE_CHAR) {
        PASS("GetFileType(console handle) returns FILE_TYPE_CHAR");
    } else {
        char buf[64];
        snprintf(buf, sizeof(buf), "expected FILE_TYPE_CHAR, got %lu", type);
        FAIL("console_type", buf);
    }
}

void test_file_handle_type(void) {
    // Create a temp file and check its type
    HANDLE hFile = CreateFileW(
        L"test_getfiletype_tmp.txt",
        GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL
    );
    if (hFile == INVALID_HANDLE_VALUE) {
        FAIL("file_create", "CreateFileW failed");
        return;
    }
    
    DWORD type = GetFileType(hFile);
    CloseHandle(hFile);
    DeleteFileW(L"test_getfiletype_tmp.txt");
    
    if (type == FILE_TYPE_DISK) {
        PASS("GetFileType(file handle) returns FILE_TYPE_DISK");
    } else {
        char buf[64];
        snprintf(buf, sizeof(buf), "expected FILE_TYPE_DISK, got %lu", type);
        FAIL("file_type", buf);
    }
}

void test_pipe_handle_type(void) {
    HANDLE readPipe, writePipe;
    SECURITY_ATTRIBUTES sa = { .nLength = sizeof(sa), .bInheritHandle = TRUE, .lpSecurityDescriptor = NULL };
    
    if (!CreatePipe(&readPipe, &writePipe, &sa, 0)) {
        FAIL("pipe_create", "CreatePipe failed");
        return;
    }
    
    DWORD type = GetFileType(readPipe);
    CloseHandle(readPipe);
    CloseHandle(writePipe);
    
    if (type == FILE_TYPE_PIPE) {
        PASS("GetFileType(pipe handle) returns FILE_TYPE_PIPE");
    } else {
        char buf[64];
        snprintf(buf, sizeof(buf), "expected FILE_TYPE_PIPE, got %lu", type);
        FAIL("pipe_type", buf);
    }
}

void test_null_handle_type(void) {
    // GetFileType(NULL) should not crash
    DWORD type = GetFileType(NULL);
    if (type == FILE_TYPE_UNKNOWN || type == FILE_TYPE_CHAR) {
        PASS("GetFileType(NULL) doesn't crash");
    } else {
        FAIL("null_type", "unexpected crash or result");
    }
}

int main(void) {
    printf("=== GetFileType Tests ===\n\n"); fflush(stdout);
    
    test_console_handle_type();
    test_file_handle_type();
    test_pipe_handle_type();
    test_null_handle_type();
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
