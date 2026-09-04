using System.Drawing;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace SkyboxChanger;

public class Service
{
  private readonly Storage _storage;
  private readonly SkyboxChanger _plugin;

  public Service(SkyboxChanger plugin, string host, int port, string user, string password, string database, string tablePrefix)
  {
    _plugin = plugin;
    _storage = new Storage(host, port, user, password, database, tablePrefix);
  }

  // ── Skybox ──────────────────────────────────────────────────────────────────

  public bool SetSkybox(CCSPlayerController player, string index)
  {
    _plugin.Logger.LogInformation("[SkyboxChanger] SetSkybox called: slot={Slot} steamId={SteamId} index='{Index}'", player.Slot, player.SteamID, index);

    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      _plugin.Logger.LogWarning("[SkyboxChanger] SetSkybox aborted: player slot={Slot} is in spectator mode", player.Slot);
      return false;
    }

    var skyData = _storage.GetPlayerSkydata(player.SteamID);
    skyData.Skybox = index;

    if (!_plugin.Config.Skyboxs.TryGetValue(index, out var skybox))
    {
      _plugin.Logger.LogError("[SkyboxChanger] SetSkybox failed: skybox key '{Index}' not found in Config.Skyboxs (available keys: {Keys})", index, string.Join(", ", _plugin.Config.Skyboxs.Keys));
      return false;
    }
    _plugin.Logger.LogInformation("[SkyboxChanger] Resolved skybox '{Index}' -> material='{Material}'", index, skybox.Material);

    if (skybox.Brightness != null)
    {
      _plugin.EnvManager.SetBrightness(player.Slot, skybox.Brightness.Value);
      skyData.Brightness = skybox.Brightness.Value;
    }

    if (skybox.Color != null)
    {
      var parts = skybox.Color.Split(' ');
      if (parts.Length == 4)
      {
        var r = int.Parse(parts[0]);
        var g = int.Parse(parts[1]);
        var b = int.Parse(parts[2]);
        var a = int.Parse(parts[3]);
        var color = Color.FromArgb(a, r, g, b);
        _plugin.EnvManager.SetTintColor(player.Slot, color);
        skyData.Color = color.ToArgb();
      }
    }

    _ = _storage.SaveAsync(player.SteamID);
    var result = _plugin.EnvManager.SetSkybox(player.Slot, skybox);
    if (!result)
    {
      _plugin.Logger.LogError("[SkyboxChanger] SetSkybox: EnvManager.SetSkybox returned false for slot={Slot} index='{Index}' material='{Material}'", player.Slot, index, skybox.Material);
    }
    else
    {
      _plugin.Logger.LogInformation("[SkyboxChanger] SetSkybox succeeded for slot={Slot} index='{Index}'", player.Slot, index);
    }
    return result;
  }

  public void SetBrightness(CCSPlayerController player, float brightness)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(player.SteamID);
    skyData.Brightness = brightness;
    _plugin.EnvManager.SetBrightness(player.Slot, brightness);
    _ = _storage.SaveAsync(player.SteamID);
  }

  public void SetTintColor(CCSPlayerController player, Color color)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(player.SteamID);
    skyData.Color = color.ToArgb();
    _plugin.EnvManager.SetTintColor(player.Slot, color);
    _ = _storage.SaveAsync(player.SteamID);
  }

  // ── Getters ─────────────────────────────────────────────────────────────────

  public Skybox? GetPlayerSkybox(CCSPlayerController player)
  {
    var data = _storage.GetPlayerSkydata(player.SteamID);
    return _plugin.Config.Skyboxs.GetValueOrDefault(data.Skybox);
  }

  public float GetPlayerBrightness(CCSPlayerController player)
  {
    return _storage.GetPlayerSkydata(player.SteamID).Brightness;
  }

  public Color GetPlayerColor(CCSPlayerController player)
  {
    return Color.FromArgb(_storage.GetPlayerSkydata(player.SteamID).Color);
  }

  public Skybox? GetMapDefaultSkybox(string map)
  {
    var maps = _plugin.Config.MapDefault;
    if (maps == null) return null;
    if (maps.TryGetValue(map, out var key)) return _plugin.Config.Skyboxs.GetValueOrDefault(key);
    if (maps.TryGetValue("*", out var wildcard)) return _plugin.Config.Skyboxs.GetValueOrDefault(wildcard);
    return null;
  }

  // ── Apply stored settings to a player ───────────────────────────────────────

  public void ApplyPlayerSettings(CCSPlayerController player)
  {
    if (_plugin.SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
      return;

    var skyData = _storage.GetPlayerSkydata(player.SteamID);

    if (!string.IsNullOrEmpty(skyData.Skybox) && _plugin.Config.Skyboxs.TryGetValue(skyData.Skybox, out var skybox))
    {
      _plugin.EnvManager.SetSkybox(player.Slot, skybox);
    }

    _plugin.EnvManager.SetBrightness(player.Slot, skyData.Brightness);

    if (skyData.Color != int.MaxValue)
    {
      _plugin.EnvManager.SetTintColor(player.Slot, Color.FromArgb(skyData.Color));
    }
  }

  // ── Persistence ─────────────────────────────────────────────────────────────

  public void Save(ulong? steamid = null)
  {
    _storage.Save(steamid);
  }

  public Task SaveAsync(ulong steamid)
  {
    return _storage.SaveAsync(steamid);
  }

  /// <summary>Removes the player's data from the in-memory cache so the next
  /// access forces a fresh database load.</summary>
  public void InvalidateCache(ulong steamid)
  {
    _storage.InvalidateCache(steamid);
  }

  /// <summary>Loads a single player's row from the database into the cache.</summary>
  public Task LoadPlayerAsync(ulong steamid)
  {
    return _storage.LoadPlayerAsync(steamid);
  }
}
