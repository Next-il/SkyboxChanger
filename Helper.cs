using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace SkyboxChanger;

public class Helper
{
  /// <summary>Set when the map's own sky material cannot be resolved through the material
  /// system. Applying a skybox is impossible in that state, so the plugin leaves the map's
  /// env_sky alone rather than replacing it with one it cannot give a material to.</summary>
  public static bool MaterialApplyBroken { get; set; } = false;


  public static bool IsPlayerSkybox(int slot, CEnvSky sky)
  {
    return slot == -1 || sky.PrivateVScripts == "skyboxchanger_" + slot;
  }

  public static void Initialize()
  {

  }

  delegate IntPtr FindOrCreateMaterialFromResourceDelegate(IntPtr pMaterialSystem, IntPtr pOut, string materialName);

  public static unsafe IntPtr FindMaterialByPath(string material, bool verbose = true)
  {
    return LookupMaterial(material, GameData.GetOffset("IMaterialSystem_FindOrCreateMaterialFromResource"), true, verbose);
  }

  /// <summary>Raw probe of the material system. <paramref name="offset"/> is the vtable
  /// index to call and <paramref name="strip"/> controls the trailing "_c" removal, so a
  /// diagnostic command can walk candidate indices and path spellings.</summary>
  public static unsafe IntPtr LookupMaterial(string material, int offset, bool strip, bool verbose = true)
  {
    var logger = SkyboxChanger.GetInstance().Logger;
    var originalMaterial = material;
    if (strip && material.EndsWith("_c"))
    {
      material = material.Substring(0, material.Length - 2);
    }
    IntPtr pIMaterialSystem2 = NativeAPI.GetValveInterface(0, "VMaterialSystem2_001");
    if (pIMaterialSystem2 == 0)
    {
      if (verbose) logger.LogError("[SkyboxChanger] FindMaterialByPath: NativeAPI.GetValveInterface(0, 'VMaterialSystem2_001') returned 0 — interface not available. material='{Material}'", originalMaterial);
      return 0;
    }
    IntPtr vtable = Marshal.ReadIntPtr(pIMaterialSystem2);
    IntPtr functionPtr = Marshal.ReadIntPtr(vtable + (offset * IntPtr.Size));
    if (functionPtr == 0)
    {
      if (verbose) logger.LogError("[SkyboxChanger] FindMaterialByPath: vtable lookup returned null functionPtr. iface=0x{Iface:X} vtable=0x{Vtable:X} offset={Offset} material='{Material}'", pIMaterialSystem2, vtable, offset, originalMaterial);
      return 0;
    }
    var FindOrCreateMaterialFromResource = Marshal.GetDelegateForFunctionPointer<FindOrCreateMaterialFromResourceDelegate>(functionPtr);
    IntPtr outMaterial = 0;
    IntPtr pOutMaterial = (nint)(&outMaterial);
    IntPtr materialptr3;
    string platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux";
    if (verbose) logger.LogInformation("[SkyboxChanger] FindMaterialByPath: invoking on {Platform} for material='{Material}' (iface=0x{Iface:X} vtable=0x{Vtable:X} offset={Offset} fnPtr=0x{Fn:X})", platform, material, pIMaterialSystem2, vtable, offset, functionPtr);
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      materialptr3 = FindOrCreateMaterialFromResource.Invoke(pIMaterialSystem2, pOutMaterial, material);
    }
    else
    {
      materialptr3 = FindOrCreateMaterialFromResource.Invoke(pOutMaterial, 0, material);
    }
    if (verbose) logger.LogInformation("[SkyboxChanger] FindMaterialByPath: post-invoke materialptr3=0x{Mp:X} outMaterial=0x{Out:X} material='{Material}'", materialptr3, outMaterial, material);
    if (materialptr3 == 0)
    {
      if (verbose) logger.LogError("[SkyboxChanger] FindMaterialByPath: FindOrCreateMaterialFromResource returned null pointer (materialptr3=0) for material='{Material}'. The resource was not found — verify the .vmat_c file exists on the server at this path and that the resource was added via OnServerPrecacheResources before this call.", material);
      return 0;
    }
    IntPtr inner = *(IntPtr*)materialptr3;
    if (inner == 0)
    {
      if (verbose) logger.LogError("[SkyboxChanger] FindMaterialByPath: materialptr3=0x{Mp:X} is non-null but inner CMaterial2* is 0 for material='{Material}'. The resource handle exists but points to no material — usually means the file is missing on disk or failed to compile/load.", materialptr3, material);
      return 0;
    }
    return inner; // CMaterial*** -> CMaterial** (InfoForResourceTypeIMaterial2)
  }

  /// <summary>Reads the spawn group an entity belongs to out of its CEntityIdentity.</summary>
  public static unsafe uint GetSpawnGroup(CBaseEntity entity)
  {
    if (entity.Entity == null) return 0;
    return *(uint*)(entity.Entity.Handle + 0x34);
  }

  public static unsafe void SpawnSkybox(int slot, string fogTargetName, string material)
  {
    var instance = SkyboxChanger.GetInstance();

    // A sky_camera is only one possible source of the spawn group; maps without a 3D skybox
    // have none, and the old fallback spawned a bare env_sky that could never be given a
    // material. Any map entity carries the same handle, so use the one captured at map load.
    uint spawngrouphandle = 0;
    var skycameras = Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera");
    if (skycameras.Count() != 0)
    {
      spawngrouphandle = GetSpawnGroup(skycameras.First());
    }
    else
    {
      spawngrouphandle = instance.EnvManager.MapSpawnGroupHandle;
    }

    if (spawngrouphandle == 0)
    {
      instance.Logger.LogError("[SkyboxChanger] SpawnSkybox: no spawn group handle available for slot={Slot}; cannot spawn a sky carrying a material", slot);
      return;
    }

    instance.Logger.LogInformation("[SkyboxChanger] SpawnSkybox: slot={Slot} spawnGroup=0x{Group:X} material='{Material}'", slot, spawngrouphandle, material);
    MemoryManager.CreateLoadingSpawnGroupAndSpawnEntities(spawngrouphandle, true, true, KvLib.MakeKeyValue(fogTargetName, "skyboxchanger_" + slot, material));
  }

  /// <summary>Spawns an env_sky carrying the material as an entity keyvalue, using
  /// CounterStrikeSharp's own CEntityKeyValues rather than kvlib, so the key name can be
  /// varied at runtime while we work out which one this game build honours.</summary>
  public static bool SpawnSkyboxWithKeyValues(int slot, string material, string key)
  {
    var instance = SkyboxChanger.GetInstance();
    var sky = Utilities.CreateEntityByName<CEnvSky>("env_sky");
    if (sky == null)
    {
      instance.Logger.LogError("[SkyboxChanger] SpawnSkyboxWithKeyValues: CreateEntityByName returned null for slot={Slot}", slot);
      return false;
    }

    sky.PrivateVScripts = "skyboxchanger_" + slot;

    var kv = new CEntityKeyValues();
    kv.SetString(key, material);
    kv.SetString("classname", "env_sky");
    kv.SetBool("StartDisabled", false);
    kv.SetFloat("brightnessscale", 1.0f);

    sky.DispatchSpawn(kv);
    instance.Logger.LogInformation("[SkyboxChanger] SpawnSkyboxWithKeyValues: slot={Slot} {Key}='{Material}' entityIndex={Index}", slot, key, material, (int)sky.Index);
    return true;
  }

  /// <summary>Destroys the player's env_sky so a fresh one can take its place.</summary>
  public static void RemovePlayerSkybox(int slot)
  {
    var env = SkyboxChanger.GetInstance().EnvManager;
    if (!env.SpawnedSkyboxes.TryGetValue(slot, out var index)) return;
    env.SpawnedSkyboxes.Remove(slot);
    var sky = Utilities.GetEntityFromIndex<CEnvSky>(index);
    if (sky != null && sky.IsValid) sky.Remove();
  }

  /// <summary>Applies a skybox by respawning the player's env_sky with the material as a
  /// "skyname" keyvalue, which the engine resolves itself. FindOrCreateMaterialFromResource
  /// returns null on current CS2 builds, so patching m_hSkyMaterial in place no longer works.</summary>
  public static bool RespawnSkybox(int slot, Skybox skybox)
  {
    var instance = SkyboxChanger.GetInstance();
    var env = instance.EnvManager;

    var player = Utilities.GetPlayerFromSlot(slot);
    if (player == null || !player.IsValid)
    {
      instance.Logger.LogError("[SkyboxChanger] RespawnSkybox: no valid player for slot={Slot}", slot);
      return false;
    }

    // Carry the player's own brightness/tint across the respawn; the keyvalues
    // hardcode 1.0 and white, so they have to be reapplied afterwards.
    float brightness = skybox.Brightness ?? instance.Service.GetPlayerBrightness(player);
    Color color = instance.Service.GetPlayerColor(player);

    var skycams = Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera").Count();
    instance.Logger.LogInformation("[SkyboxChanger] RespawnSkybox: slot={Slot} material='{Material}' brightness={Brightness} colorArgb=0x{Color:X} skyCameras={Cams} (0 cameras means the keyvalue path is unavailable and the material cannot be applied)", slot, skybox.Material, brightness, color.ToArgb(), skycams);

    RemovePlayerSkybox(slot);
    SpawnSkybox(slot, env.CubemapFogPointedSkyName ?? "", skybox.Material);

    // OnEntityCreated registers the new entity a frame later.
    Server.NextFrame(() => Server.NextFrame(() =>
    {
      if (!env.SpawnedSkyboxes.ContainsKey(slot))
      {
        instance.Logger.LogError("[SkyboxChanger] RespawnSkybox: env_sky never registered for slot={Slot}; the spawn-group path did not produce an entity", slot);
        return;
      }
      instance.Logger.LogInformation("[SkyboxChanger] RespawnSkybox: new env_sky index={Index} for slot={Slot}; applying brightness={Brightness} tint={Tint}", env.SpawnedSkyboxes[slot], slot, brightness, color.ToArgb() == int.MaxValue ? "default" : color.ToString());
      ChangeSkybox(slot, null, brightness, color.ToArgb() == int.MaxValue ? null : color);
    }));

    return true;
  }

  public static unsafe bool ChangeSkybox(int slot, Skybox? skybox = null, float? brightness = null, Color? color = null)
  {
    // materialptr2 : CMaterial2** = InfoForResourceTypeIMaterial2

    var instance = SkyboxChanger.GetInstance();
    if (!instance.EnvManager.SpawnedSkyboxes.ContainsKey(slot))
    {
      instance.Logger.LogError("[SkyboxChanger] ChangeSkybox failed: slot={Slot} has no entry in SpawnedSkyboxes (spawned slots: [{Slots}]). The env_sky entity was never spawned for this player — likely missed OnEntityCreated or InitializeSkyboxForPlayer was not called.", slot, string.Join(", ", instance.EnvManager.SpawnedSkyboxes.Keys));
      return false;
    }

    var entityIndex = instance.EnvManager.SpawnedSkyboxes[slot];
    var sky = Utilities.GetEntityFromIndex<CEnvSky>(entityIndex);
    if (sky == null)
    {
      instance.Logger.LogError("[SkyboxChanger] ChangeSkybox failed: env_sky entity at index={Index} for slot={Slot} could not be retrieved (entity destroyed?)", entityIndex, slot);
      return false;
    }

    if (skybox != null)
    {
      var materialptr2 = FindMaterialByPath(skybox.Material);
      if (materialptr2 == 0)
      {
        instance.Logger.LogError("[SkyboxChanger] ChangeSkybox failed: FindMaterialByPath returned 0 for material='{Material}' (slot={Slot}). Material file likely missing or not precached.", skybox.Material, slot);
        return false;
      }
      instance.Logger.LogInformation("[SkyboxChanger] ChangeSkybox: applying material='{Material}' to slot={Slot} entityIndex={Index}", skybox.Material, slot, entityIndex);
      Unsafe.Write((void*)sky.SkyMaterial.Handle, materialptr2);
      Unsafe.Write((void*)sky.SkyMaterialLightingOnly.Handle, materialptr2);
      Utilities.SetStateChanged(sky, "CEnvSky", "m_hSkyMaterial");
      Utilities.SetStateChanged(sky, "CEnvSky", "m_hSkyMaterialLightingOnly");
    }

    if (color != null)
    {
      sky.TintColor = (Color)color;
    }
    sky.BrightnessScale = brightness ?? skybox?.Brightness ?? sky.BrightnessScale;
    var colorData = skybox?.Color?.Split(" ");
    if (colorData != null && colorData.Length == 4)
    {
      var r = int.Parse(colorData[0]);
      var g = int.Parse(colorData[1]);
      var b = int.Parse(colorData[2]);
      var a = int.Parse(colorData[3]);
      sky.TintColor = Color.FromArgb(a, r, g, b);
    }
    Utilities.SetStateChanged(sky, "CEnvSky", "m_vTintColor");
    Utilities.SetStateChanged(sky, "CEnvSky", "m_flBrightnessScale");
    return true;
  }

  public static bool PlayerHasPermission(CCSPlayerController player, string[]? permissions, string[]? permissionsOr)
  {

    if (permissions != null)
    {
      foreach (string perm in permissions)
      {
        if (perm.StartsWith("@"))
        {
          if (!AdminManager.PlayerHasPermissions(player, [perm]))
          {
            return false;
          }
        }
        else if (perm.StartsWith("#"))
        {
          if (!AdminManager.PlayerInGroup(player, [perm]))
          {
            return false;
          }
        }
        else
        {
          ulong steamId;
          if (!ulong.TryParse(perm, out steamId))
          {
            throw new FormatException($"Unknown SteamID64 format: {perm}");
          }
          else
          {
            if (player.SteamID != steamId)
            {
              return false;
            }
          }
        }
      }
    }

    if (permissionsOr != null)
    {
      foreach (string perm in permissionsOr)
      {
        if (perm.StartsWith("@"))
        {
          if (AdminManager.PlayerHasPermissions(player, perm))
          {
            return true;
          }
        }
        else if (perm.StartsWith("#"))
        {
          if (AdminManager.PlayerInGroup(player, perm))
          {
            return true;
          }
        }
        else
        {
          ulong steamId;
          if (!ulong.TryParse(perm, out steamId))
          {
            throw new FormatException($"Unknown SteamID64 format: {perm}");
          }
          else
          {
            if (player.SteamID == steamId)
            {
              return true;
            }
          }
        }
      }
    }

    return permissionsOr == null || permissionsOr.Length == 0;
  }
}