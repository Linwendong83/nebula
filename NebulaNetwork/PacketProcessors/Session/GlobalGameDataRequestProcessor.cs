#region

using NebulaAPI.Packets;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameStates;
using NebulaModel.Packets.Session;
using NebulaWorld.GameStates;

#endregion

namespace NebulaNetwork.PacketProcessors.Session;

[RegisterPacketProcessor]
internal class GlobalGameDataRequestProcessor : PacketProcessor<GlobalGameDataRequest>
{
    protected override void ProcessPacket(GlobalGameDataRequest packet, NebulaConnection conn)
    {
        if (IsClient)
        {
            return;
        }
        if (Players.Get(conn, NebulaAPI.Networking.EConnectionStatus.Syncing) == null) return;

        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(SessionProtocol.Version);
            writer.BinaryWriter.Write(NebulaWorld.SaveManager.WorldId);
            writer.BinaryWriter.Write(GameMain.galaxy.birthStarId);
            writer.BinaryWriter.Write(GameMain.galaxy.birthPlanetId);
            writer.BinaryWriter.Write((int)GameMain.data.gameDesc.goalLevel);
            writer.BinaryWriter.Write(NebulaWorld.Multiplayer.Session.Metadata.SourceClusterKey);
            conn.SendPacket(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.Session, writer.CloseAndGetBytes()));
        }

        conn.SendPacket(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.Goals,
            NebulaWorld.Multiplayer.Session.Goals.Export()));
        var joining = Players.Get(conn, NebulaAPI.Networking.EConnectionStatus.Syncing);
        conn.SendPacket(new NebulaModel.Packets.Combat.CombatGenerationPacket
        { Data = NebulaWorld.Multiplayer.Session.Generations.Export() });
        conn.SendPacket(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.KillStatistics,
            NebulaWorld.Multiplayer.Session.Kills.ExportSnapshot(joining.Id)));

        //Export GameHistoryData, SpaceSector, TrashSystem, MilestoneSystem
        //PlanetFactory, Dysonsphere, GalacticTransport will be handle else where        

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.history.Export(writer.BinaryWriter);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.History, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.galacticTransport.Export(writer.BinaryWriter);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.GalacticTransport, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            // Note: Initial syncing from vanilla. May be refined later in future
            var previous = NebulaWorld.Combat.CombatManager.SerializeOverwrite;
            try
            {
                NebulaWorld.Combat.CombatManager.SerializeOverwrite = true;
                GameMain.data.spaceSector.BeginSave();
                try { GameMain.data.spaceSector.Export(writer.BinaryWriter); }
                finally { GameMain.data.spaceSector.EndSave(); }
            }
            finally { NebulaWorld.Combat.CombatManager.SerializeOverwrite = previous; }

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.SpaceSector, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.milestoneSystem.Export(writer.BinaryWriter);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.MilestoneSystem, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.trashSystem.Export(writer.BinaryWriter);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.TrashSystem, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.galacticDigital.Export(writer.BinaryWriter);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.GalacticDigital, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(GameMain.sandboxToolsEnabled);

            conn.SendPacket(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.Ready, writer.CloseAndGetBytes()));
        }

        conn.SendPacket(new GameStateSaveInfoPacket(GameStatesManager.LastSaveTime));
    }

}
