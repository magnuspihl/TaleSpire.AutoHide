# AutoHide

A [TaleSpire](https://talespire.com) plugin that hides terrain a player cannot see.

TaleSpire already hides *creatures* that are out of line of sight, but not the map itself — so
a player standing in a sealed room can still see the chimney on the roof outside, the corridor
around the corner, and the shape of every room they have not entered yet. AutoHide extends the
game's own line-of-sight data to terrain, so players see only what their creature can actually
see.

Optionally it behaves like fog of war: terrain stays visible once it has been seen.

## Requirements

- TaleSpire 1.6.0
- BepInEx 5.4.x
- [SetInjectionFlagPlugin](https://github.com/TaleSpire-Modding/SetInjectionFlagPlugin) 3.4.1

Install by dropping `AutoHide.dll` into `BepInEx/plugins/`. Every client that should be affected
needs the plugin — a player without it sees the map as normal.

## Using it

### As a GM: arm the board

Select any creature and press **Ctrl+G**.

This places an *AutoHide block* on the board. From that moment, every client with the plugin
hides terrain their creature cannot see. The block is:

- **Saved with the board**, so the mode stays on between sessions. It is a property of the map,
  not something each player has to remember to switch on.
- **Invisible to players.** It lives on TaleSpire's GM-only render layer, which player clients
  never draw. You see it yourself by holding the GM overview key.
- **A single switch.** Press **Ctrl+G** again to remove it and return the board to normal.

GM mode itself is never affected — you are meant to see everything, so the board setting only
binds clients that are actually playing. Switch yourself to player mode and hiding applies to
you too; switch back and the whole map returns.

Two more controls act on the whole party at once:

| Key | Effect |
| --- | --- |
| **Ctrl+M** | Toggle seen-terrain memory (fog-of-war behaviour) for everyone |
| **Ctrl+B** | Make everyone forget the terrain they have seen |

Use **Ctrl+B** when the party re-enters a dungeon you would rather they explored again, or after
moving them somewhere the old memory would spoil.

### As a player

Nothing to do. If the GM has armed the board, hiding is simply on.

**Ctrl+H** toggles hiding by hand, for a board with no AutoHide block on it — handy for solo use
or for trying the plugin out. Unlike the GM block it also switches you between GM and player
mode, since line-of-sight hiding only means anything in player mode.

### Seen-terrain memory

With memory on (the default), terrain stays visible once your creature has seen it, the way fog
of war works: you build up a picture of the dungeon as you explore instead of watching walls
vanish behind you. With it off, anything out of line of sight is hidden immediately.

Creatures are unaffected either way — TaleSpire keeps hiding those by its own line of sight, so
memory never reveals where the goblins are now.

## Configuration

`BepInEx/config/org.talespire.plugins.autohide.cfg`, written on first run. BepInEx does not
reload it while the game is running, so restart after editing.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Controls.GmBlockToggle` | Ctrl+G | Arm/disarm the board |
| `Controls.GmBlockToggleMemory` | Ctrl+M | Toggle seen-terrain memory board-wide |
| `Controls.GmBlockResetFog` | Ctrl+B | Forget seen terrain board-wide |
| `Controls.ToggleTracking` | Ctrl+H | Local toggle, also switches GM/player mode |
| `Behaviour.RememberSeenTerrain` | true | Local default, used when no AutoHide block is present |

The `Diagnostics` section holds probes used while developing the plugin. They are unbound by
default and can be left alone; `BoardVolumePurge` in particular deletes *every* hide volume on
the board, including hand-placed ones.

## How it works

TaleSpire computes line of sight on the GPU to draw fog of war for creatures. AutoHide reads
that same visibility mask, one 16×16×16 zone at a time, works out which cells the creature
cannot see, and covers them with hide volumes.

Two details matter for anyone reading the source:

- The volumes are **zone-local** (`Zone.SetHideVolume`), not board-level. Board-level hide
  volumes are networked and permanent; writing them every frame would wreck the map. A Harmony
  patch on `Zone.CollectDataForSector` additionally lifts our volumes out of the way while the
  game saves, so they can never leak into the board file.
- Hiding is per-placeable and all-or-nothing: a volume that so much as touches a tile hides the
  whole tile. The generated boxes are therefore eroded by one cell so they never abut something
  the player should still see.

The GM block is an `AtmosphereBlock` whose unused `Content` field carries a signature plus a
word of flags. TaleSpire never reads that field, and it is synced and saved like any other board
data — which is what lets the board itself carry the setting, with no separate channel between
GM and players.

## Limitations

- Terrain more than 48 units away is never processed and stays visible.
- A full refresh takes about a second, because TaleSpire processes one zone per frame.
- Board changes only save while the GM client is in GM mode. Arming the board and immediately
  quitting from player mode can lose the block.
