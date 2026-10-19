# Third-party notices

CS2 Mod Verifier is released under the [MIT License](LICENSE). The release builds (`CS2ModVerifier.exe`) are self-contained: they include the components below, which are distributed under the licenses shown here.

## .NET runtime and Windows Forms

- Projects: [dotnet/runtime](https://github.com/dotnet/runtime) and [dotnet/winforms](https://github.com/dotnet/winforms), version 10.0
- Used for: running the app, reading DLLs as metadata (`System.Reflection.Metadata`), and the window
- License: MIT, Copyright (c) .NET Foundation and Contributors (full text below)
- These projects include components from other projects, listed in their own notices:
  [runtime third-party notices](https://github.com/dotnet/runtime/blob/release/10.0/THIRD-PARTY-NOTICES.TXT) and
  [Windows Forms third-party notices](https://github.com/dotnet/winforms/blob/release/10.0/THIRD-PARTY-NOTICES.TXT)

## Roslyn (Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis.Common)

- Project: [dotnet/roslyn](https://github.com/dotnet/roslyn), version 5.9.0
- Used for: parsing C# source code (syntax only) for the source comparison
- License: MIT, Copyright (c) .NET Foundation and Contributors (full text below)
- Its own notices: [Roslyn third-party notices](https://github.com/dotnet/roslyn/blob/main/THIRD-PARTY-NOTICES.txt)

### MIT License text for the .NET runtime, Windows Forms and Roslyn

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Acknowledgements (no code included)

- **Harmony** ([pardeike/Harmony](https://github.com/pardeike/Harmony), MIT, Copyright (c) 2017 Andreas Pardeike). CS2 Mod Verifier contains no Harmony code. It includes the SHA-256 hashes of Harmony's official release files, to recognise official builds that mods bundle, and reads Harmony's GitHub releases to learn newer versions.

## Used only to build and test (not included in releases)

xUnit, Microsoft.NET.Test.Sdk and coverlet (tests), Microsoft.CodeAnalysis.Analyzers (build-time code checks), and the GitHub Actions used by the workflows in `.github/workflows`.
