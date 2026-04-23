// Passthrough shader - copies iChannel0 directly to output.
// Use this to verify the post-process pipeline without animation artifacts.

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    fragColor = texture(iChannel0, uv);
}
