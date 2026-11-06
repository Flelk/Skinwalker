# Skinwalker

R.E.P.O. mod: a monster that copies your team's voices. See `thunderstore/README.md`.

It's built on the game's "Enemy - Hidden" kidnapper, so health, stuns, physics and syncing are the game's own. The host runs the AI in `src/Brain.cs` and tells other players what to play and show through Photon events.

`.\build.ps1` builds and packages. `.\build.ps1 -Install -R2Profile repo` also installs it.
