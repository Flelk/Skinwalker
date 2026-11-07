using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;

namespace Skinwalker;

/// <summary>Finds this level's Skinwalker on every player's game, and runs the brain on the host's.</summary>
internal class SkinwalkerManager : MonoBehaviour
{
    private const string RoomKey = "skinwalkerView";

    internal static SkinwalkerManager Instance;

    internal EnemyHidden Hidden { get; private set; }
    internal Brain Brain { get; private set; }

    private EnemyParent parent;
    private float nextSearch, nextStruggle;

    private void Awake() => Instance = this;

    internal bool Center(out Vector3 center)
    {
        if (Hidden && Hidden.enemy && Hidden.enemy.HasRigidbody)
        {
            center = Hidden.enemy.Rigidbody.rb.position;
            return true;
        }
        center = default;
        return false;
    }

    private void Update()
    {
        if (!SemiFunc.RunIsLevel() || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated)
        {
            if (parent || Brain != null) Forget();
            return;
        }

        if (!parent && Time.unscaledTime >= nextSearch)
        {
            nextSearch = Time.unscaledTime + 0.5f;
            Search();
        }

        bool host = SemiFunc.IsMasterClientOrSingleplayer();
        if (host && parent && Hidden && Brain == null) Brain = new Brain(parent, Hidden);
        if (!host) Brain = null;

        if (Brain != null)
        {
            Brain.Tick(Time.deltaTime);
            if (Plugin.TestKeys.Value && SemiFunc.NoTextInputsActive())
            {
                if (Input.GetKeyDown(KeyCode.F10)) Brain.ForceLure();
                if (Input.GetKeyDown(KeyCode.F11)) Brain.ForceSwap();
            }
        }

        PlayerAvatar me = SemiFunc.PlayerAvatarLocal();
        if (me && me.isTumbling && SemiFunc.InputDown(InputKey.Jump) && Time.unscaledTime >= nextStruggle)
        {
            nextStruggle = Time.unscaledTime + 0.15f;
            Net.SendStruggle();
        }
    }

    private void FixedUpdate() => Brain?.FixedTick();

    private void LateUpdate()
    {
        Disguise.SetVisible(parent && parent.Spawned);
        if (parent && parent.Spawned && Center(out Vector3 center)) Disguise.Follow(center, Time.deltaTime);
    }

    private void Search()
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
        {
            foreach (EnemyParent candidate in EnemyDirector.instance.enemiesSpawned)
            {
                EnemyHidden found = candidate ? candidate.GetComponentInChildren<EnemyHidden>(true) : null;
                if (!found) continue;

                Claim(candidate, found);
                if (GameManager.Multiplayer() && PhotonNetwork.CurrentRoom != null)
                    PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, candidate.photonView.ViewID } });
                return;
            }
            return;
        }

        if (PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(RoomKey, out object id)) return;
        PhotonView view = PhotonView.Find((int)id);
        if (!view) return;
        EnemyParent theirs = view.GetComponent<EnemyParent>();
        EnemyHidden hidden = view.GetComponentInChildren<EnemyHidden>(true);
        if (theirs && hidden) Claim(theirs, hidden);
    }

    private void Claim(EnemyParent candidate, EnemyHidden found)
    {
        parent = candidate;
        Hidden = found;
        candidate.enemyName = "Skinwalker";
        Plugin.Log.LogInfo($"The Skinwalker on this level is view {(GameManager.Multiplayer() ? candidate.photonView.ViewID : 0)}.");
    }

    private void Forget()
    {
        parent = null;
        Hidden = null;
        Brain = null;
        Disguise.Clear();
    }
}
