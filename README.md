# Voices from the Rust World

Vintage Story mod for human-recorded narration of lore books and scrolls.

## Current Test Clip

Record the `Whinging` lore entry as Ogg Vorbis and place it here:

```text
assets/voicesfromtherustworld/sounds/narration/test/whinging/piece1.ogg
```

In game, run:

```text
/vfrw test
```

The command intentionally uses the normal Vintage Story sound pipeline, so the file should be Ogg Vorbis, not Ogg Opus.

## Planned Pack Shape

Narrator-pack metadata currently starts here:

```text
assets/voicesfromtherustworld/config/voicesfromtherustworld/narrators/test.json
```

Narration sounds should use this pattern:

```text
assets/<domain>/sounds/narration/<narrator>/<lorecode>/piece<number>.ogg
```
