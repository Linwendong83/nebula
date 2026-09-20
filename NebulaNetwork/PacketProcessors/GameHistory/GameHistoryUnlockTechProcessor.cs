#region

using NebulaAPI.Packets;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameHistory;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.GameHistory;

[RegisterPacketProcessor]
internal class GameHistoryUnlockTechProcessor : PacketProcessor<GameHistoryUnlockTechPacket>
{
    protected override void ProcessPacket(GameHistoryUnlockTechPacket packet, NebulaConnection conn)
    {
        if (IsHost && !GameMain.data.gameDesc.isSandboxMode) return;
        if (!GameMain.history.techStates.TryGetValue(packet.TechId, out var existing)) return;
        if (existing.curLevel > packet.Level || existing.unlocked && existing.curLevel >= packet.Level) return;
        using (Multiplayer.Session.History.IsIncomingRequest.On())
        {
            // Let the default method give back the items
            if (GameMain.history.currentTech == packet.TechId) GameMain.mainPlayer.mecha.lab.ManageTakeback();

            // Update techState
            var techState = GameMain.history.techStates[packet.TechId];
            Log.Info($"Unlocking tech={packet.TechId} local:{techState.curLevel} remote:{packet.Level}");
            techState.curLevel = packet.Level;
            GameMain.history.techStates[packet.TechId] = techState;

            GameMain.history.UnlockTechUnlimited(packet.TechId, false);
            if (GameMain.history.currentTech == packet.TechId) GameMain.history.DequeueTech();
        }
    }
}
