// Bundled with the wintty shader gallery.
// Source: https://github.com/zoitrok/ghostty-shaders
// Author: zoitrok
// License: Unlicense (public domain) — full text in LICENSES/zoitrok-Unlicense.txt
// Unmodified except for this header.

#define speed 0.1
#define lineheight 0.01

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
  vec2 uv = fragCoord/iResolution.xy;
  float s = step(mod(uv.y+iTime*speed,1.0), lineheight)*0.1;
  uv.x = uv.x + s*0.02;
  vec4 col = texture(iChannel0, uv);
  col = col * (1. + s*3.) + s*.5;
  fragColor = vec4(col);
}
