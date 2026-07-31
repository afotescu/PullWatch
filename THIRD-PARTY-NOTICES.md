# Third-Party Notices

PullWatch includes open-source components from third parties. PullWatch itself
is distributed under the GNU General Public License version 3; see `LICENSE`.

## FlyleafLib

- Component: FlyleafLib 3.10.4
- Copyright: SuRGeoNix
- License: GNU Lesser General Public License version 3 or later
- Source: https://github.com/SuRGeoNix/Flyleaf/tree/3b057fd645bbbf393350152bba1c7510e47b21ef
- License text: https://www.gnu.org/licenses/lgpl-3.0.html

PullWatch uses FlyleafLib without source modifications.

## Flyleaf FFmpeg runtime

- Component: FFmpeg 7.1.1 shared libraries
- Runtime version: `n7.1.1-6-g48c0f071d4-20250414`
- License reported by the runtime: GNU General Public License version 3 or later
- Distribution: https://github.com/SuRGeoNix/Flyleaf/releases/tag/v3.8.11
- Upstream source: https://github.com/FFmpeg/FFmpeg/tree/48c0f071d4
- HLS patch series: https://patchwork.ffmpeg.org/project/ffmpeg/list/?series=1018
- Build system: https://github.com/BtbN/FFmpeg-Builds

The runtime is downloaded without modification from the Flyleaf v3.8.11
release and verified with SHA-256
`5c37f187e3c3e286321273aa84046bd634e34fad65aa7791d0b9da8970e80d4d`.
Its build configuration enables GPL and version 3 licensing. The GNU General
Public License version 3 text shipped with PullWatch applies to this runtime.

## Flyleaf.FFmpeg.Bindings

- Component: Flyleaf.FFmpeg.Bindings 7.1.1
- Copyright: SuRGeoNix
- License: GNU Lesser General Public License version 3 or later
- Source: https://github.com/SuRGeoNix/Flyleaf.FFmpeg.Generator
- License text: https://www.gnu.org/licenses/lgpl-3.0.html

## Vortice.Windows and SharpGen.Runtime

- Components: Vortice.Windows 3.7.6-beta, Vortice.Mathematics 1.9.3, and
  SharpGen.Runtime 2.4.2-beta
- Copyright: Amer Koleci and contributors
- License: MIT License
- Source: https://github.com/amerkoleci/Vortice.Windows
- SharpGen source: https://github.com/SharpGenTools/SharpGenTools

## Bootstrap Icons

- Component: Bootstrap Icons (icon path data embedded in PullWatch shell icons)
- Copyright: The Bootstrap Authors
- License: MIT License
- Source: https://github.com/twbs/icons
- License text: https://github.com/twbs/icons/blob/main/LICENSE

PullWatch embeds icon path data from Bootstrap Icons; no Bootstrap Icons
package or asset file is redistributed.

These notices are provided for attribution and license compliance. The
respective licenses govern the listed third-party components.
