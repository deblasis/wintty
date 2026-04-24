# Bundled profile icons

These 16x16 PNG assets are placeholder glyphs generated for the Wintty project. Each file is a flat solid-color PNG distinguishable by hue; real iconography will replace them once the designer lands. They are original work, released under the same MIT license as the rest of Wintty.

> Copyright (c) Alessandro De Blasis and Wintty contributors.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Source per key (all are flat 16x16 RGB placeholders):

- `cmd.png` - near-black (0x1F1F1F), classic Command Prompt
- `pwsh.png` - PowerShell 7 blue (0x2E74B8)
- `powershell.png` - deep blue (0x01256E), Windows PowerShell
- `wsl.png` - Ubuntu orange (0xE95A0C)
- `git-bash.png` - Git red (0xF05033)
- `azure.png` - Azure blue (0x0078D4)
- `default.png` - neutral grey (0x808080)

Replacing these with MIT-licensed glyphs (for example from [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons), which publishes SVG + PDF under MIT) is a straightforward drop-in since the file names and dimensions are the contract consumed by `WindowsIconResolver`.
