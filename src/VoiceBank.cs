using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Skinwalker;

/// <summary>Voice clips per player. Memory only: nothing here is ever written to disk.</summary>
internal static class VoiceBank
{
    internal const int SampleRate = 12000;
    private const int MaxClipsPerPlayer = 16;

    private class Partial
    {
        public byte[][] Chunks;
        public int Received;
    }

    private static readonly Dictionary<int, List<(int id, byte[] audio)>> clips = new Dictionary<int, List<(int, byte[])>>();
    private static readonly Dictionary<(int actor, int clip), Partial> partials = new Dictionary<(int, int), Partial>();

    internal static void Clear()
    {
        clips.Clear();
        partials.Clear();
    }

    internal static void Add(int actor, int id, byte[] audio)
    {
        if (!clips.TryGetValue(actor, out var list)) clips[actor] = list = new List<(int, byte[])>();
        list.Add((id, audio));
        if (list.Count > MaxClipsPerPlayer) list.RemoveAt(0);
    }

    internal static void AddChunk(int actor, int id, int index, int count, byte[] bytes)
    {
        if (!partials.TryGetValue((actor, id), out Partial partial))
        {
            // A sender only has one clip in flight, so anything older from them is abandoned.
            foreach (var key in partials.Keys.Where(k => k.actor == actor).ToList()) partials.Remove(key);
            partials[(actor, id)] = partial = new Partial { Chunks = new byte[count][] };
        }
        if (partial.Chunks.Length != count || partial.Chunks[index] != null) return;

        partial.Chunks[index] = bytes;
        if (++partial.Received < count) return;

        partials.Remove((actor, id));
        Add(actor, id, partial.Chunks.SelectMany(c => c).ToArray());
    }

    internal static List<int> ActorsWithClips() => clips.Where(p => p.Value.Count > 0).Select(p => p.Key).ToList();

    internal static bool HasClips(int actor) => clips.TryGetValue(actor, out var list) && list.Count > 0;

    internal static int RandomClip(int actor) => HasClips(actor) ? clips[actor][Random.Range(0, clips[actor].Count)].id : -1;

    internal static int LatestClip(int actor) => HasClips(actor) ? clips[actor][clips[actor].Count - 1].id : -1;

    internal static void Play(int actor, int id, Vector3 position, bool follow)
    {
        // Nobody ever hears the monster using their own voice. Solo is the exception, as it's the only voice there is.
        if (GameManager.Multiplayer() && actor == Net.LocalActor) return;

        if (!clips.TryGetValue(actor, out var list)) return;
        int i = list.FindIndex(c => c.id == id);
        if (i < 0) return;

        byte[] audio = list[i].audio;
        var samples = new float[audio.Length];
        for (int s = 0; s < audio.Length; s++) samples[s] = MuLaw.Decode(audio[s]);

        AudioClip clip = AudioClip.Create("skinwalker line", samples.Length, 1, SampleRate, false);
        clip.SetData(samples, 0);

        var go = new GameObject("Skinwalker Line");
        go.transform.position = position;
        AudioSource source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        source.maxDistance = 22f;
        source.volume = Plugin.LineVolume.Value;
        source.dopplerLevel = 0f;

        // Same mixer as real voices, so it follows the voice volume setting and sounds like voice chat.
        PlayerAvatar speaker = Net.AvatarOf(actor) ?? SemiFunc.PlayerAvatarLocal();
        if (speaker && speaker.voiceChat) source.outputAudioMixerGroup = speaker.voiceChat.mixerMicrophoneSound;

        go.AddComponent<AudioLowPassFilter>();
        go.AddComponent<LinePlayback>().Follow = follow;
        source.Play();
    }
}

/// <summary>Follows the monster if asked, muffles the line through walls, and cleans up afterwards.</summary>
internal class LinePlayback : MonoBehaviour
{
    public bool Follow;

    private AudioSource source;
    private AudioLowPassFilter filter;
    private float nextCheck;
    private float cutoff = 22000f;

    private void Awake()
    {
        source = GetComponent<AudioSource>();
        filter = GetComponent<AudioLowPassFilter>();
    }

    private void Update()
    {
        if (!source.isPlaying)
        {
            Destroy(source.clip);
            Destroy(gameObject);
            return;
        }

        if (Follow && SkinwalkerManager.Instance && SkinwalkerManager.Instance.Center(out Vector3 center))
            transform.position = center;

        Camera cam = Camera.main;
        if (cam && Time.time >= nextCheck)
        {
            nextCheck = Time.time + 0.2f;
            bool blocked = Physics.Linecast(transform.position, cam.transform.position, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
            cutoff = blocked ? 1400f : 22000f;
        }
        filter.cutoffFrequency = Mathf.Lerp(filter.cutoffFrequency, cutoff, Time.deltaTime * 8f);
    }
}

/// <summary>8-bit audio, which keeps a two second clip around 24 KB on the network.</summary>
internal static class MuLaw
{
    private const float Mu = 255f;

    internal static byte Encode(float sample)
    {
        float value = Mathf.Log(1f + Mu * Mathf.Abs(Mathf.Clamp(sample, -1f, 1f))) / Mathf.Log(1f + Mu);
        return (byte)Mathf.RoundToInt((Mathf.Sign(sample) * value + 1f) * 127.5f);
    }

    internal static float Decode(byte value)
    {
        float y = value / 127.5f - 1f;
        return Mathf.Sign(y) * (Mathf.Pow(1f + Mu, Mathf.Abs(y)) - 1f) / Mu;
    }
}
