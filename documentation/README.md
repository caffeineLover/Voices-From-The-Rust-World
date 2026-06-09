# Voices from the Rust World

Vintage Story mod for human-recorded narration of lore books and scrolls.

## License

The framework/code mod, `voicesfromtherustworld`, is licensed under
LGPLv3-or-later with an additional GPLv3 section 7(b) attribution preservation
term. See `../LICENSE.md`, `../COPYING.LESSER`, and `../COPYING`.

The bundled default voice pack, `vfrwdefaultvoices`, including bundled
narration recordings, is licensed separately under CC BY-NC-ND 4.0 unless a
file says otherwise. See `../COPYING.CC-BY-NC-ND-4.0` and
`../voicepacks/vfrwdefaultvoices/LICENSE.md`.

LGPLv3 permits forks and modified redistribution under its terms. It does not
prohibit forks.

## Current Narration Clip

Record the `Whinging` lore entry as Ogg Vorbis and place it here:

```text
voicepacks/vfrwdefaultvoices/assets/vfrwdefaultvoices/sounds/narration/whinging/piece1.ogg
```

In game, run:

```text
.vfrw play whinging
.vfrw stop
```

To list discovered narrator packs, run:

```text
.vfrw packs
```

To check or adjust narration loudness, run:

```text
.vfrw volume
.vfrw volume 2.5
```

Narration volume is a multiplier capped at `8.0`.

Matching narration now starts automatically when you open a supported lore book. To toggle that behavior:

```text
.vfrw autoplay
.vfrw autoplay on
.vfrw autoplay off
```

To lower other sound categories while narration plays:

```text
.vfrw ducking
.vfrw ducking 50
.vfrw ducking off
```

`.vfrw ducking 50` temporarily lowers entity, ambient, weather, and music volume levels by 50% while narration is playing, then restores the exact previous levels when narration stops, finishes, or the book closes.

The normal Vintage Story `Sound` category is not ducked by default because VFRW narration currently plays through that same category. If you need to duck general sound effects too, set `DuckGeneralSoundCategory` to `true` in the client config.

The command intentionally uses the normal Vintage Story sound pipeline, so the file should be Ogg Vorbis, not Ogg Opus.

## Config

Client settings are stored in:

```text
ModConfig/voicesfromtherustworld-client.json
```

Current settings:

```text
AutoPlayOnBookOpen = true
NarrationVolume = 1.0
OtherSoundDuckingPercent = 50.0
DuckGeneralSoundCategory = false
```

If ConfigLib is installed and enabled, VFRW registers these client settings with ConfigLib as well. ConfigLib is optional; without it, the JSON config and `.vfrw` commands still work.

## Replacing Audio Without Restarting

For fast recording iteration, test from loose voice-pack files instead of a packaged zip. The launch profile should include the voice-pack folder as an extra mod path:

```text
--addModPath "E:\Path\To\Voices from the Rust World\voicepacks"
```

Then replace the `.ogg` file on disk and run:

```text
.vfrw stop
.reload sounds
.vfrw play whinging
```

`.vfrw stop` stops the currently tracked narration sound. `.reload sounds` asks Vintage Story to reload sound assets. The next `.vfrw play whinging` should use the replaced file.

If you are testing from zipped mods in the `Mods` folder, replacing the source `.ogg` in this repository will not update the already-loaded zip. Repackage the voice pack or switch to loose-file testing while recording.

## Debugging With Books

Vintage Story uses two chat command prefixes:

```text
.vfrw packs
.vfrw play whinging
.vfrw book whinging
/giveitem ...
```

`.` commands are client commands. `/` commands are server commands.

### Short Method

Use the VFRW helper command for normal debugging:

```text
.vfrw books
.vfrw book whinging
.vfrw book whinging 1
```

`.vfrw books` lists known lore codes.

`.vfrw book <lorecode>` gives you one book containing all pieces for that lore entry.

`.vfrw book <lorecode> <piece>` gives you one book containing only that one-based piece number.

Open the spawned book to test automatic narration playback. Use `.vfrw stop` if you need to stop the clip before closing the book.

The helper sends Vintage Story's `/giveitem` command for you, so your player still needs permission to use `/giveitem`. In a local creative/debug world this is usually fine.

### Raw Method

If the helper does not work, use the raw server command. To give yourself the current `Whinging` test book, run this as one line in a test world:

```text
/giveitem game:lore-book-aged-orange 1 s[] {"discoveryCode":"whinging","chapterIds":[0],"textCodes":["lore-whinging-piece1"],"titleCode":"lore-whinging-title"}
```

For other lore entries, replace these values:

```text
discoveryCode = lore code, for example whinging
titleCode = title language key, for example lore-whinging-title
textCodes = piece language keys, for example lore-whinging-piece1
chapterIds = zero-based chapter indexes, so piece1 is 0, piece2 is 1, and so on
```

The voice-pack metadata uses one-based piece numbers (`piece: 1`, `piece: 2`). The vanilla book item NBT uses zero-based `chapterIds` (`0`, `1`). This mismatch is easy to trip over when debugging.

The `.vfrw book` helper accepts one-based piece numbers to match voice-pack metadata, then converts them to zero-based `chapterIds` internally.

## Mod Split

`voicesfromtherustworld` is the framework/code mod.

`vfrwdefaultvoices` is a content-only voice pack displayed as `Voices from the Rust World - Default Voices` under:

```text
voicepacks/vfrwdefaultvoices
```

Third-party voice packs should be separate content mods that depend on `voicesfromtherustworld`.

Do not overwrite files from `vfrwdefaultvoices`. Make a separate content mod with its own `modid`.

## Making A Voice Pack

A voice pack is a normal Vintage Story content mod. Its zip root should contain `modinfo.json` and `assets/`.

Example voice-pack folder:

```text
myvoicepack/
  modinfo.json
  assets/
    myvoicepack/
      config/
        voicesfromtherustworld/
          narrators/
            myvoicepack.json
      sounds/
        narration/
          whinging/
            piece1.ogg
```

Example `modinfo.json`:

```json
{
  "type": "content",
  "modid": "myvoicepack",
  "name": "My Voice Pack",
  "authors": [ "Your Name" ],
  "description": "Human-recorded narration pack for Voices from the Rust World.",
  "version": "0.0.1",
  "dependencies": {
    "game": "1.22.3",
    "voicesfromtherustworld": "0.0.1"
  }
}
```

The `modid` is also the asset domain in paths like:

```text
assets/myvoicepack/...
myvoicepack:sounds/narration/whinging/piece1
```

When packaging the voice pack, zip the contents of `myvoicepack/`, not the parent directory. The zip should open directly to:

```text
modinfo.json
assets/
```

## Voice Pack Shape

Narrator-pack metadata currently starts here:

```text
assets/<voicepackdomain>/config/voicesfromtherustworld/narrators/<packcode>.json
```

Narration sounds should use this pattern:

```text
assets/<voicepackdomain>/sounds/narration/<lorecode>/piece<number>.ogg
```

Sound asset references omit the `.ogg` extension:

```text
<voicepackdomain>:sounds/narration/<lorecode>/piece<number>
```

Example metadata entry:

```json
{
  "loreCode": "whinging",
  "piece": 1,
  "sound": "vfrwdefaultvoices:sounds/narration/whinging/piece1"
}
```

Voice packs may also set a top-level `"volume"` multiplier, and individual entries may set their own `"volume"` multiplier. The final playback gain is:

```text
client narration volume * voice pack volume * entry volume
```

Example narrator metadata:

```json
{
  "code": "myvoicepack",
  "name": "My Voice Pack",
  "description": "Human-recorded narration by Your Name.",
  "authors": [ "Your Name" ],
  "language": "en",
  "format": "ogg-vorbis",
  "volume": 1.0,
  "entries": [
    {
      "loreCode": "whinging",
      "piece": 1,
      "sound": "myvoicepack:sounds/narration/whinging/piece1",
      "volume": 1.0
    }
  ]
}
```

Audio files should be Ogg Vorbis, not Ogg Opus. Mono is recommended for narration. Normalize and compress recordings before export so the spoken voice is clear without needing extreme in-game gain.

## Voice Pack Debug Checklist

Use this checklist when a voice pack does not work:

1. Confirm both mods are enabled: `voicesfromtherustworld` and your voice-pack content mod.
2. Run `.vfrw packs` and confirm your pack is listed.
3. Confirm the narrator JSON is under `assets/<voicepackdomain>/config/voicesfromtherustworld/narrators/`.
4. Confirm the `sound` value uses your voice-pack domain and omits `.ogg`.
5. Confirm the Ogg file exists under `assets/<voicepackdomain>/sounds/narration/<lorecode>/piece<number>.ogg`.
6. Use `.vfrw book <lorecode> [piece]` to spawn the matching lore book, then open it to test automatic playback.
7. If `.vfrw book` fails, try the raw `/giveitem` command from the Debugging With Books section.
