﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace ItemManager;

[PublicAPI]
public enum CraftingTable
{
    None,
    [InternalName("piece_workbench")] Workbench,
    [InternalName("piece_cauldron")] Cauldron,
    [InternalName("forge")] Forge,
    [InternalName("piece_artisanstation")] ArtisanTable,
    [InternalName("piece_stonecutter")] StoneCutter,
    Custom
}

public class InternalName : Attribute
{
    public readonly string internalName;
    public InternalName(string internalName) => this.internalName = internalName;
}

[PublicAPI]
public class RequiredResourceList
{
    public readonly List<Requirement> Requirements = new();

    public void Add(string itemName, int amount) =>
        Requirements.Add(new Requirement { itemName = itemName, amount = amount });

    public void Add(string itemName, ConfigEntry<int> amountConfig) =>
        Requirements.Add(new Requirement { itemName = itemName, amountConfig = amountConfig });
}

[PublicAPI]
public class CraftingStationList
{
    public readonly List<CraftingStationConfig> Stations = new();

    public void Add(CraftingTable table, int level) =>
        Stations.Add(new CraftingStationConfig { Table = table, level = level });

    public void Add(string customTable, int level) => Stations.Add(new CraftingStationConfig
        { Table = CraftingTable.Custom, level = level, custom = customTable });
}

[PublicAPI]
public class ItemRecipe
{
    public readonly RequiredResourceList RequiredItems = new();
    public readonly RequiredResourceList RequiredUpgradeItems = new();
    public readonly CraftingStationList Crafting = new();
    public int CraftAmount = 1;
    public ConfigEntryBase? RecipeIsActive = null;
}

public struct Requirement
{
    public string itemName;
    public int amount;
    public ConfigEntry<int>? amountConfig;
}

public struct CraftingStationConfig
{
    public CraftingTable Table;
    public int level;
    public string? custom;
}

[PublicAPI]
public class Item
{
    internal class ItemConfig
    {
        public ConfigEntry<string> craft = null!;
        public ConfigEntry<string>? upgrade;
        public ConfigEntry<CraftingTable> table = null!;
        public ConfigEntry<int> tableLevel = null!;
        public ConfigEntry<string> customTable = null!;
        public ConfigEntry<int>? maximumTableLevel;
        /* Damage */
        public ConfigEntry<float> durability = null!;
        public ConfigEntry<float> durabilityPerLevel = null!;
        public ConfigEntry<float> useDurabilityDrain = null!;
        public ConfigEntry<float> baseDamage = null!;
        public ConfigEntry<int> baseBlunt = null!;
        public ConfigEntry<float> baseSlash = null!;
        public ConfigEntry<float> basePierce = null!;
        public ConfigEntry<float> baseChop = null!;
        public ConfigEntry<float> basePickaxe = null!;
        public ConfigEntry<float> baseFire = null!;
        public ConfigEntry<float> baseFrost = null!;
        public ConfigEntry<float> baseLightning = null!;
        public ConfigEntry<float> basePoison = null!;
        public ConfigEntry<float> baseSpirit = null!;
        public ConfigEntry<float> baseDamagePerPerLevel = null!;
        public ConfigEntry<float> baseBluntPerLevel = null!;
        public ConfigEntry<float> baseSlashPerLevel = null!;
        public ConfigEntry<float> basePiercePerLevel = null!;
        public ConfigEntry<float> baseChopPerLevel = null!;
        public ConfigEntry<float> basePickaxePerLevel = null!;
        public ConfigEntry<float> baseFirePerLevel = null!;
        public ConfigEntry<float> baseFrostPerLevel = null!;
        public ConfigEntry<float> baseLightningPerLevel = null!;
        public ConfigEntry<float> basePoisonPerLevel = null!;
        public ConfigEntry<float> baseSpiritPerLevel = null!;
        public ConfigEntry<float> baseAttackForce = null!;
        public ConfigEntry<float> baseBlockPower = null!; // block power
        public ConfigEntry<float> baseParryForce = null!; // parry force
        public ConfigEntry<float> baseKnockbackForce = null!; // knockback force
        public ConfigEntry<float> baseBackstab = null!; // backstab
    }

    internal static readonly List<Item> registeredItems = new();
    internal static readonly Dictionary<ItemDrop, Item> itemDropMap = new();
    internal static readonly Dictionary<Item, ItemConfig> itemConfigMap = new();
    private static Dictionary<Item, Dictionary<string, List<Recipe>>> activeRecipes = new();
    private static Dictionary<Item, Dictionary<string, ItemConfig>> itemCraftConfigs = new();

    public static bool ConfigurationEnabled = true;
    public bool Configurable = true;

    public readonly GameObject Prefab;

    [Description(
        "Specifies the resources needed to craft the item.\nUse .Add to add resources with their internal ID and an amount.\nUse one .Add for each resource type the item should need.")]
    public RequiredResourceList RequiredItems => this[""].RequiredItems;

    [Description(
        "Specifies the resources needed to upgrade the item.\nUse .Add to add resources with their internal ID and an amount. This amount will be multipled by the item quality level.\nUse one .Add for each resource type the upgrade should need.")]
    public RequiredResourceList RequiredUpgradeItems => this[""].RequiredUpgradeItems;

    [Description(
        "Specifies the crafting station needed to craft the item.\nUse .Add to add a crafting station, using the CraftingTable enum and a minimum level for the crafting station.\nUse one .Add for each crafting station.")]
    public CraftingStationList Crafting => this[""].Crafting;

    [Description("Specifies a config entry which toggles whether a recipe is active.")]
    public ConfigEntryBase? RecipeIsActive
    {
        get => this[""].RecipeIsActive;
        set => this[""].RecipeIsActive = value;
    }

    [Description(
        "Specifies the number of items that should be given to the player with a single craft of the item.\nDefaults to 1.")]
    public int CraftAmount
    {
        get => this[""].CraftAmount;
        set => this[""].CraftAmount = value;
    }

    [Description(
        "Specifies the maximum required crafting station level to upgrade and repair the item.\nDefault is calculated from crafting station level and maximum quality.")]
    public int MaximumRequiredStationLevel = int.MaxValue;

    [Description("Toggles whether or not to generate the weapon configurations for damage values.")]
    public bool GenerateWeaponConfigs = true;

    public Dictionary<string, ItemRecipe> Recipes = new();

    public ItemRecipe this[string name]
    {
        get
        {
            if (Recipes.TryGetValue(name, out ItemRecipe recipe))
            {
                return recipe;
            }

            return Recipes[name] = new ItemRecipe();
        }
    }

    private LocalizeKey? _name;

    public LocalizeKey Name
    {
        get
        {
            if (_name is { } name)
            {
                return name;
            }

            ItemDrop.ItemData.SharedData data = Prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            if (data.m_name.StartsWith("$"))
            {
                _name = new LocalizeKey(data.m_name);
            }
            else
            {
                string key = "$item_" + Prefab.name.Replace(" ", "_");
                _name = new LocalizeKey(key).English(data.m_name);
                data.m_name = key;
            }

            return _name;
        }
    }

    private LocalizeKey? _description;

    public LocalizeKey Description
    {
        get
        {
            if (_description is { } description)
            {
                return description;
            }

            ItemDrop.ItemData.SharedData data = Prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            if (data.m_description.StartsWith("$"))
            {
                _description = new LocalizeKey(data.m_description);
            }
            else
            {
                string key = "$itemdesc_" + Prefab.name.Replace(" ", "_");
                _description = new LocalizeKey(key).English(data.m_description);
                data.m_description = key;
            }

            return _description;
        }
    }

    public Item(string assetBundleFileName, string prefabName, string folderName = "assets") : this(
        PrefabManager.RegisterAssetBundle(assetBundleFileName, folderName), prefabName)
    {
    }

    public Item(AssetBundle bundle, string prefabName) : this(PrefabManager.RegisterPrefab(bundle, prefabName, true),
        true)
    {
    }

    public Item(GameObject prefab, bool skipRegistering = false)
    {
        if (!skipRegistering)
        {
            PrefabManager.RegisterPrefab(prefab, true);
        }

        Prefab = prefab;
        registeredItems.Add(this);
        itemDropMap[Prefab.GetComponent<ItemDrop>()] = this;
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order;
        [UsedImplicitly] public bool? Browsable;
        [UsedImplicitly] public string? Category;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer;
    }

    private static object? configManager;

    internal static void Patch_FejdStartup()
    {
        Assembly? bepinexConfigManager = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "ConfigurationManager");

        Type? configManagerType = bepinexConfigManager?.GetType("ConfigurationManager.ConfigurationManager");
        configManager = configManagerType == null
            ? null
            : BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(configManagerType);

        void reloadConfigDisplay() =>
            configManagerType?.GetMethod("BuildSettingList")!.Invoke(configManager, Array.Empty<object>());

        if (ConfigurationEnabled)
        {
            bool SaveOnConfigSet = plugin.Config.SaveOnConfigSet;
            plugin.Config.SaveOnConfigSet = false;

            foreach (Item item in registeredItems.Where(i => i.Configurable))
            {
                itemCraftConfigs[item] = new Dictionary<string, ItemConfig>();
                foreach (string configKey in item.Recipes.Keys.DefaultIfEmpty(""))
                {
                    int order = 0;

                    string configSuffix = configKey == "" ? "" : $" ({configKey})";
                    ItemConfig cfg = itemCraftConfigs[item][configKey] = new ItemConfig();

                    if (item.Crafting.Stations.Count > 0)
                    {
                        string nameKey = item.Prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
                        string englishName = new Regex("['[\"\\]]").Replace(english.Localize(nameKey), "").Trim();
                        string localizedName = Localization.instance.Localize(nameKey).Trim();

                        List<ConfigurationManagerAttributes> hideWhenNoneAttributes = new();

                        cfg.table = config(localizedName, "Crafting Station" + configSuffix,
                            item.Recipes[configKey].Crafting.Stations.First().Table,
                            new ConfigDescription($"Crafting station where {localizedName} is available.", null,
                                new ConfigurationManagerAttributes { Order = --order, Category = localizedName }));
                        ConfigurationManagerAttributes customTableAttributes = new()
                        {
                            Order = --order, Browsable = cfg.table.Value == CraftingTable.Custom,
                            Category = localizedName
                        };
                        cfg.customTable = config(localizedName, "Custom Crafting Station" + configSuffix,
                            item.Recipes[configKey].Crafting.Stations.First().custom ?? "",
                            new ConfigDescription("", null, customTableAttributes));

                        void TableConfigChanged(object o, EventArgs e)
                        {
                            if (activeRecipes.ContainsKey(item) &&
                                activeRecipes[item].TryGetValue(configKey, out List<Recipe> recipes))
                            {
                                if (cfg.table.Value is CraftingTable.None)
                                {
                                    recipes.First().m_craftingStation = null;
                                }
                                else if (cfg.table.Value is CraftingTable.Custom)
                                {
                                    recipes.First().m_craftingStation = ZNetScene.instance
                                        .GetPrefab(cfg.customTable.Value)?.GetComponent<CraftingStation>();
                                }
                                else
                                {
                                    recipes.First().m_craftingStation = ZNetScene.instance
                                        .GetPrefab(
                                            ((InternalName)typeof(CraftingTable).GetMember(cfg.table.Value.ToString())
                                                [0].GetCustomAttributes(typeof(InternalName)).First()).internalName)
                                        .GetComponent<CraftingStation>();
                                }
                            }

                            customTableAttributes.Browsable = cfg.table.Value == CraftingTable.Custom;
                            foreach (ConfigurationManagerAttributes attributes in hideWhenNoneAttributes)
                            {
                                attributes.Browsable = cfg.table.Value != CraftingTable.None;
                            }

                            reloadConfigDisplay();
                        }

                        cfg.table.SettingChanged += TableConfigChanged;
                        cfg.customTable.SettingChanged += TableConfigChanged;

                        ConfigurationManagerAttributes tableLevelAttributes = new()
                        {
                            Order = --order, Browsable = cfg.table.Value != CraftingTable.None, Category = localizedName
                        };
                        hideWhenNoneAttributes.Add(tableLevelAttributes);
                        cfg.tableLevel = config(localizedName, "Crafting Station Level" + configSuffix,
                            item.Recipes[configKey].Crafting.Stations.First().level,
                            new ConfigDescription($"Required crafting station level to craft {localizedName}.", null,
                                tableLevelAttributes));
                        cfg.tableLevel.SettingChanged += (_, _) =>
                        {
                            if (activeRecipes.ContainsKey(item) &&
                                activeRecipes[item].TryGetValue(configKey, out List<Recipe> recipes))
                            {
                                recipes.First().m_minStationLevel = cfg.tableLevel.Value;
                            }
                        };
                        if (item.Prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxQuality > 1)
                        {
                            cfg.maximumTableLevel = config(localizedName, "Maximum Crafting Station Level" + configSuffix,
                                item.MaximumRequiredStationLevel == int.MaxValue
                                    ? item.Recipes[configKey].Crafting.Stations.First().level +
                                    item.Prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxQuality - 1
                                    : item.MaximumRequiredStationLevel,
                                new ConfigDescription(
                                    $"Maximum crafting station level to upgrade and repair {localizedName}.", null,
                                    tableLevelAttributes));
                        }

                        ConfigEntry<string> itemConfig(string name, string value, string desc)
                        {
                            ConfigurationManagerAttributes attributes = new()
                            {
                                CustomDrawer = drawConfigTable, Order = --order,
                                Browsable = cfg.table.Value != CraftingTable.None, Category = localizedName
                            };
                            hideWhenNoneAttributes.Add(attributes);
                            return config(localizedName, name, value, new ConfigDescription(desc, null, attributes));
                        }

                        cfg.craft = itemConfig("Crafting Costs" + configSuffix,
                            new SerializedRequirements(item.Recipes[configKey].RequiredItems.Requirements).ToString(),
                            $"Item costs to craft {localizedName}");
                        if (item.Prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxQuality > 1)
                        {
                            cfg.upgrade = itemConfig("Upgrading Costs" + configSuffix,
                                new SerializedRequirements(item.Recipes[configKey].RequiredUpgradeItems.Requirements)
                                    .ToString(), $"Item costs per level to upgrade {localizedName}");
                        }

                        void ConfigChanged(object o, EventArgs e)
                        {
                            if (ObjectDB.instance && activeRecipes.ContainsKey(item) &&
                                activeRecipes[item].TryGetValue(configKey, out List<Recipe> recipes))
                            {
                                foreach (Recipe recipe in recipes)
                                {
                                    recipe.m_resources = SerializedRequirements.toPieceReqs(ObjectDB.instance,
                                        new SerializedRequirements(cfg.craft.Value),
                                        new SerializedRequirements(cfg.upgrade?.Value ?? ""));
                                }
                            }
                        }

                        cfg.craft.SettingChanged += ConfigChanged;
                        if (cfg.upgrade != null)
                        {
                            cfg.upgrade.SettingChanged += ConfigChanged;
                        }
                    }

                    if (item.GenerateWeaponConfigs)
                    {
                        ItemDrop? itemdrop = item.Prefab.GetComponent<ItemDrop>();
                        ItemDrop.ItemData.SharedData? sharedData = itemdrop.m_itemData.m_shared;
                        string localizedName = Localization.instance.Localize(sharedData.m_name).Trim();
                        HitData.DamageTypes damages = sharedData.m_damages;
                        HitData.DamageTypes damagesPerLevel = sharedData.m_damagesPerLevel;
                        cfg.durability = config($"{localizedName}", "Max Durability", sharedData.m_maxDurability, $"Max Durability of {localizedName}");
                        cfg.durabilityPerLevel = config($"{localizedName}", "DurabilityPerLevel", sharedData.m_durabilityPerLevel, $"Durability per level of {localizedName}");
                        cfg.useDurabilityDrain = config($"{localizedName}", "Use Durability", sharedData.m_maxDurability, $"Use Durability of {localizedName}");
                        cfg.baseDamage = config($"{localizedName}", "Base Damage", damages.m_damage, $"Base damage of {localizedName}");
                        cfg.baseBlunt = config($"{localizedName}", "Base Blunt Damage", sharedData.m_toolTier, $"Base blunt damage of {localizedName}");
                        cfg.baseSlash = config($"{localizedName}", "Base Slash Damage", damages.m_slash, $"Base slash damage of {localizedName}");
                        cfg.basePierce = config($"{localizedName}", "Base Pierce Damage", damages.m_pierce, $"Base pierce damage of {localizedName}");
                        cfg.baseChop = config($"{localizedName}", "Base Chop Damage", damages.m_chop, $"Base chop damage of {localizedName}");
                        cfg.basePickaxe = config($"{localizedName}", "Base Pickaxe Damage", damages.m_pickaxe, "Base pickaxe damage of " + localizedName);
                        cfg.baseFire = config($"{localizedName}", "Base Fire Damage", damages.m_fire, "Base fire damage of " + localizedName);
                        cfg.baseFrost = config($"{localizedName}", "Base Frost Damage", damages.m_frost, "Base Frost damage of " + localizedName);
                        cfg.baseLightning = config($"{localizedName}", "Base Lightning Damage", damages.m_lightning, "Base Lightning damage of " + localizedName);
                        cfg.basePoison = config($"{localizedName}", "Base Poison Damage", damages.m_poison, "Base poison damage of " + localizedName);
                        cfg.baseSpirit = config($"{localizedName}", "Base Spirit Damage", damages.m_spirit, "Base spirit damage of " + localizedName);
                        cfg.baseDamagePerPerLevel = config($"{localizedName}", "Base Damage Per Level", damagesPerLevel.m_damage, "Base Damage Per Level of " + localizedName);
                        cfg.baseBluntPerLevel = config($"{localizedName}", "Base Blunt Damage Per Level", damagesPerLevel.m_blunt, "Base Blunt Damage Per Level of " + localizedName);
                        cfg.baseSlashPerLevel = config($"{localizedName}", "Base Slash Damage Per Level", damagesPerLevel.m_slash, "Base Slash Damage Per Level of " + localizedName);
                        cfg.basePiercePerLevel = config($"{localizedName}", "Base Pierce Damage Per Level", damagesPerLevel.m_pierce, "Base Pierce Damage Per Level of " + localizedName);
                        cfg.baseChopPerLevel = config($"{localizedName}", "Base Chop Damage Per Level", damagesPerLevel.m_chop, "Base Chop Damage Per Level of " + localizedName);
                        cfg.basePickaxePerLevel = config($"{localizedName}", "Base Pickaxe Damage Per Level", damagesPerLevel.m_pickaxe, "Base Pickaxe Damage Per Level of " + localizedName);
                        cfg.baseFirePerLevel = config($"{localizedName}", "Base Fire Damage Per Level", damagesPerLevel.m_fire, "Base Fire Damage Per Level of " + localizedName);
                        cfg.baseFrostPerLevel = config($"{localizedName}", "Base Frost Damage Per Level", damagesPerLevel.m_frost, "Base Frost Damage Per Level of " + localizedName);
                        cfg.baseLightningPerLevel = config($"{localizedName}", "Base Lightning Damage Per Level", damagesPerLevel.m_lightning, "Base Lightning Damage Per Level of " + localizedName);
                        cfg.basePoisonPerLevel = config($"{localizedName}", "Base Poison Damage Per Level", damagesPerLevel.m_poison, "Base Poison Damage Per Level of " + localizedName);
                        cfg.baseSpiritPerLevel = config($"{localizedName}", "Base Spirit Damage Per Level", damagesPerLevel.m_spirit, "Base Spirit Damage Per Level of " + localizedName);
                        cfg.baseAttackForce = config($"{localizedName}", "Base Attack Force (a.k.a Knockback)", sharedData.m_attackForce, "Base Attack Force (a.k.a Knockback) of " + localizedName);
                        cfg.baseBlockPower = config($"{localizedName}", "Base Block Power", sharedData.m_blockPower, "Base Block Power of " + localizedName);
                        cfg.baseParryForce = config($"{localizedName}", "Base Parry Force", sharedData.m_deflectionForce, "Base Parry Force of " + localizedName);
                        cfg.baseBackstab = config($"{localizedName}", "Base Backstab Bonus", sharedData.m_backstabBonus, "Base Backstab Bonus of " + localizedName);

                        cfg.durability.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.durabilityPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.useDurabilityDrain.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseDamage.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseDamage.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseBlunt.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseSlash.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePierce.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseChop.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePickaxe.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseFire.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseFrost.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseLightning.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePoison.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseSpirit.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseDamagePerPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseBluntPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseSlashPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePiercePerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseChopPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePickaxePerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseFirePerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseFrostPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseLightningPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.basePoisonPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseSpiritPerLevel.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseAttackForce.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseBlockPower.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseParryForce.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                        cfg.baseBackstab.SettingChanged += ((sender, e) => SetUpDamage(sender, e, itemdrop));
                    }

                    void SetUpDamage(object o, EventArgs e, ItemDrop itmdrop)
                    {
                        foreach (ItemDrop? weapon in Resources.FindObjectsOfTypeAll<ItemDrop>().Where(i =>
                                     i.m_itemData.m_shared.m_name == itmdrop.m_itemData.m_shared.m_name))
                        {
                            SetUpDamageValues(weapon.m_itemData);
                        }

                        if (Player.m_localPlayer != null)
                        {
                            foreach (ItemDrop.ItemData? invWeaponData in Player.m_localPlayer.GetInventory()
                                         .GetAllItems()
                                         .Where(i =>
                                             i.m_shared.m_name == itmdrop.m_itemData.m_shared.m_name))
                            {
                                SetUpDamageValues(invWeaponData);
                            }
                        }
                    }

                    void SetUpDamageValues(ItemDrop.ItemData weapon)
                    {
                        ItemDrop.ItemData.SharedData? sharedData = weapon.m_shared;
                        sharedData.m_maxDurability = cfg.durability.Value;
                        sharedData.m_durabilityPerLevel = cfg.durabilityPerLevel.Value;
                        sharedData.m_useDurabilityDrain = cfg.useDurabilityDrain.Value;
                        sharedData.m_damages.m_damage = cfg.baseDamage.Value;
                        sharedData.m_toolTier = cfg.baseBlunt.Value;
                        sharedData.m_damages.m_blunt = cfg.baseBlunt.Value;
                        sharedData.m_damages.m_slash = cfg.baseSlash.Value;
                        sharedData.m_damages.m_pierce = cfg.basePierce.Value;
                        sharedData.m_damages.m_chop = cfg.baseChop.Value;
                        sharedData.m_damages.m_pickaxe = cfg.basePickaxe.Value;
                        sharedData.m_damages.m_fire = cfg.baseFire.Value;
                        sharedData.m_damages.m_frost = cfg.baseFrost.Value;
                        sharedData.m_damages.m_lightning = cfg.baseLightning.Value;
                        sharedData.m_damages.m_poison = cfg.basePoison.Value;
                        sharedData.m_damages.m_spirit = cfg.baseSpirit.Value;
                        sharedData.m_damagesPerLevel.m_damage = cfg.baseDamagePerPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_blunt = cfg.baseBluntPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_slash = cfg.baseSlashPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_pierce = cfg.basePiercePerLevel.Value;
                        sharedData.m_damagesPerLevel.m_chop = cfg.baseChopPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_pickaxe = cfg.basePickaxePerLevel.Value;
                        sharedData.m_damagesPerLevel.m_fire = cfg.baseFirePerLevel.Value;
                        sharedData.m_damagesPerLevel.m_frost = cfg.baseFrostPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_lightning = cfg.baseLightningPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_poison = cfg.basePoisonPerLevel.Value;
                        sharedData.m_damagesPerLevel.m_spirit = cfg.baseSpiritPerLevel.Value;
                        sharedData.m_attackForce = cfg.baseAttackForce.Value;
                        sharedData.m_blockPower = cfg.baseBlockPower.Value;
                        sharedData.m_deflectionForce = cfg.baseParryForce.Value;
                        sharedData.m_backstabBonus = cfg.baseBackstab.Value;
                    }
                }
            }

            if (SaveOnConfigSet)
            {
                plugin.Config.SaveOnConfigSet = true;
                plugin.Config.Save();
            }
        }

        foreach (Item item in registeredItems)
        {
            foreach (KeyValuePair<string, ItemRecipe> kv in item.Recipes)
            {
                foreach (RequiredResourceList resourceList in new[]
                             { kv.Value.RequiredItems, kv.Value.RequiredUpgradeItems })
                {
                    for (int i = 0; i < resourceList.Requirements.Count; ++i)
                    {
                        if ((!ConfigurationEnabled || !item.Configurable) &&
                            resourceList.Requirements[i].amountConfig is { } amountCfg)
                        {
                            int resourceIndex = i;

                            void ConfigChanged(object o, EventArgs e)
                            {
                                if (ObjectDB.instance && activeRecipes.ContainsKey(item) &&
                                    activeRecipes[item].TryGetValue(kv.Key, out List<Recipe> recipes))
                                {
                                    foreach (Recipe recipe in recipes)
                                    {
                                        recipe.m_resources[resourceIndex].m_amount = amountCfg.Value;
                                    }
                                }
                            }

                            amountCfg.SettingChanged += ConfigChanged;
                        }
                    }
                }

                if (kv.Value.RecipeIsActive is { } enabledCfg)
                {
                    void ConfigChanged(object o, EventArgs e)
                    {
                        if (ObjectDB.instance && activeRecipes.ContainsKey(item) &&
                            activeRecipes[item].TryGetValue(kv.Key, out List<Recipe> recipes))
                        {
                            foreach (Recipe recipe in recipes)
                            {
                                recipe.m_enabled = (int)enabledCfg.BoxedValue != 0;
                            }
                        }
                    }

                    enabledCfg.GetType().GetEvent(nameof(ConfigEntry<int>.SettingChanged))
                        .AddEventHandler(enabledCfg, new EventHandler(ConfigChanged));
                }
            }
        }
    }

    [HarmonyPriority(Priority.Last)]
    internal static void Patch_ObjectDBInit(ObjectDB __instance)
    {
        if (__instance.GetItemPrefab("Wood") == null)
        {
            return;
        }

        foreach (Item item in registeredItems)
        {
            activeRecipes[item] = new Dictionary<string, List<Recipe>>();

            itemCraftConfigs.TryGetValue(item, out Dictionary<string, ItemConfig> cfgs);
            foreach (KeyValuePair<string, ItemRecipe> kv in item.Recipes)
            {
                List<Recipe> recipes = new();

                foreach (CraftingStationConfig station in kv.Value.Crafting.Stations)
                {
                    ItemConfig? cfg = cfgs?[kv.Key];

                    Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
                    recipe.name = $"{item.Prefab.name}_Recipe_{station.Table.ToString()}";
                    recipe.m_amount = item[kv.Key].CraftAmount;
                    recipe.m_enabled = true;
                    recipe.m_item = item.Prefab.GetComponent<ItemDrop>();
                    recipe.m_resources = SerializedRequirements.toPieceReqs(__instance,
                        cfg == null
                            ? new SerializedRequirements(item[kv.Key].RequiredItems.Requirements)
                            : new SerializedRequirements(cfg.craft.Value),
                        cfg == null
                            ? new SerializedRequirements(item[kv.Key].RequiredUpgradeItems.Requirements)
                            : new SerializedRequirements(cfg.upgrade?.Value ?? ""));
                    if ((cfg == null || recipes.Count > 0 ? station.Table : cfg.table.Value) is CraftingTable.None)
                    {
                        recipe.m_craftingStation = null;
                    }
                    else if ((cfg == null || recipes.Count > 0 ? station.Table : cfg.table.Value) is CraftingTable
                                 .Custom)
                    {
                        if (ZNetScene.instance.GetPrefab(cfg == null || recipes.Count > 0
                                ? station.custom
                                : cfg.customTable.Value) is { } craftingTable)
                        {
                            recipe.m_craftingStation = craftingTable.GetComponent<CraftingStation>();
                        }
                        else
                        {
                            Debug.LogWarning(
                                $"Custom crafting station '{(cfg == null || recipes.Count > 0 ? station.custom : cfg.customTable.Value)}' does not exist");
                        }
                    }
                    else
                    {
                        recipe.m_craftingStation = ZNetScene.instance
                            .GetPrefab(((InternalName)typeof(CraftingTable).GetMember(
                                    (cfg == null || recipes.Count > 0 ? station.Table : cfg.table.Value).ToString())[0]
                                .GetCustomAttributes(typeof(InternalName)).First()).internalName)
                            .GetComponent<CraftingStation>();
                    }

                    recipe.m_minStationLevel = cfg == null || recipes.Count > 0 ? station.level : cfg.tableLevel.Value;
                    recipe.m_enabled = (int)(kv.Value.RecipeIsActive?.BoxedValue ?? 1) != 0;

                    recipes.Add(recipe);
                }

                activeRecipes[item].Add(kv.Key, recipes);
                __instance.m_recipes.AddRange(recipes);
            }
        }
    }

    internal static void Patch_MaximumRequiredStationLevel(Recipe __instance, ref int __result, int quality)
    {
        if (itemDropMap.TryGetValue(__instance.m_item, out Item item))
        {
            IEnumerable<ItemConfig> configs;
            if (!itemCraftConfigs.TryGetValue(item, out Dictionary<string, ItemConfig> itemConfigs))
            {
                configs = Enumerable.Empty<ItemConfig>();
            }
            else if (Player.m_localPlayer.GetCurrentCraftingStation() is { } currentCraftingStation)
            {
                string stationName = Utils.GetPrefabName(currentCraftingStation.gameObject);
                configs = itemConfigs.Where(c => c.Value.table.Value switch
                {
                    CraftingTable.None => false,
                    CraftingTable.Custom => c.Value.customTable.Value == stationName,
                    _ => ((InternalName)typeof(CraftingTable).GetMember(c.Value.table.Value.ToString())[0]
                        .GetCustomAttributes(typeof(InternalName)).First()).internalName == stationName
                }).Select(c => c.Value);
            }
            else
            {
                configs = itemConfigs.Values;
            }

            __result = Mathf.Min(Mathf.Max(1, __instance.m_minStationLevel) + (quality - 1),
                configs.Where(cfg => cfg.maximumTableLevel is not null).Select(cfg => cfg.maximumTableLevel!.Value)
                    .DefaultIfEmpty(item.MaximumRequiredStationLevel).Max());
        }
    }

    private static bool CheckItemIsUpgrade(InventoryGui gui) => gui.m_selectedRecipe.Value?.m_quality > 0;

    internal static IEnumerable<CodeInstruction> Transpile_InventoryGui(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> instrs = instructions.ToList();
        FieldInfo amountField = AccessTools.DeclaredField(typeof(Recipe), nameof(Recipe.m_amount));
        for (int i = 0; i < instrs.Count; ++i)
        {
            yield return instrs[i];
            if (i > 1 && instrs[i - 2].opcode == OpCodes.Ldfld && instrs[i - 2].OperandIs(amountField) &&
                instrs[i - 1].opcode == OpCodes.Ldc_I4_1 && instrs[i].operand is Label)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.DeclaredMethod(typeof(Item), nameof(CheckItemIsUpgrade)));
                yield return new CodeInstruction(OpCodes.Brtrue, instrs[i].operand);
            }
        }
    }

    private static void drawConfigTable(ConfigEntryBase cfg)
    {
        bool locked = cfg.Description.Tags
            .Select(a =>
                a.GetType().Name == "ConfigurationManagerAttributes"
                    ? (bool?)a.GetType().GetField("ReadOnly")?.GetValue(a)
                    : null).FirstOrDefault(v => v != null) ?? false;

        List<Requirement> newReqs = new();
        bool wasUpdated = false;

        int RightColumnWidth =
            (int)(configManager?.GetType()
                .GetProperty("RightColumnWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetGetMethod(true)
                .Invoke(configManager, Array.Empty<object>()) ?? 130);

        GUILayout.BeginVertical();
        foreach (Requirement req in new SerializedRequirements((string)cfg.BoxedValue).Reqs)
        {
            GUILayout.BeginHorizontal();

            int amount = req.amount;
            if (int.TryParse(
                    GUILayout.TextField(amount.ToString(), new GUIStyle(GUI.skin.textField) { fixedWidth = 40 }),
                    out int newAmount) && newAmount != amount && !locked)
            {
                amount = newAmount;
                wasUpdated = true;
            }

            string newItemName = GUILayout.TextField(req.itemName,
                new GUIStyle(GUI.skin.textField) { fixedWidth = RightColumnWidth - 40 - 21 - 21 - 9 });
            string itemName = locked ? req.itemName : newItemName;
            wasUpdated = wasUpdated || itemName != req.itemName;

            if (GUILayout.Button("x", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
            {
                wasUpdated = true;
            }
            else
            {
                newReqs.Add(new Requirement { amount = amount, itemName = itemName });
            }

            if (GUILayout.Button("+", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
            {
                wasUpdated = true;
                newReqs.Add(new Requirement { amount = 1, itemName = "" });
            }

            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();

        if (wasUpdated)
        {
            cfg.BoxedValue = new SerializedRequirements(newReqs).ToString();
        }
    }

    private class SerializedRequirements
    {
        public readonly List<Requirement> Reqs;

        public SerializedRequirements(List<Requirement> reqs) => Reqs = reqs;

        public SerializedRequirements(string reqs)
        {
            Reqs = reqs.Split(',').Select(r =>
            {
                string[] parts = r.Split(':');
                return new Requirement
                {
                    itemName = parts[0],
                    amount = parts.Length > 1 && int.TryParse(parts[1], out int amount) ? amount : 1
                };
            }).ToList();
        }

        public override string ToString()
        {
            return string.Join(",", Reqs.Select(r => $"{r.itemName}:{r.amount}"));
        }

        public static Piece.Requirement[] toPieceReqs(ObjectDB objectDB, SerializedRequirements craft,
            SerializedRequirements upgrade)
        {
            ItemDrop? ResItem(Requirement r)
            {
                ItemDrop? item = objectDB.GetItemPrefab(r.itemName)?.GetComponent<ItemDrop>();
                if (item == null)
                {
                    Debug.LogWarning($"The required item '{r.itemName}' does not exist.");
                }

                return item;
            }

            Dictionary<string, Piece.Requirement?> resources = craft.Reqs.Where(r => r.itemName != "")
                .ToDictionary(r => r.itemName,
                    r => ResItem(r) is { } item
                        ? new Piece.Requirement
                            { m_amount = r.amountConfig?.Value ?? r.amount, m_resItem = item, m_amountPerLevel = 0 }
                        : null);
            foreach (Requirement req in upgrade.Reqs.Where(r => r.itemName != ""))
            {
                if ((!resources.TryGetValue(req.itemName, out Piece.Requirement? requirement) || requirement == null) &&
                    ResItem(req) is { } item)
                {
                    requirement = resources[req.itemName] = new Piece.Requirement { m_resItem = item, m_amount = 0 };
                }

                if (requirement != null)
                {
                    requirement.m_amountPerLevel = req.amountConfig?.Value ?? req.amount;
                }
            }

            return resources.Values.Where(v => v != null).ToArray()!;
        }
    }

    private static Localization? _english;

    private static Localization english
    {
        get
        {
            if (_english == null)
            {
                _english = new Localization();
                _english.SetupLanguage("English");
            }

            return _english;
        }
    }

    private static BaseUnityPlugin? _plugin;

    private static BaseUnityPlugin plugin
    {
        get
        {
            if (_plugin is null)
            {
                IEnumerable<TypeInfo> types;
                try
                {
                    types = Assembly.GetExecutingAssembly().DefinedTypes.ToList();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).Select(t => t.GetTypeInfo());
                }

                _plugin = (BaseUnityPlugin)BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(types.First(t =>
                    t.IsClass && typeof(BaseUnityPlugin).IsAssignableFrom(t)));
            }

            return _plugin;
        }
    }

    private static bool hasConfigSync = true;
    private static object? _configSync;

    private static object? configSync
    {
        get
        {
            if (_configSync == null && hasConfigSync)
            {
                if (Assembly.GetExecutingAssembly().GetType("ServerSync.ConfigSync") is { } configSyncType)
                {
                    _configSync = Activator.CreateInstance(configSyncType, plugin.Info.Metadata.GUID + " ItemManager");
                    configSyncType.GetField("CurrentVersion")
                        .SetValue(_configSync, plugin.Info.Metadata.Version.ToString());
                    configSyncType.GetProperty("IsLocked")!.SetValue(_configSync, true);
                }
                else
                {
                    hasConfigSync = false;
                }
            }

            return _configSync;
        }
    }

    private static ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
    {
        ConfigEntry<T> configEntry = plugin.Config.Bind(group, name, value, description);

        configSync?.GetType().GetMethod("AddConfigEntry")!.MakeGenericMethod(typeof(T))
            .Invoke(configSync, new object[] { configEntry });

        return configEntry;
    }

    private static ConfigEntry<T> config<T>(string group, string name, T value, string description) =>
        config(group, name, value, new ConfigDescription(description));
}

[PublicAPI]
public class LocalizeKey
{
    private static readonly List<LocalizeKey> keys = new();

    public readonly string Key;
    public readonly Dictionary<string, string> Localizations = new();

    public LocalizeKey(string key) => Key = key.Replace("$", "");

    public void Alias(string alias)
    {
        Localizations.Clear();
        if (!alias.Contains("$"))
        {
            alias = $"${alias}";
        }

        Localizations["alias"] = alias;
        Localization.instance.AddWord(Key, Localization.instance.Localize(alias));
    }

    public LocalizeKey English(string key) => addForLang("English", key);
    public LocalizeKey Swedish(string key) => addForLang("Swedish", key);
    public LocalizeKey French(string key) => addForLang("French", key);
    public LocalizeKey Italian(string key) => addForLang("Italian", key);
    public LocalizeKey German(string key) => addForLang("German", key);
    public LocalizeKey Spanish(string key) => addForLang("Spanish", key);
    public LocalizeKey Russian(string key) => addForLang("Russian", key);
    public LocalizeKey Romanian(string key) => addForLang("Romanian", key);
    public LocalizeKey Bulgarian(string key) => addForLang("Bulgarian", key);
    public LocalizeKey Macedonian(string key) => addForLang("Macedonian", key);
    public LocalizeKey Finnish(string key) => addForLang("Finnish", key);
    public LocalizeKey Danish(string key) => addForLang("Danish", key);
    public LocalizeKey Norwegian(string key) => addForLang("Norwegian", key);
    public LocalizeKey Icelandic(string key) => addForLang("Icelandic", key);
    public LocalizeKey Turkish(string key) => addForLang("Turkish", key);
    public LocalizeKey Lithuanian(string key) => addForLang("Lithuanian", key);
    public LocalizeKey Czech(string key) => addForLang("Czech", key);
    public LocalizeKey Hungarian(string key) => addForLang("Hungarian", key);
    public LocalizeKey Slovak(string key) => addForLang("Slovak", key);
    public LocalizeKey Polish(string key) => addForLang("Polish", key);
    public LocalizeKey Dutch(string key) => addForLang("Dutch", key);
    public LocalizeKey Portuguese_European(string key) => addForLang("Portuguese_European", key);
    public LocalizeKey Portuguese_Brazilian(string key) => addForLang("Portuguese_Brazilian", key);
    public LocalizeKey Chinese(string key) => addForLang("Chinese", key);
    public LocalizeKey Japanese(string key) => addForLang("Japanese", key);
    public LocalizeKey Korean(string key) => addForLang("Korean", key);
    public LocalizeKey Hindi(string key) => addForLang("Hindi", key);
    public LocalizeKey Thai(string key) => addForLang("Thai", key);
    public LocalizeKey Abenaki(string key) => addForLang("Abenaki", key);
    public LocalizeKey Croatian(string key) => addForLang("Croatian", key);
    public LocalizeKey Georgian(string key) => addForLang("Georgian", key);
    public LocalizeKey Greek(string key) => addForLang("Greek", key);
    public LocalizeKey Serbian(string key) => addForLang("Serbian", key);
    public LocalizeKey Ukrainian(string key) => addForLang("Ukrainian", key);

    private LocalizeKey addForLang(string lang, string value)
    {
        Localizations[lang] = value;
        if (Localization.instance.GetSelectedLanguage() == lang)
        {
            Localization.instance.AddWord(Key, value);
        }
        else if (lang == "English" && !Localization.instance.m_translations.ContainsKey(Key))
        {
            Localization.instance.AddWord(Key, value);
        }

        return this;
    }

    [HarmonyPriority(Priority.LowerThanNormal)]
    internal static void AddLocalizedKeys(Localization __instance, string language)
    {
        foreach (LocalizeKey key in keys)
        {
            if (key.Localizations.TryGetValue(language, out string Translation) ||
                key.Localizations.TryGetValue("English", out Translation))
            {
                __instance.AddWord(key.Key, Translation);
            }
            else if (key.Localizations.TryGetValue("alias", out string alias))
            {
                Localization.instance.AddWord(key.Key, Localization.instance.Localize(alias));
            }
        }
    }
}

[PublicAPI]
public static class PrefabManager
{
    static PrefabManager()
    {
        Harmony harmony = new("org.bepinex.helpers.ItemManager");
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PrefabManager), nameof(Patch_ObjectDBInit))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PrefabManager), nameof(Patch_ObjectDBInit))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Item), nameof(Item.Patch_ObjectDBInit))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Item), nameof(Item.Patch_ObjectDBInit))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Item), nameof(Item.Patch_FejdStartup))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PrefabManager), nameof(Patch_ZNetSceneAwake))));
        /*harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Awake)),
            postfix:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PrefabManager), nameof(Patch_ZNetSceneFXFix))));*/
        harmony.Patch(AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe)),
            transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Item),
                nameof(Item.Transpile_InventoryGui))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Recipe), nameof(Recipe.GetRequiredStationLevel)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Item),
                nameof(Item.Patch_MaximumRequiredStationLevel))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Localization), nameof(Localization.LoadCSV)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocalizeKey),
                nameof(LocalizeKey.AddLocalizedKeys))));
    }

    private struct BundleId
    {
        [UsedImplicitly] public string assetBundleFileName;
        [UsedImplicitly] public string folderName;
    }

    private static readonly Dictionary<BundleId, AssetBundle> bundleCache = new();

    public static AssetBundle RegisterAssetBundle(string assetBundleFileName, string folderName = "assets")
    {
        BundleId id = new() { assetBundleFileName = assetBundleFileName, folderName = folderName };
        if (!bundleCache.TryGetValue(id, out AssetBundle assets))
        {
            assets = bundleCache[id] =
                Resources.FindObjectsOfTypeAll<AssetBundle>().FirstOrDefault(a => a.name == assetBundleFileName) ??
                AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(Assembly.GetExecutingAssembly().GetName().Name + $".{folderName}." +
                                               assetBundleFileName));
        }

        return assets;
    }

    private static readonly List<GameObject> prefabs = new();
    private static readonly List<GameObject> ZnetOnlyPrefabs = new();

    public static GameObject
        RegisterPrefab(string assetBundleFileName, string prefabName, string folderName = "assets") =>
        RegisterPrefab(RegisterAssetBundle(assetBundleFileName, folderName), prefabName);

    public static GameObject RegisterPrefab(AssetBundle assets, string prefabName, bool addToObjectDb = false) =>
        RegisterPrefab(assets.LoadAsset<GameObject>(prefabName), addToObjectDb);

    public static GameObject RegisterPrefab(GameObject prefab, bool addToObjectDb = false)
    {
        if (addToObjectDb)
        {
            prefabs.Add(prefab);
        }
        else
        {
            ZnetOnlyPrefabs.Add(prefab);
        }

        return prefab;
    }

    [HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_ObjectDBInit(ObjectDB __instance)
    {
        foreach (GameObject prefab in prefabs)
        {
            if (!__instance.m_items.Contains(prefab))
            {
                __instance.m_items.Add(prefab);
            }

            void RegisterStatusEffect(StatusEffect? statusEffect)
            {
                if (statusEffect is not null && !__instance.GetStatusEffect(statusEffect.name))
                {
                    __instance.m_StatusEffects.Add(statusEffect);
                }
            }

            ItemDrop.ItemData.SharedData shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            RegisterStatusEffect(shared.m_attackStatusEffect);
            RegisterStatusEffect(shared.m_consumeStatusEffect);
            RegisterStatusEffect(shared.m_equipStatusEffect);
            RegisterStatusEffect(shared.m_setStatusEffect);
        }

        __instance.UpdateItemHashes();
    }

    [HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_ZNetSceneAwake(ZNetScene __instance)
    {
        foreach (GameObject prefab in prefabs.Concat(ZnetOnlyPrefabs))
        {
            __instance.m_prefabs.Add(prefab);
        }
        
        
    }
    
    /*[HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_ZNetSceneFXFix(ZNetScene __instance)
    {
        foreach (GameObject prefab in prefabs.Concat(ZnetOnlyPrefabs))
        {
            if (prefab.GetComponent<ItemDrop>())
            {
                EffectList? hitEffect = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_hitEffect;
                EffectList? blockEffect = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_blockEffect;
                hitEffect.m_effectPrefabs[0].m_prefab = __instance.GetPrefab("vfx_HitSparks");
                hitEffect.m_effectPrefabs[1].m_prefab = __instance.GetPrefab("sfx_sword_hit");
                hitEffect.m_effectPrefabs[2].m_prefab = __instance.GetPrefab("fx_hitcamshake");
                
                blockEffect.m_effectPrefabs[0].m_prefab = __instance.GetPrefab("sfx_metal_blocked");
                blockEffect.m_effectPrefabs[1].m_prefab = __instance.GetPrefab("vfx_blocked");
                blockEffect.m_effectPrefabs[2].m_prefab = __instance.GetPrefab("fx_block_camshake");
                
                prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_hitEffect = hitEffect;
                prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_blockEffect = blockEffect;
            }
        }
        
        
    }*/
}