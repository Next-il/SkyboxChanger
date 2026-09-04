using System.Reflection;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace SkyboxChanger;

public class KvLib
{

  [DllImport("kvlib", CallingConvention = CallingConvention.Cdecl)]
  public static extern void NativeInitialize(nint pGameEntitySystem);

  [DllImport("kvlib", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Auto)]
  public static extern nint NativeMakeKeyValue([MarshalAs(UnmanagedType.LPUTF8Str)] string fogTargetName, [MarshalAs(UnmanagedType.LPUTF8Str)] string vscripts, [MarshalAs(UnmanagedType.LPUTF8Str)] string material);

  public static bool Initialized { get; set; } = false;

  private delegate IntPtr DlopenDelegate(string filename, int flags);
  private delegate IntPtr DlsymDelegate(IntPtr handle, string symbol);

  private const int RTLD_NOW = 0x2;
  private const int RTLD_GLOBAL = 0x100;

  private static IntPtr ResolveExport(string name)
  {
    foreach (var lib in new[] { "libc.so.6", "libdl.so.2", "libdl.so" })
    {
      try
      {
        if (NativeLibrary.TryLoad(lib, out var h) && NativeLibrary.TryGetExport(h, name, out var fn))
          return fn;
      }
      catch { }
    }
    return IntPtr.Zero;
  }

  /// <summary>kvlib.so leaves g_bUpdateStringTokenDatabase and RegisterStringToken undefined
  /// and expects the host to supply them. The engine loads libtier0.so privately, so those
  /// symbols never reach the global namespace and dlopen of kvlib fails. Reopening tier0 with
  /// RTLD_GLOBAL publishes them without loading a second copy.</summary>
  private static void PublishTier0Symbols()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

    var logger = SkyboxChanger.GetInstance().Logger;

    var pDlopen = ResolveExport("dlopen");
    var pDlsym = ResolveExport("dlsym");
    if (pDlopen == 0 || pDlsym == 0)
    {
      logger.LogWarning("[SkyboxChanger] could not resolve dlopen/dlsym (dlopen=0x{A:X} dlsym=0x{B:X})", pDlopen, pDlsym);
      return;
    }

    var dlopen = Marshal.GetDelegateForFunctionPointer<DlopenDelegate>(pDlopen);
    var dlsym = Marshal.GetDelegateForFunctionPointer<DlsymDelegate>(pDlsym);

    // tier0 is an engine library: game/bin/linuxsteamrt64, not game/csgo/bin/linuxsteamrt64.
    var roots = new[]
    {
      Path.GetFullPath(Path.Join(Server.GameDirectory, "..", "bin", "linuxsteamrt64")),
      Path.GetFullPath(Path.Join(Server.GameDirectory, "bin", "linuxsteamrt64")),
    };

    foreach (var root in roots)
    {
      var path = Path.Join(root, "libtier0.so");
      if (!File.Exists(path))
      {
        logger.LogInformation("[SkyboxChanger] tier0 not at {Path}", path);
        continue;
      }

      var handle = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
      logger.LogInformation("[SkyboxChanger] dlopen(RTLD_GLOBAL) {Path} -> 0x{Handle:X}", path, handle);
      if (handle == 0) continue;

      // Decisive: if tier0 no longer exports this, kvlib.so has to be rebuilt.
      var sym = dlsym(handle, "g_bUpdateStringTokenDatabase");
      logger.LogInformation("[SkyboxChanger] dlsym g_bUpdateStringTokenDatabase -> 0x{Sym:X} ({Verdict})", sym,
        sym != 0 ? "exported, kvlib should now load" : "NOT exported by tier0; kvlib.so must be rebuilt against the current SDK");
      return;
    }

    logger.LogWarning("[SkyboxChanger] libtier0.so not found under any known path; GameDirectory='{Dir}'", Server.GameDirectory);
  }

  private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
  {
    if (libraryName == "kvlib")
    {
      PublishTier0Symbols();
      return NativeLibrary.Load(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "kvlib.dll" : "kvlib.so", assembly, searchPath);
    }

    return IntPtr.Zero;
  }

  public static void SetDllImportResolver()
  {
    NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);
  }

  public static unsafe void Initialize()
  {
    int entitySystemOffset = GameData.GetOffset("GameEntitySystem");
    var pGameResourceServiceServer = NativeAPI.GetValveInterface(0, "GameResourceServiceServerV001");
    var pGameEntitySystem = *(IntPtr*)(pGameResourceServiceServer + entitySystemOffset);
    var server = Path.Join(Server.GameDirectory, Constants.GameBinaryPath, Constants.ModulePrefix + "server" + Constants.ModuleSuffix);
    NativeInitialize(pGameEntitySystem);
    Initialized = true;
  }

  public static nint MakeKeyValue(string fogTargetName, string vscripts, string material)
  {
    if (!Initialized)
    {
      Initialize();
    }
    // return 0;
    return NativeMakeKeyValue(fogTargetName, vscripts, material);
  }



}