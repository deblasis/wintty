// Bundled with the wintty shader gallery as a diagnostic.
// Source: ghostty (https://github.com/ghostty-org/ghostty), MIT -- same
// license as this project.
// Unmodified except for this header.

// Passthrough shader - copies iChannel0 directly to output.
// Use this to verify the post-process pipeline without animation artifacts.

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    fragColor = texture(iChannel0, uv);
}
