using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using State = EnemyHidden.State;

namespace Skinwalker;

/// <summary>The monster's decisions. Only the host (or a solo player) runs this.</summary>
internal class Brain
{
    private enum Phase { Roam, Figure, Empty, Ambush, Snap, Grab, Fake }

    private const float SnapSeconds = 0.35f, LureTimeout = 45f, FakeTimeout = 70f;

    private readonly EnemyParent parent;
    private readonly EnemyHidden hidden;
    private readonly Enemy enemy;

    private Phase phase = Phase.Roam;
    private float nextLure, nextLine, phaseTime, stare;
    private bool forced;
    private PlayerAvatar target;
    private int speaker = -1;
    private int hops;

    private readonly HashSet<int> victims = new HashSet<int>();
    private readonly Dictionary<int, int> deathLines = new Dictionary<int, int>();

    private PlayerAvatar trapped;
    private Vector3 trapAnchor;
    private float trapTimer;

    internal bool Holding => phase != Phase.Roam && phase != Phase.Grab;

    internal Brain(EnemyParent parent, EnemyHidden hidden)
    {
        this.parent = parent;
        this.hidden = hidden;
        enemy = hidden.enemy;
        parent.enemyName = "Skinwalker";
        parent.DespawnedTimeMin = 15f;
        parent.DespawnedTimeMax = 30f;
        ScheduleLure();
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
        ResetToRoam();
        BeginSwap(me, Alive().Count > 1);
    }

    // ---------- every frame ----------

    internal void Tick(float dt)
    {
        if (phase != Phase.Roam)
        {
            if (enemy.IsStunned())
            {
                Plugin.Log.LogInfo("Stunned, giving up.");
                ResetToRoam();
                if (trapped) trapTimer = Mathf.Min(trapTimer, 3f);
                return;
            }
            if (!parent.Spawned)
            {
                ResetToRoam();
                return;
            }
            parent.SpawnedTimerPause(30f);
        }

        phaseTime += dt;
        switch (phase)
        {
            case Phase.Roam: TickRoam(dt); break;
            case Phase.Figure: TickFigure(dt); break;
            case Phase.Empty: TickEmpty(); break;
            case Phase.Ambush: TickAmbush(); break;
            case Phase.Snap: if (phaseTime >= SnapSeconds) StartGrab(target); break;
            case Phase.Grab: TickGrab(); break;
            case Phase.Fake: TickFake(dt); break;
        }
    }

    private void TickRoam(float dt)
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

        forced = false;
        if (!StartLure(null, -1)) nextLure = 5f;
    }

    // ---------- the lure ----------

    private List<PlayerAvatar> Alive() => SemiFunc.PlayerGetList().Where(p => p && !p.isDisabled && !p.deadSet).ToList();

    private static float NearestOther(PlayerAvatar player, List<PlayerAvatar> all)
    {
        float nearest = 999f;
        foreach (PlayerAvatar other in all)
            if (other != player) nearest = Mathf.Min(nearest, Vector3.Distance(player.transform.position, other.transform.position));
        return nearest;
    }

    /// <summary>Goes for someone on their own, and leaves people in the truck alone.</summary>
    private PlayerAvatar PickTarget(List<PlayerAvatar> alive)
    {
        var candidates = alive.Where(p => !p.RoomVolumeCheck || !p.RoomVolumeCheck.inTruck).ToList();
        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(p => NearestOther(p, alive) + Random.Range(0f, 8f)).First();
    }

    private bool StartLure(PlayerAvatar who, int forcedSpeaker)
    {
        List<PlayerAvatar> alive = Alive();
        target = who ? who : PickTarget(alive);
        if (!target) return false;

        if (!FindHidingSpot(target, alive, 8f, 22f, out Vector3 spot)) return false;
        enemy.EnemyTeleported(spot);
        hidden.enemy.NavMeshAgent.ResetPath();
        hops = Random.Range(1, 3);

        speaker = forcedSpeaker >= 0 ? forcedSpeaker : PickSpeaker(target, alive);

        int roll = Random.Range(0, 100);
        phase = roll < 55 ? Phase.Figure : roll < 85 ? Phase.Empty : Phase.Ambush;
        phaseTime = 0f;
        stare = 0f;
        nextLine = 0f;

        if (phase == Phase.Figure)
        {
            // It stands with its back to you, wearing whoever's voice it's using.
            int body = speaker >= 0 ? speaker : Net.ActorOf(alive.FirstOrDefault(p => p != target) ?? target);
            Vector3 away = spot - target.transform.position;
            float yaw = Quaternion.LookRotation(new Vector3(away.x, 0f, away.z)).eulerAngles.y;
            hidden.rotationTarget = Quaternion.Euler(0f, yaw, 0f);
            Net.SendDisguise(body, true, false, yaw);
        }

        Plugin.Log.LogInfo($"Lure: {phase} on {target.playerName}, using {(speaker >= 0 ? Net.AvatarOf(speaker)?.playerName : "no voice")}.");
        return true;
    }

    private int PickSpeaker(PlayerAvatar who, List<PlayerAvatar> alive)
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
        return voices.Contains(whoActor) ? whoActor : -1;
    }

    private bool FindHidingSpot(PlayerAvatar near, List<PlayerAvatar> alive, float min, float max, out Vector3 spot)
    {
        for (int i = 0; i < 12; i++)
        {
            LevelPoint point = SemiFunc.LevelPointGet(near.transform.position, min, max);
            if (!point) break;
            if (!NavMesh.SamplePosition(point.transform.position + Random.insideUnitSphere * 2f, out NavMeshHit hit, 3f, -1)) continue;
            if (alive.Any(p => CanSee(p, hit.position) || (p != near && Vector3.Distance(p.transform.position, hit.position) < 6f))) continue;
            spot = hit.position;
            return true;
        }
        spot = default;
        return false;
    }

    private static bool CanSee(PlayerAvatar player, Vector3 position)
    {
        Vector3 eye = player.transform.position + Vector3.up * 1.4f;
        return !Physics.Linecast(eye, position + Vector3.up * 1.2f, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
    }

    private static bool Facing(PlayerAvatar player, Vector3 position)
    {
        Vector3 direction = position - player.transform.position;
        direction.y = 0f;
        return Vector3.Dot(player.transform.forward, direction.normalized) > 0.6f;
    }

    private void Speak(int actor, float gapMin, float gapMax, bool follow = false)
    {
        if (phaseTime < nextLine) return;
        nextLine = phaseTime + Random.Range(gapMin, gapMax);
        if (actor < 0) return;

        int clip = deathLines.TryGetValue(actor, out int line) && Random.value < 0.5f ? line : VoiceBank.RandomClip(actor);
        if (clip >= 0) Net.SendLine(actor, clip, enemy.transform.position + Vector3.up * 1.3f, follow);
    }

    private bool TargetGone() => !target || target.isDisabled || target.deadSet;

    private PlayerAvatar ClosestTo(Vector3 position, out float distance)
    {
        PlayerAvatar closest = null;
        distance = 999f;
        foreach (PlayerAvatar player in Alive())
        {
            float d = Vector3.Distance(player.transform.position, position);
            if (d < distance)
            {
                distance = d;
                closest = player;
            }
        }
        return closest;
    }

    private void TickFigure(float dt)
    {
        if (TargetGone() || phaseTime > LureTimeout)
        {
            ResetToRoam();
            return;
        }
        Speak(speaker, 7f, 12f);

        Vector3 here = enemy.transform.position;
        PlayerAvatar closest = ClosestTo(here, out float distance);
        if (!closest) return;

        // Anyone who walks up to it, or stands there looking at it for too long.
        if (CanSee(closest, here) && Facing(closest, here) && distance < 12f) stare += dt;
        if (distance < 3.5f || stare > 2.5f)
        {
            target = closest;
            SnapAt(target);
        }
    }

    private void SnapAt(PlayerAvatar player)
    {
        Vector3 toward = player.transform.position - enemy.transform.position;
        float yaw = Quaternion.LookRotation(new Vector3(toward.x, 0.001f, toward.z)).eulerAngles.y;
        hidden.rotationTarget = Quaternion.Euler(0f, yaw, 0f);
        Net.SendDisguise(Disguise.WornActor, Disguise.WornTag, true, yaw);
        phase = Phase.Snap;
        phaseTime = 0f;
    }

    private void TickEmpty()
    {
        if (TargetGone() || phaseTime > LureTimeout)
        {
            ResetToRoam();
            return;
        }
        Speak(speaker, 6f, 10f);

        float distance = Vector3.Distance(target.transform.position, enemy.transform.position);
        if (distance >= 5f && phaseTime < 18f) return;

        List<PlayerAvatar> alive = Alive();
        if (hops-- > 0 && FindHidingSpot(target, alive, 10f, 20f, out Vector3 spot))
        {
            // The voice moves further off, as if they've gone on ahead.
            enemy.EnemyTeleported(spot);
            enemy.NavMeshAgent.ResetPath();
            phaseTime = 0f;
            nextLine = 1.5f;
            return;
        }

        // Out of places to lead you: it's right behind you.
        Vector3 behind = target.transform.position - target.transform.forward * 2.5f;
        if (NavMesh.SamplePosition(behind, out NavMeshHit hit, 2f, -1)) enemy.EnemyTeleported(hit.position);
        StartGrab(target);
    }

    private void TickAmbush()
    {
        if (TargetGone() || phaseTime > LureTimeout)
        {
            ResetToRoam();
            return;
        }
        Speak(speaker, 6f, 10f);

        PlayerAvatar closest = ClosestTo(enemy.transform.position, out float distance);
        if (closest && distance < 3f) StartGrab(closest);
    }

    // ---------- the kidnapping ----------

    private void StartGrab(PlayerAvatar player)
    {
        target = player;
        victims.Add(Net.ActorOf(player));

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
            // The game gives up after 2 seconds of this state, which is too short from around a corner.
            hidden.stateTimer = 2f;
            if (phaseTime > 10f) ResetToRoam();
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
        ResetToRoam();
    }

    private void Payoff()
    {
        PlayerAvatar victim = target;
        int actor = Net.ActorOf(victim);

        if (Plugin.DropDamage.Value > 0)
            victim.playerHealth.HurtOther(Plugin.DropDamage.Value, enemy.transform.position, false, SemiFunc.EnemyGetIndex(enemy));

        // Going straight to Idle skips the Leave state, which would despawn it.
        hidden.UpdateState(State.Idle);
        Net.SendDisguise(-1, false, false, 0f);
        phase = Phase.Roam;

        List<PlayerAvatar> others = Alive().Where(p => p != victim).ToList();
        if (others.Count > 0 && Random.Range(0, 100) < Plugin.SwapChance.Value)
        {
            BeginSwap(victim, true);
            return;
        }

        if (others.Count > 0 && StartLure(others[Random.Range(0, others.Count)], VoiceBank.HasClips(actor) ? actor : -1))
            return;

        ScheduleLure();
    }

    // ---------- the swap ----------

    private void BeginSwap(PlayerAvatar victim, bool trap)
    {
        speaker = Net.ActorOf(victim);
        List<PlayerAvatar> alive = Alive();
        target = alive.Where(p => p != victim).OrderBy(p => Vector3.Distance(p.transform.position, victim.transform.position)).FirstOrDefault() ?? victim;

        if (trap)
        {
            trapped = victim;
            trapAnchor = victim.tumble ? victim.tumble.transform.position : victim.transform.position;
            trapTimer = Plugin.TrapSeconds.Value;
        }

        if (FindHidingSpot(target, alive, 6f, 16f, out Vector3 spot))
        {
            enemy.EnemyTeleported(spot);
            enemy.NavMeshAgent.ResetPath();
        }

        Net.SendDisguise(speaker, true, false, 0f);
        phase = Phase.Fake;
        phaseTime = 0f;
        nextLine = 3f;
        Plugin.Log.LogInfo($"Swap: wearing {victim.playerName}, following {target.playerName}.");
    }

    private void TickFake(float dt)
    {
        List<PlayerAvatar> alive = Alive();
        if (alive.Count == 0 || phaseTime > FakeTimeout && !TargetGone() && Attack(alive)) return;

        PlayerAvatar real = Net.AvatarOf(speaker);
        var marks = alive.Where(p => p != real).ToList();
        if (marks.Count == 0) marks = alive;
        target = marks.OrderBy(p => Vector3.Distance(p.transform.position, enemy.transform.position)).First();

        float distance = Vector3.Distance(target.transform.position, enemy.transform.position);
        if (distance > 4f)
        {
            enemy.NavMeshAgent.SetDestination(target.transform.position);
            enemy.NavMeshAgent.OverrideAgent(2.8f, 10f, 0.25f);
        }
        else
        {
            enemy.NavMeshAgent.ResetPath();
            Vector3 toward = target.transform.position - enemy.transform.position;
            if (toward.sqrMagnitude > 0.1f) hidden.rotationTarget = Quaternion.Euler(0f, Quaternion.LookRotation(new Vector3(toward.x, 0.001f, toward.z)).eulerAngles.y, 0f);
        }

        Speak(speaker, 9f, 16f);

        // It waits until the person it's following is on their own.
        if (phaseTime > 12f && distance < 5f && NearestOther(target, marks) > 10f) SnapAt(target);
    }

    private bool Attack(List<PlayerAvatar> alive)
    {
        target = alive.OrderBy(p => Vector3.Distance(p.transform.position, enemy.transform.position)).First();
        SnapAt(target);
        return true;
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

    private void ResetToRoam()
    {
        if (phase == Phase.Roam) return;
        phase = Phase.Roam;
        phaseTime = 0f;
        Net.SendDisguise(-1, false, false, 0f);
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
