#region

using System;
using NebulaAPI.Packets;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameHistory;
using NebulaModel.Utils;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.GameHistory;

[RegisterPacketProcessor]
internal class GameHistoryResearchUpdateProcessor : PacketProcessor<GameHistoryResearchUpdatePacket>
{
    protected override void ProcessPacket(GameHistoryResearchUpdatePacket packet, NebulaConnection conn)
    {
        if (IsHost || packet.TechId < 0 || packet.HashUploaded < 0 || packet.HashNeeded < 0) return;
        var data = GameMain.data.history;
        Multiplayer.Session.Statistics.TechHashedFor10Frames = packet.TechHashedFor10Frames;
        Multiplayer.Session.Statistics.HasActiveAutomaticResearch = packet.HasActiveAutomaticResearch;
        if (packet.TechId != 0 && !data.techStates.ContainsKey(packet.TechId)) return;
        if (packet.TechId != data.currentTech)
        {
            Log.Warn($"CurrentTech mismatch! Server:{packet.TechId} Local:{data.currentTech}");
            //Replace currentTech to match with server
            data.SetHiddenProperty(nameof(GameHistoryData.currentTech), packet.TechId);
            data.techQueue[0] = packet.TechId;
        }
        if (data.currentTech > 0)
        {
            var state = data.techStates[data.currentTech];
            state.hashUploaded = packet.HashUploaded;
            state.hashNeeded = packet.HashNeeded;
            data.techStates[data.currentTech] = state;
        }

        if (packet.TechQueueLength != GameMain.history.techQueueLength)
        {
            // TechQueue length mismatch. Ask from server to get a full queue to stay in sync
            conn.SendPacket(new GameHistoryTechQueueSyncPacket(true));
        }
    }
}
