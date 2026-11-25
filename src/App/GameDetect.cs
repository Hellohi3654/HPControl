using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPControl.App
{
    /// <summary>
    /// Guesses whether an app is a game from its executable: where it's installed (Steam, Epic, GOG, Xbox, Riot, EA,
    /// Ubisoft, Battle.net libraries...), game-engine files next to it (Unity, Unreal, Godot, store SDKs) and a short
    /// list of well-known names. Launchers, browsers, video players and everyday apps are never games.
    /// Only runs when the foreground app changes, and the answer is cached per executable, so it costs nothing.
    /// (The service then also checks that the CPU or graphics is actually busy before treating it as a game.)
    /// </summary>
    static class GameDetect
    {
        static readonly Dictionary<string, bool> _cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] LibraryFolders =
        {
            @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\", @"\xboxgames\", @"\riot games\",
            @"\ea games\", @"\electronic arts\", @"\ubisoft game launcher\games\", @"\battle.net\", @"\blizzard entertainment\",
            @"\rockstar games\", @"\roblox\versions\", @"\itch\apps\", @"\amazon games\library\", @"\games\"
        };
        static readonly string[] EngineFiles =
        {
            "UnityPlayer.dll", "GameAssembly.dll", "steam_api64.dll", "steam_api.dll", "EOSSDK-Win64-Shipping.dll", "galaxy64.dll",
            "GFSDK_Aftermath_Lib.x64.dll", "nvngx_dlss.dll", "amd_fidelityfx_dx12.dll", "libxess.dll", "bink2w64.dll", "fmod.dll", "fmodstudio.dll"
        };
        static readonly HashSet<string> KnownGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft.windows", "robloxplayerbeta", "fortniteclient-win64-shipping", "valorant-win64-shipping", "leagueoflegends", "league of legends",
            "cs2", "csgo", "dota2", "gta5", "gtav", "rdr2", "eldenring", "overwatch", "rocketleague", "r5apex", "fifa", "fc24", "fc25", "fc26",
            "witcher3", "cyberpunk2077", "bg3", "bg3_dx11", "starfield", "hogwartslegacy", "palworld-win64-shipping", "terraria", "stardew valley",
            "forzahorizon5", "forzahorizon4", "msfs", "flightsimulator", "beamng.drive", "assettocorsa", "acc", "factorio", "rimworldwin64",
            "sims4", "ts4_x64", "cities", "valheim", "deadbydaylight-win64-shipping", "destiny2", "warframe.x64", "pathofexile_x64", "diablo iv"
        };
        static readonly HashSet<string> NeverGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "battle.net", "eadesktop", "origin", "upc", "ubisoftconnect", "riotclientservices",
            "xboxapp", "gamebar", "explorer", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "vlc", "mpc-hc64", "mpc-be64", "potplayermini64",
            "wmplayer", "video.ui", "spotify", "discord", "teams", "ms-teams", "slack", "zoom", "outlook", "winword", "excel", "powerpnt", "onenote",
            "code", "devenv", "obs64", "notepad", "hpcontrol", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
            "lockapp", "textinputhost", "systemsettings", "taskmgr", "cmd", "powershell", "windowsterminal", "conhost", "mstsc", "photos", "claude",
            "wallpaper32", "wallpaper64", "ui32", "blender", "soundpad", "steamvr", "vrmonitor", "vrserver"
        };

        /// <summary>True when the executable looks like a game.</summary>
        public static bool IsGame(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (_cache)
            {
                if (_cache.TryGetValue(exePath, out var known)) return known;
                bool g = false;
                try { g = Classify(exePath); } catch { }
                if (_cache.Count > 500) _cache.Clear();
                _cache[exePath] = g;
                return g;
            }
        }

        static bool Classify(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (NeverGames.Contains(name)) return false;
            if (KnownGames.Contains(name)) return true;
            string lower = path.ToLowerInvariant();
            // Minecraft Java runs on Mojang's own Java runtime (any other javaw.exe is just a Java app)
            if (name.Equals("javaw", StringComparison.OrdinalIgnoreCase)) return lower.Contains("minecraft") || lower.Contains("\\java-runtime-");
            if (lower.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant())) return false;
            if (name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase)) return true;   // Unreal
            if (LibraryFolders.Any(f => lower.Contains(f))) return true;
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            if (EngineFiles.Any(f => File.Exists(Path.Combine(dir, f)))) return true;
            if (Directory.Exists(Path.Combine(dir, name + "_Data")) && File.Exists(Path.Combine(dir, name + "_Data", "globalgamemanagers"))) return true;   // Unity
            if (File.Exists(Path.Combine(dir, name + ".pck"))) return true;   // Godot
            return false;
        }
    }
}
