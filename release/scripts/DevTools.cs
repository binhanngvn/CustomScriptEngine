// Bỏ vào <Game>/BepInEx/scripts/DevTools.cs
//
// F11            : bật/tắt cửa sổ Dev Tools
// Chuột giữa     : đặt prefab đang chọn ngay dưới chân Player
// Delete         : xóa object đã đặt gần chuột (Shift+Delete: xóa cả quái của game)
// Ctrl+Z         : hoàn tác lần đặt gần nhất
// T              : teleport người chơi tới vị trí chuột
// PageUp/PageDown: zoom camera ra/vào, Home: trả camera về cho game
//
// Map được lưu ở BepInEx/devmap/<tên>.json. Chỉ phần gần người chơi mới được tạo ra,
// phần xa bị xóa khỏi scene (dữ liệu vẫn giữ) để đỡ nặng máy.

using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using System.Collections;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkflowSO;

[Serializable]
public class DevMapItem
{
    public string prefab;
    public float x;
    public float y;
    public float z;
    public float rot;
    public float scale;
}

[Serializable]
public class DevMapFile
{
    public string scene;
    public List<DevMapItem> items = new List<DevMapItem>();
}

public class DevPlaced
{
    public string prefab;
    public Vector3 pos;
    public float rot;
    public float scale;
    public Vector2Int chunk;
    public GameObject live;
}

public sealed class DevFloatSnapshot
{
    public Component component;
    public FieldInfo field;
    public float original;
}

public sealed class DevVector2Snapshot
{
    public Component component;
    public FieldInfo field;
    public object original;
    public Vector2SO createdSO;
    public Vector2Ref reference;
    public Vector2Ref.FieldType originalSource;
    public Vector2 originalValue;
    public Vector2SO originalSO;
    public object originalVariableGO;
}

[BepInPlugin("binhanngvn.devtools", "Dev Tools", "2.2.0")]
public sealed class DevTools : BaseUnityPlugin
{
    private const float ChunkSize = 16f;
    private const int WindowId = 77123;

    // ---------------- UI ----------------
    private bool _show;
    private bool _typing;
    private Rect _win = new Rect(20f, 20f, 400f, 580f);
    private int _tab;
    private static readonly string[] Tabs = { "Place", "Map", "Player", "Enemies", "Camera", "Debug" };
    private static readonly string[] Categories = { "All", "Entity", "Enemy" };

    // ---------------- PREFABS ----------------
    private readonly List<GameObject> _prefabs = new List<GameObject>();
    private readonly List<GameObject> _filtered = new List<GameObject>();
    private readonly Dictionary<string, GameObject> _prefabByName = new Dictionary<string, GameObject>();
    private readonly HashSet<string> _entityNames = new HashSet<string>();
    private bool _prefabsReady;
    private string _lastFilterKey = "";
    private string _search = "";
    private int _category;
    private Vector2 _scroll;
    private GameObject _selected;
    private int _count = 1;
    private float _spread = 2f;
    private float _rot;
    private float _scale = 1f;
    private bool _snap;
    private float _grid = 1f;
    private float _refreshAt = -1f;

    // ---------------- MAP ----------------
    private readonly Dictionary<Vector2Int, List<DevPlaced>> _chunks = new Dictionary<Vector2Int, List<DevPlaced>>();
    private readonly List<DevPlaced> _history = new List<DevPlaced>();
    private string _mapName = "mymap";
    private string[] _savedMaps = new string[0];
    private bool _pendingMapListRefresh;
    private bool _streaming = true;
    private float _loadRadius = 80f;
    private float _nextStream;
    private int _statRecords;
    private int _statChunks;
    private int _statLive;

    // ---------------- PLAYER ----------------
    private GameObject _player;
    private bool _noclip;
    private readonly Dictionary<Collider2D, bool> _noclipSaved = new Dictionary<Collider2D, bool>();
    private bool _hasSavedPos;
    private Vector3 _savedPos;

    // ---------------- FLY ----------------
    private bool _fly;
    private float _flySpeed = 12f;
    private readonly Dictionary<Rigidbody2D, bool> _flyRbSimulatedSaved = new Dictionary<Rigidbody2D, bool>();
    private readonly Dictionary<MovementComponent, bool> _flyMovementSaved = new Dictionary<MovementComponent, bool>();
    private bool _flyForcedNoclip;

    // ---------------- ENEMY / DEBUG ----------------
    private bool _aiStealth;
    private float _nextStealthScan;
    private readonly List<DevVector2Snapshot> _stealthVector2Snapshots = new List<DevVector2Snapshot>();
    private readonly List<Vector2SO> _stealthCreatedTargets = new List<Vector2SO>();
    private readonly Dictionary<Component, IList> _stealthBlockerLists = new Dictionary<Component, IList>();
    private BoolSO _stealthBlocker;
    private Entity _selectedEnemy;
    private bool _selectedEnemyFrozen;
    private float _selectedEnemySpeed = 1f;
    private readonly Dictionary<Rigidbody2D, bool> _enemyRbSaved = new Dictionary<Rigidbody2D, bool>();
    private readonly Dictionary<Behaviour, bool> _enemyBehaviourSaved = new Dictionary<Behaviour, bool>();
    private readonly List<DevFloatSnapshot> _enemyFloatSnapshots = new List<DevFloatSnapshot>();
    private string _lastAction = "Ready";
    private string _lastError = "";

    // ---------------- TEST AREA ----------------
    private bool _testAreaVisible;
    private bool _testAreaFollowPlayer = true;
    private Vector2 _testAreaCenter;
    private Vector2 _testAreaSize = new Vector2(24f, 16f);

    // ---------------- CAMERA ----------------
    private bool _camOverride;
    private float _camSize = 8f;

    // =====================================================
    // LIFECYCLE
    // =====================================================

    private void Awake()
    {
        Camera.onPreCull += OnCamPreCull;
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnSrpBegin;
        SceneManager.activeSceneChanged += OnSceneChanged;

        _pendingMapListRefresh = true;
        _stealthBlocker = ScriptableObject.CreateInstance<BoolSO>();
        _stealthBlocker.name = "DevTools_StealthBlocker";
        _stealthBlocker.value = true;

        Logger.LogInfo("DevTools 2.2 | F11 | MMB spawn at player feet | Noclip/Fly | AI stealth | Unlock/Enemy debug");
    }

    private void OnDestroy()
    {
        Camera.onPreCull -= OnCamPreCull;
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnSrpBegin;
        SceneManager.activeSceneChanged -= OnSceneChanged;

        SetEnemyStealth(false);
        SetSelectedEnemyFrozen(false);
        ResetSelectedEnemyChanges();
        SetFly(false);
        SetNoclip(false);

        if (CountRecords() > 0)
            SaveMap("_autosave");

        ClearAll();

        if (_stealthBlocker != null)
            Destroy(_stealthBlocker);
    }

    private void OnSceneChanged(Scene from, Scene to)
    {
        if (CountRecords() > 0)
        {
            SaveMap("_autosave");
            Logger.LogInfo("Đổi scene: map đang mở đã lưu vào _autosave.json rồi xóa khỏi bộ nhớ.");
        }

        SetEnemyStealth(false);
        SetSelectedEnemyFrozen(false);
        ResetSelectedEnemyChanges();
        SetFly(false);
        SetNoclip(false);
        ClearAll();
        _player = null;
        _selectedEnemy = null;
        _noclipSaved.Clear();
        _noclip = false;
        _fly = false;
        _prefabsReady = false;
        _lastFilterKey = "";
        _refreshAt = Time.unscaledTime + 2f;
    }

    // =====================================================
    // UPDATE
    // =====================================================

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F11))
        {
            _show = !_show;

            if (_show && !_prefabsReady)
                RefreshPrefabs();
        }

        if (_refreshAt > 0f && Time.unscaledTime >= _refreshAt)
        {
            _refreshAt = -1f;
            RefreshPrefabs();
        }

        if (_pendingMapListRefresh)
        {
            _pendingMapListRefresh = false;
            RefreshMapList();
        }

        if (!_typing)
            HandleHotkeys();

        if (_streaming && Time.unscaledTime >= _nextStream)
        {
            _nextStream = Time.unscaledTime + 0.25f;
            StreamTick();
        }

        HandleFly();

        if (_aiStealth && Time.unscaledTime >= _nextStealthScan)
        {
            _nextStealthScan = Time.unscaledTime + 0.20f;
            ApplyEnemyStealth();
        }

        ApplyCamera(Camera.main);
    }

    private void LateUpdate()
    {
        ApplyCamera(Camera.main);
    }

    private void HandleHotkeys()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (Input.GetMouseButtonDown(2) && !MouseOverWindow())
            PlaceSelected(GetPlayerFeetPosition());

        if (ctrl && Input.GetKeyDown(KeyCode.Z))
            UndoLast();

        if (Input.GetKeyDown(KeyCode.Delete))
        {
            Vector3 w = MouseWorld();

            if (!DeleteNearestPlaced(w) && shift)
                DeleteNearestEntity(w);
        }

        if (Input.GetKeyDown(KeyCode.T))
            TeleportPlayerTo(MouseWorld());

        float dir = 0f;
        if (Input.GetKey(KeyCode.PageUp)) dir += 1f;
        if (Input.GetKey(KeyCode.PageDown)) dir -= 1f;

        if (dir != 0f)
        {
            StartCamOverride();
            _camSize = Mathf.Clamp(_camSize * (1f + dir * Time.unscaledDeltaTime), 2f, 120f);
        }

        if (Input.GetKeyDown(KeyCode.Home))
            _camOverride = false;
    }

    // =====================================================
    // PREFABS
    // =====================================================

    private void RefreshPrefabs()
    {
        _prefabs.Clear();
        _prefabByName.Clear();
        _entityNames.Clear();

        // 1) Standard Unity Resources assets.
        try
        {
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                RegisterPrefab(go, "Resources");
        }
        catch (Exception ex)
        {
            LogDebug("Resources scan error: " + ex.Message);
        }

        // 2) Prefabs explicitly referenced by the game's live spawn systems.
        // These are important because CelSector keeps several enemy prefabs in
        // private serialized fields instead of exposing them through Resources.
        try
        {
            foreach (EntitySpawner spawner in FindObjectsOfType<EntitySpawner>(true))
                RegisterObjectField(spawner, "_entityPrefab", "EntitySpawner._entityPrefab");
        }
        catch (Exception ex) { LogDebug("EntitySpawner scan error: " + ex.Message); }

        try
        {
            foreach (PreplacedEnemySpawner spawner in FindObjectsOfType<PreplacedEnemySpawner>(true))
                RegisterObjectField(spawner, "_enemyPrefab", "PreplacedEnemySpawner._enemyPrefab");
        }
        catch (Exception ex) { LogDebug("PreplacedEnemySpawner scan error: " + ex.Message); }

        try
        {
            foreach (SpawnDirectorSimple director in FindObjectsOfType<SpawnDirectorSimple>(true))
                ScanObjectForPrefabs(director, 0, new HashSet<object>());
        }
        catch (Exception ex) { LogDebug("SpawnDirectorSimple scan error: " + ex.Message); }

        // 3) Other live MonoBehaviours / ScriptableObjects may contain
        // SimpleSpawnCard, room, map or entity prefab references.
        try
        {
            foreach (MonoBehaviour behaviour in FindObjectsOfType<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                ScanObjectForPrefabs(behaviour, 0, new HashSet<object>());
            }
        }
        catch (Exception ex) { LogDebug("Scene object scan error: " + ex.Message); }

        try
        {
            foreach (ScriptableObject asset in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (asset == null) continue;
                ScanObjectForPrefabs(asset, 0, new HashSet<object>());
            }
        }
        catch (Exception ex) { LogDebug("SO scan error: " + ex.Message); }

        _prefabs.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        _prefabsReady = true;
        _lastFilterKey = "";
        LogDebug("Prefab registry ready: " + _prefabs.Count + " prefabs.");
    }

    private void RegisterObjectField(object owner, string fieldName, string source)
    {
        if (owner == null) return;
        try
        {
            FieldInfo field = owner.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return;
            GameObject prefab = field.GetValue(owner) as GameObject;
            if (prefab != null)
                RegisterPrefab(prefab, source);
        }
        catch (Exception ex)
        {
            LogDebug(source + " failed: " + ex.Message);
        }
    }

    private void RegisterPrefab(GameObject go, string source)
    {
        if (go == null) return;
        if (go.scene.IsValid()) return;
        if (go.transform.parent != null) return;
        if (string.IsNullOrWhiteSpace(go.name)) return;
        if (_prefabByName.ContainsKey(go.name)) return;

        _prefabByName[go.name] = go;
        _prefabs.Add(go);
        if (go.GetComponentInChildren<Entity>(true) != null)
            _entityNames.Add(go.name);
    }

    private void ScanObjectForPrefabs(object root, int depth, HashSet<object> visited)
    {
        if (root == null || depth > 3) return;
        if (root is UnityEngine.Object unityObject && unityObject == null) return;
        if (!visited.Add(root)) return;

        if (root is GameObject go)
        {
            RegisterPrefab(go, root.GetType().Name);
            return;
        }

        Type type = root.GetType();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.IsStatic) continue;
            string lower = field.Name.ToLowerInvariant();
            bool interesting = lower.Contains("prefab") || lower.Contains("spawn") ||
                               lower.Contains("entity") || lower.Contains("enemy") ||
                               lower.Contains("monster") || lower.Contains("room") ||
                               lower.Contains("map") || lower.Contains("card");
            bool collection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
            bool knownContainer = type.Name == "SimpleSpawnCard" ||
                                  type.Name == "SpawnDirectorSimple" ||
                                  type.Name == "EntitySpawner" ||
                                  type.Name == "PreplacedEnemySpawner";
            if (!interesting && !collection && !knownContainer) continue;

            object value;
            try { value = field.GetValue(root); }
            catch { continue; }
            ScanValue(value, depth + 1, visited);
        }
    }

    private void ScanValue(object value, int depth, HashSet<object> visited)
    {
        if (value == null || depth > 3) return;
        if (value is GameObject go)
        {
            RegisterPrefab(go, "field");
            return;
        }
        if (value is string) return;
        if (value is IEnumerable enumerable)
        {
            int n = 0;
            foreach (object item in enumerable)
            {
                if (item != null) ScanValue(item, depth + 1, visited);
                if (++n >= 500) break;
            }
            return;
        }
        Type t = value.GetType();
        if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) return;
        ScanObjectForPrefabs(value, depth, visited);
    }

    private void RebuildFilter()
    {
        string key = _search + "|" + _category + "|" + _prefabs.Count;

        if (key == _lastFilterKey)
            return;

        _lastFilterKey = key;
        _filtered.Clear();

        foreach (GameObject g in _prefabs)
        {
            if (g == null)
                continue;

            if (_category == 1 && !_entityNames.Contains(g.name))
                continue;

            if (_category == 2 &&
                !g.name.StartsWith("Enemy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_search.Length > 0 &&
                g.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            _filtered.Add(g);
        }
    }

    // =====================================================
    // PLACE / MAP DATA
    // =====================================================

    private Vector3 MouseWorld()
    {
        Camera cam = Camera.main;

        if (cam == null)
            return Vector3.zero;

        Vector3 m = Input.mousePosition;

        return cam.ScreenToWorldPoint(
            new Vector3(m.x, m.y, -cam.transform.position.z));
    }

    private bool MouseOverWindow()
    {
        if (!_show)
            return false;

        Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        return _win.Contains(m);
    }

    private static Vector2Int ChunkOf(Vector3 p)
    {
        return new Vector2Int(
            Mathf.FloorToInt(p.x / ChunkSize),
            Mathf.FloorToInt(p.y / ChunkSize));
    }

    private void PlaceSelected(Vector3 center)
    {
        if (_selected == null)
        {
            Logger.LogInfo("Chưa chọn prefab nào (mở F11 > Place > bấm vào tên).");
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            Vector2 off = _count > 1
                ? UnityEngine.Random.insideUnitCircle * _spread
                : Vector2.zero;

            Vector3 p = new Vector3(
                center.x + off.x,
                center.y + off.y,
                _selected.transform.position.z);

            if (_snap && _grid > 0.01f)
            {
                p.x = Mathf.Round(p.x / _grid) * _grid;
                p.y = Mathf.Round(p.y / _grid) * _grid;
            }

            AddRecord(_selected.name, p, _rot, _scale, true, true);
        }
    }

    private DevPlaced AddRecord(
        string prefab, Vector3 pos, float rot, float scale,
        bool spawnNow, bool addHistory)
    {
        DevPlaced r = new DevPlaced();
        r.prefab = prefab;
        r.pos = pos;
        r.rot = rot;
        r.scale = scale;
        r.chunk = ChunkOf(pos);

        List<DevPlaced> list;
        if (!_chunks.TryGetValue(r.chunk, out list))
        {
            list = new List<DevPlaced>();
            _chunks[r.chunk] = list;
        }

        list.Add(r);

        if (addHistory)
            _history.Add(r);

        if (spawnNow)
            Spawn(r);

        return r;
    }

    private void Spawn(DevPlaced r)
    {
        if (r.live != null)
            return;

        GameObject prefab;
        if (!_prefabByName.TryGetValue(r.prefab, out prefab) || prefab == null)
        {
            if (_refreshAt < 0f)
                _refreshAt = Time.unscaledTime + 3f;

            return;
        }

        try
        {
            GameObject go = UnityEngine.Object.Instantiate(prefab, r.pos, Quaternion.Euler(0f, 0f, r.rot));
            go.transform.localScale = prefab.transform.localScale * r.scale;
            go.SetActive(true);
            r.live = go;
            _lastAction = "Spawned " + r.prefab;
        }
        catch (Exception ex)
        {
            _lastError = "Spawn " + r.prefab + ": " + ex.Message;
            LogDebug(_lastError);
        }
    }

    private void Despawn(DevPlaced r)
    {
        if (r.live != null)
            UnityEngine.Object.Destroy(r.live);

        r.live = null;
    }

    private void RemoveRecord(DevPlaced r)
    {
        Despawn(r);

        List<DevPlaced> list;
        if (_chunks.TryGetValue(r.chunk, out list))
        {
            list.Remove(r);

            if (list.Count == 0)
                _chunks.Remove(r.chunk);
        }

        _history.Remove(r);
    }

    private void UndoLast()
    {
        if (_history.Count == 0)
            return;

        RemoveRecord(_history[_history.Count - 1]);
    }

    private bool DeleteNearestPlaced(Vector3 world)
    {
        DevPlaced best = null;
        float bestSq = 2.25f;

        foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
        {
            foreach (DevPlaced r in kv.Value)
            {
                if (r.live == null)
                    continue;

                Vector2 d = r.live.transform.position - world;
                float sq = d.sqrMagnitude;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = r;
                }
            }
        }

        if (best == null)
            return false;

        RemoveRecord(best);
        return true;
    }

    private void DeleteNearestEntity(Vector3 world)
    {
        Entity best = null;
        float bestSq = 4f;

        foreach (Entity e in FindObjectsOfType<Entity>())
        {
            if (e == null)
                continue;

            if (e.gameObject.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            Vector2 d = e.transform.position - world;
            float sq = d.sqrMagnitude;

            if (sq < bestSq)
            {
                bestSq = sq;
                best = e;
            }
        }

        if (best != null)
        {
            Logger.LogInfo("Xóa " + best.gameObject.name);
            Destroy(best.gameObject);
        }
    }

    private void ClearAll()
    {
        foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
        {
            foreach (DevPlaced r in kv.Value)
                Despawn(r);
        }

        _chunks.Clear();
        _history.Clear();
    }

    private int CountRecords()
    {
        int n = 0;

        foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
            n += kv.Value.Count;

        return n;
    }

    // =====================================================
    // STREAMING
    // =====================================================

    private void StreamTick()
    {
        if (_chunks.Count == 0)
            return;

        Vector2 p;
        GameObject player = GetPlayer();

        if (player != null)
        {
            p = player.transform.position;
        }
        else if (Camera.main != null)
        {
            p = Camera.main.transform.position;
        }
        else
        {
            return;
        }

        float load = _loadRadius;
        float unload = _loadRadius + ChunkSize;
        int budget = 40;

        foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
        {
            float minX = kv.Key.x * ChunkSize;
            float minY = kv.Key.y * ChunkSize;

            float dx = Mathf.Max(minX - p.x, 0f, p.x - (minX + ChunkSize));
            float dy = Mathf.Max(minY - p.y, 0f, p.y - (minY + ChunkSize));
            float d = Mathf.Sqrt(dx * dx + dy * dy);

            if (d <= load)
            {
                foreach (DevPlaced r in kv.Value)
                {
                    if (budget <= 0)
                        break;

                    if (r.live == null)
                    {
                        Spawn(r);
                        budget--;
                    }
                }
            }
            else if (d > unload)
            {
                foreach (DevPlaced r in kv.Value)
                    Despawn(r);
            }
        }
    }

    // =====================================================
    // SAVE / LOAD
    // =====================================================

    private static string MapDir()
    {
        string dir = Path.Combine(Paths.BepInExRootPath, "devmap");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "map";

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "");

        return name.Length == 0 ? "map" : name;
    }

    private void SaveMap(string name)
    {
        DevMapFile file = new DevMapFile();
        file.scene = SceneManager.GetActiveScene().name;

        foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
        {
            foreach (DevPlaced r in kv.Value)
            {
                DevMapItem item = new DevMapItem();
                item.prefab = r.prefab;
                item.x = r.pos.x;
                item.y = r.pos.y;
                item.z = r.pos.z;
                item.rot = r.rot;
                item.scale = r.scale;
                file.items.Add(item);
            }
        }

        string path = Path.Combine(MapDir(), SafeName(name) + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(file, true));

        _pendingMapListRefresh = true;
        Logger.LogInfo("Đã lưu " + file.items.Count + " object -> " + path);
    }

    private void LoadMap(string name)
    {
        string path = Path.Combine(MapDir(), SafeName(name) + ".json");

        if (!File.Exists(path))
        {
            Logger.LogWarning("Không có file map: " + path);
            return;
        }

        DevMapFile file = JsonUtility.FromJson<DevMapFile>(File.ReadAllText(path));

        if (file == null || file.items == null)
        {
            Logger.LogWarning("File map hỏng: " + path);
            return;
        }

        string scene = SceneManager.GetActiveScene().name;

        if (!string.IsNullOrEmpty(file.scene) && file.scene != scene)
        {
            Logger.LogWarning("Map này lưu ở scene '" + file.scene +
                              "' nhưng đang ở '" + scene + "'. Vẫn tải.");
        }

        ClearAll();

        foreach (DevMapItem item in file.items)
        {
            AddRecord(
                item.prefab,
                new Vector3(item.x, item.y, item.z),
                item.rot,
                item.scale,
                false,
                false);
        }

        _nextStream = 0f;
        Logger.LogInfo("Đã tải " + file.items.Count + " object từ " + path);
    }

    private void RefreshMapList()
    {
        try
        {
            _savedMaps = Directory.GetFiles(MapDir(), "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToArray();
        }
        catch (Exception)
        {
            _savedMaps = new string[0];
        }
    }

    // =====================================================
    // PLAYER
    // =====================================================

    private GameObject GetPlayer()
    {
        // Fast path: the old DevTools behavior.
        if (_player != null)
        {
            Entity cachedEntity = _player.GetComponent<Entity>();
            if (cachedEntity == null || cachedEntity.id == null ||
                string.Equals(cachedEntity.id.value, "Player", StringComparison.OrdinalIgnoreCase))
                return _player;
            _player = null;
        }

        try
        {
            GameObject tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null)
            {
                _player = ResolvePlayerRoot(tagged);
                if (_player != null) return _player;
            }
        }
        catch { }

        // Robust path for CelSector: PlayerInputComponent + MovementComponent /
        // PlayerWeaponComponent are much more reliable than the Unity tag.
        try
        {
            PlayerInputComponent[] inputs = FindObjectsOfType<PlayerInputComponent>(true);
            foreach (PlayerInputComponent input in inputs)
            {
                if (input == null) continue;
                GameObject root = ResolvePlayerRoot(input.gameObject);
                if (IsPlayerRoot(root))
                {
                    _player = root;
                    return _player;
                }
            }
        }
        catch { }

        try
        {
            PlayerWeaponComponent[] weapons = FindObjectsOfType<PlayerWeaponComponent>(true);
            foreach (PlayerWeaponComponent weapon in weapons)
            {
                if (weapon == null) continue;
                GameObject root = ResolvePlayerRoot(weapon.gameObject);
                if (IsPlayerRoot(root))
                {
                    _player = root;
                    return _player;
                }
            }
        }
        catch { }

        try
        {
            MovementComponent[] movements = FindObjectsOfType<MovementComponent>(true);
            foreach (MovementComponent movement in movements)
            {
                if (movement == null) continue;
                GameObject root = ResolvePlayerRoot(movement.gameObject);
                if (IsPlayerRoot(root))
                {
                    _player = root;
                    return _player;
                }
            }
        }
        catch { }

        // Final path: Entity whose id is literally Player.
        try
        {
            foreach (Entity entity in FindObjectsOfType<Entity>(true))
            {
                if (IsPlayerEntity(entity))
                {
                    _player = entity.gameObject;
                    return _player;
                }
            }
        }
        catch { }

        return null;
    }

    private GameObject ResolvePlayerRoot(GameObject source)
    {
        if (source == null) return null;
        Entity local = source.GetComponentInParent<Entity>();
        if (local != null && IsPlayerEntity(local))
            return local.gameObject;

        PlayerInputComponent input = source.GetComponentInParent<PlayerInputComponent>();
        if (input != null)
        {
            Entity entity = input.GetComponentInParent<Entity>();
            if (entity != null && IsPlayerEntity(entity)) return entity.gameObject;
        }

        PlayerWeaponComponent weapon = source.GetComponentInParent<PlayerWeaponComponent>();
        if (weapon != null)
        {
            Entity entity = weapon.GetComponentInParent<Entity>();
            if (entity != null && IsPlayerEntity(entity)) return entity.gameObject;
        }

        MovementComponent movement = source.GetComponentInParent<MovementComponent>();
        if (movement != null)
        {
            Entity entity = movement.GetComponentInParent<Entity>();
            if (entity != null && IsPlayerEntity(entity)) return entity.gameObject;
        }

        return null;
    }

    private bool IsPlayerRoot(GameObject go)
    {
        return go != null && IsPlayerEntity(go.GetComponent<Entity>());
    }

    private bool IsPlayerEntity(Entity entity)
    {
        return entity != null && entity.id != null &&
               string.Equals(entity.id.value, "Player", StringComparison.OrdinalIgnoreCase);
    }

    private Vector3 GetPlayerFeetPosition()
    {
        GameObject p = GetPlayer();
        if (p == null) return Vector3.zero;

        float lowest = float.PositiveInfinity;
        Collider2D[] colliders = p.GetComponentsInChildren<Collider2D>(true);
        foreach (Collider2D c in colliders)
        {
            if (c == null || c.isTrigger) continue;
            if (c.bounds.min.y < lowest) lowest = c.bounds.min.y;
        }

        if (float.IsInfinity(lowest))
            lowest = p.transform.position.y;

        return new Vector3(p.transform.position.x, lowest, p.transform.position.z);
    }

    private void TeleportPlayerTo(Vector3 world)
    {
        GameObject p = GetPlayer();

        if (p == null)
        {
            Logger.LogWarning("Không tìm thấy Player.");
            return;
        }

        Vector3 pos = new Vector3(world.x, world.y, p.transform.position.z);
        p.transform.position = pos;

        Rigidbody2D rb = p.GetComponent<Rigidbody2D>();
        if (rb == null)
            rb = p.GetComponentInChildren<Rigidbody2D>();

        if (rb != null)
        {
            rb.position = new Vector2(pos.x, pos.y);
            rb.velocity = Vector2.zero;
        }

        Physics2D.SyncTransforms();
        _nextStream = 0f;
    }

    private void PlaceSelectedAtPlayerFeet()
    {
        if (_selected == null)
        {
            LogDebug("No prefab selected.");
            return;
        }

        Vector3 center = GetPlayerFeetPosition();
        if (GetPlayer() == null)
        {
            LogDebug("Player not found; cannot use player-feet anchor.");
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            Vector2 offset = _count > 1
                ? UnityEngine.Random.insideUnitCircle * _spread
                : Vector2.zero;
            Vector3 p = new Vector3(
                center.x + offset.x,
                center.y + offset.y,
                _selected.transform.position.z);

            if (_snap && _grid > 0.01f)
            {
                p.x = Mathf.Round(p.x / _grid) * _grid;
                p.y = Mathf.Round(p.y / _grid) * _grid;
            }

            AddRecord(_selected.name, p, _rot, _scale, true, true);
        }

        _lastAction = "Spawned " + _count + " x " + _selected.name + " at player feet";
    }

    private void SetNoclip(bool on)
    {
        if (on)
        {
            GameObject p = GetPlayer();

            if (p == null)
                return;

            foreach (Collider2D c in p.GetComponentsInChildren<Collider2D>(true))
            {
                if (c == null || c.isTrigger)
                    continue;

                if (!_noclipSaved.ContainsKey(c))
                    _noclipSaved[c] = c.enabled;

                c.enabled = false;
            }
        }
        else
        {
            foreach (KeyValuePair<Collider2D, bool> kv in _noclipSaved)
            {
                if (kv.Key != null)
                    kv.Key.enabled = kv.Value;
            }

            _noclipSaved.Clear();
        }

        _noclip = on;
    }

    // =====================================================
    // FLY
    // =====================================================

    private void SetFly(bool on)
    {
        if (on == _fly) return;

        GameObject p = GetPlayer();
        if (on)
        {
            if (p == null)
            {
                LogDebug("Fly: Player not found.");
                return;
            }

            _fly = true;
            _flyRbSimulatedSaved.Clear();
            _flyMovementSaved.Clear();

            foreach (Rigidbody2D rb in p.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rb == null) continue;
                _flyRbSimulatedSaved[rb] = rb.simulated;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = false;
            }

            foreach (MovementComponent movement in p.GetComponentsInChildren<MovementComponent>(true))
            {
                if (movement == null) continue;
                _flyMovementSaved[movement] = movement.enabled;
                movement.enabled = false;
            }

            if (!_noclip)
            {
                SetNoclip(true);
                _flyForcedNoclip = true;
            }

            LogDebug("Fly ON. WASD/Arrow move, Shift faster, Ctrl slower.");
        }
        else
        {
            foreach (KeyValuePair<MovementComponent, bool> kv in _flyMovementSaved)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
            _flyMovementSaved.Clear();

            foreach (KeyValuePair<Rigidbody2D, bool> kv in _flyRbSimulatedSaved)
                if (kv.Key != null) kv.Key.simulated = kv.Value;
            _flyRbSimulatedSaved.Clear();

            if (_flyForcedNoclip)
            {
                _flyForcedNoclip = false;
                if (_noclip) SetNoclip(false);
            }

            _fly = false;
            LogDebug("Fly OFF.");
        }
    }

    private void HandleFly()
    {
        if (!_fly || MouseOverWindow()) return;
        GameObject p = GetPlayer();
        if (p == null) return;

        Vector2 input = Vector2.zero;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) input.x -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) input.x += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) input.y -= 1f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) input.y += 1f;
        if (input.sqrMagnitude > 1f) input.Normalize();

        float speed = _flySpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) speed *= 2f;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) speed *= 0.35f;

        if (input.sqrMagnitude > 0f)
            p.transform.position += new Vector3(input.x, input.y, 0f) * speed * Time.unscaledDeltaTime;
        Physics2D.SyncTransforms();
    }

    // =====================================================
    // ENEMY / GOD MODE
    // =====================================================

    private bool IsEnemyEntity(Entity entity)
    {
        if (entity == null || IsPlayerEntity(entity)) return false;
        return entity.GetComponentsInChildren<AI_Dog>(true).Length > 0 ||
               entity.GetComponentsInChildren<AI_Walker>(true).Length > 0 ||
               entity.GetComponentsInChildren<AI_Flyer>(true).Length > 0 ||
               entity.GetComponentsInChildren<AI_MoveTowards>(true).Length > 0 ||
               entity.GetComponentsInChildren<State_Chase>(true).Length > 0 ||
               entity.GetComponentsInChildren<State_ChaseFacehuggerBig>(true).Length > 0 ||
               entity.GetComponentsInChildren<State_FlyTowards>(true).Length > 0 ||
               entity.GetComponentsInChildren<State_Pounce>(true).Length > 0 ||
               entity.GetComponentsInChildren<Raper>(true).Length > 0;
    }

    private bool IsEnemyAIComponent(Component c)
    {
        if (c == null) return false;
        GameObject go = c.gameObject;
        Entity entity = go.GetComponentInParent<Entity>();
        if (!IsEnemyEntity(entity)) return false;

        string n = c.GetType().Name;
        return n == "AI_Dog" || n == "AI_Walker" || n == "AI_Flyer" ||
               n == "AI_MoveTowards" || n == "State_Chase" ||
               n == "State_ChaseFacehuggerBig" || n == "State_FlyTowards" ||
               n == "State_Pounce" || n == "Raper";
    }

    private void SetEnemyStealth(bool on)
    {
        if (on == _aiStealth) return;
        _aiStealth = on;
        _nextStealthScan = 0f;

        if (!on)
        {
            RestoreEnemyStealth();
            LogDebug("God Mode OFF (AI stealth restored; player health untouched).");
            return;
        }

        LogDebug("God Mode ON (AI stealth only; player health untouched).");
        ApplyEnemyStealth();
    }

    private void ApplyEnemyStealth()
    {
        GameObject player = GetPlayer();
        if (player == null) return;

        Vector2 decoy = new Vector2(player.transform.position.x + 10000f, player.transform.position.y + 10000f);

        int patched = 0;
        foreach (MonoBehaviour behaviour in FindObjectsOfType<MonoBehaviour>(true))
        {
            if (behaviour == null || !IsEnemyAIComponent(behaviour)) continue;
            PatchEnemyAIComponent(behaviour, decoy);
            patched++;
        }
        if (patched == 0)
            LogDebug("God Mode scan: no known enemy AI components found.");
        else
            LogDebug("God Mode scan: patched " + patched + " enemy AI components.");
    }

    private void PatchEnemyAIComponent(Component component, Vector2 decoy)
    {
        Type type = component.GetType();
        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (FieldInfo field in fields)
        {
            if (field.IsStatic) continue;
            string n = field.Name;
            bool targetField = n == "_destination" || n == "_target" ||
                               n == "_targetPosition" || n == "_playerPosition";

            if (targetField && field.FieldType == typeof(Vector2SO))
            {
                Vector2SO current = field.GetValue(component) as Vector2SO;
                DevVector2Snapshot snap = _stealthVector2Snapshots.Find(x => x.component == component && x.field == field);
                if (snap == null)
                {
                    Vector2SO decoySO = ScriptableObject.CreateInstance<Vector2SO>();
                    decoySO.name = "DevTools_StealthTarget_" + type.Name;
                    decoySO.value = decoy;
                    _stealthCreatedTargets.Add(decoySO);
                    _stealthVector2Snapshots.Add(new DevVector2Snapshot
                    {
                        component = component,
                        field = field,
                        original = current,
                        createdSO = decoySO
                    });
                    try { field.SetValue(component, decoySO); } catch { }
                }
                else
                {
                    try
                    {
                        if (snap.createdSO != null) snap.createdSO.value = decoy;
                        if (field.GetValue(component) != snap.createdSO)
                            field.SetValue(component, snap.createdSO);
                    }
                    catch { }
                }
            }
            else if (targetField && field.FieldType == typeof(Vector2Ref))
            {
                Vector2Ref reference = field.GetValue(component) as Vector2Ref;
                if (reference == null) continue;
                DevVector2Snapshot snap = _stealthVector2Snapshots.Find(x => x.component == component && x.field == field);
                if (snap == null)
                {
                    try
                    {
                        Vector2Ref.FieldType source = reference.valueSource;
                        Vector2 oldValue = reference.value;
                        Vector2SO oldSO = reference.so;
                        FieldInfo variableField = typeof(Vector2Ref).GetField("_variableGO", BindingFlags.Instance | BindingFlags.NonPublic);
                        object oldVariableGO = variableField != null ? variableField.GetValue(reference) : null;
                        reference.valueSource = Vector2Ref.FieldType.Value;
                        reference.value = decoy;
                        _stealthVector2Snapshots.Add(new DevVector2Snapshot
                        {
                            component = component,
                            field = field,
                            reference = reference,
                            originalSource = source,
                            originalValue = oldValue,
                            originalSO = oldSO,
                            originalVariableGO = oldVariableGO,
                            original = reference
                        });
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        snap.reference.valueSource = Vector2Ref.FieldType.Value;
                        snap.reference.value = decoy;
                    }
                    catch { }
                }
            }
        }

        FieldInfo blockersField = type.GetField("_blockers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (blockersField != null)
        {
            try
            {
                IList list = blockersField.GetValue(component) as IList;
                if (list != null && _stealthBlocker != null)
                {
                    if (!list.Contains(_stealthBlocker)) list.Add(_stealthBlocker);
                    _stealthBlockerLists[component] = list;
                }
            }
            catch { }
        }
    }

    private void RestoreEnemyStealth()
    {
        foreach (DevVector2Snapshot snap in _stealthVector2Snapshots)
        {
            try
            {
                if (snap.component == null || snap.field == null) continue;
                if (snap.createdSO != null)
                    snap.field.SetValue(snap.component, snap.original);
                else if (snap.reference != null)
                {
                    snap.reference.valueSource = snap.originalSource;
                    if (snap.originalSource == Vector2Ref.FieldType.Value)
                        snap.reference.value = snap.originalValue;
                    snap.reference.so = snap.originalSO;
                    FieldInfo variableField = typeof(Vector2Ref).GetField("_variableGO", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (variableField != null)
                        variableField.SetValue(snap.reference, snap.originalVariableGO);
                }
            }
            catch { }
        }
        _stealthVector2Snapshots.Clear();

        foreach (KeyValuePair<Component, IList> pair in _stealthBlockerLists)
        {
            try
            {
                if (pair.Value != null && _stealthBlocker != null && pair.Value.Contains(_stealthBlocker))
                    pair.Value.Remove(_stealthBlocker);
            }
            catch { }
        }
        _stealthBlockerLists.Clear();

        foreach (Vector2SO so in _stealthCreatedTargets)
            if (so != null) UnityEngine.Object.Destroy(so);
        _stealthCreatedTargets.Clear();
    }

    private Entity FindNearestEnemyToPlayer()
    {
        Vector3 center = GetPlayerFeetPosition();
        Entity best = null;
        float bestSq = float.MaxValue;
        foreach (Entity entity in FindObjectsOfType<Entity>(true))
        {
            if (!IsEnemyEntity(entity)) continue;
            float sq = (entity.transform.position - center).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = entity; }
        }
        return best;
    }

    private void SelectNearestEnemy()
    {
        if (_selectedEnemyFrozen) SetSelectedEnemyFrozen(false);
        ResetSelectedEnemyChanges();
        _selectedEnemy = FindNearestEnemyToPlayer();
        LogDebug(_selectedEnemy != null ? "Selected enemy: " + _selectedEnemy.name : "No enemy found.");
    }

    private void SetSelectedEnemyFrozen(bool on)
    {
        if (_selectedEnemy == null) { _selectedEnemyFrozen = false; return; }
        if (on == _selectedEnemyFrozen) return;

        if (on)
        {
            _enemyRbSaved.Clear();
            _enemyBehaviourSaved.Clear();
            foreach (Rigidbody2D rb in _selectedEnemy.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rb == null) continue;
                _enemyRbSaved[rb] = rb.simulated;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.simulated = false;
            }
            foreach (Behaviour b in _selectedEnemy.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null) continue;
                string n = b.GetType().Name;
                if (n.StartsWith("AI_", StringComparison.Ordinal) || n.StartsWith("State_", StringComparison.Ordinal))
                {
                    _enemyBehaviourSaved[b] = b.enabled;
                    b.enabled = false;
                }
            }
            _selectedEnemyFrozen = true;
            LogDebug("Enemy frozen: " + _selectedEnemy.name);
        }
        else
        {
            foreach (KeyValuePair<Behaviour, bool> kv in _enemyBehaviourSaved)
                if (kv.Key != null) kv.Key.enabled = kv.Value;
            foreach (KeyValuePair<Rigidbody2D, bool> kv in _enemyRbSaved)
                if (kv.Key != null) kv.Key.simulated = kv.Value;
            _enemyBehaviourSaved.Clear();
            _enemyRbSaved.Clear();
            _selectedEnemyFrozen = false;
            LogDebug("Enemy unfrozen.");
        }
    }

    private void SetSelectedEnemySpeed(float multiplier)
    {
        _selectedEnemySpeed = Mathf.Clamp(multiplier, 0f, 3f);
        ResetSelectedEnemySpeedFields();
        if (_selectedEnemy == null || Mathf.Approximately(_selectedEnemySpeed, 1f)) return;

        foreach (Component component in _selectedEnemy.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsStatic || field.FieldType != typeof(float)) continue;
                string n = field.Name.ToLowerInvariant();
                if (!n.Contains("speed") || n.Contains("variation")) continue;
                try
                {
                    float original = (float)field.GetValue(component);
                    _enemyFloatSnapshots.Add(new DevFloatSnapshot { component = component, field = field, original = original });
                    field.SetValue(component, original * _selectedEnemySpeed);
                }
                catch { }
            }
        }
    }

    private void ResetSelectedEnemySpeedFields()
    {
        foreach (DevFloatSnapshot snap in _enemyFloatSnapshots)
        {
            try { if (snap.component != null && snap.field != null) snap.field.SetValue(snap.component, snap.original); } catch { }
        }
        _enemyFloatSnapshots.Clear();
    }

    private void ResetSelectedEnemyChanges()
    {
        ResetSelectedEnemySpeedFields();
        _selectedEnemySpeed = 1f;
    }

    private void UnlockAllEquipables(bool persist)
    {
        try
        {
            SaveManager manager = FindObjectOfType<SaveManager>(true);
            if (manager == null) { LogDebug("SaveManager not found."); return; }

            FieldInfo ownedField = typeof(SaveManager).GetField("_ownedEquipables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo allField = typeof(SaveManager).GetField("_allEquipables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ownedField == null || allField == null) { LogDebug("Equipable fields not found."); return; }

            ListSO<Equipable> owned = ownedField.GetValue(manager) as ListSO<Equipable>;
            ListSO<Equipable> all = allField.GetValue(manager) as ListSO<Equipable>;
            if (owned == null || all == null) { LogDebug("Equipable lists not available."); return; }

            int added = 0;
            foreach (Equipable item in all)
            {
                if (item == null || owned.Contains(item)) continue;
                owned.Add(item);
                added++;
            }
            if (persist) manager.Dirty();
            LogDebug("Unlock all equipables: added=" + added + ", persist=" + persist);
        }
        catch (Exception ex)
        {
            _lastError = "Unlock all: " + ex.Message;
            LogDebug(_lastError);
        }
    }

    // =====================================================
    // TEST AREA
    // =====================================================

    private void UpdateTestArea()
    {
        if (!_testAreaFollowPlayer) return;
        Vector3 feet = GetPlayerFeetPosition();
        if (GetPlayer() != null) _testAreaCenter = new Vector2(feet.x, feet.y);
    }

    private Rect GetTestAreaRect()
    {
        UpdateTestArea();
        return new Rect(
            _testAreaCenter.x - _testAreaSize.x * 0.5f,
            _testAreaCenter.y - _testAreaSize.y * 0.5f,
            _testAreaSize.x,
            _testAreaSize.y);
    }

    private void DrawTestAreaOverlay()
    {
        if (!_testAreaVisible || Camera.main == null) return;
        Rect area = GetTestAreaRect();
        Vector3 a = Camera.main.WorldToScreenPoint(new Vector3(area.xMin, area.yMin, 0f));
        Vector3 b = Camera.main.WorldToScreenPoint(new Vector3(area.xMax, area.yMax, 0f));
        float x = Mathf.Min(a.x, b.x);
        float y = Screen.height - Mathf.Max(a.y, b.y);
        float w = Mathf.Abs(b.x - a.x);
        float h = Mathf.Abs(b.y - a.y);
        GUI.color = new Color(1f, 1f, 0f, 0.35f);
        GUI.Box(new Rect(x, y, w, h), "");
        GUI.color = Color.white;
    }

    // =====================================================
    // CAMERA
    // =====================================================

    private void StartCamOverride()
    {
        if (_camOverride)
            return;

        Camera c = Camera.main;

        if (c != null && c.orthographic)
            _camSize = c.orthographicSize;

        _camOverride = true;
    }

    private void ApplyCamera(Camera cam)
    {
        if (!_camOverride || cam == null || !cam.orthographic)
            return;

        cam.orthographicSize = _camSize;
    }

    private void OnCamPreCull(Camera cam)
    {
        if (_camOverride && cam == Camera.main)
            ApplyCamera(cam);
    }

    private void OnSrpBegin(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
    {
        if (_camOverride && cam == Camera.main)
            ApplyCamera(cam);
    }

    private void LogDebug(string message)
    {
        _lastAction = message ?? "";
        try
        {
            Logger.LogInfo("[DevTools] " + (_lastAction ?? ""));
        }
        catch { }
    }

    // =====================================================
    // GUI
    // =====================================================

    private void OnGUI()
    {
        Event e = Event.current;

        if (e.type == EventType.KeyDown &&
            (e.keyCode == KeyCode.Return ||
             e.keyCode == KeyCode.KeypadEnter ||
             e.keyCode == KeyCode.Escape))
        {
            GUI.FocusControl("");
        }

        if (e.type == EventType.MouseDown && (!_show || !_win.Contains(e.mousePosition)))
            GUI.FocusControl("");

        if (!_show)
        {
            _typing = false;
            return;
        }

        if (e.type == EventType.Repaint)
            _typing = GUI.GetNameOfFocusedControl().StartsWith("txt_");

        DrawTestAreaOverlay();
        _win = GUILayout.Window(WindowId, _win, DrawWindow, "Dev Tools 2.2 (F11)");

        if (_selected != null)
        {
            Vector3 m = Input.mousePosition;

            GUI.Label(
                new Rect(m.x + 14f, Screen.height - m.y + 10f, 300f, 22f),
                "MMB: " + _selected.name);
        }
    }

    private void DrawWindow(int id)
    {
        _tab = GUILayout.Toolbar(_tab, Tabs);

        switch (_tab)
        {
            case 0: DrawPlaceTab(); break;
            case 1: DrawMapTab(); break;
            case 2: DrawPlayerTab(); break;
            case 3: DrawEnemiesTab(); break;
            case 4: DrawCameraTab(); break;
            case 5: DrawDebugTab(); break;
        }

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    private void DrawPlaceTab()
    {
        if (Event.current.type == EventType.Layout)
            RebuildFilter();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(50f));
        GUI.SetNextControlName("txt_search");
        _search = GUILayout.TextField(_search);

        if (GUILayout.Button("Reload", GUILayout.Width(60f)))
            RefreshPrefabs();

        GUILayout.EndHorizontal();

        _category = GUILayout.Toolbar(_category, Categories);

        GUILayout.Label("Prefabs: " + _filtered.Count + " / " + _prefabs.Count);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(230f));

        int shown = 0;

        foreach (GameObject g in _filtered)
        {
            if (shown++ >= 150)
            {
                GUILayout.Label("... còn nữa, gõ vào ô Search để lọc");
                break;
            }

            bool sel = g == _selected;

            if (GUILayout.Button((sel ? "> " : "") + g.name))
                _selected = g;
        }

        GUILayout.EndScrollView();

        GUILayout.Label("Selected: " + (_selected != null ? _selected.name : "(none)"));

        GUILayout.Label("Count: " + _count);
        _count = Mathf.RoundToInt(GUILayout.HorizontalSlider(_count, 1f, 50f));

        GUILayout.Label("Spread radius: " + _spread.ToString("0.0"));
        _spread = GUILayout.HorizontalSlider(_spread, 0f, 15f);

        GUILayout.Label("Rotation Z: " + _rot.ToString("0"));
        _rot = GUILayout.HorizontalSlider(_rot, 0f, 360f);

        GUILayout.Label("Scale x: " + _scale.ToString("0.00"));
        _scale = GUILayout.HorizontalSlider(_scale, 0.1f, 5f);

        GUILayout.BeginHorizontal();
        _snap = GUILayout.Toggle(_snap, "Snap to grid", GUILayout.Width(110f));
        GUILayout.Label("Grid: " + _grid.ToString("0.0"), GUILayout.Width(70f));
        _grid = GUILayout.HorizontalSlider(_grid, 0.5f, 8f);
        GUILayout.EndHorizontal();

        if (GUILayout.Button("SPAWN UNDER PLAYER FEET"))
            PlaceSelectedAtPlayerFeet();

        GUILayout.Label("MMB / Spawn = PLAYER FEET | Delete = remove | Ctrl+Z = undo");
    }

    private void DrawMapTab()
    {
        if (Event.current.type == EventType.Layout)
        {
            _statRecords = CountRecords();
            _statChunks = _chunks.Count;

            int live = 0;

            foreach (KeyValuePair<Vector2Int, List<DevPlaced>> kv in _chunks)
            {
                foreach (DevPlaced r in kv.Value)
                {
                    if (r.live != null)
                        live++;
                }
            }

            _statLive = live;
        }

        GUILayout.Label("Map name");
        GUI.SetNextControlName("txt_map");
        _mapName = GUILayout.TextField(_mapName);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Save"))
            SaveMap(_mapName);

        if (GUILayout.Button("Load"))
            LoadMap(_mapName);

        if (GUILayout.Button("Undo"))
            UndoLast();

        if (GUILayout.Button("Clear"))
            ClearAll();

        GUILayout.EndHorizontal();

        GUILayout.Space(8f);

        _streaming = GUILayout.Toggle(_streaming, "Chunk streaming (only keep objects near the player)");

        GUILayout.Label("Load radius: " + _loadRadius.ToString("0") +
                        "  (unload at +" + ChunkSize.ToString("0") + ")");
        _loadRadius = GUILayout.HorizontalSlider(_loadRadius, 20f, 300f);

        GUILayout.Space(6f);
        GUILayout.Label("TEST AREA");
        _testAreaVisible = GUILayout.Toggle(_testAreaVisible, "Show test area");
        _testAreaFollowPlayer = GUILayout.Toggle(_testAreaFollowPlayer, "Center on player feet");
        GUILayout.Label("Width: " + _testAreaSize.x.ToString("0.0"));
        _testAreaSize.x = GUILayout.HorizontalSlider(_testAreaSize.x, 2f, 200f);
        GUILayout.Label("Height: " + _testAreaSize.y.ToString("0.0"));
        _testAreaSize.y = GUILayout.HorizontalSlider(_testAreaSize.y, 2f, 120f);
        if (GUILayout.Button("Set test area center to player feet"))
        {
            Vector3 feet = GetPlayerFeetPosition();
            _testAreaCenter = new Vector2(feet.x, feet.y);
        }

        GUILayout.Label("Records: " + _statRecords +
                        " | Chunks: " + _statChunks +
                        " | Live: " + _statLive);

        GUILayout.Space(8f);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Saved maps (click to load)");

        if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
            _pendingMapListRefresh = true;

        GUILayout.EndHorizontal();

        foreach (string m in _savedMaps)
        {
            if (GUILayout.Button(m))
            {
                _mapName = m;
                LoadMap(m);
            }
        }
    }

    private void DrawPlayerTab()
    {
        GameObject p = GetPlayer();
        GUILayout.Label(p != null ? "Player: " + p.name : "Player: NOT FOUND");
        GUILayout.Label("Player feet: " + GetPlayerFeetPosition().ToString("F2"));
        GUILayout.Label("Mouse world: " + MouseWorld().ToString("F1"));

        if (GUILayout.Button("Teleport to mouse (T)"))
            TeleportPlayerTo(MouseWorld());

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save position") && p != null)
        {
            _savedPos = p.transform.position;
            _hasSavedPos = true;
        }
        if (GUILayout.Button("Go to saved") && _hasSavedPos)
            TeleportPlayerTo(_savedPos);
        GUILayout.EndHorizontal();

        bool nc = GUILayout.Toggle(_noclip, "Noclip");
        if (nc != _noclip) SetNoclip(nc);

        bool fly = GUILayout.Toggle(_fly, "Fly");
        if (fly != _fly) SetFly(fly);
        GUILayout.Label("Fly speed: " + _flySpeed.ToString("0.0"));
        _flySpeed = GUILayout.HorizontalSlider(_flySpeed, 1f, 60f);

        bool stealth = GUILayout.Toggle(_aiStealth, "God Mode (enemy AI stealth only)");
        if (stealth != _aiStealth) SetEnemyStealth(stealth);
        GUILayout.Label("Does NOT change HP. Enemy target/destination inputs are blocked/redirected.");

        GUILayout.Space(6f);
        if (GUILayout.Button("Unlock all equipables (runtime)")) UnlockAllEquipables(false);
        if (GUILayout.Button("Unlock all equipables + save")) UnlockAllEquipables(true);
        if (GUILayout.Button("Debug player discovery")) LogPlayerComponents();
    }

    private void LogPlayerComponents()
    {
        GameObject p = GetPlayer();
        if (p == null)
        {
            LogDebug("Player discovery FAILED. Tag + PlayerInputComponent + PlayerWeaponComponent + MovementComponent + Entity(id=Player) all checked.");
            return;
        }

        LogDebug("Player root = " + p.name + " | feet=" + GetPlayerFeetPosition().ToString("F2"));
        foreach (Component c in p.GetComponentsInChildren<Component>(true))
        {
            if (c != null) LogDebug("  component: " + c.GetType().FullName + " @ " + c.gameObject.name);
        }
    }

    private void DrawEnemiesTab()
    {
        GUILayout.Label("Selected enemy: " + (_selectedEnemy != null ? _selectedEnemy.name : "(none)"));
        if (_selectedEnemy != null)
            GUILayout.Label("Position: " + _selectedEnemy.transform.position.ToString("F1"));

        if (GUILayout.Button("Select nearest enemy"))
            SelectNearestEnemy();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Freeze") && _selectedEnemy != null) SetSelectedEnemyFrozen(true);
        if (GUILayout.Button("Unfreeze") && _selectedEnemy != null) SetSelectedEnemyFrozen(false);
        GUILayout.EndHorizontal();

        GUILayout.Label("Speed x" + _selectedEnemySpeed.ToString("0.00"));
        float speed = GUILayout.HorizontalSlider(_selectedEnemySpeed, 0f, 3f);
        if (!Mathf.Approximately(speed, _selectedEnemySpeed)) SetSelectedEnemySpeed(speed);
        if (GUILayout.Button("Reset enemy changes")) ResetSelectedEnemyChanges();

        GUILayout.Space(8f);
        GUILayout.Label("God Mode: " + (_aiStealth ? "ON" : "OFF"));
        if (GUILayout.Button(_aiStealth ? "Disable AI stealth" : "Enable AI stealth"))
            SetEnemyStealth(!_aiStealth);
    }

    private void DrawDebugTab()
    {
        GUILayout.Label("Scene: " + SceneManager.GetActiveScene().name);
        GameObject p = GetPlayer();
        GUILayout.Label("Player: " + (p != null ? p.name : "NOT FOUND"));
        GUILayout.Label("Feet: " + GetPlayerFeetPosition().ToString("F2"));
        GUILayout.Label("Prefabs: " + _prefabs.Count);
        GUILayout.Label("Noclip: " + (_noclip ? "ON" : "OFF") + " | Fly: " + (_fly ? "ON" : "OFF"));
        GUILayout.Label("AI stealth: " + (_aiStealth ? "ON" : "OFF"));
        GUILayout.Label("Last action: " + _lastAction);
        if (!string.IsNullOrEmpty(_lastError)) GUILayout.Label("Last error: " + _lastError);
        if (GUILayout.Button("Refresh prefab registry")) RefreshPrefabs();
        if (GUILayout.Button("Debug player discovery")) LogPlayerComponents();
    }

    private void DrawCameraTab()
    {
        bool ov = GUILayout.Toggle(_camOverride, "Override camera size");

        if (ov != _camOverride)
        {
            if (ov)
                StartCamOverride();
            else
                _camOverride = false;
        }

        GUILayout.Label("Ortho size: " + _camSize.ToString("0.0"));

        float s = GUILayout.HorizontalSlider(_camSize, 2f, 120f);

        if (!Mathf.Approximately(s, _camSize))
        {
            _camSize = s;
            _camOverride = true;
        }

        Camera cam = Camera.main;

        if (cam != null && !cam.orthographic)
            GUILayout.Label("Main camera is perspective: size override is ignored.");

        GUILayout.Label("PageUp/PageDown = zoom out/in | Home = give control back to the game");
        GUILayout.Label("Zoom out far -> raise Map load radius so placed objects stay visible.");
    }
}

