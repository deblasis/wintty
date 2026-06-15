#include "imgui.h"

// This file contains custom extensions for functionality that isn't
// properly supported by Dear Bindings yet. Namely:
// https://github.com/dearimgui/dear_bindings/issues/55

// Wrap this in a namespace to keep it separate from the C++ API
namespace cimgui
{
#include "dcimgui.h"
}

extern "C"
{
CIMGUI_API void ImFontConfig_ImFontConfig(cimgui::ImFontConfig* self)
{
    static_assert(sizeof(cimgui::ImFontConfig) == sizeof(::ImFontConfig), "ImFontConfig size mismatch");
    static_assert(alignof(cimgui::ImFontConfig) == alignof(::ImFontConfig), "ImFontConfig alignment mismatch");
    ::ImFontConfig defaults;
    *reinterpret_cast<::ImFontConfig*>(self) = defaults;
}

CIMGUI_API void ImGuiStyle_ImGuiStyle(cimgui::ImGuiStyle* self)
{
    static_assert(sizeof(cimgui::ImGuiStyle) == sizeof(::ImGuiStyle), "ImGuiStyle size mismatch");
    static_assert(alignof(cimgui::ImGuiStyle) == alignof(::ImGuiStyle), "ImGuiStyle alignment mismatch");
    ::ImGuiStyle defaults;
    *reinterpret_cast<::ImGuiStyle*>(self) = defaults;
}

// Perform the OpenGL3 backend shutdown and then zero out the imgl3w
// function pointer table. ImGui_ImplOpenGL3_Shutdown() calls
// imgl3wShutdown() which dlcloses the GL library handles but does not
// zero out the function pointers. A subsequent ImGui_ImplOpenGL3_Init()
// sees the stale (non-null) pointers, skips loader re-initialization,
// and crashes when calling through them. Zeroing the table forces the
// next Init to reload the GL function pointers via imgl3wInit().
#ifndef IMGUI_DISABLE
#if __has_include("backends/imgui_impl_opengl3.h")
#ifdef ZIGPKG_IMGUI_ENABLE_OPENGL3
#include "backends/imgui_impl_opengl3.h"
#include "backends/imgui_impl_opengl3_loader.h"

CIMGUI_API void ImGui_ImplOpenGL3_ShutdownWithLoaderCleanup()
{
    ::ImGui_ImplOpenGL3_Shutdown();
    memset(&imgl3wProcs, 0, sizeof(imgl3wProcs));
}
#endif // ZIGPKG_IMGUI_ENABLE_OPENGL3
#endif // __has_include("backends/imgui_impl_opengl3.h")
#endif // IMGUI_DISABLE

// DX12 backend layout accessors. The Zig bindings mirror
// ImGui_ImplDX12_InitInfo as an extern struct; these let the unit test
// assert the Zig layout matches the compiled C++ layout (which omits the
// obsolete LegacySingleSrv* fields because IMGUI_DISABLE_OBSOLETE_FUNCTIONS
// is defined).
#ifndef IMGUI_DISABLE
#if __has_include("backends/imgui_impl_dx12.h")
#ifdef ZIGPKG_IMGUI_ENABLE_DX12
#include "backends/imgui_impl_dx12.h"

CIMGUI_API size_t ghostty_ImGui_ImplDX12_InitInfo_size()
{
    return sizeof(ImGui_ImplDX12_InitInfo);
}

CIMGUI_API size_t ghostty_ImGui_ImplDX12_InitInfo_align()
{
    return alignof(ImGui_ImplDX12_InitInfo);
}
#endif // ZIGPKG_IMGUI_ENABLE_DX12
#endif // __has_include("backends/imgui_impl_dx12.h")
#endif // IMGUI_DISABLE

// Core struct layout accessors. The Zig cImport in main.zig translates
// dcimgui.h with only IMGUI_USE_WCHAR32 + IMGUI_HAS_DOCK, while this library
// is compiled with IMGUI_DISABLE_OBSOLETE_FUNCTIONS + IMGUI_ENABLE_FREETYPE
// on top. If any of those change the layout of structs the embedded apprt
// writes through (ImGuiIO every frame, ImGuiStyle on content-scale changes),
// Zig would write fields at the wrong offsets and corrupt adjacent heap.
// These let a unit test assert the Zig view matches the compiled layout.
CIMGUI_API size_t ghostty_ImGuiIO_size()
{
    return sizeof(cimgui::ImGuiIO);
}

CIMGUI_API size_t ghostty_ImGuiStyle_size()
{
    return sizeof(cimgui::ImGuiStyle);
}

}
