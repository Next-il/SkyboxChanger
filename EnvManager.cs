using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace SkyboxChanger;

public class EnvManager
{
  public string DefaultMaterial { get; set; } = "";

  public string? CubemapFogPointedSkyName { get; set; } = null;

  public Dictionary<int, int> SpawnedSkyboxes = new();

  /// <summary>Spawn group the map's own entities live in. Captured from the map's env_sky
  /// before it is removed, because maps without a sky_camera have no other source for it and
  /// the keyvalue spawn path needs one.</summary>
  public uint MapSpawnGroupHandle { get; set; } = 0;

  public void InitializeSkyboxForPlayer(CCSPlayerController player)
  {
    var instance = SkyboxChanger.GetInstance();
    if (!instance.Config.Enabled) return;

    Skybox? skybox = instance.Service.GetPlayerSkybox(player);
    float brightness = instance.Service.GetPlayerBrightness(player);
    Color color = instance.Service.GetPlayerColor(player);

    // A saved choice is only restored if the player still has the permission for it,
    // so losing VIP puts them back on the map's own sky.
    if (skybox != null && !Helper.CanUseSkybox(player, skybox)) skybox = null;

    // The material can only be bound at spawn, so spawn straight into the right one.
    Helper.SpawnSkybox(player.Slot, CubemapFogPointedSkyName ?? "", skybox?.Material ?? DefaultMaterial);

    // after 2 tick avoid conflict with SpawnSkybox initialization
    Server.NextFrame(() =>
    {
      Server.NextFrame(() =>
      {
        Helper.ChangeSkybox(player.Slot, brightness, color.ToArgb() == int.MaxValue ? null : color);
      });
    });
  }

  public void OnPlayerLeave(int slot)
  {
    Helper.RemovePlayerSkybox(slot);
  }

  public void Shutdown()
  {
    DefaultMaterial = "";
    CubemapFogPointedSkyName = null;
    MapSpawnGroupHandle = 0;
    SkyboxChanger.GetInstance().Config.Skyboxs.Remove("");
    SpawnedSkyboxes.Clear();
  }

  public bool SetSkybox(int slot, Skybox skybox)
  {
    return Helper.RespawnSkybox(slot, skybox);
  }

  public void SetBrightness(int slot, float value)
  {
    Helper.ChangeSkybox(slot, value, null);
  }

  public void SetTintColor(int slot, Color color)
  {
    Helper.ChangeSkybox(slot, null, color);
  }

  public void OnCheckTransmit(CCheckTransmitInfoList infoList)
  {
    foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
    {
      if (player == null) continue;
      SpawnedSkyboxes.Values.ToList().ForEach(index =>
      {
        info.TransmitAlways.Remove(index);
        info.TransmitEntities.Remove(index);
      });
      if (!SpawnedSkyboxes.ContainsKey(player.Slot)) continue;
      var index = SpawnedSkyboxes[player.Slot];
      info.TransmitAlways.Add(index);
      info.TransmitEntities.Add(index);
    }
  }
}
