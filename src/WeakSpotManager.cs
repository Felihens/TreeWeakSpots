using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TreeWeakSpots
{
    /// Tracks the fallen log the local player is chopping, puts glowing bands ("slices") around it
    /// and turns axe hits that land on a band into critical hits.
    public class WeakSpotManager : MonoBehaviour
    {
        /// Field lookup that walks base classes and returns null quietly (AccessTools.Field logs a
        /// warning every time a field is missing, which flooded the BepInEx log).
        public static System.Reflection.FieldInfo FindField(System.Type t, string name)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            for (; t != null; t = t.BaseType)
            {
                System.Reflection.FieldInfo f = t.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }

        private static WeakSpotManager _inst;

        private TreeLog _target;
        private float _lastSeenTime;
        private float _nextScan;
        private readonly List<LogRing> _rings = new List<LogRing>();
        private int _critFrame = -1;

        // Shape of the target log, in the local space of its mesh.
        private Transform _meshT;
        private Vector3 _centerLocal;
        private Vector3 _axisLocal;
        private float _halfLenLocal;
        private float _radiusLocal;

        private Texture2D _glowTex;
        private Texture2D _bandTex;
        private Mesh _quadMesh;
        private Shader _shader;
        private AudioClip _critClip;

        public Transform MeshTransform { get { return _meshT; } }
        public Shader BandShader { get { return _shader; } }
        public Texture2D BandTexture { get { return _bandTex; } }

        private void Awake()
        {
            _inst = this;
            _glowTex = MakeGlowTexture(64);
            _bandTex = MakeBandTexture(64);
            _quadMesh = MakeQuadMesh();
            string[] shaderNames = { "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Unlit/Transparent", "UI/Default" };
            for (int i = 0; i < shaderNames.Length && _shader == null; i++)
            {
                _shader = Shader.Find(shaderNames[i]);
                if (_shader != null) Plugin.Log.LogInfo("Shader usado: " + shaderNames[i]);
            }
            if (_shader == null) Plugin.Log.LogWarning("Nenhum shader transparente encontrado; só a luz da faixa será visível.");
            _critClip = MakeCritClip();
            StartCoroutine(LoadSoundFiles());
        }

        // ---------------------------------------------------------------- hits

        public static void OnAxeHit(TreeLog log, HitData hit)
        {
            if (_inst == null || hit == null || log == null) return;
            if (!Plugin.Enabled.Value) return;
            Player player = Player.m_localPlayer;
            if (player == null) return;
            Character attacker = hit.GetAttacker();
            string why = null;
            if (attacker != player) why = "golpe não é do jogador local";
            // Axe hits on trees and logs train WoodCutting, so that's the skill the hit carries.
            else if (hit.m_skill != Skills.SkillType.WoodCutting && hit.m_skill != Skills.SkillType.Axes)
                why = "ferramenta não é machado (" + hit.m_skill + ")";
            else if (!HoldingAxe(player)) why = "não está com machado na mão";
            else if (!ModActive(player)) why = "mão secundária ocupada (" + OffHandItem(player).m_shared.m_name + ")";
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo("Golpe no tronco " + log.name + ": " + (why == null ? "aceito" : "ignorado, " + why));
            if (why != null) return;
            _swingLog = log;
            _swingLogPos = log.transform.position;
            _swingLogGoneAt = -1f;
            _inst.HandleHit(log, hit);
        }

        // Last log the player hit with the axe. The sledge swing is used only on this one, so the
        // first hit on every log is the normal axe swing. When that log splits, the pieces that
        // appear where it was inherit the sledge swing.
        private static TreeLog _swingLog;
        private static Vector3 _swingLogPos;
        private static float _swingLogGoneAt = -1f;
        private const float SplitRadius = 12f;     // pieces spawn along the old log
        private const float SplitWindow = 60f;     // seconds after the split

        public static bool CanUseSledgeSwing(Player player)
        {
            TreeLog aimed = LogUnderCrosshair(player);
            if (aimed == null) return false;
            if (_swingLog != null) return aimed == _swingLog;

            // The log we were chopping is gone: was it split, and is this one of its pieces?
            if ((object)_swingLog != null && _swingLogGoneAt < 0f) _swingLogGoneAt = Time.time;
            if (_swingLogGoneAt < 0f || Time.time - _swingLogGoneAt > SplitWindow) return false;
            if (Vector3.Distance(aimed.transform.position, _swingLogPos) > SplitRadius) return false;
            _swingLog = aimed;
            _swingLogPos = aimed.transform.position;
            _swingLogGoneAt = -1f;
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo("Tora do tronco partido herdou a martelada: " + aimed.name);
            return true;
        }

        /// The fallen log under the crosshair. Uses the game's hover object first, then a ray
        /// from the camera in case the log has no hover text.
        private static TreeLog LogUnderCrosshair(Player player)
        {
            GameObject hover = player.GetHoverObject();
            if (hover != null)
            {
                TreeLog l = hover.GetComponentInParent<TreeLog>();
                if (l != null) return l;
            }
            Camera cam = Camera.main;
            if (cam == null) return null;
            float reach = Vector3.Distance(cam.transform.position, player.GetEyePoint()) + 4f;
            RaycastHit[] hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, reach, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (h1, h2) => h1.distance.CompareTo(h2.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.transform.IsChildOf(player.transform)) continue;
                return hits[i].collider.GetComponentInParent<TreeLog>();
            }
            return null;
        }

        public static bool AimingAtLog(Player player)
        {
            return LogUnderCrosshair(player) != null;
        }

        public static bool HoldingAxe(Player player)
        {
            ItemDrop.ItemData weapon = player.GetCurrentWeapon();
            return weapon != null && weapon.m_shared != null &&
                   weapon.m_shared.m_skillType == Skills.SkillType.Axes;
        }

        private static System.Reflection.FieldInfo _leftItemField;

        /// Item in the off hand (shield, torch...), or null.
        public static ItemDrop.ItemData OffHandItem(Player player)
        {
            if (_leftItemField == null) _leftItemField = FindField(typeof(Humanoid), "m_leftItem");
            return _leftItemField != null ? _leftItemField.GetValue(player) as ItemDrop.ItemData : null;
        }

        /// The mod only works with an axe and a free off hand. Two-handed axes leave no off hand,
        /// so they always qualify.
        public static bool ModActive(Player player)
        {
            if (!HoldingAxe(player)) return false;
            ItemDrop.ItemData.ItemType it = player.GetCurrentWeapon().m_shared.m_itemType;
            if (it == ItemDrop.ItemData.ItemType.TwoHandedWeapon || it == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft) return true;
            return OffHandItem(player) == null;
        }

        private void HandleHit(TreeLog log, HitData hit)
        {
            _lastSeenTime = Time.time;
            NoteHit(hit);

            if (_critFrame == Time.frameCount && _target == log)
            {
                ApplyCrit(hit);
                return;
            }

            if (_target != log)
            {
                SetTarget(log);
                return;
            }

            LogRing best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _rings.Count; i++)
            {
                LogRing r = _rings[i];
                if (!r.IsHittable) continue;
                float d = AxialDistance(r.T, hit.m_point);
                if (d < bestDist) { bestDist = d; best = r; }
            }

            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo("Distância do golpe até a faixa mais próxima = " +
                                   (best == null ? "nenhuma faixa" : bestDist.ToString("0.00") + " m"));

            if (best != null && bestDist <= Plugin.HitTolerance.Value)
            {
                _critFrame = Time.frameCount;
                ApplyCrit(hit);
                PlayCritEffects(hit.m_point, best.Color);
                best.Relocate(0.5f);
            }
        }

        private void ApplyCrit(HitData hit)
        {
            float before = hit.m_damage.m_chop;
            hit.m_damage.m_chop *= Plugin.CritMultiplier.Value;
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo("Crítico: dano de corte " + before.ToString("0.0") + " → " + hit.m_damage.m_chop.ToString("0.0") +
                                   " (x" + Plugin.CritMultiplier.Value.ToString("0.##") + ")");
        }

        // Last hit we handled, so the damage number the game shows for it can be logged too.
        private static float _lastHitTime = -100f;
        private static Vector3 _lastHitPos;

        private static void NoteHit(HitData hit)
        {
            _lastHitTime = Time.time;
            _lastHitPos = hit.m_point;
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo("Dano do golpe: corte " + hit.m_damage.m_chop.ToString("0.0") +
                                   ", total " + hit.m_damage.GetTotalDamage().ToString("0.0"));
        }

        public static bool IsRecentHit(Vector3 pos)
        {
            return Time.time - _lastHitTime < 1.5f && (pos - _lastHitPos).sqrMagnitude < 16f;
        }

        // ---------------------------------------------------------------- log shape

        /// Finds the log's mesh and its long axis. A fallen log is a stretched box in its mesh's
        /// local space: the longest side is the trunk's axis, the other two give its thickness.
        private bool Measure(TreeLog log)
        {
            MeshFilter[] filters = log.GetComponentsInChildren<MeshFilter>();
            MeshFilter bestF = null;
            float bestVol = 0f;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter f = filters[i];
                if (f.sharedMesh == null || !f.gameObject.activeInHierarchy) continue;
                MeshRenderer mr = f.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;
                Vector3 s = Vector3.Scale(f.sharedMesh.bounds.size, f.transform.lossyScale);
                float vol = Mathf.Abs(s.x * s.y * s.z);
                if (vol > bestVol) { bestVol = vol; bestF = f; }
            }
            if (bestF == null) return false;

            Bounds b = bestF.sharedMesh.bounds;
            Vector3 size = b.size;
            Vector3 world = Vector3.Scale(size, bestF.transform.lossyScale);
            int axis = 0;
            if (Mathf.Abs(world.y) > Mathf.Abs(world[axis])) axis = 1;
            if (Mathf.Abs(world.z) > Mathf.Abs(world[axis])) axis = 2;

            _meshT = bestF.transform;
            _centerLocal = b.center;
            _axisLocal = Vector3.zero; _axisLocal[axis] = 1f;
            _halfLenLocal = size[axis] * 0.5f;
            float r1 = size[(axis + 1) % 3] * 0.5f, r2 = size[(axis + 2) % 3] * 0.5f;
            _radiusLocal = Mathf.Max(r1, r2);
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo("Tronco " + log.name + ": comprimento " + (AxisScale() * size[axis]).ToString("0.0") +
                                   " m, raio " + (_radiusLocal * AxisScale()).ToString("0.00") + " m, malha " + bestF.name);
            return true;
        }

        /// How many meters one local unit along the log's axis is.
        public float AxisScale()
        {
            return _meshT != null ? _meshT.TransformVector(_axisLocal).magnitude : 1f;
        }

        public Vector3 LocalPointAt(float t) { return _centerLocal + _axisLocal * t; }
        public Vector3 AxisLocal { get { return _axisLocal; } }
        public float RadiusLocal { get { return _radiusLocal; } }

        /// Distance in meters, along the log, between a band at position t and a world point.
        private float AxialDistance(float t, Vector3 worldPoint)
        {
            Vector3 local = _meshT.InverseTransformPoint(worldPoint);
            float along = Vector3.Dot(local - _centerLocal, _axisLocal);
            return Mathf.Abs(along - t) * AxisScale();
        }

        /// Picks a spot along the log for a band: away from the ends, apart from other bands,
        /// and within reach of the player.
        public bool PickPosition(LogRing self, out float t)
        {
            t = 0f;
            Player player = Player.m_localPlayer;
            if (player == null || _meshT == null) return false;
            float scale = AxisScale();
            float margin = Mathf.Min(_halfLenLocal * 0.4f, 0.3f / Mathf.Max(scale, 0.001f));
            float min = -_halfLenLocal + margin, max = _halfLenLocal - margin;
            if (max <= min) { min = max = 0f; }
            Vector3 eye = player.GetEyePoint();

            for (int attempt = 0; attempt < 30; attempt++)
            {
                float c = Random.Range(min, max);
                Vector3 w = _meshT.TransformPoint(LocalPointAt(c));
                if (Vector3.Distance(eye, w) > Plugin.MaxDistance.Value) continue;
                bool crowded = false;
                for (int i = 0; i < _rings.Count; i++)
                {
                    LogRing o = _rings[i];
                    if (o == self || !o.IsPlaced) continue;
                    if (Mathf.Abs(o.T - c) * scale < Plugin.MinSpacing.Value) { crowded = true; break; }
                }
                if (crowded) continue;
                t = c;
                return true;
            }
            return false;
        }

        private void SetTarget(TreeLog log)
        {
            ClearRings();
            if (!Measure(log)) return;
            _target = log;
            _lastSeenTime = Time.time;
            for (int i = 0; i < Plugin.SpotCount.Value; i++)
            {
                LogRing r = new LogRing(this);
                r.Relocate(i * 0.3f);
                _rings.Add(r);
            }
        }

        private void ClearRings()
        {
            for (int i = 0; i < _rings.Count; i++) _rings[i].Destroy();
            _rings.Clear();
            _target = null;
            _meshT = null;
        }

        // ---------------------------------------------------------------- update

        private void Update()
        {
            if ((_target == null || _meshT == null) && _rings.Count > 0) ClearRings();   // log destroyed

            // Follow the swing log while it rolls, and note when it disappears (split or chopped up).
            if (_swingLog != null) _swingLogPos = _swingLog.transform.position;
            else if ((object)_swingLog != null && _swingLogGoneAt < 0f) _swingLogGoneAt = Time.time;

            Player player = Player.m_localPlayer;
            if (!Plugin.Enabled.Value || player == null || !ModActive(player))
            {
                if (_target != null) ClearRings();
                return;
            }

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 0.1f;
                TreeLog aimed = LogUnderCrosshair(player);
                if (aimed != null)
                {
                    if (aimed != _target) SetTarget(aimed);
                    else _lastSeenTime = Time.time;
                }
            }
            if (_target == null) return;

            bool tooFar = Vector3.Distance(player.transform.position, _target.transform.position) > 15f;
            bool idle = Time.time - _lastSeenTime > Plugin.ActiveDuration.Value;
            if (tooFar || idle)
            {
                ClearRings();
                return;
            }

            for (int i = 0; i < _rings.Count; i++) _rings[i].Tick();
        }

        // ---------------------------------------------------------------- visuals

        public Color CurrentColor()
        {
            if (Plugin.SpotColor.Value == SpotColorOption.Azul) return new Color(0.35f, 0.75f, 1f);
            if (Plugin.SpotColor.Value == SpotColorOption.Verde) return new Color(0.45f, 1f, 0.35f);
            return new Color(1f, 0.78f, 0.25f);
        }

        /// Open tube around the Z axis: radius r, width w, centered on the origin. Built by hand so it
        /// has no collider. v = 0..1 across the width, so the band texture fades at both edges.
        public static Mesh MakeBandMesh(float r, float w)
        {
            const int seg = 48;
            Vector3[] v = new Vector3[(seg + 1) * 2];
            Vector2[] uv = new Vector2[v.Length];
            int[] tris = new int[seg * 6];
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * r, y = Mathf.Sin(a) * r;
                v[i * 2] = new Vector3(x, y, -w * 0.5f);
                v[i * 2 + 1] = new Vector3(x, y, w * 0.5f);
                uv[i * 2] = new Vector2(i / (float)seg, 0f);
                uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
            }
            for (int i = 0; i < seg; i++)
            {
                int k = i * 6, b = i * 2;
                tris[k] = b; tris[k + 1] = b + 1; tris[k + 2] = b + 2;
                tris[k + 3] = b + 2; tris[k + 4] = b + 1; tris[k + 5] = b + 3;
            }
            Mesh m = new Mesh();
            m.name = "TreeWeakSpots_Band";
            m.vertices = v; m.uv = uv; m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh MakeQuadMesh()
        {
            Mesh m = new Mesh();
            m.name = "TreeWeakSpots_Quad";
            m.vertices = new Vector3[] {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            m.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public GameObject MakeQuad(string name, Transform parent, float size)
        {
            GameObject q = new GameObject(name);
            q.transform.SetParent(parent, false);
            q.transform.localScale = Vector3.one * size;
            q.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
            MeshRenderer r = q.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (_shader != null)
            {
                Material m = new Material(_shader);
                m.mainTexture = _glowTex;
                m.renderQueue = 3100;
                r.material = m;
            }
            else r.enabled = false;
            return q;
        }

        // Where and when our last crit landed, so CritNumberPatch can restyle the damage
        // number the game shows for it (it arrives a moment later by RPC).
        private static float _lastCritTime = -100f;
        private static Vector3 _lastCritPos;

        public static bool IsRecentCrit(Vector3 pos)
        {
            return Time.time - _lastCritTime < 1.5f && (pos - _lastCritPos).sqrMagnitude < 16f;
        }

        private void PlayCritEffects(Vector3 pos, Color color)
        {
            _lastCritTime = Time.time;
            _lastCritPos = pos;

            GameObject burst = new GameObject("TreeWeakSpots_Burst");
            burst.transform.position = pos;
            burst.AddComponent<CritBurst>().Init(this, color);

            PlayCritSound();
        }

        // ---------------------------------------------------------------- crit sound
        // Same as OreWeakSpots: AudioSource on the player, routed through the game's mixer group.

        private readonly List<AudioClip> _fileClips = new List<AudioClip>();
        private readonly List<AudioClip> _gameClips = new List<AudioClip>();
        private UnityEngine.Audio.AudioMixerGroup _mixerGroup;
        private bool _gameSoundSearched;
        private AudioSource _playerAudio;
        private Player _audioOwner;

        private void PlayCritSound()
        {
            float vol = Plugin.SoundVolume.Value;
            CritSoundOption mode = Plugin.CritSound.Value;
            if (vol <= 0f || mode == CritSoundOption.Nenhum) return;
            FindGameCritSound();

            List<AudioClip> pool = null;
            if (mode == CritSoundOption.Automatico)
                pool = _fileClips.Count > 0 ? _fileClips : (_gameClips.Count > 0 ? _gameClips : null);
            else if (mode == CritSoundOption.Arquivo) pool = _fileClips.Count > 0 ? _fileClips : null;
            else if (mode == CritSoundOption.Jogo) pool = _gameClips.Count > 0 ? _gameClips : null;

            AudioClip clip = pool != null ? pool[Random.Range(0, pool.Count)] : _critClip;
            AudioSource src = GetPlayerAudio();
            if (src == null || clip == null) return;
            src.pitch = Random.Range(0.95f, 1.05f);
            src.PlayOneShot(clip, vol);
        }

        private AudioSource GetPlayerAudio()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return null;
            if (_playerAudio != null && _audioOwner == p) return _playerAudio;

            GameObject go = new GameObject("TreeWeakSpots_SFX");
            go.transform.SetParent(p.transform, false);
            go.transform.localPosition = Vector3.up * 1.5f;
            _playerAudio = go.AddComponent<AudioSource>();
            _playerAudio.playOnAwake = false;
            _playerAudio.loop = false;
            _playerAudio.spatialBlend = 0f;
            _playerAudio.outputAudioMixerGroup = _mixerGroup != null ? _mixerGroup : FindAnyMixerGroup(p);
            _audioOwner = p;
            return _playerAudio;
        }

        private static UnityEngine.Audio.AudioMixerGroup FindAnyMixerGroup(Player p)
        {
            AudioSource[] srcs = p.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < srcs.Length; i++)
                if (srcs[i].outputAudioMixerGroup != null) return srcs[i].outputAudioMixerGroup;
            return null;
        }

        private void FindGameCritSound()
        {
            if (_gameSoundSearched || ZNetScene.instance == null) return;
            _gameSoundSearched = true;
            string[] creatures = { "Greydwarf", "Skeleton", "Draugr", "Troll", "Boar", "Deer" };
            for (int c = 0; c < creatures.Length && _gameClips.Count == 0; c++)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(creatures[c]);
                if (prefab == null) continue;
                Character ch = prefab.GetComponent<Character>();
                if (ch == null || ch.m_critHitEffects == null || ch.m_critHitEffects.m_effectPrefabs == null) continue;
                EffectList.EffectData[] list = ch.m_critHitEffects.m_effectPrefabs;
                for (int i = 0; i < list.Length; i++)
                    if (list[i].m_prefab != null) CollectClips(list[i].m_prefab);
            }
        }

        private void CollectClips(GameObject fx)
        {
            Component[] comps = fx.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                Component comp = comps[i];
                if (comp == null) continue;
                System.Reflection.FieldInfo f = FindField(comp.GetType(), "m_audioClips");
                if (f != null)
                {
                    AudioClip[] clips = f.GetValue(comp) as AudioClip[];
                    if (clips != null)
                        for (int k = 0; k < clips.Length; k++)
                            if (clips[k] != null && !_gameClips.Contains(clips[k])) _gameClips.Add(clips[k]);
                }
                AudioSource src = comp as AudioSource;
                if (src != null)
                {
                    if (src.clip != null && !_gameClips.Contains(src.clip)) _gameClips.Add(src.clip);
                    if (_mixerGroup == null && src.outputAudioMixerGroup != null) _mixerGroup = src.outputAudioMixerGroup;
                }
            }
        }

        private System.Collections.IEnumerator LoadSoundFiles()
        {
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "SFX");
            if (!System.IO.Directory.Exists(dir))
            {
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
                yield break;
            }
            string[] files = System.IO.Directory.GetFiles(dir);
            for (int i = 0; i < files.Length; i++)
            {
                string ext = System.IO.Path.GetExtension(files[i]).ToLowerInvariant();
                AudioType type = ext == ".wav" ? AudioType.WAV : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.UNKNOWN;
                if (type == AudioType.UNKNOWN) continue;
                using (UnityEngine.Networking.UnityWebRequest req =
                       UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip("file:///" + files[i].Replace('\\', '/'), type))
                {
                    yield return req.SendWebRequest();
                    AudioClip clip = req.result == UnityEngine.Networking.UnityWebRequest.Result.Success
                        ? UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(req) : null;
                    if (clip != null)
                    {
                        clip.name = System.IO.Path.GetFileNameWithoutExtension(files[i]);
                        _fileClips.Add(clip);
                    }
                }
            }
        }

        private static Texture2D MakeGlowTexture(int size)
        {
            Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            float c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float core = Mathf.Clamp01(1f - d * 3f);
                    float halo = Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(halo * 0.8f + core)));
                }
            t.Apply();
            return t;
        }

        /// Bright line in the middle of the band, fading out toward both edges.
        private static Texture2D MakeBandTexture(int size)
        {
            Texture2D t = new Texture2D(4, size, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                float d = Mathf.Abs(y / (float)(size - 1) * 2f - 1f);          // 0 middle, 1 edge
                float core = Mathf.Clamp01(1f - d * 2.5f);
                float soft = Mathf.Pow(Mathf.Clamp01(1f - d), 1.8f);
                float a = Mathf.Clamp01(soft * 0.75f + core * 0.6f);
                for (int x = 0; x < 4; x++) t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            t.Apply();
            return t;
        }

        private static AudioClip MakeCritClip()
        {
            int rate = 44100;
            int len = (int)(rate * 0.35f);
            float[] data = new float[len];
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)rate;
                float env = Mathf.Exp(-t * 14f);
                float attack = Mathf.Clamp01(t * 400f);
                data[i] = attack * env * (0.55f * Mathf.Sin(2f * Mathf.PI * 1568f * t) +
                                          0.30f * Mathf.Sin(2f * Mathf.PI * 2349f * t) +
                                          0.15f * Mathf.Sin(2f * Mathf.PI * 3136f * t));
            }
            AudioClip clip = AudioClip.Create("TreeWeakSpots_Crit", len, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    /// One glowing band wrapped around the log, like a slice of the trunk lit from inside.
    /// It is a child of the log's mesh, so it rolls and slides with the log.
    public class LogRing
    {
        private const float FadeIn = 0.5f;
        private const float FadeOut = 0.7f;

        private readonly WeakSpotManager _mgr;
        private GameObject _root;
        private Material _coreMat;
        private Material _haloMat;
        private Mesh _coreMesh;
        private Mesh _haloMesh;
        private Light _light;

        private float _t;
        private bool _placed;
        private float _bornAt;
        private float _lifetime;
        private float _retryAt;

        public float T { get { return _t; } }
        public Color Color { get; private set; }
        public bool IsPlaced { get { return _placed; } }
        public bool IsHittable { get { return _placed && Time.time >= _bornAt; } }

        public LogRing(WeakSpotManager mgr) { _mgr = mgr; }

        public void Relocate(float delay)
        {
            _placed = false;
            if (_root != null) _root.SetActive(false);
            _retryAt = Time.time + delay;
        }

        private void Build()
        {
            Transform mt = _mgr.MeshTransform;
            float scale = _mgr.AxisScale();
            float r = _mgr.RadiusLocal * 1.04f;                                   // just outside the bark
            float w = Plugin.BandWidth.Value / Mathf.Max(scale, 0.001f);          // meters -> mesh units

            _root = new GameObject("TreeWeakSpot");
            _root.transform.SetParent(mt, false);
            _root.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, _mgr.AxisLocal);

            _coreMesh = WeakSpotManager.MakeBandMesh(r, w);
            _haloMesh = WeakSpotManager.MakeBandMesh(r * 1.06f, w * 2.6f);
            _coreMat = AddBand("Core", _coreMesh, 3101);
            _haloMat = AddBand("Halo", _haloMesh, 3100);

            _light = _root.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.range = Mathf.Max(1.5f, r * scale * 4f);
            _light.shadows = LightShadows.None;
        }

        private Material AddBand(string name, Mesh mesh, int queue)
        {
            GameObject g = new GameObject(name);
            g.transform.SetParent(_root.transform, false);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = g.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            if (_mgr.BandShader == null) { mr.enabled = false; return null; }
            Material m = new Material(_mgr.BandShader);
            m.mainTexture = _mgr.BandTexture;
            m.renderQueue = queue;
            mr.material = m;
            return m;
        }

        private void TryPlace()
        {
            float t;
            if (_mgr.MeshTransform == null || !_mgr.PickPosition(this, out t))
            {
                _retryAt = Time.time + 0.5f;
                return;
            }
            if (_root == null) Build();
            _t = t;
            _root.transform.localPosition = _mgr.LocalPointAt(t);
            Color = _mgr.CurrentColor();
            _lifetime = Plugin.SpotLifetime.Value * Random.Range(0.85f, 1.15f);
            _bornAt = Time.time;
            _placed = true;
            _root.SetActive(true);
        }

        public void Tick()
        {
            if (!_placed)
            {
                if (Time.time >= _retryAt) TryPlace();
                return;
            }
            if (_root == null) { _placed = false; return; }   // log was destroyed with the band on it

            float age = Time.time - _bornAt;
            if (age > _lifetime) { Relocate(0.4f); return; }

            float fadeIn = Mathf.SmoothStep(0f, 1f, age / FadeIn);
            float fadeOut = Mathf.SmoothStep(0f, 1f, (_lifetime - age) / FadeOut);
            float a = fadeIn * fadeOut;
            float pulse = 0.8f + 0.2f * Mathf.Sin(age * 6f);

            // Widens out of a thin line while fading in.
            float grow = Mathf.Lerp(0.3f, 1f, a);
            _root.transform.localScale = new Vector3(1f, 1f, grow);

            Color c = Color;
            if (_coreMat != null)
                _coreMat.color = new Color(Mathf.Lerp(c.r, 1f, 0.5f), Mathf.Lerp(c.g, 1f, 0.5f), Mathf.Lerp(c.b, 1f, 0.5f), a * 0.95f);
            if (_haloMat != null)
                _haloMat.color = new Color(c.r, c.g, c.b, 0.45f * a * pulse);
            _light.color = c;
            _light.intensity = 1.6f * a * pulse;
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_coreMat != null) Object.Destroy(_coreMat);
            if (_haloMat != null) Object.Destroy(_haloMat);
            if (_coreMesh != null) Object.Destroy(_coreMesh);
            if (_haloMesh != null) Object.Destroy(_haloMesh);
        }
    }

    /// Flash shown on a critical hit: an expanding ring of light that fades out.
    public class CritBurst : MonoBehaviour
    {
        private const float Duration = 0.4f;
        private float _start;
        private Material _mat;
        private Light _light;
        private Color _color;
        private Transform _quad;

        public void Init(WeakSpotManager mgr, Color color)
        {
            _start = Time.time;
            _color = color;
            GameObject q = mgr.MakeQuad("Flash", transform, 0.4f);
            _quad = q.transform;
            _mat = q.GetComponent<MeshRenderer>().material;
            _light = gameObject.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.range = 3f;
            _light.color = color;
            _light.shadows = LightShadows.None;
        }

        private void Update()
        {
            float t = (Time.time - _start) / Duration;
            if (t >= 1f)
            {
                Object.Destroy(_mat);
                Object.Destroy(gameObject);
                return;
            }
            float a = 1f - t;
            _quad.localScale = Vector3.one * Mathf.Lerp(0.4f, 1.6f, t);
            _mat.color = new Color(Mathf.Lerp(_color.r, 1f, 0.4f), Mathf.Lerp(_color.g, 1f, 0.4f), Mathf.Lerp(_color.b, 1f, 0.4f), a);
            _light.intensity = 5f * a;
            Camera cam = Camera.main;
            if (cam != null) transform.rotation = cam.transform.rotation;
        }
    }
}
