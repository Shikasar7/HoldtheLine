# Hold the Line SFX — CC0 sources and delivery format

All 12 files in this directory are production-ready derivatives of Kenney game-audio packs, each
published under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). CC0 permits commercial
use; attribution is not required, but the source record is retained here for auditability.

| SfxBank key | Shipped file | Original package / source clip |
| --- | --- | --- |
| `attack` | `attack.ogg` | [RPG Audio](https://kenney.nl/assets/rpg-audio) / `chop.ogg` |
| `shoot` | `shoot.ogg` | [Sci-fi Sounds](https://kenney.nl/assets/sci-fi-sounds) / `laserSmall_000.ogg` |
| `cast` | `cast.ogg` | Sci-fi Sounds / `forceField_001.ogg` |
| `death` | `death.ogg` | Sci-fi Sounds / `explosionCrunch_003.ogg` |
| `leaderhit` | `leaderhit.ogg` | Sci-fi Sounds / `lowFrequency_explosion_000.ogg` |
| `play` | `play.ogg` | RPG Audio / `bookPlace2.ogg` |
| `move` | `move.ogg` | RPG Audio / `footstep05.ogg` |
| `draw` | `draw.ogg` | RPG Audio / `bookFlip2.ogg` |
| `turnstart` | `turnstart.ogg` | [Interface Sounds](https://kenney.nl/assets/interface-sounds) / `bong_001.ogg` |
| `tide` | `tide.ogg` | [Impact Sounds](https://kenney.nl/assets/impact-sounds) / `impactSoft_heavy_003.ogg` |
| `button` | `button.ogg` | Interface Sounds / `click_003.ogg` |
| `victory` | `victory.ogg` | [Music Jingles](https://kenney.nl/assets/music-jingles) / `jingles_PIZZI00.ogg` |
| `defeat` | `defeat.ogg` | Music Jingles / `jingles_HIT00.ogg` |

## Delivery processing

Each shipped clip is stereo OGG/Vorbis at 44,100 Hz. During integration its decoded sample peak was
measured with FFmpeg `volumedetect`; gain was applied and the encoded result re-measured so its peak
does not exceed the `-6 dBFS` delivery ceiling (`-ar 44100 -ac 2 -c:a libvorbis -q:a 5`).

`SfxBank` loads `res://assets/audio/sfx/<key>.ogg` first. If a file is missing or Godot cannot import
it, it retains the former synthesized clip for that key, so incomplete exports degrade safely rather
than becoming silent.
