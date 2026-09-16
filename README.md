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
- [RadialUI](https://github.com/TaleSpire-Modding/RadialUI) 3.2.1 (from Thunderstore — NuGet stops
  at 3.1.3, which is too old to run on TaleSpire 1.6.0)

Install by dropping `AutoHide.dll` into `BepInEx/plugins/`. Every client that should be affected
needs the plugin — a player without it sees the map as normal.

## Using it

### As a GM: arm the board

1. Enter GM mode and tap **Tab** to open the GM overview. (Holding Tab only peeks — it closes
   again when you let go.)
2. Pick the **atmosphere block** tool from the overview's tool strip and left-click on terrain
   to place one. Click bare ground: clicking an existing GM block adds to that one instead.
   TaleSpire opens its atmosphere panel afterwards, which you can just close.
3. Right-click the block and choose **AutoHide**. Right-clicking only works while the GM
   overview is open — outside it the block is not on a clickable layer.

From that moment, every client with the plugin hides terrain their creature cannot see. The
block you picked is now the board's AutoHide switch:

- **Saved with the board**, so the mode stays on between sessions. It is a property of the map,
  not something each player has to remember to switch on.
- **Invisible to players.** GM blocks live on TaleSpire's GM-only render layer, which player
  clients never draw.
- **A single switch.** Choosing **AutoHide** on a different block moves the setting there.

Choosing **AutoHide** again turns it off and hands the block back to TaleSpire as an ordinary
atmosphere block, which you can keep or delete with the menu's own delete button.

GM mode itself is never affected — you are meant to see everything, so the board setting only
binds clients that are actually playing. Switch yourself to player mode and hiding applies to
you too; switch back and the whole map returns.

### The block's menu

Right-clicking the AutoHide block gives you three buttons alongside TaleSpire's own:

| Button | Effect |
| --- | --- |
| **AutoHide** | Whether this board hides out-of-sight terrain at all |
| **Fog** | Whether seen terrain stays visible, for everyone on the board |
| **Reset fog** | Make everyone forget the terrain they have seen |

**Reset fog** asks for confirmation, because it cannot be undone. Use it when the party
re-enters a dungeon you would rather they explored again, or after moving them somewhere the
old memory would spoil.

There are no keyboard shortcuts for any of this. These are settings you make once per map, and
a stray keystroke should not be able to wipe the party's explored map.

### As a player

Nothing to do. If the GM has armed the board, hiding is simply on.

**Ctrl+H** toggles hiding by hand, for a board with no AutoHide block on it — handy for solo use
or for trying the plugin out. Unlike the board setting it also switches you between GM and player
mode, since line-of-sight hiding only means anything in player mode.

### Seen-terrain memory

With **Fog** on (the default), terrain stays visible once your creature has seen it, the way fog
of war works: you build up a picture of the dungeon as you explore instead of watching walls
vanish behind you. With it off, anything out of line of sight is hidden immediately.

Creatures are unaffected either way — TaleSpire keeps hiding those by its own line of sight, so
memory never reveals where the goblins are now.

The explored map **survives quitting the game**. It is written to `BepInEx/AutoHide/<board>.fog`,
one file per board, saved every 15 seconds while you explore and again when hiding stops. Reopen
the board next week and the dungeon is still mapped. **Reset fog** deletes the file, so the
party's map does not quietly come back.

That file is yours alone: it records what *your* client saw. Players each build their own, and
someone joining an ongoing campaign starts with the map dark even if the rest of the party has
walked every corridor.

## Configuration

`BepInEx/config/org.talespire.plugins.autohide.cfg`, written on first run. BepInEx does not
reload it while the game is running, so restart after editing.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Controls.ToggleTracking` | Ctrl+H | Local toggle, also switches GM/player mode |
| `Behaviour.RememberSeenTerrain` | true | Local default, used when no AutoHide block is present |

Seen-terrain memory lives next to it, in `BepInEx/AutoHide/`. Deleting a `.fog` file there is the
same as resetting that board's fog for yourself.

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

The board setting rides on the chosen `AtmosphereBlock`'s unused `Content` field, as a signature
plus a word of flags. TaleSpire never reads that field, and it is synced and saved like any other
board data — which is what lets the board itself carry the setting, with no separate channel
between GM and players. Clearing the signature is what hands the block back to TaleSpire.

## Limitations

- Hidden terrain leaves a dark footprint on the board mat, tracing the shape of the tiles that
  were removed. That is TaleSpire's own doing: the mat shades itself differently underneath board
  content, and it keeps doing so whether or not the content is drawn. The game's hand-placed hide
  volumes have exactly the same effect, and nothing a hide volume can be told to hide reaches the
  mat. Picking a board mat colour with less contrast between its two tones makes it less obvious.
- Terrain more than 48 units away is never processed and stays visible.
- A full refresh takes about a second, because TaleSpire processes one zone per frame.
- Board changes only save while the GM client is in GM mode. Arming the board and immediately
  quitting from player mode can lose the setting.
