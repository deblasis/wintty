// shader_wrapper.cpp
// A single wrapper DLL built entirely with MSVC that handles the full
// GLSL -> SPIR-V -> HLSL pipeline, keeping all C++ code isolated from
// ghostty.dll's Zig/Clang-compiled C++ runtime.
//
// Export: shader_wrapper_compile_hlsl(source, source_len, out_buf, out_buf_cap, out_len)
// Returns: 0 on success, non-zero on failure.

#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <string>

// glslang headers
#include "glslang/Include/glslang_c_interface.h"
#include "glslang/Public/resource_limits_c.h"

// SPIRV-Cross C API headers
#include "spirv_cross_c.h"

static int g_initialized = 0;

// Thread-local buffer for last error message
static thread_local char g_error_msg[4096] = {};

extern "C" {

__declspec(dllexport)
int shader_wrapper_init(void) {
    if (!g_initialized) {
        if (!glslang_initialize_process())
            return -1;
        g_initialized = 1;
    }
    return 0;
}

// Compile GLSL to SPIR-V and then to HLSL. The output is a null-terminated HLSL string.
// On failure, call shader_wrapper_get_error to get the error message.
__declspec(dllexport)
int shader_wrapper_compile_hlsl(
    const char* source,
    int source_len,
    char* out_buf,
    int out_buf_cap,
    int* out_len
) {
    if (!g_initialized) return -1;
    if (!source || !out_buf || !out_len) return -2;

    *out_len = 0;

    // Step 0: Remap binding=1 -> binding=0 in the GLSL uniform block declaration.
    // The shadertoy prefix declares Globals at binding=1 because Metal binds
    // uniforms at buffer index 1 (index 0 is for vertex buffers). DX12's
    // post-process root signature provides CBV at b0, so we remap here on
    // the GLSL input so SPIRV-Cross naturally produces register(b0).
    // This is safe because user GLSL code won't contain the string
    // "binding = 1" in a uniform block context (it's only in our prefix).
    std::string source_str(source, source_len);
    {
        const std::string from = "binding = 1, std140) uniform Globals";
        const std::string to   = "binding = 0, std140) uniform Globals";
        size_t pos = 0;
        while ((pos = source_str.find(from, pos)) != std::string::npos) {
            source_str.replace(pos, from.length(), to);
            pos += to.length();
        }
    }

    // Step 1: GLSL -> SPIR-V via glslang
    const glslang_resource_t* resource = glslang_default_resource();
    if (!resource) return -3;

    glslang_input_t input = {};
    input.language = GLSLANG_SOURCE_GLSL;
    input.stage = GLSLANG_STAGE_FRAGMENT;
    input.client = GLSLANG_CLIENT_VULKAN;
    input.client_version = GLSLANG_TARGET_VULKAN_1_2;
    input.target_language = GLSLANG_TARGET_SPV;
    input.target_language_version = GLSLANG_TARGET_SPV_1_5;
    input.code = source_str.c_str();
    input.default_version = 100;
    input.default_profile = GLSLANG_NO_PROFILE;
    input.force_default_version_and_profile = 0;
    input.forward_compatible = 0;
    input.messages = GLSLANG_MSG_DEFAULT_BIT;
    input.resource = resource;

    glslang_shader_t* shader = glslang_shader_create(&input);
    if (!shader) return -4;

    if (!glslang_shader_preprocess(shader, &input)) {
        glslang_shader_delete(shader);
        return -5;
    }

    if (!glslang_shader_parse(shader, &input)) {
        glslang_shader_delete(shader);
        return -6;
    }

    glslang_program_t* program = glslang_program_create();
    if (!program) {
        glslang_shader_delete(shader);
        return -7;
    }

    glslang_program_add_shader(program, shader);

    if (!glslang_program_link(program,
        GLSLANG_MSG_SPV_RULES_BIT | GLSLANG_MSG_VULKAN_RULES_BIT)) {
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -8;
    }

    glslang_program_SPIRV_generate(program, GLSLANG_STAGE_FRAGMENT);
    size_t spirv_word_count = glslang_program_SPIRV_get_size(program);
    const unsigned int* spirv_data = glslang_program_SPIRV_get_ptr(program);

    // Step 2: SPIR-V -> HLSL via SPIRV-Cross
    spvc_context spv_ctx = nullptr;
    if (spvc_context_create(&spv_ctx) != SPVC_SUCCESS) {
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -9;
    }

    spvc_parsed_ir ir = nullptr;
    if (spvc_context_parse_spirv(spv_ctx, spirv_data, spirv_word_count, &ir) != SPVC_SUCCESS) {
        snprintf(g_error_msg, sizeof(g_error_msg), "spvc_context_parse_spirv failed: %s",
            spvc_context_get_last_error_string(spv_ctx));
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -10;
    }

    spvc_compiler compiler = nullptr;
    if (spvc_context_create_compiler(spv_ctx, SPVC_BACKEND_HLSL, ir, SPVC_CAPTURE_MODE_TAKE_OWNERSHIP, &compiler) != SPVC_SUCCESS) {
        snprintf(g_error_msg, sizeof(g_error_msg), "spvc_context_create_compiler failed: %s",
            spvc_context_get_last_error_string(spv_ctx));
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -11;
    }

    spvc_compiler_options options = nullptr;
    if (spvc_compiler_create_compiler_options(compiler, &options) != SPVC_SUCCESS) {
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -12;
    }

    // Target Shader Model 6.0
    spvc_compiler_options_set_uint(options, SPVC_COMPILER_OPTION_HLSL_SHADER_MODEL, 60);

    if (spvc_compiler_install_compiler_options(compiler, options) != SPVC_SUCCESS) {
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -13;
    }

    const char* hlsl_result = nullptr;
    if (spvc_compiler_compile(compiler, &hlsl_result) != SPVC_SUCCESS) {
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -14;
    }

    size_t hlsl_len = strlen(hlsl_result);
    if ((int)(hlsl_len + 1) > out_buf_cap) {
        spvc_context_destroy(spv_ctx);
        glslang_program_delete(program);
        glslang_shader_delete(shader);
        return -15;
    }

    memcpy(out_buf, hlsl_result, hlsl_len + 1);
    *out_len = (int)hlsl_len;

    spvc_context_destroy(spv_ctx);
    glslang_program_delete(program);
    glslang_shader_delete(shader);
    return 0;
}

__declspec(dllexport)
const char* shader_wrapper_get_error(void) {
    return g_error_msg;
}

} // extern "C"
