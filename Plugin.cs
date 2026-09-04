using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MenuManager;

namespace SkyboxChanger;

public class SkyboxChanger : BasePlugin, IPluginConfig<SkyboxConfig>
{
  public override string ModuleName => "Skybox Changer";
  public override string ModuleVersion => "1.5.0";
  public override string ModuleAuthor => "samyyc (fork by luca.uy)";

  public SkyboxConfig Config { get; set; } = new();

  public required EnvManager EnvManager { get; set; } = new();

  public required Service Service { get; set; }

  public required SpectatorSkyboxManager SpectatorManager { get; set; }

  // MenuManager capability
  private IMenuApi? _menuApi;
  private readonly PluginCapability<IMenuApi?> _menuCapability = new("menu:nfcore");

  private static SkyboxChanger? _Instance { get; set; }

  public override unsafe void Load(bool hotReload)
  {
    if (hotReload)
    {
      Logger.LogError("HOT RELOAD DETECTED. It's NOT recommended to hot reload this plugin, please restart your server.");
    }
    KvLib.SetDllImportResolver();
    MemoryManager.Load();
    _Instance = this;

    SpectatorManager = new SpectatorSkyboxManager(this);

    RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
    RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
    RegisterListener<Listeners.OnMapStart>((map) =>
    {
      Server.NextFrame(() =>
      {
        foreach (var fog in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("env_cubemap_fog"))
        {
          if (fog != null && fog.IsValid)
          {
            fog.Remove();
          }
        }
      });
      if (!Config.Skyboxs.ContainsKey(""))
      {
        var skybox = Service.GetMapDefaultSkybox(map);
        if (skybox != null)
        {
          var defaultSkybox = new Skybox
          {
            Name = Localizer["menu.defaultskybox"],
            Material = skybox.Material,
          };
          Config.Skyboxs.Add("", defaultSkybox);
          EnvManager.DefaultMaterial = skybox.Material;
          Logger.LogInformation("[SkyboxChanger] Map default skybox material resolved as '{Material}' — custom entries must use this same path shape", skybox.Material);
        }
      }
      SpectatorManager.Initialize();
    });
    RegisterListener<Listeners.OnMapEnd>(() =>
    {
      SpectatorManager.Shutdown();
      Helper.MaterialApplyBroken = false;
      EnvManager.Shutdown();
      Service.Save();
      MemoryManager.RemoveCachedFactory();
    });
    RegisterListener<Listeners.OnServerPreFatalShutdown>(() =>
    {
      SpectatorManager.Shutdown();
      Service.Save();
    });
    RegisterListener<Listeners.OnEntityCreated>((entity) =>
    {
      Server.NextFrame(() =>
      {
        if (entity.DesignerName == "env_cubemap_fog")
        {
          // CEnvCubemapFog fog = new CEnvCubemapFog(entity.Handle);
          // EnvManager.CubemapFogPointedSkyName = "[PR#]" + fog.SkyEntity;
          entity.Remove();
          return;
        }
        if (entity.DesignerName == "env_sky")
        {
          CEnvSky sky = new CEnvSky(entity.Handle);
          if (entity.PrivateVScripts == null || !entity.PrivateVScripts.StartsWith("skyboxchanger_"))
          {
            nint materialptr = *(IntPtr*)sky.SkyMaterial.Value;
            var GetMaterialName = VirtualFunction.Create<IntPtr, string>(materialptr, 0);
            string skyMaterial = GetMaterialName.Invoke(materialptr);

            // Capture the spawn group while a map entity still exists. Maps without a
            // sky_camera have no other source, and the keyvalue spawn path needs it.
            if (EnvManager.MapSpawnGroupHandle == 0)
            {
              EnvManager.MapSpawnGroupHandle = Helper.GetSpawnGroup(sky);
              Logger.LogInformation("[SkyboxChanger] Captured map spawn group 0x{Group:X} from env_sky index={Index}", EnvManager.MapSpawnGroupHandle, (int)entity.Index);
            }

            if (!Config.Skyboxs.ContainsKey(""))
            {
              EnvManager.DefaultMaterial = skyMaterial;
              Logger.LogInformation("[SkyboxChanger] Map default sky material is '{Material}'", skyMaterial);
              Config.Skyboxs.Add(
                "",
                new Skybox { Name = Localizer["menu.defaultskybox"], Material = skyMaterial }
              );
            }

            // Never probe the material system here. FindOrCreateMaterialFromResource is a
            // *create* call and the index we have lands on the wrong function, which corrupts
            // the map's sky into the missing-texture material just by being called.
            if (!Config.Enabled)
            {
              if (!Helper.MaterialApplyBroken)
              {
                Helper.MaterialApplyBroken = true;
                Logger.LogWarning("[SkyboxChanger] Disabled in config (\"Enabled\": false), so the map's env_sky is left in place and the sky renders normally. Use css_skykv to find a sky material keyvalue this build honours, then set SkyMaterialKey and Enabled in the config.");
              }
              return;
            }

            sky.Remove();
          }
          else
          {
            EnvManager.SpawnedSkyboxes.Add(int.Parse(entity.PrivateVScripts.Replace("skyboxchanger_", "")), (int)entity.Index);
          }
        }
      });
    });
    RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
    {
      var slot = @event.Userid!.Slot;
      var player = @event.Userid!;
      Server.NextFrame(() =>
      {
        foreach (var sky in Utilities.FindAllEntitiesByDesignerName<CEnvSky>("env_sky"))
        {
          if (Helper.IsPlayerSkybox(slot, sky))
          {
            sky.Remove();
            EnvManager.SpawnedSkyboxes.Remove(slot);
          }
        }
        if (player.AuthorizedSteamID != null)
        {
          Service?.InvalidateCache(player.AuthorizedSteamID.SteamId64);
          _ = LoadPlayerSettingsOnConnectAndInitialize(player.AuthorizedSteamID.SteamId64, player);
        }
        else
        {
          EnvManager.InitializeSkyboxForPlayer(player);
        }
      });
      return HookResult.Continue;
    });
    RegisterListener<Listeners.OnClientDisconnect>(slot =>
    {
      EnvManager.OnPlayerLeave(slot);
      SpectatorManager.OnPlayerDisconnect(slot);
      var player = Utilities.GetPlayerFromSlot(slot);
      if (player != null && player.AuthorizedSteamID != null && Service != null)
      {
        Service.Save(player.AuthorizedSteamID.SteamId64);
        Service.InvalidateCache(player.AuthorizedSteamID.SteamId64);
      }
    });
    Helper.Initialize();
  }

  public override void OnAllPluginsLoaded(bool hotReload)
  {
    _menuApi = _menuCapability.Get();

    if (_menuApi == null)
    {
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine("[SkyboxChanger] CRITICAL ERROR: MenuManager API not found!");
      Console.WriteLine("[SkyboxChanger] MenuManager is a required dependency for this plugin to function.");
      Console.WriteLine("[SkyboxChanger] Please install MenuManagerCS2 from: https://github.com/NickFox007/MenuManagerCS2");
      Console.WriteLine("[SkyboxChanger] Plugin will now unload automatically.");
      Console.ResetColor();

      Server.NextFrame(() =>
      {
        try
        {
          Server.ExecuteCommand($"css_plugins unload {ModuleName}");
        }
        catch (Exception ex)
        {
          Console.WriteLine($"[SkyboxChanger] Error during auto-unload: {ex.Message}");
        }
      });

      return;
    }
  }

  private void OnCheckTransmit(CCheckTransmitInfoList infoList)
  {
    EnvManager.OnCheckTransmit(infoList);
  }

  public override void Unload(bool hotReload)
  {
    if (_menuApi != null)
    {
      foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid))
      {
        _menuApi.CloseMenu(player);
      }
    }

    SpectatorManager.Shutdown();
    Service.Save();
    MemoryManager.Unload();
    _menuApi = null;
  }

  public static SkyboxChanger GetInstance()
  {
    if (_Instance == null)
    {
      throw new Exception("SkyboxChanger is not loaded");
    }

    return _Instance;
  }


  public void OnConfigParsed(SkyboxConfig config)
  {
    Config = config;
    Service = new Service(this, Config.Database.Host, Config.Database.Port, Config.Database.User, Config.Database.Password, Config.Database.Database, Config.Database.TablePrefix);
  }

  private async Task LoadPlayerSettingsOnConnectAndInitialize(ulong steamId64, CCSPlayerController player)
  {
    try
    {
      await Service.LoadPlayerAsync(steamId64);
    }
    catch (Exception ex)
    {
      Logger.LogError("[SkyboxChanger] Failed to load settings for {SteamId}: {Error}", steamId64, ex.Message);
    }

    Server.NextFrame(() =>
    {
      if (!player.IsValid) return;
      EnvManager.InitializeSkyboxForPlayer(player);
    });
  }

  public void OnServerPrecacheResources(ResourceManifest manifest)
  {
    Logger.LogInformation("[SkyboxChanger] OnServerPrecacheResources: precaching {Count} skybox material(s)", Config.Skyboxs.Count);
    foreach (var skybox in Config.Skyboxs)
    {
      if (skybox.Value.Name == "")
      {
        skybox.Value.Name = skybox.Key;
      }
      Logger.LogInformation("[SkyboxChanger] OnServerPrecacheResources: adding resource key='{Key}' material='{Material}'", skybox.Key, skybox.Value.Material);
      manifest.AddResource(skybox.Value.Material);
    }
  }

  [ConsoleCommand("css_sky")]
  [ConsoleCommand("css_skybox")]
  [CommandHelper(0, "Change skybox", CommandUsage.CLIENT_ONLY)]
  public unsafe void SkyboxCommand(CCSPlayerController player, CommandInfo info)
  {
    if (_menuApi == null)
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["menu.error"]}");
      return;
    }

    if (Config.MenuPermission != "" && Config.MenuPermission != null && !AdminManager.PlayerHasPermissions(player, [Config.MenuPermission]))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["no.permission"]}");
      return;
    }

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["need.alive"]}");
      return;
    }

    if (Helper.MaterialApplyBroken)
    {
      player.PrintToChat($"{Localizer["prefix"]} Skybox changing is unavailable on this game build.");
      return;
    }

    ShowMainMenu(player);
  }

  [ConsoleCommand("css_skytest")]
  [CommandHelper(0, "Diagnose skybox material resolution", CommandUsage.CLIENT_AND_SERVER)]
  [RequiresPermissions("@css/root")]
  public unsafe void SkyTestCommand(CCSPlayerController? player, CommandInfo info)
  {
    void Reply(string msg)
    {
      info.ReplyToCommand(msg);
      Logger.LogInformation("[SkyboxChanger] skytest: {Msg}", msg);
    }

    var arg = info.ArgCount > 1 ? info.GetArg(1) : "";

    // No argument: bulk-test every configured material and report how many resolve.
    if (arg == "")
    {
      int ok = 0;
      var failed = new List<string>();
      foreach (var kv in Config.Skyboxs)
      {
        if (Helper.FindMaterialByPath(kv.Value.Material, false) != 0) ok++;
        else if (failed.Count < 8) failed.Add(kv.Key);
      }
      Reply($"resolved {ok}/{Config.Skyboxs.Count} configured materials");
      if (failed.Count > 0) Reply($"first failures: {string.Join(", ", failed)}");
      return;
    }

    var configured = GameData.GetOffset("IMaterialSystem_FindOrCreateMaterialFromResource");

    // "offset <n> [material]": call one vtable index directly. Probing the wrong slot
    // can hard-crash the server, so this deliberately does a single index per command.
    if (arg == "offset")
    {
      if (info.ArgCount < 3 || !int.TryParse(info.GetArg(2), out var probe))
      {
        Reply($"usage: css_skytest offset <index> [material]   (configured index is {configured})");
        return;
      }
      var mat = info.ArgCount > 3 ? info.GetArg(3) : "materials/skybox/sky_black.vmat";
      Reply($"probing vtable index {probe} with '{mat}' ...");
      var r = Helper.LookupMaterial(mat, probe, true, false);
      Reply($"index {probe} -> {(r != 0 ? "NON-NULL 0x" + r.ToString("X") + "  <== candidate" : "null")}");
      return;
    }

    // "path <material>": try the spellings the engine might actually want.
    var target = arg == "path" && info.ArgCount > 2 ? info.GetArg(2) : arg;
    var bare = target.EndsWith("_c") ? target.Substring(0, target.Length - 2) : target;
    var noPrefix = bare.StartsWith("materials/") ? bare.Substring("materials/".Length) : "materials/" + bare;
    var noExt = bare.EndsWith(".vmat") ? bare.Substring(0, bare.Length - 5) : bare;

    var variants = new List<(string path, bool strip)>
    {
      (bare, true), (bare + "_c", false), (noPrefix, true), (noPrefix + "_c", false),
      (noExt, true), (bare.ToLowerInvariant(), true),
    };

    foreach (var (path, strip) in variants.Distinct())
    {
      var ptr = Helper.LookupMaterial(path, configured, strip, false);
      Reply($"'{path}'{(strip ? "" : " (raw)")} -> {(ptr != 0 ? "OK 0x" + ptr.ToString("X") : "NULL")}");
    }
  }

  [ConsoleCommand("css_skyinfo")]
  [CommandHelper(0, "Report sky entity state", CommandUsage.CLIENT_AND_SERVER)]
  [RequiresPermissions("@css/root")]
  public unsafe void SkyInfoCommand(CCSPlayerController? player, CommandInfo info)
  {
    void Reply(string msg)
    {
      info.ReplyToCommand("[SkyboxChanger] " + msg);
      Logger.LogInformation("[SkyboxChanger] skyinfo: {Msg}", msg);
    }

    var cams = Utilities.FindAllEntitiesByDesignerName<CSkyCamera>("sky_camera").Count();
    Reply($"Enabled={Config.Enabled}  SkyMaterialKey='{Config.SkyMaterialKey}'  MaterialApplyBroken={Helper.MaterialApplyBroken}");
    Reply($"sky_camera count={cams}  ({(cams > 0 ? "spawn-group path available" : "NO 3d skybox: SpawnSkybox falls back to CreateEntityByName, which cannot carry a material")})");
    Reply($"EnvManager.DefaultMaterial='{EnvManager.DefaultMaterial}'");
    Reply($"MapSpawnGroupHandle=0x{EnvManager.MapSpawnGroupHandle:X}  ({(EnvManager.MapSpawnGroupHandle != 0 ? "keyvalue spawn path usable" : "NOT captured; spawning cannot carry a material")})");

    var skies = Utilities.FindAllEntitiesByDesignerName<CEnvSky>("env_sky").ToList();
    Reply($"env_sky entities: {skies.Count}");
    foreach (var sky in skies)
    {
      if (sky == null || !sky.IsValid) continue;
      string mat = "<unreadable>";
      try
      {
        unsafe
        {
          nint mp = *(IntPtr*)sky.SkyMaterial.Value;
          if (mp != 0)
          {
            var GetName = VirtualFunction.Create<IntPtr, string>(mp, 0);
            mat = GetName.Invoke(mp);
          }
          else mat = "<null material>";
        }
      }
      catch (Exception ex) { mat = "<error: " + ex.Message + ">"; }
      Reply($"  index={(int)sky.Index} vscripts='{sky.PrivateVScripts}' brightness={sky.BrightnessScale} material='{mat}'");
    }
  }

  [ConsoleCommand("css_skykv")]
  [CommandHelper(0, "Find which keyvalue applies a sky material", CommandUsage.CLIENT_ONLY)]
  [RequiresPermissions("@css/root")]
  public void SkyKvCommand(CCSPlayerController player, CommandInfo info)
  {
    if (info.ArgCount < 3)
    {
      info.ReplyToCommand("[SkyboxChanger] usage: css_skykv <keyname> <material>");
      info.ReplyToCommand("[SkyboxChanger] e.g. css_skykv skyname materials/skybox/sky_de_dust2.vmat");
      return;
    }

    var key = info.GetArg(1);
    var material = info.GetArg(2);

    // Clear every env_sky, the map's included, so only the test entity can be rendering.
    foreach (var s in Utilities.FindAllEntitiesByDesignerName<CEnvSky>("env_sky"))
    {
      if (s != null && s.IsValid) s.Remove();
    }
    EnvManager.SpawnedSkyboxes.Clear();

    var slot = player.Slot;
    Logger.LogInformation("[SkyboxChanger] css_skykv: testing key='{Key}' material='{Material}' for slot={Slot}", key, material, slot);

    Server.NextFrame(() =>
    {
      var ok = Helper.SpawnSkyboxWithKeyValues(slot, material, key);
      var p = Utilities.GetPlayerFromSlot(slot);
      if (p != null && p.IsValid)
      {
        p.PrintToChat($"{Localizer["prefix"]} css_skykv {key}='{material}' -> {(ok ? "spawned, look up" : "spawn FAILED")}");
      }
    });
  }

  private void ShowMainMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    var mainMenu = _menuApi.GetMenu(Localizer["menu.title"]);

    mainMenu.AddMenuOption(Localizer["menu.skybox"], (p, option) =>
    {
      ShowSkyboxMenu(p);
    });

    mainMenu.AddMenuOption(Localizer["menu.brightness"], (p, option) =>
    {
      ShowBrightnessMenu(p);
    });

    mainMenu.AddMenuOption(Localizer["menu.tintcolor"], (p, option) =>
    {
      ShowColorMenu(p);
    });

    mainMenu.Open(player);
  }

  private void ShowSkyboxMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var skyboxMenu = _menuApi.GetMenu(Localizer["menu.title"]);

    var skyboxes = Config.Skyboxs.ToList();
    skyboxes.RemoveAll(kv => kv.Key == "");
    if (Config.Skyboxs.ContainsKey(""))
    {
      var def = Config.Skyboxs[""];
      skyboxes.Insert(0, new KeyValuePair<string, Skybox>("", def));
    }

    skyboxes.ForEach(skybox =>
    {
      if (!Helper.PlayerHasPermission(player, skybox.Value.Permissions, skybox.Value.PermissionsOr)) return;

      skyboxMenu.AddMenuOption(skybox.Value.Name, (p, option) =>
      {
        Logger.LogInformation("[SkyboxChanger] Menu: player slot={Slot} steamId={SteamId} selected skybox key='{Key}' name='{Name}'", p.Slot, p.SteamID, skybox.Key, skybox.Value.Name);
        var result = Service.SetSkybox(p, skybox.Key);
        if (result)
        {
          p.PrintToChat($"{Localizer["prefix"]} {Localizer["change.success"]}");
        }
        else
        {
          Logger.LogError("[SkyboxChanger] Menu: change.failed shown to player slot={Slot} for key='{Key}' — see prior log lines for the actual failure point", p.Slot, skybox.Key);
          p.PrintToChat($"{Localizer["prefix"]} {Localizer["change.failed"]}");
        }
        // _menuApi?.CloseMenu(p);
      });
    });

    skyboxMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    skyboxMenu.Open(player);
  }

  private void ShowBrightnessMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var brightnessMenu = _menuApi.GetMenu(Localizer["menu.brightness"]);

    float currentBrightness = Service.GetPlayerBrightness(player);

    brightnessMenu.AddMenuOption("-- (- 0.5)", (p, option) =>
    {
      float newValue = Math.Max(0.0f, currentBrightness - 0.5f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("- (- 0.1)", (p, option) =>
    {
      float newValue = Math.Max(0.0f, currentBrightness - 0.1f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption($"{Localizer["menu.current"]}: {currentBrightness:F1}", (p, option) =>
    {
      // Do nothing, just display
    });

    brightnessMenu.AddMenuOption("+ (+ 0.1)", (p, option) =>
    {
      float newValue = Math.Min(10.0f, currentBrightness + 0.1f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("++ (+ 0.5)", (p, option) =>
    {
      float newValue = Math.Min(10.0f, currentBrightness + 0.5f);
      Service.SetBrightness(p, newValue);
      ShowBrightnessMenu(p);
    });

    brightnessMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    brightnessMenu.Open(player);
  }

  private void ShowColorMenu(CCSPlayerController player)
  {
    if (_menuApi == null) return;

    if (SpectatorManager.IsPlayerInSpectatorMode(player.Slot))
    {
      player.PrintToChat($"{Localizer["prefix"]} {Localizer["spectator.cannot_change"]}");
      return;
    }

    var colorMenu = _menuApi.GetMenu(Localizer["menu.tintcolor"]);

    foreach (var knownColor in (KnownColor[])Enum.GetValues(typeof(KnownColor)))
    {
      if (Color.FromKnownColor(knownColor).IsSystemColor) continue;

      colorMenu.AddMenuOption(knownColor.ToString(), (p, option) =>
      {
        Service.SetTintColor(p, Color.FromKnownColor(knownColor));
        // _menuApi?.CloseMenu(p);
      });
    }

    colorMenu.AddMenuOption("← " + Localizer["menu.back"], (p, option) =>
    {
      ShowMainMenu(p);
    });

    colorMenu.Open(player);
  }
}