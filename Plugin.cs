using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ItemManager;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace EvilSpiritKatana
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class EvilSpiritKatanaPlugin : BaseUnityPlugin
    {
        internal const string ModName = "EvilSpiritKatana";
        internal const string ModVersion = "1.0.9";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        internal static string ConnectionError = "";

        private readonly Harmony _harmony = new(ModGUID);

        public static readonly ManualLogSource EvilSpiritKatanaLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID)
            { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

        public void Awake()
        {
            _serverConfigLocked = config("General", "Force Server Config", true, "Force Server Config");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

            Item EvilSpiritKatana = new("evilkatana", "EvilSpiritKatana");
            EvilSpiritKatana.Name.English("Evil Spirit Katana");
            EvilSpiritKatana.Description.English("A katana that is said to be the weapon of an evil spirit.");
            EvilSpiritKatana.Crafting.Add(CraftingTable.Forge, 3);
            EvilSpiritKatana.RequiredItems.Add("BlackMetal", 40);
            EvilSpiritKatana.RequiredItems.Add("Silver", 40);
            EvilSpiritKatana.RequiredUpgradeItems.Add("BlackMetal", 20);
            EvilSpiritKatana.RequiredUpgradeItems.Add("Silver", 10);


            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private void OnDestroy()
        {
            Config.Save();
        }

        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                EvilSpiritKatanaLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                EvilSpiritKatanaLogger.LogError($"There was an issue loading your {ConfigFileName}");
                EvilSpiritKatanaLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        private static ConfigEntry<bool> _serverConfigLocked = null!;
        //private static ConfigEntry<bool>? _recipeIsActiveConfig = null!;

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description,
            bool synchronizedSetting = true)
        {
            ConfigDescription extendedDescription =
                new(
                    description.Description +
                    (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                    description.AcceptableValues, description.Tags);
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
            //var configEntry = Config.Bind(group, name, value, description);

            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order = null!;
            [UsedImplicitly] public bool? Browsable = null!;
            [UsedImplicitly] public string? Category = null!;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
        }

        #endregion
    }
}