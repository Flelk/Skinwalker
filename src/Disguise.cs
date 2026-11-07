using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Skinwalker;

/// <summary>A lifeless copy of a player's model and cosmetics that sits on the monster, on every player's screen.</summary>
internal static class Disguise
{
    internal static int WornActor { get; private set; } = -1;
    internal static bool WornTag { get; private set; }

    private static GameObject body;
    private static Animator animator;
    private static FakeNameTag tag;
    private static Vector3 lastPosition;
    private static float yaw, stretch, stretchTarget = 1f;
    private static Vector3 baseScale;
    private static int builtCosmetics;

    internal static void Clear()
    {
        if (body) UnityEngine.Object.Destroy(body);
        if (tag) UnityEngine.Object.Destroy(tag.gameObject);
        body = null;
        animator = null;
        tag = null;
        WornActor = -1;
        WornTag = false;
    }

    internal static void Apply(int actor, bool nameTag, bool snap, float newYaw)
    {
        if (!float.IsNaN(newYaw)) yaw = newYaw;
        // Other players' cosmetics arrive over the network, possibly after the copy was made.
        if (actor != WornActor || CosmeticCount(Net.AvatarOf(actor)) != builtCosmetics)
        {
            Clear();
            if (actor < 0) return;
            if (!Build(actor)) return;
            stretch = 1f;
            stretchTarget = 1f;
        }

        if (nameTag && !tag) tag = FakeNameTag.Create(Net.AvatarOf(actor));
        WornTag = nameTag;
        stretchTarget = snap ? 1.2f : 1f;
    }

    private static int CosmeticCount(PlayerAvatar player) =>
        player && player.playerAvatarVisuals && player.playerAvatarVisuals.playerCosmetics
            ? player.playerAvatarVisuals.playerCosmetics.cosmeticEquipped.Count
            : builtCosmetics;

    private static bool Build(int actor)
    {
        PlayerAvatar source = Net.AvatarOf(actor);
        if (!source || !source.playerAvatarVisuals) return false;

        try
        {
            // Under an inactive parent none of the copied scripts wake up, so they can be removed before they run.
            var holder = new GameObject("Skinwalker Body");
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(source.playerAvatarVisuals.gameObject, holder.transform);
            SettleCosmetics(source.playerAvatarVisuals, copy);
            PlayerAvatarVisuals copiedVisuals = copy.GetComponent<PlayerAvatarVisuals>();
            GameObject copiedMeshes = copiedVisuals ? copiedVisuals.meshParent : null;
            Strip(copy);

            Transform original = source.playerAvatarVisuals.transform;
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = original.lossyScale;

            // The real body hides its meshes and moves them around for things like death and the truck.
            if (copiedMeshes)
            {
                copiedMeshes.SetActive(true);
                copiedMeshes.transform.localPosition = Vector3.zero;
                copiedMeshes.transform.localRotation = Quaternion.identity;
            }

            // Your own body is on a layer your camera doesn't draw, and is shadow-only. A copy of it would be too,
            // which is why parts went missing. Other players' bodies are on PlayerVisuals.
            int visibleLayer = LayerMask.NameToLayer("PlayerVisuals");
            foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (visibleLayer >= 0) renderer.gameObject.layer = visibleLayer;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                if (renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = true;
            }

            animator = copy.GetComponent<Animator>();
            if (animator)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.speed = 1f;
                animator.Rebind();
            }

            body = holder;
            baseScale = holder.transform.localScale;
            holder.SetActive(true);
            lastPosition = Vector3.zero;
            builtCosmetics = CosmeticCount(source);
            WornActor = actor;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Couldn't build the disguise: {e}");
            Clear();
            return false;
        }
    }

    /// <summary>
    /// Cosmetics are animated by scripts the copy doesn't keep, so whatever they were doing at that moment would stick.
    /// A cosmetic still popping in would stay shrunken, and a part hidden for a pose like tumbling would stay at size zero.
    /// This puts everything where it belongs for that player's outfit, ignoring their current pose.
    /// </summary>
    private static void SettleCosmetics(PlayerAvatarVisuals source, GameObject copy)
    {
        Cosmetic[] real = source.GetComponentsInChildren<Cosmetic>(true);
        Cosmetic[] copied = copy.GetComponentsInChildren<Cosmetic>(true);
        for (int i = 0; i < real.Length && i < copied.Length; i++)
        {
            var scales = real[i].meshParentsScale;
            var parents = copied[i].meshParents;
            for (int j = 0; j < scales.Count && j < parents.Count; j++)
                if (parents[j]) parents[j].localScale = scales[j];
        }

        PlayerCosmetics cosmetics = source.playerCosmetics;
        if (!cosmetics) return;

        var worn = cosmetics.cosmeticEquipped.Where(c => c).ToList();
        foreach (CosmeticHideCondition condition in copy.GetComponentsInChildren<CosmeticHideCondition>(true))
        {
            bool hidden = worn.Any(c =>
                condition.cosmeticTypeList.Contains(c.type) ||
                condition.cosmeticList.Contains(c.cosmeticAsset) ||
                (c.cosmeticAsset && c.cosmeticAsset.customTypeList.Any(condition.customList.Contains)) ||
                (c.cosmeticTypeAsset && c.cosmeticTypeAsset.customTypeList.Any(condition.customList.Contains)));
            condition.transform.localScale = hidden ? Vector3.zero : Vector3.one;
        }
    }

    /// <summary>The monster spends part of each level despawned, and the copy has to go with it.</summary>
    internal static void SetVisible(bool visible)
    {
        if (body && body.activeSelf != visible) body.SetActive(visible);
        if (tag && tag.gameObject.activeSelf != visible) tag.gameObject.SetActive(visible);
        if (!visible) lastPosition = Vector3.zero;
    }

    /// <summary>Everything but the meshes and animator has to go, or it would act like a second copy of that player.</summary>
    private static void Strip(GameObject copy)
    {
        void Remove<T>() where T : Component
        {
            foreach (T component in copy.GetComponentsInChildren<T>(true)) UnityEngine.Object.DestroyImmediate(component);
        }

        Remove<MonoBehaviour>();
        Remove<Joint>();
        Remove<Rigidbody>();
        Remove<Collider>();
        Remove<ParticleSystem>();
        Remove<AudioLowPassFilter>();
        Remove<AudioSource>();
        Remove<AudioListener>();
        Remove<Light>();
        Remove<Camera>();
    }

    /// <summary>Keeps the copy standing on the floor under the monster, whose own collider floats above it.</summary>
    internal static void Follow(Vector3 center, float dt)
    {
        if (!body) return;

        Vector3 position = center;
        if (Physics.Raycast(center + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 4f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
            position = hit.point;
        else
            position.y -= 1f;

        Vector3 delta = position - lastPosition;
        delta.y = 0f;
        float speed = lastPosition == Vector3.zero || dt <= 0f ? 0f : delta.magnitude / dt;
        lastPosition = position;

        if (speed > 0.4f && speed < 30f)
            yaw = Mathf.LerpAngle(yaw, Quaternion.LookRotation(delta).eulerAngles.y, 8f * dt);

        if (animator)
        {
            animator.SetBool("Moving", speed > 0.4f);
            animator.SetBool("Sprinting", speed > 3.6f);
        }

        // The wrong part: when it turns on you, it grows.
        stretch = Mathf.MoveTowards(stretch, stretchTarget, 2.5f * dt);

        body.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        body.transform.localScale = new Vector3(baseScale.x, baseScale.y * stretch, baseScale.z);

        if (tag) tag.WorldHead = position + Vector3.up * 2f * stretch;
    }
}

/// <summary>The game's player name tag, moved onto the copy. Shows when you look at it, like a real one.</summary>
internal class FakeNameTag : WorldSpaceUIChild
{
    public Vector3 WorldHead;
    private TextMeshProUGUI text;

    internal static FakeNameTag Create(PlayerAvatar player)
    {
        WorldSpaceUIParent parent = WorldSpaceUIParent.instance;
        if (!parent || !parent.playerNamePrefab || !player) return null;

        GameObject go = Instantiate(parent.playerNamePrefab, parent.transform.position, parent.transform.rotation, parent.transform);
        WorldSpaceUIPlayerName real = go.GetComponent<WorldSpaceUIPlayerName>();
        TextMeshProUGUI label = real ? real.text : go.GetComponentInChildren<TextMeshProUGUI>();
        if (real) DestroyImmediate(real);

        FakeNameTag tag = go.AddComponent<FakeNameTag>();
        tag.text = label;
        if (label)
        {
            label.text = player.playerName;
            label.color = new Color(1f, 1f, 1f, 0f);
        }
        return tag;
    }

    public override void Update()
    {
        worldPosition = WorldHead;
        base.Update();
        if (!text) return;

        Camera cam = Camera.main;
        bool show = false;
        float distance = 99f;
        if (cam)
        {
            Vector3 toHead = WorldHead - cam.transform.position;
            distance = toHead.magnitude;
            show = distance < 12f && Vector3.Dot(cam.transform.forward, toHead.normalized) > 0.97f &&
                   !Physics.Linecast(cam.transform.position, WorldHead, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
        }

        text.color = Color.Lerp(text.color, new Color(1f, 1f, 1f, show ? 0.5f : 0f), Time.deltaTime * (show ? 5f : 20f));
        text.fontSize = 20f - distance;
    }
}
