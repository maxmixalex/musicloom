# Third-party notices

MusicLoom currently depends on third-party libraries with their own licenses.

## ATL.NET

- Package: `z440.atl.core`
- Version: 7.18.0
- License: MIT
- Upstream: Zeugma440/atldotnet

Used for streaming/seek-based metadata scanning.

## TagLib#

- Package: `TagLibSharp`
- Version: 2.3.0
- License: LGPL-2.1-only
- Upstream: mono/taglib-sharp

Used only by the mutation layer for streaming media-tag writes. MusicLoom avoids the reflection-based `TagLib.File.Create` factory in NativeAOT paths and explicitly constructs the relevant format implementation.

NativeAOT may statically link managed dependencies into a native executable. Anyone distributing MusicLoom binaries is responsible for satisfying the LGPL-2.1-only requirements applicable to that form of distribution, including the user's ability to modify/relink the LGPL component. MusicLoom's own source remains under the repository's MIT license unless explicitly relicensed later.

## NAudio

- Package: `NAudio.Wasapi`
- Version: 3.1.0
- License: MIT
- Upstream: naudio/NAudio

Used for Windows Media Foundation decoding/resampling in the Windows audio adapter.
