using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TreeWeakSpots
{
    public enum SpotColorOption { Azul, Dourado, Verde }
    public enum CritSoundOption { Automatico, Arquivo, Jogo, Mod, Nenhum }
    public enum CritNumberColorOption { Amarelo, Vermelho, Normal }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "felih.TreeWeakSpots";
        public const string NAME = "Tree Weak Spots";
        public const string VERSION = "0.5.0";

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
                Log.LogInfo("ComboChopping desligado nos troncos caídos (continua nas árvores em pé).");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Não consegui ajustar o ComboChopping: " + e.Message);
            }
        }

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("1 - Geral", "Ativado", true, "Liga ou desliga o mod.");
            CritMultiplier = Config.Bind("1 - Geral", "MultiplicadorCritico", 2f,
                new ConfigDescription("Multiplicador de dano do machado ao acertar uma faixa.",
                    new AcceptableValueRange<float>(1f, 10f)));

            SpotCount = Config.Bind("2 - Pontos fracos", "Quantidade", 1,
                new ConfigDescription("Quantas faixas brilhantes aparecem ao mesmo tempo no tronco caído.",
                    new AcceptableValueRange<int>(1, 4)));
            MinSpacing = Config.Bind("2 - Pontos fracos", "DistanciaEntrePontos", 1.2f,
                new ConfigDescription("Distância mínima (metros) entre duas faixas.",
                    new AcceptableValueRange<float>(0.3f, 5f)));
            HitTolerance = Config.Bind("2 - Pontos fracos", "ToleranciaDeAcerto", 0.35f,
                new ConfigDescription("Distância máxima (metros), ao longo do tronco, entre o golpe e o centro da faixa para contar como crítico.",
                    new AcceptableValueRange<float>(0.1f, 2f)));
            SpotLifetime = Config.Bind("2 - Pontos fracos", "DuracaoDoPonto", 5f,
                new ConfigDescription("Segundos que cada faixa fica visível antes de mudar de lugar.",
                    new AcceptableValueRange<float>(1f, 30f)));
            MaxDistance = Config.Bind("2 - Pontos fracos", "DistanciaMaxima", 8f,
                new ConfigDescription("Distância máxima (metros) dos seus olhos em que uma faixa pode surgir.",
                    new AcceptableValueRange<float>(2f, 20f)));
            ActiveDuration = Config.Bind("2 - Pontos fracos", "TempoSemMirar", 3f,
                new ConfigDescription("Segundos sem mirar nem golpear o tronco até as faixas sumirem.",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            SpotColor = Config.Bind("3 - Visual", "Cor", SpotColorOption.Dourado, "Cor das faixas.");
            BandWidth = Config.Bind("3 - Visual", "LarguraDaFaixa", 0.3f,
                new ConfigDescription("Largura da faixa brilhante, em metros.", new AcceptableValueRange<float>(0.1f, 1f)));
            SoundVolume = Config.Bind("3 - Visual", "VolumeDoSom", 1f,
                new ConfigDescription("Volume do som de crítico (0 desliga).", new AcceptableValueRange<float>(0f, 1f)));
            CritSound = Config.Bind("3 - Visual", "SomDoCritico", CritSoundOption.Automatico,
                "Automatico = arquivo da pasta SFX do mod se houver, senão o som do jogo. " +
                "Arquivo = só arquivos .wav/.ogg da pasta BepInEx/plugins/TreeWeakSpots/SFX (sorteia um). " +
                "Jogo = som do Valheim ao acertar inimigo de postura quebrada. Mod = o \"tim\" do mod. Nenhum = sem som.");
            CritNumberColor = Config.Bind("3 - Visual", "CorDoNumeroCritico", CritNumberColorOption.Amarelo,
                "Cor do número de dano de um golpe crítico. Normal = cor padrão do jogo.");
            CritNumberScale = Config.Bind("3 - Visual", "TamanhoDoNumeroCritico", 2f,
                new ConfigDescription("Tamanho do número de dano de um golpe crítico (2 = o dobro do normal, 1 = sem mudança).",
                    new AcceptableValueRange<float>(1f, 4f)));

            SledgeSwing = Config.Bind("4 - Golpe", "GolpeDeMarreta", true,
                "Depois do primeiro golpe normal num tronco caído, os próximos golpes nele viram a martelada de cima para baixo da marreta. " +
                "Com escudo, tocha ou outro item na mão secundária o mod inteiro fica desligado. " +
                "No resto do tempo (árvores em pé, combate) o machado continua normal.");
            SwingSource = Config.Bind("4 - Golpe", "ArmaDaAnimacao", "SledgeStagbreaker",
                "Nome do item de onde a animação é copiada (padrão: a marreta Stagbreaker).");

            BlockComboChopping = Config.Bind("4 - Golpe", "ComboChoppingSoEmArvoresEmPe", true,
                "Se o mod ComboChopping estiver instalado, ele continua valendo nas árvores em pé, mas deixa de mexer " +
                "no dano e no combo dos troncos caídos, onde vale só o Tree Weak Spots. Precisa reiniciar o jogo para mudar.");

            DebugLog = Config.Bind("9 - Debug", "LogDetalhado", false,
                "Escreve no log do BepInEx cada golpe, o tamanho do tronco e a troca de animação.");

            GameObject go = new GameObject("TreeWeakSpots_Manager");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<WeakSpotManager>();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo(NAME + " " + VERSION + " carregado.");
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
            Plugin.Log.LogInfo("Animação da marreta: " + (_anim ?? "não encontrada") + " (combo " + _chain + ")");
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
                Plugin.Log.LogInfo("Golpe de marreta no tronco (" + _anim + ")");
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
                    Plugin.Log.LogInfo("Número na tela: " + text + (crit ? " (crítico)" : ""));
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
                    Color c = Plugin.CritNumberColor.Value == CritNumberColorOption.Vermelho
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
