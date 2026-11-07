using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using State = EnemyHidden.State;

namespace Skinwalker;

/// <summary>
/// The monster's decisions. Only the host (or a solo player) runs this.
/// It's a trap: it stands completely still in an empty room, calling out, and only moves once someone walks in.
/// </summary>
internal class Brain
{
    private enum Phase { Wait, Trap, Empty, Snap, Grab }

    private const float SnapSeconds = 0.35f, CallSeconds = 45f, TrapSeconds = 180f, TakenCooldown = 300f, BumpDistance = 3.5f;

    private readonly EnemyParent parent;
    private readonly EnemyHidden hidden;
    private readonly Enemy enemy;

    private Phase phase = Phase.Wait;
    private float nextLure, nextLine, phaseTime;
    private bool forced, testing, rushes;
    private int pendingVoice = -1;
    private PlayerAvatar target;
    private int speaker = -1;
    private int hops;
    private RoomVolume trapRoom;

    // What it's currently wearing. Sent again every few seconds for anyone still loading when it changed.
    private int wornActor = -1;
    private bool wornSnap;
    private float nextWear;

    private readonly HashSet<int> victims = new HashSet<int>();
    private readonly Dictionary<int, float> lastTaken = new Dictionary<int, float>();
    private bool wearingVictim, grabFromFake;
    private readonly Dictionary<int, int> deathLines = new Dictionary<int, int>();

    private PlayerAvatar trapped;
    private Vector3 trapAnchor;
    private float trapTimer;

    /// <summary>True whenever the game's own roaming and investigating should stay switched off.</summary>
    internal bool Holding => phase != Phase.Grab;

    internal Brain(EnemyParent parent, EnemyHidden hidden)
    {
        this.parent = parent;
        this.hidden = hidden;
        enemy = hidden.enemy;
        parent.enemyName = "Skinwalker";
        parent.DespawnedTimeMin = 15f;
        parent.DespawnedTimeMax = 30f;
        nextLure = Plugin.StartGraceSeconds.Value;
    }

    /// <summary>It always looks like a player, so there's never an invisible monster.</summary>
    private void Wear(int actor, bool snap, float yaw)
    {
        wornActor = actor;
        wornSnap = snap;
        nextWear = Time.unscaledTime + 5f;
        Net.SendDisguise(actor, true, snap, yaw);
    }

    private void WearSomeone()
    {
        List<PlayerAvatar> alive = Alive();
        if (alive.Count > 0) Wear(Net.ActorOf(alive[Random.Range(0, alive.Count)]), false, float.NaN);
    }

    private void KeepWearing()
    {
        if (wornActor < 0 || Net.AvatarOf(wornActor) == null) WearSomeone();
        else if (Time.unscaledTime >= nextWear) Wear(wornActor, wornSnap, float.NaN);
    }

    private void ScheduleLure()
    {
        float a = Plugin.LureMinSeconds.Value, b = Plugin.LureMaxSeconds.Value;
        nextLure = Random.Range(Mathf.Min(a, b), Mathf.Max(a, b));
    }

    internal void ForceLure()
    {
        forced = true;
        nextLure = 0f;
        if (phase != Phase.Grab && phase != Phase.Snap) phase = Phase.Wait;
    }

    internal void ForceSwap()
    {
        PlayerAvatar me = SemiFunc.PlayerAvatarLocal();
        if (!parent.Spawned)
        {
            parent.DespawnedTimerSet(0f);
            Plugin.Log.LogInfo("Test: it was despawned and is coming back, press F11 again in a moment.");
            return;
        }
        Stop(false);
        testing = true;
        BeginSwap(me, Alive().Count > 1);
    }

    // ---------- every frame ----------

    internal void Tick(float dt)
    {
        if (parent.Spawned) KeepWearing();

        if (phase != Phase.Wait)
        {
            if (enemy.IsStunned())
            {
                Plugin.Log.LogInfo("Stunned, giving up.");
                Stop(false);
                if (trapped) trapTimer = Mathf.Min(trapTimer, 3f);
                return;
            }
            if (!parent.Spawned)
            {
                Stop(false);
                return;
            }
            parent.SpawnedTimerPause(30f);
        }

        phaseTime += dt;
        switch (phase)
        {
            case Phase.Wait: TickWait(dt); break;
            case Phase.Trap: TickTrap(); break;
            case Phase.Empty: TickEmpty(); break;
            case Phase.Snap: if (phaseTime >= SnapSeconds) StartGrab(target); break;
            case Phase.Grab: TickGrab(); break;
        }
    }

    private void TickWait(float dt)
    {
        nextLure -= dt;
        if (nextLure > 0f) return;

        if (!parent.Spawned)
        {
            parent.DespawnedTimerSet(0f);
            return;
        }
        if (hidden.currentState != State.Idle && hidden.currentState != State.Roam) return;
        if (!forced && SemiFunc.EnemySpawnIdlePause()) return;

        testing = forced;
        forced = false;
        int voice = pendingVoice;
        if (StartLure(voice)) pendingVoice = -1;
        else
        {
            testing = false;
            nextLure = 5f;
        }
    }

    // ---------- who's where ----------

    private List<PlayerAvatar> Alive() => SemiFunc.PlayerGetList().Where(p => p && !p.isDisabled && !p.deadSet).ToList();

    private static float NearestOther(PlayerAvatar player, List<PlayerAvatar> all)
    {
        float nearest = 999f;
        foreach (PlayerAvatar other in all)
            if (other != player) nearest = Mathf.Min(nearest, Vector3.Distance(player.transform.position, other.transform.position));
        return nearest;
    }

    private bool Isolated(PlayerAvatar player, List<PlayerAvatar> alive) =>
        testing || NearestOther(player, alive) >= Plugin.IsolationDistance.Value;

    private bool RecentlyTaken(PlayerAvatar player) =>
        !testing && lastTaken.TryGetValue(Net.ActorOf(player), out float time) && Time.time - time < TakenCooldown;

    /// <summary>It never grabs the person it's copying. Alone in a lobby, you're the only person it can copy.</summary>
    private bool IsCopying(PlayerAvatar player)
    {
        if (SemiFunc.PlayerGetList().Count <= 1) return false;
        int actor = Net.ActorOf(player);
        return actor == wornActor || actor == speaker;
    }

    private bool TargetGone() => !target || target.isDisabled || target.deadSet;

    private static bool CanSee(PlayerAvatar player, Vector3 position)
    {
        Vector3 eye = player.transform.position + Vector3.up * 1.4f;
        return !Physics.Linecast(eye, position + Vector3.up * 1.2f, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
    }

    private static bool SameRoom(RoomVolume a, RoomVolume b) => a && b && (a == b || (a.MapModule && a.MapModule == b.MapModule));

    private static bool InRoom(PlayerAvatar player, RoomVolume room) =>
        player.RoomVolumeCheck && player.RoomVolumeCheck.CurrentRooms.Any(r => SameRoom(r, room));

    private bool InTrapRoom(PlayerAvatar player)
    {
        if (Vector3.Distance(player.transform.position, enemy.transform.position) < BumpDistance) return true;
        if (trapRoom) return InRoom(player, trapRoom);
        return parent.currentRooms.Any(room => InRoom(player, room));
    }

    /// <summary>Someone it can spring on who's walked into its room, closest first. Groups walk past it unharmed.</summary>
    private PlayerAvatar Entrant()
    {
        List<PlayerAvatar> alive = Alive();
        return alive
            .Where(p => InTrapRoom(p) && !IsCopying(p) && !RecentlyTaken(p) && Isolated(p, alive))
            .OrderBy(p => Vector3.Distance(p.transform.position, enemy.transform.position))
            .FirstOrDefault();
    }

    /// <summary>Goes for someone on their own, and leaves people in the truck alone.</summary>
    private PlayerAvatar PickTarget(List<PlayerAvatar> alive)
    {
        var candidates = alive.Where(p => (!p.RoomVolumeCheck || !p.RoomVolumeCheck.inTruck) && Isolated(p, alive) && !RecentlyTaken(p)).ToList();
        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(p => NearestOther(p, alive) + Random.Range(0f, 8f)).First();
    }

    /// <summary>A spot nobody can see, in a room nobody's in.</summary>
    private bool FindHidingSpot(PlayerAvatar near, List<PlayerAvatar> alive, float min, float max, out Vector3 spot, out RoomVolume room)
    {
        for (int i = 0; i < 16; i++)
        {
            LevelPoint point = SemiFunc.LevelPointGet(near.transform.position, min, max);
            if (!point) break;
            if (!NavMesh.SamplePosition(point.transform.position + Random.insideUnitSphere * 2f, out NavMeshHit hit, 3f, -1)) continue;
            if (alive.Any(p => CanSee(p, hit.position) || InRoom(p, point.Room) || Vector3.Distance(p.transform.position, hit.position) < 6f)) continue;
            spot = hit.position;
            room = point.Room;
            return true;
        }
        spot = default;
        room = null;
        return false;
    }

    private void MoveTo(Vector3 spot, RoomVolume room)
    {
        enemy.EnemyTeleported(spot);
        enemy.NavMeshAgent.ResetPath();
        trapRoom = room;
    }

    // ---------- setting the trap ----------

    private bool StartLure(int forcedSpeaker)
    {
        List<PlayerAvatar> alive = Alive();
        target = PickTarget(alive);
        if (!target) return false;
        if (!FindHidingSpot(target, alive, 8f, 22f, out Vector3 spot, out RoomVolume room)) return false;

        MoveTo(spot, room);
        hops = Random.Range(1, 3);
        speaker = forcedSpeaker >= 0 && forcedSpeaker != Net.ActorOf(target) ? forcedSpeaker : PickSpeaker(target);

        int roll = Random.Range(0, 100);
        phase = roll >= 55 && roll < 85 ? Phase.Empty : Phase.Trap;
        rushes = roll >= 85;
        phaseTime = 0f;
        nextLine = 0f;

        // It stands with its back to you, wearing whoever's voice it's using.
        int body = speaker >= 0 ? speaker : Net.ActorOf(SemiFunc.PlayerGetList().FirstOrDefault(p => p && p != target) ?? target);
        Vector3 away = spot - target.transform.position;
        float yaw = Quaternion.LookRotation(new Vector3(away.x, 0.001f, away.z)).eulerAngles.y;
        hidden.rotationTarget = Quaternion.Euler(0f, yaw, 0f);
        Wear(body, false, yaw);

        Plugin.Log.LogInfo($"Trap set: {(phase == Phase.Empty ? "empty" : rushes ? "rush" : "figure")} for {target.playerName}, using {(speaker >= 0 ? Net.AvatarOf(speaker)?.playerName : "no voice")}.");
        return true;
    }

    private int PickSpeaker(PlayerAvatar who)
    {
        int whoActor = Net.ActorOf(who);
        var voices = VoiceBank.ActorsWithClips();
        var others = voices.Where(a => a != whoActor).ToList();

        // Someone who isn't nearby is the most convincing, and someone who died is the worst to hear.
        var dead = others.Where(deathLines.ContainsKey).ToList();
        if (dead.Count > 0 && Random.value < 0.4f) return dead[Random.Range(0, dead.Count)];
        if (others.Count > 0)
        {
            return others.OrderByDescending(a =>
            {
                PlayerAvatar avatar = Net.AvatarOf(a);
                return (avatar ? Vector3.Distance(avatar.transform.position, who.transform.position) : 50f) + Random.Range(0f, 15f);
            }).First();
        }
        // Solo, your own voice is all it has. Online you'd never hear it, so it stays silent instead.
        return !GameManager.Multiplayer() && voices.Contains(whoActor) ? whoActor : -1;
    }

    /// <summary>It only calls to someone who's still alone, and after a while it goes quiet and just waits.</summary>
    private void Call(float gapMin, float gapMax)
    {
        if (phaseTime > CallSeconds || TargetGone() || !Isolated(target, Alive())) return;
        Speak(speaker, gapMin, gapMax);
    }

    private void Speak(int actor, float gapMin, float gapMax, bool follow = false)
    {
        if (phaseTime < nextLine) return;
        nextLine = phaseTime + Random.Range(gapMin, gapMax);
        if (actor < 0) return;

        int clip = deathLines.TryGetValue(actor, out int line) && Random.value < 0.5f ? line : VoiceBank.RandomClip(actor);
        if (clip >= 0) Net.SendLine(actor, clip, enemy.transform.position + Vector3.up * 1.3f, follow);
    }

    // ---------- the trap ----------

    private void TickTrap()
    {
        if (phaseTime > TrapSeconds)
        {
            Stop(true);
            return;
        }
        Call(7f, 12f);

        PlayerAvatar entrant = Entrant();
        if (!entrant) return;

        target = entrant;
        if (rushes) StartGrab(entrant);
        else SnapAt(entrant);
    }

    private void TickEmpty()
    {
        if (phaseTime > TrapSeconds)
        {
            Stop(true);
            return;
        }
        Call(6f, 10f);

        PlayerAvatar entrant = Entrant();
        if (!entrant) return;
        target = entrant;

        // The voice moves on to another room, as if they've gone on ahead.
        if (hops-- > 0 && FindHidingSpot(target, Alive(), 10f, 22f, out Vector3 spot, out RoomVolume room))
        {
            MoveTo(spot, room);
            Vector3 away = spot - target.transform.position;
            float yaw = Quaternion.LookRotation(new Vector3(away.x, 0.001f, away.z)).eulerAngles.y;
            hidden.rotationTarget = Quaternion.Euler(0f, yaw, 0f);
            Wear(wornActor, false, yaw);
            phaseTime = 0f;
            nextLine = 1.5f;
            return;
        }

        // Out of places to lead you: it's right behind you.
        Vector3 behind = target.transform.position - target.transform.forward * 2.5f;
        if (NavMesh.SamplePosition(behind, out NavMeshHit hit, 2f, -1)) enemy.EnemyTeleported(hit.position);
        StartGrab(target);
    }

    private void SnapAt(PlayerAvatar player)
    {
        Vector3 toward = player.transform.position - enemy.transform.position;
        float yaw = Quaternion.LookRotation(new Vector3(toward.x, 0.001f, toward.z)).eulerAngles.y;
        hidden.rotationTarget = Quaternion.Euler(0f, yaw, 0f);
        Wear(wornActor, true, yaw);
        phase = Phase.Snap;
        phaseTime = 0f;
    }

    // ---------- the kidnapping ----------

    private void StartGrab(PlayerAvatar player)
    {
        if (IsCopying(player))
        {
            Plugin.Log.LogInfo($"Not grabbing {player.playerName}, it's copying them.");
            Stop(true);
            return;
        }

        target = player;
        grabFromFake = wearingVictim;
        wearingVictim = false;
        victims.Add(Net.ActorOf(player));
        lastTaken[Net.ActorOf(player)] = Time.time;

        hidden.playerTarget = player;
        if (SemiFunc.IsMultiplayer()) hidden.photonView.RPC("UpdatePlayerTargetRPC", RpcTarget.All, player.photonView.ViewID);
        hidden.UpdateState(State.PlayerGoTo);

        phase = Phase.Grab;
        phaseTime = 0f;
        nextLine = 1f;
        Plugin.Log.LogInfo($"Going for {player.playerName}.");
    }

    private void TickGrab()
    {
        State state = hidden.currentState;
        if (state == State.PlayerGoTo)
        {
            // The game gives up after 2 seconds of this state, which is too short from across a room.
            hidden.stateTimer = 2f;
            if (phaseTime > 10f) Stop(true);
            return;
        }
        if (state == State.PlayerPickup || state == State.PlayerMove || state == State.PlayerRelease)
        {
            // It screams for help in your own voice while it has you.
            Speak(Net.ActorOf(target), 3f, 5f, true);
            return;
        }
        if (state == State.PlayerReleaseWait)
        {
            Payoff();
            return;
        }
        if (!TargetGone()) Plugin.Log.LogInfo($"Grab ended early in state {state}.");
        Stop(true);
    }

    private void Payoff()
    {
        PlayerAvatar victim = target;
        int actor = Net.ActorOf(victim);

        if (Plugin.DropDamage.Value > 0)
            victim.playerHealth.HurtOther(Plugin.DropDamage.Value, enemy.transform.position, false, SemiFunc.EnemyGetIndex(enemy));

        // Going straight to Idle skips the Leave state.
        hidden.UpdateState(State.Idle);

        List<PlayerAvatar> others = Alive().Where(p => p != victim).ToList();

        // A fake that just kidnapped someone doesn't get to swap again, or it never stops.
        if (!grabFromFake && others.Count > 0 && Random.Range(0, 100) < Plugin.SwapChance.Value)
        {
            BeginSwap(victim, true);
            return;
        }

        // It uses your voice on the next teammate it finds alone, whenever that is.
        if (VoiceBank.HasClips(actor) && others.Count > 0) pendingVoice = actor;
        Stop(true);
    }

    // ---------- the swap ----------

    /// <summary>It leaves you stuck, then waits in a room near your team, wearing you and calling in your voice.</summary>
    private void BeginSwap(PlayerAvatar victim, bool trap)
    {
        speaker = Net.ActorOf(victim);
        wearingVictim = true;
        List<PlayerAvatar> alive = Alive();
        target = alive.Where(p => p != victim).OrderBy(p => Vector3.Distance(p.transform.position, victim.transform.position)).FirstOrDefault() ?? victim;

        if (trap)
        {
            trapped = victim;
            trapAnchor = victim.tumble ? victim.tumble.transform.position : victim.transform.position;
            trapTimer = Plugin.TrapSeconds.Value;
        }

        if (FindHidingSpot(target, alive, 8f, 22f, out Vector3 spot, out RoomVolume room)) MoveTo(spot, room);
        else
        {
            enemy.NavMeshAgent.ResetPath();
            trapRoom = null;
        }

        Wear(speaker, false, float.NaN);
        phase = Phase.Trap;
        rushes = false;
        phaseTime = 0f;
        nextLine = 3f;
        Plugin.Log.LogInfo($"Swap: wearing {victim.playerName}, waiting near {target.playerName}.");
    }

    // ---------- things that happen to it ----------

    internal void OnStruggle(int actor)
    {
        if (trapped && Net.ActorOf(trapped) == actor) trapTimer -= 1f;
        if (phase == Phase.Grab && hidden.currentState == State.PlayerMove && target && Net.ActorOf(target) == actor)
            hidden.maxMoveTimer -= 0.4f;
    }

    internal void OnPlayerDied(PlayerAvatar player)
    {
        int actor = Net.ActorOf(player);
        if (trapped == player) trapped = null;
        if (!victims.Contains(actor)) return;

        int line = VoiceBank.LatestClip(actor);
        if (line < 0) return;
        deathLines[actor] = line;
        Plugin.Log.LogInfo($"Keeping {player.playerName}'s last words.");
    }

    /// <summary>Back to waiting, harmless, out of the way, until the next lure.</summary>
    private void Stop(bool hide)
    {
        phase = Phase.Wait;
        phaseTime = 0f;
        testing = false;
        wearingVictim = false;
        speaker = -1;
        trapRoom = null;

        List<PlayerAvatar> alive = Alive();
        if (hide && alive.Count > 0 && parent.Spawned && !enemy.IsStunned() && !alive.Any(p => CanSee(p, enemy.transform.position)) &&
            FindHidingSpot(alive[Random.Range(0, alive.Count)], alive, 15f, 60f, out Vector3 spot, out RoomVolume room))
        {
            MoveTo(spot, room);
        }

        WearSomeone();
        ScheduleLure();
    }

    /// <summary>Same forces the game uses to hold someone it's carrying, aimed at the spot it left them.</summary>
    internal void FixedTick()
    {
        if (!trapped) return;

        PlayerTumble tumble = trapped.tumble;
        trapTimer -= Time.fixedDeltaTime;
        if (!tumble || trapped.isDisabled || trapTimer <= 0f || tumble.physGrabObject.playerGrabbing.Count > 0)
        {
            Plugin.Log.LogInfo("The trapped player got free.");
            trapped = null;
            return;
        }

        if (!tumble.isTumbling) tumble.TumbleRequest(_isTumbling: true, _playerInput: false);
        tumble.TumbleOverrideTime(1f);
        trapped.FallDamageResetSet(0.1f);
        tumble.physGrabObject.OverrideMass(1f);
        tumble.physGrabObject.OverrideDrag(1f);
        tumble.physGrabObject.OverrideAngularDrag(2f);
        tumble.OverrideEnemyHurt(0.1f);

        Vector3 pull = SemiFunc.PhysFollowPosition(tumble.transform.position, trapAnchor, tumble.rb.linearVelocity, 7.5f);
        tumble.rb.AddForce(pull * (10f * Time.fixedDeltaTime * 0.75f), ForceMode.Impulse);
    }
}
