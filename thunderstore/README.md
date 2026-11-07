# Skinwalker

Something on the level has been listening to your team.

- **It copies voices.** You hear a teammate say something they really said earlier, from around a corner. You never hear it use your own voice, so you won't know, and it never grabs the person it's copying.
- **It waits until you're alone.** It only goes after someone with no teammate nearby, and backs off if one catches up. It leaves you alone for the first 3 minutes of a level, and for 5 minutes after it's taken you.
- **The corner.** It always looks like one of your team. Usually it stands there facing away, until you get close or stare too long. Sometimes it's gone before you get there and the voice moves further away. Sometimes it just rushes you.
- **The kidnapping.** It drags you off. Teammates can stun it to free you, and pressing jump shortens the drag. It drops you hurt, far from everyone, and uses your voice to lure them.
- **The Swap.** Sometimes it leaves you stuck and walks back wearing your body, name tag and voice. A teammate can grab you to free you, and pressing jump frees you sooner.
- **Death lines.** If you die after it's had you, your last words stay in its rotation for the rest of the level.

It can also wander the level looking like a teammate, so take a second look at who you're following.

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
| ShowVoiceNotice | true | show a reminder at the start of a level |
| LineVolume | 0.7 | how loud the copied voices are for you |
| IsolationDistance | 15 | it only goes after someone with no teammate this close, in meters (host only) |
| SpawnChance | 100 | percent chance per level (host only) |
| StartGraceSeconds | 180 | how long a level runs before its first lure (host only) |
| MinSecondsBetweenLures / MaxSecondsBetweenLures | 150 / 300 | time between lures (host only) |
| SwapChance | 35 | percent chance it swaps after a kidnapping (host only) |
| DropDamage | 25 | damage when it drops you (host only) |
| TrapSeconds | 25 | how long the Swap keeps you stuck (host only) |
| TestKeys | false | F10 forces a lure, F11 forces the Swap (host or solo) |
