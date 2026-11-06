using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Skinwalker;

/// <summary>
/// The game passes the local player's processed mic audio through a small private class. It only reports audio that
/// would actually be transmitted when captureEnabled is on, so mute and push-to-talk are respected.
/// This runs on Photon's audio thread, so it only copies the samples into a queue.
/// </summary>
[HarmonyPatch]
internal static class VoiceFramePatch
{
    private static FieldInfo capture, rate, channels;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        System.Type type = AccessTools.Inner(typeof(PlayerVoiceChat), "LocalVoicePlaybackProcessor");
        capture = AccessTools.Field(type, "captureEnabled");
        rate = AccessTools.Field(type, "samplingRate");
        channels = AccessTools.Field(type, "channels");
        yield return AccessTools.Method(type, "ProcessedFrame", new[] { typeof(short[]), typeof(bool) });
        yield return AccessTools.Method(type, "ProcessedFrame", new[] { typeof(float[]), typeof(bool) });
    }

    private static void Postfix(object __instance, object _samples, bool _active)
    {
        if (!_active || !Plugin.ShareMyVoice.Value || !VoiceCapture.Listening || !(bool)capture.GetValue(__instance)) return;

        float[] copy;
        if (_samples is short[] shorts)
        {
            copy = new float[shorts.Length];
            for (int i = 0; i < shorts.Length; i++) copy[i] = shorts[i] / 32768f;
        }
        else
        {
            copy = (float[])((float[])_samples).Clone();
        }
        VoiceCapture.Frames.Enqueue((copy, (int)rate.GetValue(__instance), (int)channels.GetValue(__instance)));
    }
}

/// <summary>Cuts the local player's speech into clips at the pauses, and shares them a chunk at a time.</summary>
internal class VoiceCapture : MonoBehaviour
{
    internal static readonly ConcurrentQueue<(float[] samples, int rate, int channels)> Frames = new ConcurrentQueue<(float[], int, int)>();
    internal static volatile bool Listening;

    private const float SpeechLevel = 0.012f, PauseSeconds = 0.4f, MinSeconds = 0.6f, MaxSeconds = 4f;
    private const float ShareInterval = 6f, ChunkInterval = 0.15f;
    private const int ChunkBytes = 3000, BlockSamples = VoiceBank.SampleRate / 50;

    private readonly List<float> block = new List<float>(BlockSamples);
    private readonly Queue<float[]> preRoll = new Queue<float[]>();
    private readonly List<float> speech = new List<float>();
    private bool speaking;
    private int silentSamples;
    private float resampleStep;

    private readonly List<(int id, byte[] audio)> shareQueue = new List<(int, byte[])>();
    private byte[] sending;
    private int sendingId, sendingOffset;
    private float nextChunk, nextShare;
    private int nextClipId = 1;

    private string micDevice;
    private bool micOn;
    private int micPosition;
    private AudioClip micClip;

    private bool wasInRoom;

    private void Update()
    {
        bool inRoom = !GameManager.instance || !GameManager.Multiplayer() || Photon.Pun.PhotonNetwork.InRoom;
        if (wasInRoom && !inRoom) ForgetEverything();
        wasInRoom = inRoom;

        bool want = Plugin.ShareMyVoice.Value && SemiFunc.RunIsLevel();
        Listening = want;
        SoloMic(want && !GameManager.Multiplayer());

        while (Frames.TryDequeue(out var frame))
            if (want) Feed(frame.samples, frame.channels, frame.rate);

        SendNext();
    }

    private void OnDestroy() => ForgetEverything();

    private void ForgetEverything()
    {
        VoiceBank.Clear();
        shareQueue.Clear();
        sending = null;
        speech.Clear();
        speaking = false;
    }

    /// <summary>Solo play has no voice connection, so nothing reaches the patch above. Read the mic directly.</summary>
    private void SoloMic(bool want)
    {
        bool muted = DataDirector.instance && (DataDirector.instance.toggleMute ||
            (DataDirector.instance.SettingValueFetch(DataDirector.Setting.PushToTalk) == 1 && !SemiFunc.InputHold(InputKey.PushToTalk)));

        if (want && !micOn)
        {
            micDevice = SessionManager.instance ? SessionManager.instance.micDeviceCurrent : "";
            if (string.IsNullOrEmpty(micDevice)) micDevice = null;
            if (Microphone.devices.Length == 0) return;
            micClip = Microphone.Start(micDevice, true, 2, VoiceBank.SampleRate);
            micPosition = 0;
            micOn = micClip;
        }
        else if (!want && micOn)
        {
            Microphone.End(micDevice);
            Destroy(micClip);
            micOn = false;
            return;
        }
        if (!micOn) return;

        int position = Microphone.GetPosition(micDevice);
        if (position == micPosition) return;

        int count = (position - micPosition + micClip.samples) % micClip.samples;
        var samples = new float[count];
        micClip.GetData(samples, micPosition);
        micPosition = position;
        if (!muted) Feed(samples, 1, VoiceBank.SampleRate);
    }

    private void Feed(float[] samples, int channels, int rate)
    {
        channels = Mathf.Max(1, channels);
        float step = rate / (float)VoiceBank.SampleRate;
        for (int i = 0; i + channels <= samples.Length; i += channels)
        {
            resampleStep += 1f;
            if (resampleStep < step) continue;
            resampleStep -= step;

            block.Add(samples[i]);
            if (block.Count < BlockSamples) continue;
            OnBlock(block.ToArray());
            block.Clear();
        }
    }

    private void OnBlock(float[] samples)
    {
        float sum = 0f;
        foreach (float s in samples) sum += s * s;
        bool loud = Mathf.Sqrt(sum / samples.Length) >= SpeechLevel;

        if (!speaking)
        {
            preRoll.Enqueue(samples);
            if (preRoll.Count > 8) preRoll.Dequeue();
            if (!loud) return;

            speaking = true;
            silentSamples = 0;
            speech.Clear();
            foreach (float[] old in preRoll) speech.AddRange(old);
            preRoll.Clear();
            return;
        }

        speech.AddRange(samples);
        silentSamples = loud ? 0 : silentSamples + samples.Length;

        bool paused = silentSamples >= PauseSeconds * VoiceBank.SampleRate;
        if (paused || speech.Count >= MaxSeconds * VoiceBank.SampleRate)
        {
            if (paused) speech.RemoveRange(speech.Count - silentSamples, silentSamples);
            speaking = false;
            FinishClip();
        }
    }

    private void FinishClip()
    {
        if (speech.Count < MinSeconds * VoiceBank.SampleRate) return;

        float peak = speech.Max(Mathf.Abs);
        float gain = peak > 0.001f ? Mathf.Min(4f, 0.85f / peak) : 1f;
        byte[] audio = speech.Select(s => MuLaw.Encode(s * gain)).ToArray();

        int id = nextClipId++;
        VoiceBank.Add(Net.LocalActor, id, audio);

        // Only the newest few wait to be sent, so talking a lot doesn't build up network traffic.
        shareQueue.Add((id, audio));
        if (shareQueue.Count > 3) shareQueue.RemoveAt(0);
    }

    private void SendNext()
    {
        if (!Plugin.ShareMyVoice.Value)
        {
            shareQueue.Clear();
            sending = null;
            return;
        }
        if (!GameManager.Multiplayer() || !Photon.Pun.PhotonNetwork.InRoom) return;

        float now = Time.unscaledTime;
        if (sending == null)
        {
            if (shareQueue.Count == 0 || now < nextShare) return;
            (sendingId, sending) = shareQueue[shareQueue.Count - 1];
            shareQueue.RemoveAt(shareQueue.Count - 1);
            sendingOffset = 0;
            nextShare = now + ShareInterval;
        }
        if (now < nextChunk) return;
        nextChunk = now + ChunkInterval;

        int count = Mathf.CeilToInt(sending.Length / (float)ChunkBytes);
        int length = Mathf.Min(ChunkBytes, sending.Length - sendingOffset);
        var chunk = new byte[length];
        System.Array.Copy(sending, sendingOffset, chunk, 0, length);
        Net.SendClipChunk(sendingId, sendingOffset / ChunkBytes, count, chunk);

        sendingOffset += length;
        if (sendingOffset >= sending.Length) sending = null;
    }
}
