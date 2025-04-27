using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI; // 如果使用标准 UI Text，请保留
using TMPro;          // 如果使用 TextMeshProUGUI，请保留
#if UNITY_EDITOR
using UnityEditor;    // 需要用于 PrefabUtility (仅编辑器)
#endif

// --- 建筑记录类定义 ---
public class BuildingRecord
{
    public GameObject instance;         // 实例化的游戏对象
    public Vector2Int size;             // 占地尺寸 (e.g., 1x1, 2x2)
    public MapGenerator.ZoneType type;  // 区域类型 (道路, 住宅等)
    public Quaternion rotation;         // 旋转

    // --- 交通数据字段 ---
    public int capacity = -1;           // v: 车辆容量 (道路为 5-20, 其他为 -1)
    public int currentVehicles = 0;     // n: 当前车辆数 (道路为 0-30)

    public BuildingRecord(GameObject instance, Vector2Int size, MapGenerator.ZoneType type, Quaternion rotation)
    {
        this.instance = instance;
        this.size = size;
        this.type = type;
        this.rotation = rotation;
    }
}


/// <summary>
/// 生成城市布局，模拟交通，处理路径选择和高亮。
/// 使用独立的 AStar 组件基于通行时间进行寻路计算。
/// 定期根据交通状况自动更新高亮路径。
/// **现在集成了 CarPlacementController 以在生成后放置车辆和墙体。**
/// </summary>
[RequireComponent(typeof(AStar))]
public class MapGenerator : MonoBehaviour
{
    // --- 公共检视面板变量 ---

    [Header("Prefabs & Parent (预设体与父对象)")]
    [Tooltip("重要: 用于所有道路瓦片的预设体。应有 BoxCollider 且尺寸严格匹配 1x1。必须在 Road Layer 上。")]
    [SerializeField] private GameObject roadPrefab;
    [Tooltip("重要: 用于高亮显示路径的道路预设体。应有 BoxCollider 且尺寸/轴心点与 Road Prefab 匹配。")]
    [SerializeField] private GameObject highlightedRoadPrefab;

    [Tooltip("3x3 建筑预设体列表。")] public List<GameObject> buildingPrefabs3x3;
    [Tooltip("2x2 建筑预设体列表。")] public List<GameObject> buildingPrefabs2x2;
    [Tooltip("1x1 建筑预设体列表。")] public List<GameObject> buildingPrefabs1x1;
    [Tooltip("所有生成对象的父级变换。")] public Transform mapParent;

    [Header("Map Dimensions (地图尺寸)")]
    [Min(10)] public int mapWidth = 50;
    [Min(10)] public int mapHeight = 50;

    [Header("Voronoi Road Network Options (Voronoi 道路网络选项)")]
    [Min(2)] public int voronoiSiteSpacing = 15;
    [Range(0, 10)] public int voronoiSiteJitter = 3;

    [Header("Noise Branch Connection Options (噪声分支连接选项)")]
    public float branchNoiseScale = 10.0f;
    [Range(0.0f, 1.0f)] public float noiseBranchThreshold = 0.8f;

    [Header("Map Edge Connection (地图边缘连接)")]
    public bool ensureEdgeConnections = true;

    [Header("Performance & Features (性能与特性)")]
    public Vector2 noiseOffset = Vector2.zero;
    [Min(1)] public int objectsPerFrame = 200;
    [Range(100, 10000)] public int yieldBatchSize = 500;
    public bool asyncGeneration = true;

    [Header("Traffic Simulation (交通模拟)")]
    [Min(0.1f)] public float travelTimeBaseCost = 1.0f;
    [Range(0.0f, 2.0f)] public float trafficCongestionThreshold = 1.0f;
    [Tooltip("车辆计数和路径重新计算的更新频率（秒）。")]
    [Min(1.0f)] public float vehicleUpdateInterval = 15.0f;
    [Tooltip("可选: 用于显示悬停信息的 TextMeshProUGUI 元素。")] public TextMeshProUGUI hoverInfoTextElement;

    [Header("Pathfinding & Highlighting (寻路与高亮)")]
    [Tooltip("用于射线投射以仅击中道路瓦片的层。确保道路预设体在此层上。")]
    public LayerMask roadLayerMask = default;

    [Header("Optional Integrations (可选集成)")]
    [Tooltip("（可选）对 CarPlacementController 的引用，用于在生成后放置车辆和墙体。")]
    [SerializeField] private CarPlacementController carPlacementController;

    // --- 私有变量 ---

    public enum ZoneType
    {
        Empty, Road, Residential, Commercial, Industrial
    }
    private ZoneType[,] map;
    private Queue<GameObject> generationQueue = new Queue<GameObject>();
    private Dictionary<Vector2Int, BuildingRecord> trackedObjects = new Dictionary<Vector2Int, BuildingRecord>();
    private Dictionary<Vector2Int, Vector2Int> occupiedTiles = new Dictionary<Vector2Int, Vector2Int>();
    private readonly List<Vector2Int> directions = new List<Vector2Int> { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
    private List<Vector2Int> voronoiSites = new List<Vector2Int>();
    private HashSet<Vector2Int> roadTiles = new HashSet<Vector2Int>();
    private List<Vector2Int> noisePoints = new List<Vector2Int>();
    private bool prefabsValidated = false;
    private Coroutine instantiationCoroutine = null;
    private Coroutine mainGenerationCoroutine = null;
    private Coroutine trafficAndPathUpdateCoroutine = null;
    private string currentHoverInfo = "";
    private Camera mainCamera;
    private AStar aStarFinder;
    private Vector2Int? pathStartPoint = null;
    private Vector2Int? pathEndPoint = null;
    private List<GameObject> highlightedPathInstances = new List<GameObject>();
    private List<Vector2Int> currentHighlightedPath = new List<Vector2Int>();


    // --- Unity 生命周期方法 ---

    void Awake()
    {
        aStarFinder = GetComponent<AStar>();
        if (aStarFinder == null)
        {
            Debug.LogError("在 MapGenerator 所在的 GameObject 上找不到 AStar 组件！寻路功能将无法工作。");
        }
    }

    void Start()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("MapGenerator 需要场景中有一个标签为 'MainCamera' 的主摄像机！");
        }
        if (hoverInfoTextElement != null)
        {
            hoverInfoTextElement.text = "";
        }
        if (mainGenerationCoroutine == null)
        {
            Debug.Log("请按 Play 按钮旁的 'Start Generation' 按钮，或右键点击脚本 Inspector > 'Start Generation' 开始生成。");
            // StartGenerationProcess(); // 取消注释以在构建中自动开始
        }
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0)) HandleMapClick(0);
        else if (Input.GetMouseButtonDown(1)) HandleMapClick(1);
        if (map != null) HandleMouseHover();
    }

    void OnDestroy()
    {
        StopTrafficAndPathUpdates();
        if (mainGenerationCoroutine != null) StopCoroutine(mainGenerationCoroutine);
        if (instantiationCoroutine != null) StopCoroutine(instantiationCoroutine);
    }

    void OnGUI()
    {
        if (hoverInfoTextElement == null && !string.IsNullOrEmpty(currentHoverInfo))
        {
            GUI.backgroundColor = new Color(0, 0, 0, 0.7f);
            GUI.contentColor = Color.white;
            GUI.Box(new Rect(10, Screen.height - 80, 250, 70), currentHoverInfo);
        }

        string startText = pathStartPoint.HasValue ? pathStartPoint.Value.ToString() : "未选择";
        string endText = pathEndPoint.HasValue ? pathEndPoint.Value.ToString() : "未选择";
        string pathInfo = "路径选择:\n" + $"起点: {startText}\n" + $"终点: {endText}";

        if (hoverInfoTextElement == null)
        {
            float yPos = string.IsNullOrEmpty(currentHoverInfo) ? Screen.height - 65 : Screen.height - 140;
            GUI.Box(new Rect(10, yPos, 200, 55), pathInfo);
        }
    }


    // --- 生成流程与控制 ---

    [ContextMenu("Start Generation")]
    public void StartGenerationProcess()
    {
        Debug.Log("StartGenerationProcess: 开始生成流程。");
        if (mainGenerationCoroutine != null)
        {
            Debug.LogWarning("生成已经在进行中！");
            return;
        }
        StopTrafficAndPathUpdates();
        ClearHighlightedPath();
        Debug.Log("StartGenerationProcess: 启动 RunGenerationPipeline 协程。");
        mainGenerationCoroutine = StartCoroutine(RunGenerationPipeline());
    }

    IEnumerator RunGenerationPipeline()
    {
        Debug.Log("RunGenerationPipeline: 协程已启动。");
        if (mapParent == null)
        {
            GameObject parentObj = GameObject.Find("GeneratedCity") ?? new GameObject("GeneratedCity");
            mapParent = parentObj.transform;
            Debug.Log($"RunGenerationPipeline: 设置 mapParent 为 '{mapParent.name}'。");
        }

        ClearPreviousGeneration();
        Debug.Log("RunGenerationPipeline: 已清理之前的生成。");
        ValidatePrefabs();
        Debug.Log($"RunGenerationPipeline: 预设体验证结果: {prefabsValidated}");
        if (!prefabsValidated)
        {
            Debug.LogError("必要的预设体缺失或配置错误。中止生成。");
            mainGenerationCoroutine = null; yield break;
        }
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            Debug.LogError("地图尺寸必须大于 0。");
            mainGenerationCoroutine = null; yield break;
        }

        map = new ZoneType[mapWidth, mapHeight];
        if (noiseOffset == Vector2.zero)
        {
            noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));
        }

        Debug.Log("--- 开始生成阶段 ---");
        Debug.Log("RunGenerationPipeline: 开始 GenerateRoadsPhase。");
        yield return StartCoroutine(GenerateRoadsPhase());
        Debug.Log("RunGenerationPipeline: 完成 GenerateRoadsPhase。");

        Debug.Log("--- 道路生成完毕 --- 等待输入以放置 3x3 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 3x3 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(3));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 3x3 放置完毕 --- 等待输入以放置 2x2 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 2x2 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(2));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 2x2 放置完毕 --- 等待输入以放置 1x1 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 1x1 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(1));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 所有放置阶段完成！ ---");

        StartTrafficAndPathUpdates();

        if (carPlacementController != null)
        {
            Debug.Log("RunGenerationPipeline: 调用 CarPlacementController 初始化墙体和车辆位置。");
            carPlacementController.InitializePlacementAndWalls();
        }
        else
        {
            Debug.Log("RunGenerationPipeline: 未在 MapGenerator 中设置 CarPlacementController 引用。(可选)");
        }

        mainGenerationCoroutine = null;
        Debug.Log("生成完成。请在道路上点击鼠标左键设置起点，右键设置终点。路径将自动更新。");
    }

    IEnumerator WaitForInput()
    {
        Debug.Log("请在 Game 视图中单击鼠标左键或按空格键以继续...");
        while (!Input.GetMouseButtonDown(0) && !Input.GetKeyDown(KeyCode.Space))
        {
            yield return null;
        }
        Debug.Log("收到输入，继续...");
        yield return null;
    }

    void OnValidate()
    {
        if (voronoiSiteSpacing < 1) voronoiSiteSpacing = 1;
        if (mapWidth < 1) mapWidth = 1;
        if (mapHeight < 1) mapHeight = 1;
        if (yieldBatchSize < 10) yieldBatchSize = 10;
        if (objectsPerFrame < 1) objectsPerFrame = 1;
        voronoiSiteJitter = Mathf.Clamp(voronoiSiteJitter, 0, voronoiSiteSpacing / 2);
    }

    void ValidatePrefabs()
    {
        bool essentialRoadsOk = true;
        if (roadPrefab == null) { Debug.LogError("Road Prefab (道路预设体) 缺失！"); essentialRoadsOk = false; }
        if (highlightedRoadPrefab == null) { Debug.LogError("Highlighted Road Prefab (高亮道路预设体) 缺失！"); essentialRoadsOk = false; }

        Action<GameObject, string, bool> checkComponents = (prefab, name, isCritical) => {
            if (prefab != null)
            {
                if (prefab.GetComponent<Collider>() == null) { Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Collider 组件！", prefab); if (isCritical) essentialRoadsOk = false; }
                if (prefab.GetComponentInChildren<Renderer>() == null) { Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Renderer 组件！", prefab); if (isCritical) essentialRoadsOk = false; }
            }
        };
        checkComponents(roadPrefab, "Road Prefab", true);
        checkComponents(highlightedRoadPrefab, "Highlighted Road Prefab", true);

        bool essentialBuildingsOk = buildingPrefabs1x1.Any(p => p != null) || buildingPrefabs2x2.Any(p => p != null) || buildingPrefabs3x3.Any(p => p != null);
        if (!essentialBuildingsOk) Debug.LogWarning("所有建筑预设体列表都为空或无效。将只生成道路。");

        prefabsValidated = essentialRoadsOk;
    }


    // --- 道路生成阶段 ---

    IEnumerator GenerateRoadsPhase()
    {
        Debug.Log("GenerateRoadsPhase: 开始。");
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        roadTiles.Clear(); voronoiSites.Clear(); noisePoints.Clear();

        yield return StartCoroutine(GenerateVoronoiSites());
        yield return StartCoroutine(SelectNoisePoints());
        if (voronoiSites.Count >= 2) yield return StartCoroutine(ComputeVoronoiEdgesAndMarkRoads());
        if (noisePoints.Count > 0 && (roadTiles.Count > 0 || voronoiSites.Count > 0)) yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());
        if (ensureEdgeConnections && (roadTiles.Count > 0 || voronoiSites.Count > 0)) yield return StartCoroutine(EnsureMapEdgeConnections());
        yield return StartCoroutine(EnsureRoadConnectivity());
        Debug.Log($"GenerateRoadsPhase: 道路连通性检查完毕。逻辑道路瓦片数量: {roadTiles.Count}");
        if (roadTiles.Count == 0) Debug.LogWarning("GenerateRoadsPhase: 没有生成任何逻辑道路瓦片！");
        Debug.Log("GenerateRoadsPhase: 开始 InstantiateRoadVisuals。");
        yield return StartCoroutine(InstantiateRoadVisuals());
        Debug.Log("GenerateRoadsPhase: 完成 InstantiateRoadVisuals。");
        Debug.Log("GenerateRoadsPhase: 开始立即实例化对象。");
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null) { Debug.Log("GenerateRoadsPhase: 等待实例化协程。"); yield return instantiationCoroutine; }
        Debug.Log("GenerateRoadsPhase: 完成立即实例化对象。");
        timer.Stop();
        Debug.Log($"--- 道路生成阶段完成 ({roadTiles.Count} 个道路瓦片, {timer.ElapsedMilliseconds}ms) ---");
    }

    IEnumerator GenerateVoronoiSites()
    {
        voronoiSites.Clear();
        int spacing = Mathf.Max(1, voronoiSiteSpacing);
        int jitter = Mathf.Clamp(voronoiSiteJitter, 0, spacing / 2);
        int count = 0;
        for (int x = spacing / 2; x < mapWidth; x += spacing) { for (int y = spacing / 2; y < mapHeight; y += spacing) { int jX = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0; int jY = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0; Vector2Int p = new Vector2Int(Mathf.Clamp(x + jX, 0, mapWidth - 1), Mathf.Clamp(y + jY, 0, mapHeight - 1)); bool tooClose = voronoiSites.Any(site => Mathf.Abs(site.x - p.x) <= 1 && Mathf.Abs(site.y - p.y) <= 1); if (!tooClose && IsInMap(p) && map[p.x, p.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(p)) { voronoiSites.Add(p); } count++; if (asyncGeneration && count % yieldBatchSize == 0) yield return null; } }
        if (voronoiSites.Count < 4 && mapWidth > 10 && mapHeight > 10) { List<Vector2Int> corners = new List<Vector2Int> { new Vector2Int(Mathf.Clamp(spacing / 2, 1, mapWidth - 2), Mathf.Clamp(spacing / 2, 1, mapHeight - 2)), new Vector2Int(Mathf.Clamp(mapWidth - 1 - spacing / 2, 1, mapWidth - 2), Mathf.Clamp(spacing / 2, 1, mapHeight - 2)), new Vector2Int(Mathf.Clamp(spacing / 2, 1, mapWidth - 2), Mathf.Clamp(mapHeight - 1 - spacing / 2, 1, mapHeight - 2)), new Vector2Int(Mathf.Clamp(mapWidth - 1 - spacing / 2, 1, mapWidth - 2), Mathf.Clamp(mapHeight - 1 - spacing / 2, 1, mapHeight - 2)) }; corners = corners.Where(IsInMap).Distinct().ToList(); foreach (var corner in corners) { if (!voronoiSites.Any(site => (site - corner).sqrMagnitude < spacing * spacing * 0.1f) && IsInMap(corner) && map[corner.x, corner.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(corner)) { voronoiSites.Add(corner); } } voronoiSites = voronoiSites.Distinct().ToList(); }
        Debug.Log($"生成了 {voronoiSites.Count} 个 Voronoi 站点。");
    }

    IEnumerator SelectNoisePoints()
    {
        noisePoints.Clear();
        float noiseOffsetX = noiseOffset.x; float noiseOffsetY = noiseOffset.y; int checkedCount = 0;
        for (int x = 0; x < mapWidth; x++) { for (int y = 0; y < mapHeight; y++) { Vector2Int currentPos = new Vector2Int(x, y); if (IsInMap(currentPos) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos)) { float normX = (float)x / mapWidth; float normY = (float)y / mapHeight; float nX = noiseOffsetX + normX * branchNoiseScale; float nY = noiseOffsetY + normY * branchNoiseScale; float noiseValue = Mathf.PerlinNoise(nX, nY); if (noiseValue > noiseBranchThreshold) { if (map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos)) { noisePoints.Add(currentPos); } } } checkedCount++; if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null; } }
        Debug.Log($"选择了 {noisePoints.Count} 个噪声点。");
    }

    IEnumerator ComputeVoronoiEdgesAndMarkRoads()
    {
        if (voronoiSites.Count < 2) { Debug.LogWarning("Voronoi 站点少于 2 个。"); yield break; }
        int processed = 0; int marked = 0;
        for (int x = 0; x < mapWidth; x++) { for (int y = 0; y < mapHeight; y++) { Vector2Int currentPos = new Vector2Int(x, y); if (roadTiles.Contains(currentPos) || occupiedTiles.ContainsKey(currentPos)) { processed++; if (asyncGeneration && processed % yieldBatchSize == 0) yield return null; continue; } int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites); if (nearestSiteIndex < 0) continue; foreach (var dir in directions) { Vector2Int neighborPos = currentPos + dir; if (!IsInMap(neighborPos)) continue; int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites); if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex) { TryClaimTileForRoadLogicOnly(currentPos, out bool claimed); if (claimed) marked++; break; } } processed++; if (asyncGeneration && processed % yieldBatchSize == 0) yield return null; } }
        Debug.Log($"标记了 {marked} 个 Voronoi 道路瓦片。");
    }

    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites) { if (sites == null || sites.Count == 0) return -1; int nearestIndex = -1; float minDistSq = float.MaxValue; for (int i = 0; i < sites.Count; i++) { float distSq = (sites[i] - point).sqrMagnitude; if (distSq < minDistSq) { minDistSq = distSq; nearestIndex = i; } } return nearestIndex; }
    Vector2Int? FindNearestSite(Vector2Int point, List<Vector2Int> sites) { int index = FindNearestSiteIndex(point, sites); return index >= 0 ? sites[index] : (Vector2Int?)null; }

    IEnumerator ConnectNoiseAndRoadsWithMST()
    {
        if (noisePoints.Count == 0) yield break;
        HashSet<Vector2Int> anchorNodes = new HashSet<Vector2Int>(roadTiles); anchorNodes.UnionWith(voronoiSites.Where(site => IsInMap(site)));
        if (anchorNodes.Count == 0) { Debug.LogWarning("无锚点连接噪声点。"); yield break; }
        int pathsDrawn = 0; int totalClaimedOnPaths = 0;
        foreach (var noiseStart in noisePoints) { if (!IsInMap(noiseStart) || roadTiles.Contains(noiseStart) || occupiedTiles.ContainsKey(noiseStart) || map[noiseStart.x, noiseStart.y] != ZoneType.Empty) continue; Vector2Int? nearestAnchor = FindNearestPointInSet(noiseStart, anchorNodes); if (nearestAnchor.HasValue) { List<Vector2Int> path = FindPath_InternalFixedCost(noiseStart, new HashSet<Vector2Int> { nearestAnchor.Value }); int claimedOnThisPath = 0; if (path != null && path.Count > 0) { foreach (var roadPos in path) { if (!IsInMap(roadPos)) continue; bool claimed = false; yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result)); if (claimed) { claimedOnThisPath++; anchorNodes.Add(roadPos); } } totalClaimedOnPaths += claimedOnThisPath; } else { Debug.LogWarning($"内部 A* 失败: {noiseStart} -> {nearestAnchor.Value}。"); } } else { Debug.LogWarning($"噪声点 {noiseStart} 未找到锚点。"); } pathsDrawn++; if (asyncGeneration && pathsDrawn % 20 == 0) yield return null; }
        Debug.Log($"尝试连接 {pathsDrawn} 个噪声点，添加了 {totalClaimedOnPaths} 道路瓦片。");
    }

    Vector2Int? FindNearestPointInList(Vector2Int startPoint, List<Vector2Int> targetPoints) { if (targetPoints == null || targetPoints.Count == 0) return null; Vector2Int? nearest = null; float minDistanceSq = float.MaxValue; foreach (Vector2Int target in targetPoints) { float distSq = (startPoint - target).sqrMagnitude; if (distSq < minDistanceSq) { minDistanceSq = distSq; nearest = target; if (minDistanceSq <= 1.1f) break; } } return nearest; }
    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints) { if (targetPoints == null || targetPoints.Count == 0) return null; Vector2Int? nearest = null; float minDistanceSq = float.MaxValue; foreach (Vector2Int target in targetPoints) { float distSq = (startPoint - target).sqrMagnitude; if (distSq < minDistanceSq) { minDistanceSq = distSq; nearest = target; if (minDistanceSq <= 1.1f) break; } } return nearest; }

    IEnumerator EnsureMapEdgeConnections()
    {
        List<Vector2Int> edgeAnchors = new List<Vector2Int> { new Vector2Int(mapWidth / 2, mapHeight - 2), new Vector2Int(mapWidth / 2, 1), new Vector2Int(1, mapHeight / 2), new Vector2Int(mapWidth - 2, mapHeight / 2), new Vector2Int(2, 2), new Vector2Int(mapWidth - 3, 2), new Vector2Int(2, mapHeight - 3), new Vector2Int(mapWidth - 3, mapHeight - 3) }; edgeAnchors = edgeAnchors.Where(p => IsInMap(p) && p.x > 0 && p.x < mapWidth - 1 && p.y > 0 && p.y < mapHeight - 1).Distinct().ToList();
        int connectionsAttempted = 0; int connectionsMade = 0; HashSet<Vector2Int> currentNetworkPoints = new HashSet<Vector2Int>(roadTiles); currentNetworkPoints.UnionWith(voronoiSites.Where(site => IsInMap(site)));
        if (currentNetworkPoints.Count == 0) { Debug.LogWarning("跳过边缘连接：网络为空。"); yield break; }
        foreach (var edgePoint in edgeAnchors) { connectionsAttempted++; if (currentNetworkPoints.Contains(edgePoint)) continue; Vector2Int? connectFrom = FindNearestPointInSet(edgePoint, currentNetworkPoints); if (connectFrom.HasValue) { bool success = false; yield return StartCoroutine(ConnectTwoPoints(connectFrom.Value, edgePoint, result => success = result)); if (success) connectionsMade++; } else { Debug.LogWarning($"未能找到连接点: {edgePoint}"); } if (asyncGeneration && connectionsAttempted % 2 == 0) yield return null; }
        Debug.Log($"尝试 {connectionsAttempted} 次边缘连接，成功 {connectionsMade} 次。");
    }

    IEnumerator ConnectTwoPoints(Vector2Int start, Vector2Int end, Action<bool> callback)
    {
        List<Vector2Int> path = FindPath_InternalFixedCost(start, new HashSet<Vector2Int> { end }); int appliedCount = 0; bool success = false; bool pathBlockedMidway = false;
        if (path != null && path.Count > 1) { for (int i = 0; i < path.Count; ++i) { Vector2Int roadPos = path[i]; if (!IsInMap(roadPos)) continue; bool claimed = false; if (!roadTiles.Contains(roadPos)) { yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result)); } else { claimed = true; } if (claimed) { appliedCount++; } else if (roadPos != start && roadPos != end) { Debug.LogWarning($"路径段 {roadPos} 无法声明。"); pathBlockedMidway = true; } if (asyncGeneration && i > 0 && i % 100 == 0) yield return null; } success = (appliedCount > 0 || roadTiles.Contains(end)) && !pathBlockedMidway; }
        else if (path != null && path.Count == 1 && path[0] == end && roadTiles.Contains(end)) { success = true; } else if (path == null) { Debug.LogWarning($"内部 A* 失败: {start} -> {end}"); success = false; }
        if (asyncGeneration && appliedCount > 0) yield return null; callback?.Invoke(success);
    }

    IEnumerator EnsureRoadConnectivity()
    {
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents(); if (roadComponents.Count <= 1) { Debug.Log($"道路网络连通性：找到 {roadComponents.Count} 个组件。"); yield break; }
        Debug.Log($"发现 {roadComponents.Count} 个组件，尝试连接..."); roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList(); HashSet<Vector2Int> mainNetwork = roadComponents[0]; Debug.Log($"主网络大小: {mainNetwork.Count}"); int connectionsMade = 0; int componentsMerged = 1;
        for (int i = 1; i < roadComponents.Count; i++) { HashSet<Vector2Int> currentComponent = roadComponents[i]; if (currentComponent.Count == 0) continue; Debug.Log($"尝试连接组件 {i + 1} (大小: {currentComponent.Count})..."); bool connected = false; yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork, result => connected = result)); if (connected) { connectionsMade++; componentsMerged++; Debug.Log($"组件 {i + 1} 已连接。"); mainNetwork.UnionWith(currentComponent); } else { Debug.LogWarning($"未能连接组件 {i + 1}。"); } if (asyncGeneration && i % 5 == 0) yield return null; }
        Debug.Log($"连通性检查完成。合并 {componentsMerged}/{roadComponents.Count} 组件 ({connectionsMade} 次连接)。");
    }

    List<HashSet<Vector2Int>> FindAllRoadComponents()
    {
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>(); HashSet<Vector2Int> visited = new HashSet<Vector2Int>(); HashSet<Vector2Int> currentRoadTilesSnapshot = new HashSet<Vector2Int>(roadTiles);
        foreach (Vector2Int startPos in currentRoadTilesSnapshot) { if (!visited.Contains(startPos) && roadTiles.Contains(startPos)) { HashSet<Vector2Int> newComponent = new HashSet<Vector2Int>(); Queue<Vector2Int> queue = new Queue<Vector2Int>(); queue.Enqueue(startPos); visited.Add(startPos); newComponent.Add(startPos); while (queue.Count > 0) { Vector2Int node = queue.Dequeue(); foreach (var dir in directions) { Vector2Int neighbor = node + dir; if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor)) { visited.Add(neighbor); newComponent.Add(neighbor); queue.Enqueue(neighbor); } } } if (newComponent.Count > 0) components.Add(newComponent); } }
        return components;
    }

    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork, Action<bool> callback)
    {
        if (componentToConnect == null || targetNetwork == null || componentToConnect.Count == 0 || targetNetwork.Count == 0) { Debug.LogWarning("ConnectComponentToNetwork: 输入为空。"); callback?.Invoke(false); yield break; }
        Vector2Int? bestStart = null; Vector2Int? bestTarget = null; float minDistanceSq = float.MaxValue; HashSet<Vector2Int> searchSet = (componentToConnect.Count < targetNetwork.Count) ? componentToConnect : targetNetwork; HashSet<Vector2Int> destinationSet = (searchSet == componentToConnect) ? targetNetwork : componentToConnect; int maxSearchPoints = 300 + (int)Mathf.Sqrt(searchSet.Count); var pointsToSearchFrom = searchSet.Count <= maxSearchPoints ? searchSet : searchSet.OrderBy(p => UnityEngine.Random.value).Take(maxSearchPoints); int searchedCount = 0;
        foreach (var startCandidate in pointsToSearchFrom) { Vector2Int? currentNearestTarget = FindNearestPointInSet(startCandidate, destinationSet); if (currentNearestTarget.HasValue) { float distSq = (startCandidate - currentNearestTarget.Value).sqrMagnitude; if (distSq < minDistanceSq) { minDistanceSq = distSq; bestStart = (searchSet == componentToConnect) ? startCandidate : currentNearestTarget.Value; bestTarget = (searchSet == componentToConnect) ? currentNearestTarget.Value : startCandidate; } } searchedCount++; if (minDistanceSq <= 2.0f) break; if (asyncGeneration && searchedCount % 50 == 0) yield return null; }
        if (!bestStart.HasValue || !bestTarget.HasValue) { Debug.LogError($"无法找到连接点。Comp: {componentToConnect.Count}, Target: {targetNetwork.Count}"); callback?.Invoke(false); yield break; }
        if (minDistanceSq <= 2.0f) { Debug.Log($"组件已接近 ({bestStart.Value} -> {bestTarget.Value})。"); callback?.Invoke(true); yield break; }
        bool connected = false; Debug.Log($"尝试连接组件：{bestStart.Value} -> {bestTarget.Value} (DistSq: {minDistanceSq:F1})"); yield return StartCoroutine(ConnectTwoPoints(bestStart.Value, bestTarget.Value, result => connected = result)); callback?.Invoke(connected);
    }

    IEnumerator InstantiateRoadVisuals()
    {
        int roadsProcessed = 0; int placeAttempts = 0; List<Vector2Int> currentRoadTilesSnapshot = new List<Vector2Int>(roadTiles); Debug.Log($"InstantiateRoadVisuals: 处理 {currentRoadTilesSnapshot.Count} 个潜在道路。"); if (roadPrefab == null) { Debug.LogError("Road Prefab 未分配！"); yield break; }
        foreach (Vector2Int pos in currentRoadTilesSnapshot) { placeAttempts++; if (!roadTiles.Contains(pos)) continue; bool isMarkedAsRoad = map[pos.x, pos.y] == ZoneType.Road; bool isAvailable = !occupiedTiles.ContainsKey(pos) || occupiedTiles[pos] == pos; bool alreadyHasTrackedObjectOrigin = trackedObjects.ContainsKey(pos); if (isMarkedAsRoad && isAvailable && !alreadyHasTrackedObjectOrigin) { PlaceObject(pos, Vector2Int.one, roadPrefab, ZoneType.Road, Quaternion.identity); roadsProcessed++; } else if (!isMarkedAsRoad && roadTiles.Contains(pos)) { Debug.LogWarning($"InstantiateRoadVisuals: {pos} 在 roadTiles 中但 map 类型为 '{map[pos.x, pos.y]}'."); } else if (!isAvailable) { Vector2Int occupierOrigin = occupiedTiles.ContainsKey(pos) ? occupiedTiles[pos] : pos; Debug.LogWarning($"InstantiateRoadVisuals: {pos} 被 {occupierOrigin} 占用。"); } else if (alreadyHasTrackedObjectOrigin) { /* Already handled by PlaceObject check? */ } if (asyncGeneration && placeAttempts > 0 && placeAttempts % yieldBatchSize == 0) yield return null; }
        Debug.Log($"InstantiateRoadVisuals: {placeAttempts} 次尝试中触发 {roadsProcessed} 次 PlaceObject。");
    }


    // --- 建筑放置阶段 ---

    IEnumerator FillSpecificSizeGreedy(int sizeToFill) { Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 开始。"); int buildingsPlaced = 0; int tilesChecked = 0; System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew(); Vector2Int buildingSize = Vector2Int.one * sizeToFill; List<GameObject> prefabList; switch (sizeToFill) { case 3: prefabList = buildingPrefabs3x3; break; case 2: prefabList = buildingPrefabs2x2; break; case 1: prefabList = buildingPrefabs1x1; break; default: Debug.LogError($"无效尺寸: {sizeToFill}"); yield break; } if (prefabList == null || !prefabList.Any(p => p != null)) { Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 无有效预设体。"); yield break; } for (int y = 0; y <= mapHeight - sizeToFill; y++) { for (int x = 0; x <= mapWidth - sizeToFill; x++) { tilesChecked++; Vector2Int currentOrigin = new Vector2Int(x, y); if (IsInMap(currentOrigin) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentOrigin)) { if (CanPlaceBuildingHere(currentOrigin, buildingSize)) { GameObject prefab = GetRandomValidPrefab(prefabList); if (prefab != null) { PlaceObject(currentOrigin, buildingSize, prefab, DetermineBuildingType(), Quaternion.identity); buildingsPlaced++; x += (sizeToFill - 1); } else { Debug.LogError($"GetRandomValidPrefab 返回 null！"); } } } if (asyncGeneration && tilesChecked % (yieldBatchSize * 2) == 0) yield return null; } if (asyncGeneration && y % 10 == 0) yield return null; } timer.Stop(); Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 完成。放置 {buildingsPlaced} 个建筑。耗时: {timer.ElapsedMilliseconds} ms。"); yield return null; }
    bool CanPlaceBuildingHere(Vector2Int origin, Vector2Int size) { if (!IsInMap(origin) || !IsInMap(origin + size - Vector2Int.one)) return false; for (int x = origin.x; x < origin.x + size.x; x++) { for (int y = origin.y; y < origin.y + size.y; y++) { Vector2Int currentTile = new Vector2Int(x, y); if (!IsInMap(currentTile) || map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentTile)) return false; } } return true; }
    GameObject GetRandomValidPrefab(List<GameObject> list) { if (list == null) return null; List<GameObject> validPrefabs = list.Where(p => p != null).ToList(); if (validPrefabs.Count == 0) return null; return validPrefabs[UnityEngine.Random.Range(0, validPrefabs.Count)]; }
    ZoneType DetermineBuildingType() { int r = UnityEngine.Random.Range(0, 3); if (r == 0) return ZoneType.Residential; if (r == 1) return ZoneType.Commercial; return ZoneType.Industrial; }


    // --- 核心逻辑：放置、移除、声明 ---

    void PlaceObject(Vector2Int origin, Vector2Int size, GameObject prefab, ZoneType type, Quaternion rotation)
    {
        if (prefab == null) { Debug.LogError($"PlaceObject @ {origin}: 预设体为 null。"); return; }
        Vector2Int extent = origin + size - Vector2Int.one; if (!IsInMap(origin) || !IsInMap(extent)) { Debug.LogError($"PlaceObject: 区域 {origin} 尺寸 {size} 超出边界。"); return; }
        for (int x = origin.x; x < origin.x + size.x; x++) { for (int y = origin.y; y < origin.y + size.y; y++) { Vector2Int tile = new Vector2Int(x, y); if (IsInMap(tile)) { bool isOccupied = occupiedTiles.ContainsKey(tile); bool mapNotEmpty = map[x, y] != ZoneType.Empty; if (isOccupied || mapNotEmpty) { RemoveTrackedObject(tile); } } } }
        Vector3 instantiationPosition; Vector3 targetCornerWorldPos = new Vector3(origin.x, 0, origin.y); Vector3 colliderOffsetFromPivotXZ = Vector3.zero; BoxCollider boxCol = prefab.GetComponent<BoxCollider>(); if (boxCol != null) { Vector3 localBottomLeftOffset = boxCol.center - new Vector3(boxCol.size.x / 2.0f, 0, boxCol.size.z / 2.0f); colliderOffsetFromPivotXZ = new Vector3(localBottomLeftOffset.x, 0, localBottomLeftOffset.z); } else { Collider generalCol = prefab.GetComponent<Collider>(); if (generalCol != null) { Debug.LogWarning($"预设体 {prefab.name} @ {origin}: 无 BoxCollider。", prefab); } else { Debug.LogError($"预设体 {prefab.name} @ {origin}: 无 Collider！", prefab); } }
        instantiationPosition = targetCornerWorldPos - colliderOffsetFromPivotXZ;
        GameObject instance = null; try { instance = Instantiate(prefab, instantiationPosition, rotation, mapParent); } catch (Exception e) { Debug.LogError($"PlaceObject @ {origin}: 实例化 '{prefab.name}' 失败！ {e.Message}"); return; }
        if (instance == null) { Debug.LogError($"PlaceObject @ {origin}: Instantiate 返回 NULL for '{prefab.name}'！"); return; }
        instance.name = $"{prefab.name}_{origin.x}_{origin.y}"; instance.SetActive(false);
        BuildingRecord record = new BuildingRecord(instance, size, type, rotation); trackedObjects[origin] = record;
        if (type == ZoneType.Road && size == Vector2Int.one) { record.capacity = UnityEngine.Random.Range(5, 21); record.currentVehicles = UnityEngine.Random.Range(0, 31); }
        for (int x = origin.x; x < origin.x + size.x; x++) { for (int y = origin.y; y < origin.y + size.y; y++) { Vector2Int currentTile = new Vector2Int(x, y); if (IsInMap(currentTile)) { map[x, y] = type; occupiedTiles[currentTile] = origin; if (type != ZoneType.Road) roadTiles.Remove(currentTile); } } }
        if (type == ZoneType.Road && size == Vector2Int.one) { roadTiles.Add(origin); }
        generationQueue.Enqueue(instance); StartObjectInstantiation();
    }

    void RemoveTrackedObject(Vector2Int pos)
    {
        if (!IsInMap(pos)) return; Vector2Int originToRemove = pos; bool foundInOccupied = false; if (occupiedTiles.TryGetValue(pos, out Vector2Int foundOrigin)) { originToRemove = foundOrigin; foundInOccupied = true; } else if (!trackedObjects.ContainsKey(pos)) { if (map != null && IsInMap(pos) && map[pos.x, pos.y] != ZoneType.Empty) { map[pos.x, pos.y] = ZoneType.Empty; } roadTiles.Remove(pos); return; }
        if (trackedObjects.TryGetValue(originToRemove, out BuildingRecord recordToRemove)) { Vector2Int size = recordToRemove.size; for (int x = originToRemove.x; x < originToRemove.x + size.x; x++) { for (int y = originToRemove.y; y < originToRemove.y + size.y; y++) { Vector2Int currentTile = new Vector2Int(x, y); if (IsInMap(currentTile)) { occupiedTiles.Remove(currentTile); if (map != null) map[x, y] = ZoneType.Empty; roadTiles.Remove(currentTile); } } } trackedObjects.Remove(originToRemove); if (recordToRemove.instance != null) { if (highlightedPathInstances.Contains(recordToRemove.instance)) { highlightedPathInstances.Remove(recordToRemove.instance); } DestroyObjectInstance(recordToRemove.instance); } }
        else { if (foundInOccupied) { Debug.LogError($"RemoveTrackedObject({pos}): 不一致！瓦片被 {originToRemove} 占用，但在 trackedObjects 中无记录。"); occupiedTiles.Remove(pos); if (map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty; roadTiles.Remove(pos); } else { Debug.LogError($"RemoveTrackedObject({pos}): 不一致！初始找到 {originToRemove}，但记录消失。"); if (map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty; roadTiles.Remove(pos); } }
    }

    IEnumerator TryClaimTileForRoad(Vector2Int pos, Action<bool> callback)
    {
        bool success = false;
        if (IsInMap(pos)) { if (roadTiles.Contains(pos)) { success = true; } else { bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty; if (needsDemolition) { RemoveTrackedObject(pos); if (asyncGeneration) yield return null; } if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos)) { map[pos.x, pos.y] = ZoneType.Road; roadTiles.Add(pos); success = true; } else if (roadTiles.Contains(pos)) { success = true; } } }
        callback?.Invoke(success);
    }

    void TryClaimTileForRoadLogicOnly(Vector2Int pos, out bool success)
    {
        success = false; if (!IsInMap(pos)) return; if (roadTiles.Contains(pos)) { success = true; return; }
        bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty; if (needsDemolition) { RemoveTrackedObject(pos); }
        if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos)) { map[pos.x, pos.y] = ZoneType.Road; roadTiles.Add(pos); success = true; } else if (roadTiles.Contains(pos)) { success = true; }
    }


    // --- 实例化与清理 ---

    void StartObjectInstantiation(bool processAllImmediately = false) { if (processAllImmediately) { if (instantiationCoroutine != null) { StopCoroutine(instantiationCoroutine); instantiationCoroutine = null; } int processedCount = 0; while (generationQueue.Count > 0) { GameObject obj = generationQueue.Dequeue(); if (ValidateObjectForActivation(obj)) { obj.SetActive(true); processedCount++; } else if (obj != null) { DestroyObjectInstance(obj); } } } else { if (instantiationCoroutine == null && generationQueue.Count > 0) { instantiationCoroutine = StartCoroutine(ActivateQueuedObjects()); } } }
    IEnumerator ActivateQueuedObjects() { if (generationQueue.Count == 0) { instantiationCoroutine = null; yield break; } int processedSinceYield = 0; int totalActivated = 0; System.Diagnostics.Stopwatch batchTimer = new System.Diagnostics.Stopwatch(); float maxFrameTimeMs = 8.0f; while (generationQueue.Count > 0) { batchTimer.Restart(); int activatedThisBatch = 0; while (generationQueue.Count > 0 && activatedThisBatch < objectsPerFrame) { GameObject obj = generationQueue.Dequeue(); bool isValid = ValidateObjectForActivation(obj); if (isValid) { obj.SetActive(true); activatedThisBatch++; processedSinceYield++; totalActivated++; } else if (obj != null) { DestroyObjectInstance(obj); } if (asyncGeneration && batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs) break; } if (asyncGeneration && (batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs || processedSinceYield >= yieldBatchSize)) { processedSinceYield = 0; yield return null; } else if (!asyncGeneration && generationQueue.Count > 0) yield return null; } instantiationCoroutine = null; }
    bool ValidateObjectForActivation(GameObject obj) { if (obj == null) return false; if (TryParsePositionFromName(obj.name, out Vector2Int origin)) { if (trackedObjects.TryGetValue(origin, out BuildingRecord record)) { if (record.instance == obj) return true; else { Debug.LogWarning($"Validate: 失败 '{obj.name}' @ {origin}。记录指向不同实例。"); return false; } } else { Debug.LogWarning($"Validate: 失败 '{obj.name}' @ {origin}。无记录。"); return false; } } else { Debug.LogError($"Validate: 失败 '{obj.name}'。无法从名称解析。"); return false; } }

    void ClearPreviousGeneration()
    {
        Debug.Log("开始清理之前的生成...");
        if (mainGenerationCoroutine != null) { StopCoroutine(mainGenerationCoroutine); mainGenerationCoroutine = null; }
        if (instantiationCoroutine != null) { StopCoroutine(instantiationCoroutine); instantiationCoroutine = null; }
        StopTrafficAndPathUpdates(); ClearHighlightedPath();
        if (mapParent != null) { for (int i = mapParent.childCount - 1; i >= 0; i--) { Transform child = mapParent.GetChild(i); if (child != null) DestroyObjectInstance(child.gameObject); } } else { GameObject defaultParent = GameObject.Find("GeneratedCity"); if (defaultParent != null) { for (int i = defaultParent.transform.childCount - 1; i >= 0; i--) { Transform child = defaultParent.transform.GetChild(i); if (child != null) DestroyObjectInstance(child.gameObject); } } else { Debug.LogWarning("mapParent 未设置，也找不到 'GeneratedCity'。"); } }
        map = null; trackedObjects.Clear(); occupiedTiles.Clear(); generationQueue.Clear(); roadTiles.Clear(); voronoiSites.Clear(); noisePoints.Clear(); highlightedPathInstances.Clear(); currentHighlightedPath.Clear();
        prefabsValidated = false; currentHoverInfo = ""; if (hoverInfoTextElement != null) hoverInfoTextElement.text = ""; pathStartPoint = null; pathEndPoint = null;
        Debug.Log("之前的生成清理完毕。");
    }

    void DestroyObjectInstance(GameObject obj) { if (obj == null) return; if (Application.isEditor && !Application.isPlaying) { DestroyImmediate(obj); } else { Destroy(obj); } }


    // --- 内部固定成本 A* ---

    private class Internal_PathNode { public Vector2Int position; public float gScore; public float hScore; public float fScore => gScore + hScore; public Internal_PathNode parent; public Internal_PathNode(Vector2Int pos, float g, float h, Internal_PathNode p) { position = pos; gScore = g; hScore = h; parent = p; } }
    private List<Vector2Int> FindPath_InternalFixedCost(Vector2Int start, HashSet<Vector2Int> targets)
    {
        if (targets == null || targets.Count == 0 || !IsInMap(start)) return null; if (targets.Contains(start)) return new List<Vector2Int>() { start }; List<Internal_PathNode> openSet = new List<Internal_PathNode>(); HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>(); Dictionary<Vector2Int, Internal_PathNode> nodeMap = new Dictionary<Vector2Int, Internal_PathNode>(); float startHScore = InternalHeuristic(start, targets); Internal_PathNode startNode = new Internal_PathNode(start, 0f, startHScore, null); openSet.Add(startNode); nodeMap[start] = startNode; int iterations = 0; int maxIterations = mapWidth * mapHeight * 4;
        while (openSet.Count > 0) { iterations++; if (iterations > maxIterations) { Debug.LogError($"内部 A* 迭代超限"); return null; } openSet.Sort((a, b) => a.fScore.CompareTo(b.fScore)); Internal_PathNode current = openSet[0]; openSet.RemoveAt(0); if (targets.Contains(current.position)) return InternalReconstructPath(current); closedSet.Add(current.position); foreach (var dir in directions) { Vector2Int neighborPos = current.position + dir; if (!IsInMap(neighborPos) || closedSet.Contains(neighborPos)) continue; float terrainCost = GetTerrainCost_InternalFixed(neighborPos); if (float.IsPositiveInfinity(terrainCost)) continue; float tentativeGScore = current.gScore + terrainCost; nodeMap.TryGetValue(neighborPos, out Internal_PathNode neighborNode); if (neighborNode == null || tentativeGScore < neighborNode.gScore) { float neighborHScore = InternalHeuristic(neighborPos, targets); if (neighborNode == null) { neighborNode = new Internal_PathNode(neighborPos, tentativeGScore, neighborHScore, current); nodeMap[neighborPos] = neighborNode; openSet.Add(neighborNode); } else { neighborNode.gScore = tentativeGScore; neighborNode.parent = current; } } } }
        Debug.LogWarning($"内部 A* 无法找到路径: {start} -> targets"); return null;
    }
    private float GetTerrainCost_InternalFixed(Vector2Int pos) { if (!IsInMap(pos)) return float.PositiveInfinity; if (occupiedTiles.TryGetValue(pos, out Vector2Int origin) && origin != pos) { if (trackedObjects.TryGetValue(origin, out BuildingRecord occupier) && occupier.type != ZoneType.Road) return float.PositiveInfinity; } if (map == null) return 10f; switch (map[pos.x, pos.y]) { case ZoneType.Road: return 1.0f; case ZoneType.Empty: return 5.0f; default: return float.PositiveInfinity; } }
    private float InternalHeuristic(Vector2Int current, HashSet<Vector2Int> targets) { float minDistance = float.MaxValue; foreach (var target in targets) minDistance = Mathf.Min(minDistance, InternalManhattanDistance(current, target)); return minDistance; }
    private int InternalManhattanDistance(Vector2Int a, Vector2Int b) { return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y); }
    private List<Vector2Int> InternalReconstructPath(Internal_PathNode targetNode) { List<Vector2Int> path = new List<Vector2Int>(); Internal_PathNode current = targetNode; int safetyCounter = 0; int maxPathLength = mapWidth * mapHeight + 1; while (current != null && safetyCounter < maxPathLength) { path.Add(current.position); current = current.parent; safetyCounter++; } if (safetyCounter >= maxPathLength) { Debug.LogError("内部 A* 重构超长！"); return null; } path.Reverse(); return path; }


    // --- 辅助与帮助方法 ---

    bool IsInMap(Vector2Int pos) { return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight; }
    bool TryParsePositionFromName(string name, out Vector2Int position) { position = Vector2Int.zero; if (string.IsNullOrEmpty(name)) return false; try { int lastUnderscore = name.LastIndexOf('_'); if (lastUnderscore <= 0 || lastUnderscore >= name.Length - 1) return false; int secondLastUnderscore = name.LastIndexOf('_', lastUnderscore - 1); if (secondLastUnderscore < 0) return false; string xStr = name.Substring(secondLastUnderscore + 1, lastUnderscore - secondLastUnderscore - 1); string yStr = name.Substring(lastUnderscore + 1); if (int.TryParse(xStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x) && int.TryParse(yStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y)) { position = new Vector2Int(x, y); return true; } else { return false; } } catch (Exception ex) { Debug.LogError($"TryParsePositionFromName Error: {ex.Message} for name '{name}'"); return false; } }
    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet) { Vector2Int current = start; pathTilesSet.Add(current); int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x); while (current.x != end.x) { current.x += xDir; if (!IsInMap(current)) return; pathTilesSet.Add(current); } int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y); while (current.y != end.y) { current.y += yDir; if (!IsInMap(current)) return; pathTilesSet.Add(current); } }


    // --- 交通模拟与自动路径更新 ---

    void StartTrafficAndPathUpdates() { StopTrafficAndPathUpdates(); if (vehicleUpdateInterval > 0 && Application.isPlaying) { Debug.Log($"启动交通/路径更新 (每 {vehicleUpdateInterval}s)"); trafficAndPathUpdateCoroutine = StartCoroutine(UpdateTrafficAndPathCoroutine()); } else if (vehicleUpdateInterval <= 0) { Debug.LogWarning("自动交通/路径更新已禁用 (间隔 <= 0)。"); } else if (!Application.isPlaying) { Debug.Log("非播放模式下不启动交通/路径更新。"); } }
    void StopTrafficAndPathUpdates() { if (trafficAndPathUpdateCoroutine != null) { StopCoroutine(trafficAndPathUpdateCoroutine); trafficAndPathUpdateCoroutine = null; } }
    IEnumerator UpdateTrafficAndPathCoroutine()
    {
        yield return new WaitForSeconds(Mathf.Max(1.0f, vehicleUpdateInterval / 2f));
        while (true) { foreach (BuildingRecord record in trackedObjects.Values.ToList()) { if (record?.type == ZoneType.Road && record.size == Vector2Int.one && record.capacity > 0) { record.currentVehicles = UnityEngine.Random.Range(0, 31); } } if (pathStartPoint.HasValue && pathEndPoint.HasValue) { FindAndHighlightPath(false); } yield return new WaitForSeconds(vehicleUpdateInterval); }
    }
    public float CalculateTravelTime(BuildingRecord roadRecord) { if (roadRecord == null || roadRecord.type != ZoneType.Road || roadRecord.size != Vector2Int.one || roadRecord.capacity <= 0) return float.PositiveInfinity; float loadFactor = (float)roadRecord.currentVehicles / roadRecord.capacity; float congestionMultiplier = (loadFactor <= trafficCongestionThreshold) ? 1.0f : (1.0f + Mathf.Exp(loadFactor)); float baseCost = Mathf.Max(0.01f, travelTimeBaseCost); float finalTime = baseCost * congestionMultiplier; return Mathf.Max(0.01f, finalTime); }
    void HandleMouseHover()
    {
        if (mainCamera == null) return; string infoToShow = ""; Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask)) { int gridX = Mathf.FloorToInt(hit.point.x); int gridY = Mathf.FloorToInt(hit.point.z); Vector2Int gridPos = new Vector2Int(gridX, gridY); if (IsInMap(gridPos) && trackedObjects.TryGetValue(gridPos, out BuildingRecord record)) { if (record.type == ZoneType.Road && record.size == Vector2Int.one && record.instance != null) { float travelTime = CalculateTravelTime(record); infoToShow = $"道路 @ ({gridPos.x},{gridPos.y})\n容量 (v): {record.capacity}\n当前车辆 (n): {record.currentVehicles}\n通行时间: {travelTime:F2}"; } } }
        currentHoverInfo = infoToShow; if (hoverInfoTextElement != null) { hoverInfoTextElement.text = infoToShow; }
    }


    // --- 寻路与高亮 - 用户交互部分 ---

    void HandleMapClick(int mouseButton)
    {
        if (mainCamera == null || map == null || aStarFinder == null) return; Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask)) { int gridX = Mathf.FloorToInt(hit.point.x); int gridY = Mathf.FloorToInt(hit.point.z); Vector2Int clickedGridPos = new Vector2Int(gridX, gridY); if (IsInMap(clickedGridPos) && trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord record) && record.type == ZoneType.Road && record.size == Vector2Int.one) { if (mouseButton == 0) { if (pathStartPoint != clickedGridPos) { pathStartPoint = clickedGridPos; Debug.Log($"起点: {pathStartPoint.Value}"); if (pathEndPoint.HasValue) FindAndHighlightPath(true); else ClearHighlightedPathVisualsOnly(); } } else if (mouseButton == 1) { if (pathEndPoint != clickedGridPos) { pathEndPoint = clickedGridPos; Debug.Log($"终点: {pathEndPoint.Value}"); if (pathStartPoint.HasValue) FindAndHighlightPath(true); else ClearHighlightedPathVisualsOnly(); } } } else { string reason = "?"; if (!IsInMap(clickedGridPos)) reason = "出界"; else if (!trackedObjects.ContainsKey(clickedGridPos)) reason = "无记录"; else if (trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord r)) reason = $"Type={r.type},Size={r.size}"; Debug.LogWarning($"HandleMapClick: 点击 {clickedGridPos} 无效 ({reason}). 命中: {hit.collider.name}"); } }
    }
    void FindAndHighlightPath(bool logStartMessage = true)
    {
        if (!pathStartPoint.HasValue || !pathEndPoint.HasValue) { ClearHighlightedPathVisualsOnly(); return; }
        if (aStarFinder == null) { Debug.LogError("寻路失败：缺少 AStar！"); ClearHighlightedPath(); return; }
        if (pathStartPoint.Value == pathEndPoint.Value) { if (logStartMessage) Debug.Log("寻路：起点终点相同。"); HighlightPath(new List<Vector2Int> { pathStartPoint.Value }); return; }
        if (logStartMessage) Debug.Log($"寻路：请求 {pathStartPoint.Value} -> {pathEndPoint.Value}...");
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew(); List<Vector2Int> path = aStarFinder.FindPath(pathStartPoint.Value, pathEndPoint.Value, GetPathfindingCost, IsTileWalkableForPath, travelTimeBaseCost); timer.Stop();
        if (path != null && path.Count > 0) { float totalTime = CalculateTotalPathTime(path); if (logStartMessage) Debug.Log($"寻路：找到路径 ({path.Count} 瓦片)。时间: {totalTime:F2}。耗时: {timer.ElapsedMilliseconds}ms"); HighlightPath(path); } else { if (logStartMessage) Debug.LogWarning($"寻路：未找到路径 {pathStartPoint.Value} -> {pathEndPoint.Value}。耗时: {timer.ElapsedMilliseconds}ms"); ClearHighlightedPathVisualsOnly(); }
    }
    private float GetPathfindingCost(Vector2Int pos) { if (!IsInMap(pos)) return float.PositiveInfinity; if (trackedObjects.TryGetValue(pos, out BuildingRecord record) && record.type == ZoneType.Road && record.size == Vector2Int.one) { float time = CalculateTravelTime(record); return Mathf.Max(0.01f, time); } return float.PositiveInfinity; }
    private bool IsTileWalkableForPath(Vector2Int pos) { if (!IsInMap(pos)) return false; return trackedObjects.TryGetValue(pos, out BuildingRecord record) && record.type == ZoneType.Road && record.size == Vector2Int.one; }
    float CalculateTotalPathTime(List<Vector2Int> path) { if (path == null || path.Count == 0) return 0f; float totalTime = 0f; for (int i = 0; i < path.Count; i++) { float cost = GetPathfindingCost(path[i]); if (float.IsPositiveInfinity(cost)) { Debug.LogError($"CalcPathTime: 路径含不可通行瓦片 {path[i]}！"); return float.PositiveInfinity; } totalTime += cost; } return totalTime; }
    void HighlightPath(List<Vector2Int> path)
    {
        if (highlightedRoadPrefab == null || roadPrefab == null) { Debug.LogError("无法高亮：预设体未分配！"); return; }
        ClearHighlightedPathVisualsOnly(); currentHighlightedPath = (path != null) ? new List<Vector2Int>(path) : new List<Vector2Int>(); if (currentHighlightedPath.Count == 0) return;
        foreach (Vector2Int pos in currentHighlightedPath) { if (trackedObjects.TryGetValue(pos, out BuildingRecord record) && record.type == ZoneType.Road && record.size == Vector2Int.one && record.instance != null) { GameObject currentInstance = record.instance; bool alreadyHighlighted = currentInstance.name.StartsWith("HighlightedRoad_"); if (alreadyHighlighted) { if (!highlightedPathInstances.Contains(currentInstance)) highlightedPathInstances.Add(currentInstance); } else { Vector3 position = currentInstance.transform.position; Quaternion rotation = currentInstance.transform.rotation; GameObject newHighlightInstance = Instantiate(highlightedRoadPrefab, position, rotation, mapParent); newHighlightInstance.name = $"HighlightedRoad_{pos.x}_{pos.y}"; record.instance = newHighlightInstance; highlightedPathInstances.Add(newHighlightInstance); DestroyObjectInstance(currentInstance); } } else { string reason = "?"; if (!trackedObjects.ContainsKey(pos)) reason = "无记录"; else if (trackedObjects.TryGetValue(pos, out BuildingRecord r)) reason = $"T={r.type},S={r.size},I={(r.instance == null ? "null" : "ok")}"; Debug.LogWarning($"HighlightPath: 无法在 {pos} 高亮 ({reason})"); } }
    }
    void ClearHighlightedPath() { ClearHighlightedPathVisualsOnly(); currentHighlightedPath.Clear(); pathStartPoint = null; pathEndPoint = null; }
    void ClearHighlightedPathVisualsOnly()
    {
        if (roadPrefab == null) { Debug.LogError("无法清除高亮：Road Prefab 未分配！"); return; }
        if (highlightedPathInstances.Count == 0) return; List<GameObject> instancesToRevert = new List<GameObject>(highlightedPathInstances); highlightedPathInstances.Clear();
        foreach (GameObject highlightedInstance in instancesToRevert) { if (highlightedInstance == null) continue; if (TryParsePositionFromName(highlightedInstance.name, out Vector2Int pos)) { if (trackedObjects.TryGetValue(pos, out BuildingRecord record)) { if (record.instance == highlightedInstance) { Vector3 position = highlightedInstance.transform.position; Quaternion rotation = highlightedInstance.transform.rotation; GameObject originalInstance = Instantiate(roadPrefab, position, rotation, mapParent); originalInstance.name = $"{roadPrefab.name}_{pos.x}_{pos.y}"; record.instance = originalInstance; DestroyObjectInstance(highlightedInstance); } else { Debug.LogWarning($"ClearVisuals: {pos} 记录不再指向高亮。销毁。"); DestroyObjectInstance(highlightedInstance); } } else { Debug.LogError($"ClearVisuals: 找不到 {pos} 记录。销毁高亮 '{highlightedInstance.name}'。"); DestroyObjectInstance(highlightedInstance); } } else { Debug.LogWarning($"ClearVisuals: 无法从名称 '{highlightedInstance.name}' 解析位置。销毁。"); DestroyObjectInstance(highlightedInstance); } }
    }

    // ---- 已修正 IsPrefabSource 方法 ----
    bool IsPrefabSource(GameObject instance, GameObject prefab)
    {
        if (instance == null || prefab == null) return false;

        // --- 编辑器特定检查 ---
#if UNITY_EDITOR
        // 仅在编辑器非播放模式下使用 PrefabUtility，避免运行时开销
        if (!Application.isPlaying)
        {
            try
            {
                // 尝试获取实例对应的预设体源
                GameObject sourcePrefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(instance);
                // 比较源预设体是否与传入的预设体相同
                return sourcePrefab == prefab;
            }
            catch (Exception e)
            {
                // 如果检查过程中出错，记录警告并返回 false
                Debug.LogWarning($"IsPrefabSource (Editor Check) failed for instance '{instance.name}': {e.Message}");
                return false;
            }
        }
        // 如果在编辑器播放模式下，则继续执行下面的运行时检查
#endif

        // --- 运行时检查 (基于名称) ---
        // 这是备用检查，不如编辑器检查可靠，但在构建版本中可用
        // 确保这行 return 在 #endif 之后的新行上
        return instance.name.StartsWith(prefab.name);

    } // <-- 方法结束括号


    // --- 公共访问器 ---

    /// <summary>
    /// 返回当前逻辑道路瓦片位置的 HashSet 副本。
    /// 这是为了让外部脚本（如 CarPlacementController）可以安全地访问道路信息。
    /// </summary>
    public HashSet<Vector2Int> GetRoadTiles()
    {
        // 返回副本以防止外部修改内部集合
        return new HashSet<Vector2Int>(this.roadTiles);
    }

} // --- MapGenerator 类结束 ---