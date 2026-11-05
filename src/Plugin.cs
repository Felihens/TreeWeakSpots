using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TreeWeakSpots
{
    public enum SpotColorOption { Blue, Gold, Green }
    public enum CritSoundOption { Auto, File, Game, Mod, None }
    public enum CritNumberColorOption { Yellow, Red, Normal }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "felih.TreeWeakSpots";
        public const string NAME = "Tree Weak Spots";
        public const string VERSION = "0.9.0";

        public static ManualLogSource Log;

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> CritMultiplier;
        public static ConfigEntry<int> SpotCount;
        public static ConfigEntry<float> HitTolerance;
        public static ConfigEntry<float> SpotLifetime;
        public static ConfigEntry<float> MaxDistance;
        public static ConfigEntry<float> ActiveDuration;
        public static ConfigEntry<float> MinSpacing;
        public static ConfigEntry<SpotColorOption> SpotColor;
        public static ConfigEntry<float> BandWidth;
        public static ConfigEntry<float> SoundVolume;
        public static ConfigEntry<CritSoundOption> CritSound;
        public static ConfigEntry<CritNumberColorOption> CritNumberColor;
        public static ConfigEntry<float> CritNumberScale;
        public static ConfigEntry<bool> SledgeSwing;
        public static ConfigEntry<string> SwingSource;
        public static ConfigEntry<bool> BlockComboChopping;
        public static ConfigEntry<bool> DebugLog;

        private const string ComboChoppingId = "diffByDefault.ComboChopping";
        private Harmony _harmony;

        // Start runs after every plugin's Awake, so ComboChopping's patches are already in place.
        private void Start()
        {
            if (!BlockComboChopping.Value) return;
            try
            {
                System.Reflection.MethodInfo damage = AccessTools.Method(typeof(TreeLog), "Damage");
                Patches info = Harmony.GetPatchInfo(damage);
                if (info == null || !info.Owners.Contains(ComboChoppingId)) return;
                // ComboChopping marks the swing as a "tree hit" from this prefix: without it on logs, its
                // damage bonus and combo handling stay on standing trees only.
                _harmony.Unpatch(damage, HarmonyPatchType.All, ComboChoppingId);
                Log.LogInfo("ComboChopping turned off on fallen logs (still active on standing trees).");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Could not adjust ComboChopping: " + e.Message);
            }
        }

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("1 - General", "Enabled", true, "Turns the mod on or off.");
            CritMultiplier = Config.Bind("1 - General", "CritMultiplier", 2f,
                new ConfigDescription("Axe damage multiplier when you hit a band.",
                    new AcceptableValueRange<float>(1f, 10f)));

            SpotCount = Config.Bind("2 - Weak spots", "Count", 1,
                new ConfigDescription("How many glowing bands appear on the fallen log at the same time.",
                    new AcceptableValueRange<int>(1, 4)));
            MinSpacing = Config.Bind("2 - Weak spots", "MinSpacing", 1.2f,
                new ConfigDescription("Minimum distance (meters) between two bands.",
                    new AcceptableValueRange<float>(0.3f, 5f)));
            HitTolerance = Config.Bind("2 - Weak spots", "HitTolerance", 0.35f,
                new ConfigDescription("Maximum distance (meters) along the log between the hit and the band's center for a crit.",
                    new AcceptableValueRange<float>(0.1f, 2f)));
            SpotLifetime = Config.Bind("2 - Weak spots", "SpotLifetime", 5f,
                new ConfigDescription("Seconds each band stays visible before it moves.",
                    new AcceptableValueRange<float>(1f, 30f)));
            MaxDistance = Config.Bind("2 - Weak spots", "MaxDistance", 8f,
                new ConfigDescription("Maximum distance (meters) from your eyes where a band can appear.",
                    new AcceptableValueRange<float>(2f, 20f)));
            ActiveDuration = Config.Bind("2 - Weak spots", "IdleTimeout", 3f,
                new ConfigDescription("Seconds without aiming at or hitting the log before the bands disappear.",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            SpotColor = Config.Bind("3 - Visuals", "Color", SpotColorOption.Gold, "Band color.");
            BandWidth = Config.Bind("3 - Visuals", "BandWidth", 0.3f,
                new ConfigDescription("Width of the glowing band, in meters.", new AcceptableValueRange<float>(0.1f, 1f)));
            SoundVolume = Config.Bind("3 - Visuals", "SoundVolume", 1f,
                new ConfigDescription("Crit sound volume (0 = off).", new AcceptableValueRange<float>(0f, 1f)));
            CritSound = Config.Bind("3 - Visuals", "CritSound", CritSoundOption.Auto,
                "Auto = a file from the mod's SFX folder if there is one, otherwise the game sound. " +
                "File = only .wav/.ogg files from BepInEx/plugins/TreeWeakSpots/SFX (one at random). " +
                "Game = Valheim's sound for hitting a staggered enemy. Mod = the mod's own chime. None = no sound.");
            CritNumberColor = Config.Bind("3 - Visuals", "CritNumberColor", CritNumberColorOption.Yellow,
                "Color of a crit's damage number. Normal = the game's default color.");
            CritNumberScale = Config.Bind("3 - Visuals", "CritNumberSize", 2f,
                new ConfigDescription("Size of a crit's damage number (2 = double, 1 = unchanged).",
                    new AcceptableValueRange<float>(1f, 4f)));

            SledgeSwing = Config.Bind("4 - Swing", "SledgeSwing", true,
                "Axe swings aimed at a fallen log use the sledgehammer's overhead strike. " +
                "With a shield, torch or any other item in the off hand, the whole mod stays off. " +
                "Everywhere else (standing trees, combat) the axe works as usual.");
            SwingSource = Config.Bind("4 - Swing", "SwingSource", "SledgeStagbreaker",
                "Name of the item the swing animation is copied from (default: the Stagbreaker sledge).");

            BlockComboChopping = Config.Bind("4 - Swing", "ComboChoppingStandingTreesOnly", true,
                "With the ComboChopping mod installed, it keeps working on standing trees but no longer changes " +
                "the damage and combo on fallen logs, where only Tree Weak Spots applies. Restart the game after changing this.");

            DebugLog = Config.Bind("9 - Debug", "DetailedLog", false,
                "Writes each hit, the log's size and the swing animation swap to the BepInEx log.");

            GameObject go = new GameObject("TreeWeakSpots_Manager");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<WeakSpotManager>();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(TreeLog), "Damage")]
    internal static class TreeLogDamagePatch
    {
        private static void Prefix(TreeLog __instance, HitData __0)
        {
            WeakSpotManager.OnAxeHit(__instance, __0);
        }
    }

    /// While the local player aims at a fallen log with an axe, the swing borrows the sledge's
    /// overhead animation. Humanoid.StartAttack clones the weapon's Attack right away, so the shared
    /// data is changed only for the duration of that call and restored in the postfix.
    [HarmonyPatch]
    internal static class SledgeSwingPatch
    {
        // Found by name so a changed parameter list in a game update doesn't break patching.
        private static System.Reflection.MethodBase TargetMethod()
        {
            foreach (System.Reflection.MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Humanoid)))
                if (m.Name == "StartAttack") return m;
            return null;
        }

        private class Saved
        {
            public Attack Attack;
            public string Animation;
            public int Chain;
            public int Random;
            public Attack.AttackType Type;
        }

        private static string _anim;
        private static int _chain = 1;
        private static int _random;
        private static bool _looked;
        private static float _lastLog = -10f;

        private static void FindSledge()
        {
            if (_looked || ObjectDB.instance == null) return;
            _looked = true;
            GameObject prefab = ObjectDB.instance.GetItemPrefab(Plugin.SwingSource.Value);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null && drop.m_itemData.m_shared.m_attack != null)
            {
                Attack a = drop.m_itemData.m_shared.m_attack;
                _anim = a.m_attackAnimation;
                _chain = a.m_attackChainLevels;
                _random = a.m_attackRandomAnimations;
            }
            Plugin.Log.LogInfo("Sledge animation: " + (_anim ?? "not found") + " (combo " + _chain + ")");
        }

        private static void Prefix(Humanoid __instance, ref Saved __state)
        {
            __state = null;
            if (!Plugin.Enabled.Value || !Plugin.SledgeSwing.Value) return;
            Player p = __instance as Player;
            if (p == null || p != Player.m_localPlayer) return;
            // ModActive: axe in hand and off hand free (shield/torch turn the whole mod off).
            if (!WeakSpotManager.ModActive(p) || !WeakSpotManager.CanUseSledgeSwing(p)) return;
            FindSledge();
            if (string.IsNullOrEmpty(_anim)) return;

            Attack atk = p.GetCurrentWeapon().m_shared.m_attack;
            if (atk == null) return;
            __state = new Saved { Attack = atk, Animation = atk.m_attackAnimation, Chain = atk.m_attackChainLevels,
                                  Random = atk.m_attackRandomAnimations, Type = atk.m_attackType };
            atk.m_attackAnimation = _anim;
            atk.m_attackChainLevels = _chain;
            atk.m_attackRandomAnimations = _random;
            atk.m_attackType = Attack.AttackType.Vertical;   // hits along the overhead arc, down onto the log
            // StartAttack is called every frame while the button is held: log once per swing.
            if (Plugin.DebugLog.Value && Time.time - _lastLog > 0.8f)
            {
                _lastLog = Time.time;
                Plugin.Log.LogInfo("Sledge swing on log (" + _anim + ")");
            }
        }

        private static void Postfix(Saved __state)
        {
            if (__state == null) return;
            __state.Attack.m_attackAnimation = __state.Animation;
            __state.Attack.m_attackChainLevels = __state.Chain;
            __state.Attack.m_attackRandomAnimations = __state.Random;
            __state.Attack.m_attackType = __state.Type;
        }
    }

    // Damage numbers arrive by RPC shortly after our hit (the log's owner sends them).
    // Any number that pops up near a crit we just landed gets the crit color and size.
    [HarmonyPatch(typeof(DamageText), "AddInworldText")]
    internal static class CritNumberPatch
    {
        private static System.Reflection.FieldInfo _worldTexts;
        private static System.Reflection.FieldInfo _textField;

        private static void Postfix(DamageText __instance, Vector3 pos, string text)
        {
            try
            {
                bool crit = WeakSpotManager.IsRecentCrit(pos);
                if (Plugin.DebugLog.Value && (crit || WeakSpotManager.IsRecentHit(pos)))
                    Plugin.Log.LogInfo("On-screen number: " + text + (crit ? " (crit)" : ""));
                if (!crit) return;
                if (_worldTexts == null) _worldTexts = AccessTools.Field(typeof(DamageText), "m_worldTexts");
                System.Collections.IList list = _worldTexts.GetValue(__instance) as System.Collections.IList;
                if (list == null || list.Count == 0) return;
                object inst = list[list.Count - 1];
                if (_textField == null) _textField = AccessTools.Field(inst.GetType(), "m_textField");
                object tmp = _textField.GetValue(inst);
                if (tmp == null) return;

                System.Type t = tmp.GetType();
                if (Plugin.CritNumberColor.Value != CritNumberColorOption.Normal)
                {
                    Color c = Plugin.CritNumberColor.Value == CritNumberColorOption.Red
                        ? new Color(1f, 0.18f, 0.12f) : new Color(1f, 0.82f, 0.1f);
                    t.GetProperty("color").SetValue(tmp, c, null);
                }
                System.Reflection.PropertyInfo fs = t.GetProperty("fontSize");
                float size = (float)fs.GetValue(tmp, null);
                fs.SetValue(tmp, size * Plugin.CritNumberScale.Value, null);
            }
            catch (System.Exception e)
            {
                if (Plugin.DebugLog.Value) Plugin.Log.LogWarning("CritNumberPatch: " + e.Message);
            }
        }
    }
}
