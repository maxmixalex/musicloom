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

Used for Windows Media Foundation decoding of FLAC, M4A and WMA, plus shared audio primitives.

## NLayer

- Package: `NLayer.NAudioSupport`
- Version: 3.0.0
- License: MIT
- Upstream: naudio/NLayer

Used for fully managed MP3 decoding. MusicLoom requests `DownmixToMono` during decode before resampling to the fingerprint sample rate.

## NAudio.Vorbis / NVorbis

- Package: `NAudio.Vorbis`
- Version: 3.0.0
- License: MIT
- Upstream: naudio/Vorbis
- Decoder dependency: NVorbis (MIT, NVorbis/NVorbis)

Used for Ogg Vorbis decoding without relying on optional Windows codecs.

## Concentus.Oggfile / Concentus

- Package: `Concentus.Oggfile`
- Version: 1.0.7
- License: MIT
- Upstream: lostromb/concentus.oggfile
- Codec dependency: Concentus 2.2.2 (Opus/BSD-style license, lostromb/concentus)

Used for Ogg Opus parsing and decoding. MusicLoom requests 8 kHz output directly from the Opus decoder for fingerprinting.
