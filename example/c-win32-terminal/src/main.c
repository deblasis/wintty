// example/c-win32-terminal/src/main.c
//
// Minimal Win32 host for libghostty. Creates an HWND and passes it to
// ghostty which creates a surface with DX11 rendering and ConPTY.
// This is the skeleton -- no input forwarding yet, so the terminal
// won't accept keyboard or mouse input.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ghostty.h>
#include <stdio.h>

// --- Globals ---

static HWND g_hwnd = NULL;
static ghostty_app_t g_app = NULL;
static ghostty_surface_t g_surface = NULL;

// --- Forward declarations ---

static LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);

// --- Runtime callbacks ---
// ghostty calls wakeup from background threads when the app needs to tick.
// We post a message to the main thread's message loop.

#define WM_GHOSTTY_WAKEUP (WM_APP + 1)

static void wakeup_cb(void* userdata) {
    (void)userdata;
    if (g_hwnd) PostMessage(g_hwnd, WM_GHOSTTY_WAKEUP, 0, 0);
}

// Stub callbacks -- minimum required by ghostty_runtime_config_s.
// A real app would implement clipboard, close surface, etc.

static bool action_cb(ghostty_app_t app, ghostty_target_s target,
                       ghostty_action_s action) {
    (void)app; (void)target; (void)action;
    return false;
}

static bool read_clipboard_cb(void* userdata, ghostty_clipboard_e loc,
                                void* state) {
    (void)userdata; (void)loc; (void)state;
    return false;
}

static void confirm_read_clipboard_cb(void* userdata, const char* str,
                                       void* state,
                                       ghostty_clipboard_request_e req) {
    (void)userdata; (void)str; (void)state; (void)req;
}

static void write_clipboard_cb(void* userdata, ghostty_clipboard_e loc,
                                const ghostty_clipboard_content_s* content,
                                size_t content_count, bool confirm) {
    (void)userdata; (void)loc; (void)content;
    (void)content_count; (void)confirm;
}

static void close_surface_cb(void* userdata, bool process_alive) {
    (void)userdata; (void)process_alive;
    if (g_hwnd) PostMessage(g_hwnd, WM_CLOSE, 0, 0);
}

// --- Window procedure ---

static LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_GHOSTTY_WAKEUP:
        if (g_app) ghostty_app_tick(g_app);
        return 0;

    case WM_SIZE:
        if (g_surface) {
            ghostty_surface_set_size(g_surface, LOWORD(lp), HIWORD(lp));
        }
        return 0;

    case WM_SETFOCUS:
        if (g_surface) ghostty_surface_set_focus(g_surface, true);
        return 0;

    case WM_KILLFOCUS:
        if (g_surface) ghostty_surface_set_focus(g_surface, false);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    default:
        return DefWindowProc(hwnd, msg, wp, lp);
    }
}

// --- Entry point ---

int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR cmdLine, int show) {
    (void)hPrev; (void)cmdLine;

    // 1. Register window class
    WNDCLASSEX wc = {
        .cbSize = sizeof(wc),
        .style = CS_HREDRAW | CS_VREDRAW,
        .lpfnWndProc = wnd_proc,
        .hInstance = hInst,
        .hCursor = LoadCursor(NULL, IDC_IBEAM),
        .hbrBackground = NULL,  // ghostty renders the background
        .lpszClassName = "GhosttyExample",
    };
    RegisterClassEx(&wc);

    // 2. Create window
    g_hwnd = CreateWindowEx(
        0, "GhosttyExample", "Ghostty Win32 Example",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 800, 600,
        NULL, NULL, hInst, NULL);
    if (!g_hwnd) {
        fprintf(stderr, "CreateWindowEx failed: %lu\n", GetLastError());
        return 1;
    }

    // 3. Initialize ghostty global state
    char* argv[] = { "ghostty-example" };
    if (ghostty_init(1, argv) != GHOSTTY_SUCCESS) {
        fprintf(stderr, "ghostty_init failed\n");
        return 1;
    }

    // 4. Create ghostty config
    ghostty_config_t config = ghostty_config_new();
    ghostty_config_load_default_files(config);
    ghostty_config_load_recursive_files(config);
    ghostty_config_finalize(config);

    // 5. Create ghostty app with runtime callbacks
    ghostty_runtime_config_s runtime_cfg = {
        .userdata = NULL,
        .supports_selection_clipboard = false,
        .wakeup_cb = wakeup_cb,
        .action_cb = action_cb,
        .read_clipboard_cb = read_clipboard_cb,
        .confirm_read_clipboard_cb = confirm_read_clipboard_cb,
        .write_clipboard_cb = write_clipboard_cb,
        .close_surface_cb = close_surface_cb,
    };

    g_app = ghostty_app_new(&runtime_cfg, config);
    ghostty_config_free(config);
    if (!g_app) {
        fprintf(stderr, "ghostty_app_new failed\n");
        return 1;
    }

    // 6. Create surface with HWND
    UINT dpi = GetDpiForWindow(g_hwnd);
    double scale = (double)dpi / 96.0;

    ghostty_surface_config_s surface_cfg = ghostty_surface_config_new();
    surface_cfg.platform_tag = GHOSTTY_PLATFORM_WINDOWS;
    surface_cfg.platform.windows.hwnd = (void*)g_hwnd;
    surface_cfg.scale_factor = scale;

    g_surface = ghostty_surface_new(g_app, &surface_cfg);
    if (!g_surface) {
        fprintf(stderr, "ghostty_surface_new failed\n");
        ghostty_app_free(g_app);
        return 1;
    }

    // 7. Set initial size
    RECT rc;
    GetClientRect(g_hwnd, &rc);
    ghostty_surface_set_size(g_surface,
        (uint32_t)(rc.right - rc.left),
        (uint32_t)(rc.bottom - rc.top));

    // 8. Show window and tell ghostty the surface is visible and focused
    ShowWindow(g_hwnd, show);
    UpdateWindow(g_hwnd);
    ghostty_surface_set_occlusion(g_surface, true);
    ghostty_surface_set_focus(g_surface, true);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    // 9. Cleanup
    ghostty_surface_free(g_surface);
    ghostty_app_free(g_app);

    return (int)msg.wParam;
}
