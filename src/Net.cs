using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Skinwalker;

/// <summary>Photon events between copies of this mod. In solo play they're delivered straight to the handler.</summary>
internal static class Net
{
    private const byte ClipChunk = 171, PlayLine = 172, SetDisguise = 173, Struggle = 174;
    private const int MaxChunkBytes = 8000, MaxChunks = 32;

    internal static int LocalActor => GameManager.Multiplayer() ? PhotonNetwork.LocalPlayer.ActorNumber : 0;

    internal static int ActorOf(PlayerAvatar player) => GameManager.Multiplayer() ? player.photonView.OwnerActorNr : 0;

    internal static PlayerAvatar AvatarOf(int actor)
    {
        foreach (PlayerAvatar player in SemiFunc.PlayerGetList())
            if (player && ActorOf(player) == actor) return player;
        return null;
    }

    internal static void Init() => PhotonNetwork.NetworkingClient.EventReceived += OnEvent;

    private static void Raise(byte code, object content, ReceiverGroup receivers) =>
        PhotonNetwork.RaiseEvent(code, content, new RaiseEventOptions { Receivers = receivers }, SendOptions.SendReliable);

    internal static void SendClipChunk(int clipId, int index, int count, byte[] data)
    {
        if (GameManager.Multiplayer())
            Raise(ClipChunk, new object[] { clipId, index, count, data }, ReceiverGroup.Others);
    }

    internal static void SendLine(int actor, int clipId, Vector3 position, bool follow)
    {
        if (GameManager.Multiplayer()) Raise(PlayLine, new object[] { actor, clipId, position, follow }, ReceiverGroup.All);
        else VoiceBank.Play(actor, clipId, position, follow);
    }

    /// <param name="actor">Whose body to wear, or -1 for none.</param>
    internal static void SendDisguise(int actor, bool nameTag, bool snap, float yaw)
    {
        if (GameManager.Multiplayer()) Raise(SetDisguise, new object[] { actor, nameTag, snap, yaw }, ReceiverGroup.All);
        else Disguise.Apply(actor, nameTag, snap, yaw);
    }

    internal static void SendStruggle()
    {
        if (GameManager.Multiplayer()) Raise(Struggle, null, ReceiverGroup.MasterClient);
        else SkinwalkerManager.Instance?.Brain?.OnStruggle(0);
    }

    private static void OnEvent(EventData ev)
    {
        if (ev.Code < ClipChunk || ev.Code > Struggle) return;

        bool fromHost = PhotonNetwork.MasterClient != null && ev.Sender == PhotonNetwork.MasterClient.ActorNumber;
        object[] data = ev.CustomData as object[];

        switch (ev.Code)
        {
            case ClipChunk:
                if (data == null || data.Length != 4 || !(data[3] is byte[] bytes)) return;
                int index = (int)data[1], count = (int)data[2];
                if (bytes.Length > MaxChunkBytes || count < 1 || count > MaxChunks || index < 0 || index >= count) return;
                VoiceBank.AddChunk(ev.Sender, (int)data[0], index, count, bytes);
                break;

            case PlayLine:
                if (fromHost && data != null && data.Length == 4)
                    VoiceBank.Play((int)data[0], (int)data[1], (Vector3)data[2], (bool)data[3]);
                break;

            case SetDisguise:
                if (fromHost && data != null && data.Length == 4)
                    Disguise.Apply((int)data[0], (bool)data[1], (bool)data[2], (float)data[3]);
                break;

            case Struggle:
                if (PhotonNetwork.IsMasterClient) SkinwalkerManager.Instance?.Brain?.OnStruggle(ev.Sender);
                break;
        }
    }
}
