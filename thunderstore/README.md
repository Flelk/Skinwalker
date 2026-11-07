# Skinwalker

Something on the level has been listening to your team.

- **It copies voices.** You hear a teammate say something they really said earlier, from around a corner. You never hear it use your own voice, so you won't know, and it never grabs the person it's copying.
- **It waits until you're alone.** It only goes after someone with no teammate nearby, and groups can walk past it unharmed. It leaves you alone for the first 3 minutes of a level, and for 5 minutes after it's taken you.
- **The corner.** It's a trap. It stands completely still in an empty room, looking like one of your team with its back to you, and only moves once someone walks in. Usually it turns on you. Sometimes it's gone and the voice has moved to another room. Sometimes it just rushes you.
- **The kidnapping.** It drags you off. Teammates can stun it to free you, and pressing jump shortens the drag. It drops you hurt, far from everyone, and uses your voice to lure them.
- **The Swap.** Sometimes it leaves you stuck and waits in a room near your team, wearing your body, name tag and voice. A teammate can grab you to free you, and pressing jump frees you sooner.
- **Death lines.** If you die after it's had you, your last words stay in its rotation for the rest of the level.

## Your voice
**This mod records voice chat, so make sure everyone in your lobby knows and agrees.**

- Clips of what you say are sent to the other players in your lobby.
- Clips are kept in memory only and are never saved to disk. They're gone when you leave the lobby or close the game.
- Audio is not captured while you're muted or not holding push-to-talk.
- To opt out, set `ShareMyVoice` to false. Your mic is then not captured at all.

## Who needs it
**Everyone in the lobby.** Players without it only get an ordinary invisible monster, and can't hear or see its tricks.

## Config (`flelk.skinwalker.cfg`, or r2modman's Config editor)
| Setting | Default | |
| --- | --- | --- |
| ShareMyVoice | true | capture your voice and share it with the lobby |
| LineVolume | 0.7 | how loud the copied voices are for you |
| IsolationDistance | 15 | it only goes after someone with no teammate this close, in meters (host only) |
| SpawnChance | 100 | percent chance per level (host only) |
| StartGraceSeconds | 180 | how long a level runs before its first lure (host only) |
| MinSecondsBetweenLures / MaxSecondsBetweenLures | 150 / 300 | time between lures (host only) |
| SwapChance | 35 | percent chance it swaps after a kidnapping (host only) |
| DropDamage | 25 | damage when it drops you (host only) |
| TrapSeconds | 25 | how long the Swap keeps you stuck (host only) |
| TestKeys | false | F10 forces a lure, F11 forces the Swap (host or solo) |
