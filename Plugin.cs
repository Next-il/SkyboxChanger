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
        }
      }
      SpectatorManager.Initialize();
    });
    RegisterListener<Listeners.OnMapEnd>(() =>
    {
      SpectatorManager.Shutdown();
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
            if (!Config.Enabled) return;

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
    Logger.LogInformation("[SkyboxChanger] Precaching {Count} skybox material(s)", Config.Skyboxs.Count);
    foreach (var skybox in Config.Skyboxs)
    {
      if (skybox.Value.Name == "")
      {
        skybox.Value.Name = skybox.Key;
      }
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

    ShowMainMenu(player);
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
        var result = Service.SetSkybox(p, skybox.Key);
        if (result)
        {
          p.PrintToChat($"{Localizer["prefix"]} {Localizer["change.success"]}");
        }
        else
        {
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