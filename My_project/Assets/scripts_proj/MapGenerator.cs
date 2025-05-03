using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if UNITY_EDITOR
using UnityEditor; 
#endif

#region Building Record Class Definition
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
#endregion // Building Record Class Definition

/// <summary>
/// 生成城市布局，模拟交通，处理路径选择和高亮。
/// 使用独立的 AStar 组件基于通行时间进行寻路计算。
/// 定期根据交通状况自动更新高亮路径。
/// 集成了 CarPlacementController 以在生成后放置车辆和墙体。
/// </summary>
[RequireComponent(typeof(AStar))]
public class MapGenerator : MonoBehaviour
{
    #region Public Inspector Variables
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
    #endregion // Public Inspector Variables

    #region Private Variables & Enum
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
    #endregion // Private Variables & Enum

    #region Unity Lifecycle Methods
    // --- Unity 生命周期方法 ---

    void Awake()
    {
        aStarFinder = GetComponent<AStar>();
        if (aStarFinder == null)
        {
            // Debug.LogError("在 MapGenerator 所在的 GameObject 上找不到 AStar 组件！寻路功能将无法工作。");
        }
    }

    void Start()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            // Debug.LogError("MapGenerator 需要场景中有一个标签为 'MainCamera' 的主摄像机！");
        }
        if (hoverInfoTextElement != null)
        {
            hoverInfoTextElement.text = "";
        }
        if (mainGenerationCoroutine == null)
        {
            // Debug.Log("请按 Play 按钮旁的 'Start Generation' 按钮，或右键点击脚本 Inspector > 'Start Generation' 开始生成。");
            // StartGenerationProcess(); // 取消注释以在构建中自动开始
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            StartGenerationProcess();
        }
        if (Input.GetMouseButtonDown(0))
        {
            HandleMapClick(0);
        }
        else if (Input.GetMouseButtonDown(1))
        {
            HandleMapClick(1);
        }
        if (map != null)
        {
            HandleMouseHover();
        }
    }

    void OnDestroy()
    {
        StopTrafficAndPathUpdates();
        if (mainGenerationCoroutine != null)
        {
            StopCoroutine(mainGenerationCoroutine);
        }
        if (instantiationCoroutine != null)
        {
            StopCoroutine(instantiationCoroutine);
        }
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
    #endregion // Unity Lifecycle Methods

    #region Generation Pipeline & Control
    // --- 生成流程与控制 ---

    [ContextMenu("Start Generation")]
    public void StartGenerationProcess()
    {
        // Debug.Log("StartGenerationProcess: 开始生成流程。");
        if (mainGenerationCoroutine != null)
        {
            // Debug.LogWarning("生成已经在进行中！");
            return;
        }
        StopTrafficAndPathUpdates();
        ClearHighlightedPath();
        // Debug.Log("StartGenerationProcess: 启动 RunGenerationPipeline 协程。");
        mainGenerationCoroutine = StartCoroutine(RunGenerationPipeline());
    }

    IEnumerator RunGenerationPipeline()
    {
        // Debug.Log("RunGenerationPipeline: 协程已启动。");
        if (mapParent == null)
        {
            GameObject parentObj = GameObject.Find("GeneratedCity") ?? new GameObject("GeneratedCity");
            mapParent = parentObj.transform;
            // Debug.Log($"RunGenerationPipeline: 设置 mapParent 为 '{mapParent.name}'。");
        }

        ClearPreviousGeneration();
        // Debug.Log("RunGenerationPipeline: 已清理之前的生成。");
        ValidatePrefabs();
        // Debug.Log($"RunGenerationPipeline: 预设体验证结果: {prefabsValidated}");
        if (!prefabsValidated)
        {
            // Debug.LogError("必要的预设体缺失或配置错误。中止生成。");
            mainGenerationCoroutine = null;
            yield break;
        }
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            // Debug.LogError("地图尺寸必须大于 0。");
            mainGenerationCoroutine = null;
            yield break;
        }

        map = new ZoneType[mapWidth, mapHeight];
        if (noiseOffset == Vector2.zero)
        {
            noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));
        }

        // Debug.Log("--- 开始生成阶段 ---");
        // Debug.Log("RunGenerationPipeline: 开始 GenerateRoadsPhase。");
        yield return StartCoroutine(GenerateRoadsPhase());
        // Debug.Log("RunGenerationPipeline: 完成 GenerateRoadsPhase。");

        // Debug.Log("--- 道路生成完毕 --- 等待输入以放置 3x3 建筑...");
        //yield return WaitForInput();
        // Debug.Log("--- 开始放置 3x3 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(3));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null)
        {
            yield return instantiationCoroutine;
        }

        // Debug.Log("--- 3x3 放置完毕 --- 等待输入以放置 2x2 建筑...");
        //yield return WaitForInput();
        yield return new WaitForSeconds(0.5f); // 等待 0.5 秒以便于观察
        // Debug.Log("--- 开始放置 2x2 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(2));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null)
        {
            yield return instantiationCoroutine;
        }

        // Debug.Log("--- 2x2 放置完毕 --- 等待输入以放置 1x1 建筑...");
        //yield return WaitForInput();
        yield return new WaitForSeconds(0.5f); // 等待 0.5 秒以便于观察
        // Debug.Log("--- 开始放置 1x1 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(1));
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null)
        {
            yield return instantiationCoroutine;
        }

        // Debug.Log("--- 所有放置阶段完成！ ---");

        StartTrafficAndPathUpdates();

        if (carPlacementController != null)
        {
            // Debug.Log("RunGenerationPipeline: 调用 CarPlacementController 初始化墙体和车辆位置。");
            carPlacementController.InitializePlacementAndWalls();
        }
        else
        {
            // Debug.Log("RunGenerationPipeline: 未在 MapGenerator 中设置 CarPlacementController 引用。(可选)");
        }

        mainGenerationCoroutine = null;
        // Debug.Log("生成完成。请在道路上点击鼠标左键设置起点，右键设置终点。路径将自动更新。");
    }

    IEnumerator WaitForInput()
    {
        // Debug.Log("请在 Game 视图中单击鼠标左键或按空格键以继续...");
        while (!Input.GetMouseButtonDown(0) && !Input.GetKeyDown(KeyCode.Space))
        {
            yield return null;
        }
        // Debug.Log("收到输入，继续...");
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
        if (roadPrefab == null)
        {
            // Debug.LogError("Road Prefab (道路预设体) 缺失！");
            essentialRoadsOk = false;
        }
        if (highlightedRoadPrefab == null)
        {
            // Debug.LogError("Highlighted Road Prefab (高亮道路预设体) 缺失！");
            essentialRoadsOk = false;
        }

        Action<GameObject, string, bool> checkComponents = (prefab, name, isCritical) =>
        {
            if (prefab != null)
            {
                if (prefab.GetComponent<Collider>() == null)
                {
                    // Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Collider 组件！", prefab);
                    if (isCritical) essentialRoadsOk = false;
                }
                if (prefab.GetComponentInChildren<Renderer>() == null)
                {
                    // Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Renderer 组件！", prefab);
                    if (isCritical) essentialRoadsOk = false;
                }
            }
        };
        checkComponents(roadPrefab, "Road Prefab", true);
        checkComponents(highlightedRoadPrefab, "Highlighted Road Prefab", true);

        bool essentialBuildingsOk = buildingPrefabs1x1.Any(p => p != null) || buildingPrefabs2x2.Any(p => p != null) || buildingPrefabs3x3.Any(p => p != null);
        if (!essentialBuildingsOk)
        {
            // Debug.LogWarning("所有建筑预设体列表都为空或无效。将只生成道路。");
        }

        prefabsValidated = essentialRoadsOk;
    }
    #endregion // Generation Pipeline & Control

    #region Road Generation
    // --- 道路生成阶段 ---

    IEnumerator GenerateRoadsPhase()
    {
        // Debug.Log("GenerateRoadsPhase: 开始。");
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        roadTiles.Clear();
        voronoiSites.Clear();
        noisePoints.Clear();

        yield return StartCoroutine(GenerateVoronoiSites());
        yield return StartCoroutine(SelectNoisePoints());
        if (voronoiSites.Count >= 2)
        {
            yield return StartCoroutine(ComputeVoronoiEdgesAndMarkRoads());
        }
        if (noisePoints.Count > 0 && (roadTiles.Count > 0 || voronoiSites.Count > 0))
        {
            yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());
        }
        if (ensureEdgeConnections && (roadTiles.Count > 0 || voronoiSites.Count > 0))
        {
            yield return StartCoroutine(EnsureMapEdgeConnections());
        }
        yield return StartCoroutine(EnsureRoadConnectivity());
        // Debug.Log($"GenerateRoadsPhase: 道路连通性检查完毕。逻辑道路瓦片数量: {roadTiles.Count}");
        if (roadTiles.Count == 0)
        {
            // Debug.LogWarning("GenerateRoadsPhase: 没有生成任何逻辑道路瓦片！");
        }
        // Debug.Log("GenerateRoadsPhase: 开始 InstantiateRoadVisuals。");
        yield return StartCoroutine(InstantiateRoadVisuals());
        // Debug.Log("GenerateRoadsPhase: 完成 InstantiateRoadVisuals。");
        // Debug.Log("GenerateRoadsPhase: 开始立即实例化对象。");
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null)
        {
            // Debug.Log("GenerateRoadsPhase: 等待实例化协程。");
            yield return instantiationCoroutine;
        }
        // Debug.Log("GenerateRoadsPhase: 完成立即实例化对象。");
        timer.Stop();
        // Debug.Log($"--- 道路生成阶段完成 ({roadTiles.Count} 个道路瓦片, {timer.ElapsedMilliseconds}ms) ---");
    }

    IEnumerator GenerateVoronoiSites()
    {
        voronoiSites.Clear();
        int spacing = Mathf.Max(1, voronoiSiteSpacing);
        int jitter = Mathf.Clamp(voronoiSiteJitter, 0, spacing / 2);
        int count = 0;
        for (int x = spacing / 2; x < mapWidth; x += spacing)
        {
            for (int y = spacing / 2; y < mapHeight; y += spacing)
            {
                int jX = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                int jY = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                Vector2Int p = new Vector2Int(Mathf.Clamp(x + jX, 0, mapWidth - 1), Mathf.Clamp(y + jY, 0, mapHeight - 1));
                bool tooClose = voronoiSites.Any(site => Mathf.Abs(site.x - p.x) <= 1 && Mathf.Abs(site.y - p.y) <= 1);
                if (!tooClose && IsInMap(p) && map[p.x, p.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(p))
                {
                    voronoiSites.Add(p);
                }
                count++;
                if (asyncGeneration && count % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }
        if (voronoiSites.Count < 4 && mapWidth > 10 && mapHeight > 10)
        {
            List<Vector2Int> corners = new List<Vector2Int>
            {
                new Vector2Int(Mathf.Clamp(spacing / 2, 1, mapWidth - 2), Mathf.Clamp(spacing / 2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(mapWidth - 1 - spacing / 2, 1, mapWidth - 2), Mathf.Clamp(spacing / 2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(spacing / 2, 1, mapWidth - 2), Mathf.Clamp(mapHeight - 1 - spacing / 2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(mapWidth - 1 - spacing / 2, 1, mapWidth - 2), Mathf.Clamp(mapHeight - 1 - spacing / 2, 1, mapHeight - 2))
            };
            corners = corners.Where(IsInMap).Distinct().ToList();
            foreach (var corner in corners)
            {
                if (!voronoiSites.Any(site => (site - corner).sqrMagnitude < spacing * spacing * 0.1f) && IsInMap(corner) && map[corner.x, corner.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(corner))
                {
                    voronoiSites.Add(corner);
                }
            }
            voronoiSites = voronoiSites.Distinct().ToList();
        }
        // Debug.Log($"生成了 {voronoiSites.Count} 个 Voronoi 站点。");
    }

    IEnumerator SelectNoisePoints()
    {
        noisePoints.Clear();
        float noiseOffsetX = noiseOffset.x;
        float noiseOffsetY = noiseOffset.y;
        int checkedCount = 0;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                if (IsInMap(currentPos) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos))
                {
                    float normX = (float)x / mapWidth;
                    float normY = (float)y / mapHeight;
                    float nX = noiseOffsetX + normX * branchNoiseScale;
                    float nY = noiseOffsetY + normY * branchNoiseScale;
                    float noiseValue = Mathf.PerlinNoise(nX, nY);
                    if (noiseValue > noiseBranchThreshold)
                    {
                        if (map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos))
                        {
                            noisePoints.Add(currentPos);
                        }
                    }
                }
                checkedCount++;
                if (asyncGeneration && checkedCount % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }
        // Debug.Log($"选择了 {noisePoints.Count} 个噪声点。");
    }

    IEnumerator ComputeVoronoiEdgesAndMarkRoads()
    {
        if (voronoiSites.Count < 2)
        {
            // Debug.LogWarning("Voronoi 站点少于 2 个。");
            yield break;
        }
        int processed = 0;
        int marked = 0;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                if (roadTiles.Contains(currentPos) || occupiedTiles.ContainsKey(currentPos))
                {
                    processed++;
                    if (asyncGeneration && processed % yieldBatchSize == 0)
                    {
                        yield return null;
                    }
                    continue;
                }
                int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites);
                if (nearestSiteIndex < 0)
                {
                    continue;
                }
                foreach (var dir in directions)
                {
                    Vector2Int neighborPos = currentPos + dir;
                    if (!IsInMap(neighborPos))
                    {
                        continue;
                    }
                    int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites);
                    if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex)
                    {
                        TryClaimTileForRoadLogicOnly(currentPos, out bool claimed);
                        if (claimed)
                        {
                            marked++;
                        }
                        break; // Found an edge, mark and move to next tile
                    }
                }
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }
        // Debug.Log($"标记了 {marked} 个 Voronoi 道路瓦片。");
    }

    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites)
    {
        if (sites == null || sites.Count == 0)
        {
            return -1;
        }
        int nearestIndex = -1;
        float minDistSq = float.MaxValue;
        for (int i = 0; i < sites.Count; i++)
        {
            float distSq = (sites[i] - point).sqrMagnitude;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                nearestIndex = i;
            }
        }
        return nearestIndex;
    }

    Vector2Int? FindNearestSite(Vector2Int point, List<Vector2Int> sites)
    {
        int index = FindNearestSiteIndex(point, sites);
        return index >= 0 ? sites[index] : (Vector2Int?)null;
    }

    IEnumerator ConnectNoiseAndRoadsWithMST()
    {
        if (noisePoints.Count == 0)
        {
            yield break;
        }
        HashSet<Vector2Int> anchorNodes = new HashSet<Vector2Int>(roadTiles);
        anchorNodes.UnionWith(voronoiSites.Where(site => IsInMap(site)));
        if (anchorNodes.Count == 0)
        {
            // Debug.LogWarning("无锚点连接噪声点。");
            yield break;
        }
        int pathsDrawn = 0;
        int totalClaimedOnPaths = 0;
        foreach (var noiseStart in noisePoints)
        {
            if (!IsInMap(noiseStart) || roadTiles.Contains(noiseStart) || occupiedTiles.ContainsKey(noiseStart) || map[noiseStart.x, noiseStart.y] != ZoneType.Empty)
            {
                continue;
            }
            Vector2Int? nearestAnchor = FindNearestPointInSet(noiseStart, anchorNodes);
            if (nearestAnchor.HasValue)
            {
                List<Vector2Int> path = FindPath_InternalFixedCost(noiseStart, new HashSet<Vector2Int> { nearestAnchor.Value });
                int claimedOnThisPath = 0;
                if (path != null && path.Count > 0)
                {
                    foreach (var roadPos in path)
                    {
                        if (!IsInMap(roadPos))
                        {
                            continue;
                        }
                        bool claimed = false;
                        yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                        if (claimed)
                        {
                            claimedOnThisPath++;
                            anchorNodes.Add(roadPos); // Add newly created road to potential anchors
                        }
                    }
                    totalClaimedOnPaths += claimedOnThisPath;
                }
                else
                {
                    // Debug.LogWarning($"内部 A* 失败: {noiseStart} -> {nearestAnchor.Value}。");
                }
            }
            else
            {
                // Debug.LogWarning($"噪声点 {noiseStart} 未找到锚点。");
            }
            pathsDrawn++;
            if (asyncGeneration && pathsDrawn % 20 == 0) // Yield periodically
            {
                yield return null;
            }
        }
        // Debug.Log($"尝试连接 {pathsDrawn} 个噪声点，添加了 {totalClaimedOnPaths} 道路瓦片。");
    }

    Vector2Int? FindNearestPointInList(Vector2Int startPoint, List<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0)
        {
            return null;
        }
        Vector2Int? nearest = null;
        float minDistanceSq = float.MaxValue;
        foreach (Vector2Int target in targetPoints)
        {
            float distSq = (startPoint - target).sqrMagnitude;
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                nearest = target;
                if (minDistanceSq <= 1.1f) break; // Optimization: if it's adjacent, it's close enough
            }
        }
        return nearest;
    }

    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0)
        {
            return null;
        }
        Vector2Int? nearest = null;
        float minDistanceSq = float.MaxValue;
        foreach (Vector2Int target in targetPoints)
        {
            float distSq = (startPoint - target).sqrMagnitude;
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                nearest = target;
                if (minDistanceSq <= 1.1f) break; // Optimization
            }
        }
        return nearest;
    }

    IEnumerator EnsureMapEdgeConnections()
    {
        List<Vector2Int> edgeAnchors = new List<Vector2Int>
        {
            new Vector2Int(mapWidth / 2, mapHeight - 2), // Top center-ish
            new Vector2Int(mapWidth / 2, 1),           // Bottom center-ish
            new Vector2Int(1, mapHeight / 2),           // Left center-ish
            new Vector2Int(mapWidth - 2, mapHeight / 2), // Right center-ish
            // Optional corners
            new Vector2Int(2, 2),
            new Vector2Int(mapWidth - 3, 2),
            new Vector2Int(2, mapHeight - 3),
            new Vector2Int(mapWidth - 3, mapHeight - 3)
        };
        edgeAnchors = edgeAnchors.Where(p => IsInMap(p) && p.x > 0 && p.x < mapWidth - 1 && p.y > 0 && p.y < mapHeight - 1).Distinct().ToList();

        int connectionsAttempted = 0;
        int connectionsMade = 0;
        HashSet<Vector2Int> currentNetworkPoints = new HashSet<Vector2Int>(roadTiles);
        currentNetworkPoints.UnionWith(voronoiSites.Where(site => IsInMap(site)));

        if (currentNetworkPoints.Count == 0)
        {
            // Debug.LogWarning("跳过边缘连接：网络为空。");
            yield break;
        }

        foreach (var edgePoint in edgeAnchors)
        {
            connectionsAttempted++;
            if (currentNetworkPoints.Contains(edgePoint)) // Already connected or is part of network
            {
                continue;
            }
            Vector2Int? connectFrom = FindNearestPointInSet(edgePoint, currentNetworkPoints);
            if (connectFrom.HasValue)
            {
                bool success = false;
                yield return StartCoroutine(ConnectTwoPoints(connectFrom.Value, edgePoint, result => success = result));
                if (success)
                {
                    connectionsMade++;
                    // Add the newly connected path to the network for future checks
                    // Note: ConnectTwoPoints should already add tiles to roadTiles, so updating currentNetworkPoints might be needed if more edge connections rely on previous ones.
                    // For simplicity, we'll assume ConnectTwoPoints updates roadTiles correctly, and we re-query FindNearestPointInSet each time.
                }
            }
            else
            {
                // Debug.LogWarning($"未能找到连接点: {edgePoint}");
            }
            if (asyncGeneration && connectionsAttempted % 2 == 0) // Yield periodically
            {
                yield return null;
            }
        }
        // Debug.Log($"尝试 {connectionsAttempted} 次边缘连接，成功 {connectionsMade} 次。");
    }

    IEnumerator ConnectTwoPoints(Vector2Int start, Vector2Int end, Action<bool> callback)
    {
        List<Vector2Int> path = FindPath_InternalFixedCost(start, new HashSet<Vector2Int> { end });
        int appliedCount = 0;
        bool success = false;
        bool pathBlockedMidway = false;

        if (path != null && path.Count > 1) // path[0] is start, path[last] is end
        {
            // Don't try to claim start, assume it's valid. Claim path[1] onwards.
            for (int i = 0; i < path.Count; ++i) // Iterate through all including start/end
            {
                Vector2Int roadPos = path[i];
                if (!IsInMap(roadPos)) continue; // Should not happen with IsInMap checks in A*

                bool claimed = false;
                if (!roadTiles.Contains(roadPos)) // Only claim if it's not already a road
                {
                    yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                }
                else
                {
                    claimed = true; // Already a road, consider it 'claimed' for path validity
                }

                if (claimed)
                {
                    appliedCount++;
                }
                else if (roadPos != start && roadPos != end) // If a non-endpoint tile failed to claim
                {
                    // Debug.LogWarning($"路径段 {roadPos} 无法声明。");
                    pathBlockedMidway = true;
                    // Don't necessarily break, maybe a later part can connect? A* should avoid unwalkable though.
                }
                 if (asyncGeneration && i > 0 && i % 100 == 0) yield return null; // Yield on long paths
            }
             // Success if we claimed at least one new tile OR the end was already a road, AND the path wasn't blocked.
             success = (appliedCount > 0 || roadTiles.Contains(end)) && !pathBlockedMidway;
        }
        else if (path != null && path.Count == 1 && path[0] == end && roadTiles.Contains(end))
        {
            // Path found is just the end point itself, which is already a road tile. Success.
            success = true;
        }
        else if (path == null)
        {
            // Debug.LogWarning($"内部 A* 失败: {start} -> {end}");
            success = false;
        }

        if (asyncGeneration && appliedCount > 0) yield return null; // Yield if work was done
        callback?.Invoke(success);
    }

    IEnumerator EnsureRoadConnectivity()
    {
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();
        if (roadComponents.Count <= 1)
        {
            // Debug.Log($"道路网络连通性：找到 {roadComponents.Count} 个组件。");
            yield break; // Already connected or no roads
        }

        // Debug.Log($"发现 {roadComponents.Count} 个组件，尝试连接...");
        roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList(); // Start with the largest component
        HashSet<Vector2Int> mainNetwork = roadComponents[0];
        // Debug.Log($"主网络大小: {mainNetwork.Count}");
        int connectionsMade = 0;
        int componentsMerged = 1;

        for (int i = 1; i < roadComponents.Count; i++)
        {
            HashSet<Vector2Int> currentComponent = roadComponents[i];
            if (currentComponent.Count == 0) continue; // Skip empty components if any

            // Debug.Log($"尝试连接组件 {i + 1} (大小: {currentComponent.Count})...");
            bool connected = false;
            // Pass the current mainNetwork (which might have grown)
            yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork, result => connected = result));

            if (connected)
            {
                connectionsMade++;
                componentsMerged++;
                // Debug.Log($"组件 {i + 1} 已连接。");
                mainNetwork.UnionWith(currentComponent); // Merge the connected component into the main network
            }
            else
            {
                // Debug.LogWarning($"未能连接组件 {i + 1}。");
            }
            if (asyncGeneration && i % 5 == 0) yield return null; // Yield periodically
        }
        // Debug.Log($"连通性检查完成。合并 {componentsMerged}/{roadComponents.Count} 组件 ({connectionsMade} 次连接)。");
    }

    List<HashSet<Vector2Int>> FindAllRoadComponents()
    {
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>();
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        HashSet<Vector2Int> currentRoadTilesSnapshot = new HashSet<Vector2Int>(roadTiles); // Work on a copy

        foreach (Vector2Int startPos in currentRoadTilesSnapshot)
        {
            if (!visited.Contains(startPos) && roadTiles.Contains(startPos)) // Check roadTiles again in case it changed
            {
                HashSet<Vector2Int> newComponent = new HashSet<Vector2Int>();
                Queue<Vector2Int> queue = new Queue<Vector2Int>();

                queue.Enqueue(startPos);
                visited.Add(startPos);
                newComponent.Add(startPos);

                while (queue.Count > 0)
                {
                    Vector2Int node = queue.Dequeue();
                    foreach (var dir in directions)
                    {
                        Vector2Int neighbor = node + dir;
                        // Check bounds, if it's a road tile, and not visited yet
                        if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            newComponent.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
                if (newComponent.Count > 0)
                {
                    components.Add(newComponent);
                }
            }
        }
        return components;
    }

    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork, Action<bool> callback)
    {
        if (componentToConnect == null || targetNetwork == null || componentToConnect.Count == 0 || targetNetwork.Count == 0)
        {
            // Debug.LogWarning("ConnectComponentToNetwork: 输入为空。");
            callback?.Invoke(false);
            yield break;
        }

        Vector2Int? bestStart = null;
        Vector2Int? bestTarget = null;
        float minDistanceSq = float.MaxValue;

        // Optimization: Search from the smaller set to the larger set
        HashSet<Vector2Int> searchSet = (componentToConnect.Count < targetNetwork.Count) ? componentToConnect : targetNetwork;
        HashSet<Vector2Int> destinationSet = (searchSet == componentToConnect) ? targetNetwork : componentToConnect;

        // Further Optimization: Don't check every point in large sets. Sample or take boundary points.
        // Let's sample a limited number of points from the search set.
        int maxSearchPoints = 300 + (int)Mathf.Sqrt(searchSet.Count); // Heuristic limit
        var pointsToSearchFrom = searchSet.Count <= maxSearchPoints ? searchSet : searchSet.OrderBy(p => UnityEngine.Random.value).Take(maxSearchPoints);
        int searchedCount = 0;

        foreach (var startCandidate in pointsToSearchFrom)
        {
            Vector2Int? currentNearestTarget = FindNearestPointInSet(startCandidate, destinationSet);
            if (currentNearestTarget.HasValue)
            {
                float distSq = (startCandidate - currentNearestTarget.Value).sqrMagnitude;
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    // Assign start/target based on which set was which
                    bestStart = (searchSet == componentToConnect) ? startCandidate : currentNearestTarget.Value;
                    bestTarget = (searchSet == componentToConnect) ? currentNearestTarget.Value : startCandidate;
                }
            }
            searchedCount++;
            if (minDistanceSq <= 2.0f) break; // Already adjacent or overlapping, good enough.
             if (asyncGeneration && searchedCount % 50 == 0) yield return null; // Yield during search
        }


        if (!bestStart.HasValue || !bestTarget.HasValue)
        {
            // Debug.LogError($"无法找到连接点。Comp: {componentToConnect.Count}, Target: {targetNetwork.Count}");
            callback?.Invoke(false);
            yield break;
        }

        // If points are already adjacent/close, consider them connected (might happen due to boundary conditions)
        if (minDistanceSq <= 2.0f) // Allow direct adjacency (dist=1 or sqrt(2))
        {
            // Debug.Log($"组件已接近 ({bestStart.Value} -> {bestTarget.Value})。");
            // Technically connected, but let's try ConnectTwoPoints to ensure a path exists / fill diagonals if needed?
            // No, just call it success to avoid potentially long A* on adjacent points.
            callback?.Invoke(true); // Consider them connected if very close
            yield break;
        }


        bool connected = false;
        // Debug.Log($"尝试连接组件：{bestStart.Value} -> {bestTarget.Value} (DistSq: {minDistanceSq:F1})");
        yield return StartCoroutine(ConnectTwoPoints(bestStart.Value, bestTarget.Value, result => connected = result));
        callback?.Invoke(connected);
    }


    IEnumerator InstantiateRoadVisuals()
    {
        int roadsProcessed = 0;
        int placeAttempts = 0;
        List<Vector2Int> currentRoadTilesSnapshot = new List<Vector2Int>(roadTiles); // Work on a copy
        // Debug.Log($"InstantiateRoadVisuals: 处理 {currentRoadTilesSnapshot.Count} 个潜在道路。");
        if (roadPrefab == null)
        {
            // Debug.LogError("Road Prefab 未分配！");
            yield break;
        }

        foreach (Vector2Int pos in currentRoadTilesSnapshot)
        {
            placeAttempts++;
            if (!roadTiles.Contains(pos)) continue; // Check again, might have been removed

            // Check map data integrity
            bool isMarkedAsRoad = map[pos.x, pos.y] == ZoneType.Road;
            bool isAvailable = !occupiedTiles.ContainsKey(pos) || occupiedTiles[pos] == pos; // Tile is not occupied OR occupied by itself (a 1x1 road)
            bool alreadyHasTrackedObjectOrigin = trackedObjects.ContainsKey(pos); // Check if an object *originates* here

            if (isMarkedAsRoad && isAvailable && !alreadyHasTrackedObjectOrigin)
            {
                // Place the road if map says road, tile is free (or occupied by self), and no object originates here yet.
                PlaceObject(pos, Vector2Int.one, roadPrefab, ZoneType.Road, Quaternion.identity);
                roadsProcessed++;
            }
            else if (!isMarkedAsRoad && roadTiles.Contains(pos))
            {
                 // Inconsistency: In roadTiles set, but map doesn't say Road. Log it. Should ideally not happen.
                 // Debug.LogWarning($"InstantiateRoadVisuals: {pos} 在 roadTiles 中但 map 类型为 '{map[pos.x, pos.y]}'.");
            }
            else if (!isAvailable)
            {
                // Tile is occupied by something else (a larger building). This is expected if a building overwrote a planned road spot.
                 Vector2Int occupierOrigin = occupiedTiles.ContainsKey(pos) ? occupiedTiles[pos] : pos;
                 // Debug.LogWarning($"InstantiateRoadVisuals: {pos} 被 {occupierOrigin} 占用。");
            }
            else if (alreadyHasTrackedObjectOrigin)
            {
                // Already has a tracked object (likely the road we are trying to place, or something else placed erroneously).
                // PlaceObject should handle overwriting if needed, but this check helps understand flow.
                // If it's already tracked, PlaceObject will remove the old one first.
                // Let PlaceObject handle it, or add specific checks if needed.
            }


            if (asyncGeneration && placeAttempts > 0 && placeAttempts % yieldBatchSize == 0)
            {
                yield return null;
            }
        }
        // Debug.Log($"InstantiateRoadVisuals: {placeAttempts} 次尝试中触发 {roadsProcessed} 次 PlaceObject。");
    }

    #endregion // Road Generation

    #region Building Placement
    // --- 建筑放置阶段 ---

    IEnumerator FillSpecificSizeGreedy(int sizeToFill)
    {
        // Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 开始。");
        int buildingsPlaced = 0;
        int tilesChecked = 0;
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

        Vector2Int buildingSize = Vector2Int.one * sizeToFill;
        List<GameObject> prefabList;
        switch (sizeToFill)
        {
            case 3: prefabList = buildingPrefabs3x3; break;
            case 2: prefabList = buildingPrefabs2x2; break;
            case 1: prefabList = buildingPrefabs1x1; break;
            default:
                // Debug.LogError($"无效尺寸: {sizeToFill}");
                yield break;
        }

        if (prefabList == null || !prefabList.Any(p => p != null))
        {
            // Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 无有效预设体。");
            yield break;
        }

        // Iterate through potential origin points
        for (int y = 0; y <= mapHeight - sizeToFill; y++)
        {
            for (int x = 0; x <= mapWidth - sizeToFill; x++)
            {
                tilesChecked++;
                Vector2Int currentOrigin = new Vector2Int(x, y);

                // Quick check: Is the origin tile itself empty and available?
                if (IsInMap(currentOrigin) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentOrigin))
                {
                    // Full check: Can the entire footprint fit?
                    if (CanPlaceBuildingHere(currentOrigin, buildingSize))
                    {
                        GameObject prefab = GetRandomValidPrefab(prefabList);
                        if (prefab != null)
                        {
                            PlaceObject(currentOrigin, buildingSize, prefab, DetermineBuildingType(), Quaternion.identity);
                            buildingsPlaced++;
                            // Optimization: Skip ahead since we filled these tiles
                            x += (sizeToFill - 1);
                        }
                        else
                        {
                            // Debug.LogError($"GetRandomValidPrefab 返回 null！");
                        }
                    }
                    // else: Cannot place here, continue to next x
                }
                // else: Origin tile not suitable, continue to next x

                 if (asyncGeneration && tilesChecked % (yieldBatchSize * 2) == 0) // Yield more often during building placement
                 {
                      yield return null;
                 }
            }
             if (asyncGeneration && y % 10 == 0) yield return null; // Yield periodically per row
        }

        timer.Stop();
        // Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 完成。放置 {buildingsPlaced} 个建筑。耗时: {timer.ElapsedMilliseconds} ms。");
        yield return null; // Ensure one frame passes after completion if sync
    }

    bool CanPlaceBuildingHere(Vector2Int origin, Vector2Int size)
    {
        // Check bounds first
        if (!IsInMap(origin) || !IsInMap(origin + size - Vector2Int.one))
        {
            return false;
        }

        // Check every tile in the footprint
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentTile = new Vector2Int(x, y);
                // Check bounds (redundant?), map type, and occupation status
                if (!IsInMap(currentTile) || map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentTile))
                {
                    return false; // Found an obstruction
                }
            }
        }
        return true; // No obstructions found
    }

    GameObject GetRandomValidPrefab(List<GameObject> list)
    {
        if (list == null) return null;
        List<GameObject> validPrefabs = list.Where(p => p != null).ToList();
        if (validPrefabs.Count == 0) return null;
        return validPrefabs[UnityEngine.Random.Range(0, validPrefabs.Count)];
    }

    ZoneType DetermineBuildingType()
    {
        // Simple random distribution for now
        int r = UnityEngine.Random.Range(0, 3); // 0, 1, 2
        if (r == 0) return ZoneType.Residential;
        if (r == 1) return ZoneType.Commercial;
        return ZoneType.Industrial;
    }
    #endregion // Building Placement

    #region Core Logic: Placement, Removal, Claiming
    // --- 核心逻辑：放置、移除、声明 ---

    void PlaceObject(Vector2Int origin, Vector2Int size, GameObject prefab, ZoneType type, Quaternion rotation)
    {
        if (prefab == null)
        {
            // Debug.LogError($"PlaceObject @ {origin}: 预设体为 null。");
            return;
        }

        // Bounds check
        Vector2Int extent = origin + size - Vector2Int.one;
        if (!IsInMap(origin) || !IsInMap(extent))
        {
            // Debug.LogError($"PlaceObject: 区域 {origin} 尺寸 {size} 超出边界。");
            return;
        }

        // --- Clear Existing Objects in Footprint ---
        // Iterate through all tiles the new object will occupy
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int tile = new Vector2Int(x, y);
                if (IsInMap(tile))
                {
                    // Check if tile is occupied OR if map data isn't empty (might be leftover logic)
                    bool isOccupied = occupiedTiles.ContainsKey(tile);
                    bool mapNotEmpty = map[x, y] != ZoneType.Empty;
                    if (isOccupied || mapNotEmpty)
                    {
                        // This tile has something that needs removal.
                        // RemoveTrackedObject handles finding the origin and cleaning up.
                        RemoveTrackedObject(tile);
                    }
                    // If it wasn't occupied/not empty, it's clear, do nothing here.
                }
            }
        }


        // --- Instantiate New Object ---
        Vector3 instantiationPosition;
        Vector3 targetCornerWorldPos = new Vector3(origin.x, 0, origin.y); // Target bottom-left grid corner in world space
        Vector3 colliderOffsetFromPivotXZ = Vector3.zero; // Offset from object's pivot to its collider's bottom-left corner

        // Attempt to get BoxCollider for precise alignment
        BoxCollider boxCol = prefab.GetComponent<BoxCollider>();
        if (boxCol != null)
        {
            // Calculate the local offset from the pivot to the bottom-left corner of the collider bounds
            Vector3 localBottomLeftOffset = boxCol.center - new Vector3(boxCol.size.x / 2.0f, 0, boxCol.size.z / 2.0f); // Ignore Y component for ground placement
             colliderOffsetFromPivotXZ = new Vector3(localBottomLeftOffset.x, 0, localBottomLeftOffset.z);
        }
        else
        {
            // Fallback or warning if no BoxCollider
            Collider generalCol = prefab.GetComponent<Collider>();
            if (generalCol != null)
            {
                // Debug.LogWarning($"预设体 {prefab.name} @ {origin}: 无 BoxCollider。将使用枢轴点进行放置。", prefab);
            }
            else
            {
                // Debug.LogError($"预设体 {prefab.name} @ {origin}: 无 Collider！无法保证正确放置。", prefab);
            }
             // In fallback case, colliderOffsetFromPivotXZ remains zero, placing the pivot at the grid corner
        }

        // Calculate final instantiation position: Grid Corner - Offset = Pivot Position
        instantiationPosition = targetCornerWorldPos - colliderOffsetFromPivotXZ;


        GameObject instance = null;
        try
        {
            instance = Instantiate(prefab, instantiationPosition, rotation, mapParent);
        }
        catch (Exception e)
        {
             // Debug.LogError($"PlaceObject @ {origin}: 实例化 '{prefab.name}' 失败！ {e.Message}");
             return; // Stop if instantiation failed
        }

        if (instance == null)
        {
             // Debug.LogError($"PlaceObject @ {origin}: Instantiate 返回 NULL for '{prefab.name}'！");
             return;
        }

        instance.name = $"{prefab.name}_{origin.x}_{origin.y}"; // Naming convention for later lookup
        instance.SetActive(false); // Keep inactive until batch activation

        // --- Update Data Structures ---
        BuildingRecord record = new BuildingRecord(instance, size, type, rotation);
        trackedObjects[origin] = record; // Store record using origin as key

        // Initialize traffic data for roads
        if (type == ZoneType.Road && size == Vector2Int.one)
        {
            record.capacity = UnityEngine.Random.Range(5, 21); // Example capacity
            record.currentVehicles = UnityEngine.Random.Range(0, 31); // Example initial vehicles
        }

        // Mark all occupied tiles in the map grid and occupiedTiles dictionary
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentTile = new Vector2Int(x, y);
                if (IsInMap(currentTile))
                {
                    map[x, y] = type;
                    occupiedTiles[currentTile] = origin; // Point back to the origin of the object occupying this tile
                    if (type != ZoneType.Road) // If placing a building, remove from road set
                    {
                       roadTiles.Remove(currentTile);
                    }
                }
            }
        }
        // If it was a 1x1 road, ensure it's in the road set
        if (type == ZoneType.Road && size == Vector2Int.one)
        {
             roadTiles.Add(origin);
        }


        // --- Queue for Activation ---
        generationQueue.Enqueue(instance);
        StartObjectInstantiation(); // Start or continue the activation coroutine if needed
    }


    void RemoveTrackedObject(Vector2Int pos)
    {
        if (!IsInMap(pos)) return; // Ignore calls for positions outside the map

        Vector2Int originToRemove = pos; // Assume pos is the origin initially
        bool foundInOccupied = false;

        // 1. Find the actual origin of the object at 'pos'
        if (occupiedTiles.TryGetValue(pos, out Vector2Int foundOrigin))
        {
            // Found the origin via the occupiedTiles dictionary
            originToRemove = foundOrigin;
            foundInOccupied = true;
        }
        else if (!trackedObjects.ContainsKey(pos))
        {
            // Not in occupiedTiles and not an origin in trackedObjects.
            // This might be an empty tile, or just map data inconsistency.
            // Clean up map data just in case and remove from roadTiles.
            if (map != null && IsInMap(pos) && map[pos.x, pos.y] != ZoneType.Empty)
            {
                 map[pos.x, pos.y] = ZoneType.Empty;
            }
             roadTiles.Remove(pos); // Ensure it's not marked as a road tile
            return; // Nothing tracked to remove
        }
        // If not found in occupiedTiles, but pos *is* a key in trackedObjects,
        // then 'pos' is the origin of a 1x1 object. originToRemove remains 'pos'.

        // 2. Remove the tracked object using the found origin
        if (trackedObjects.TryGetValue(originToRemove, out BuildingRecord recordToRemove))
        {
            Vector2Int size = recordToRemove.size;

            // Clear data for all tiles covered by this object
            for (int x = originToRemove.x; x < originToRemove.x + size.x; x++)
            {
                for (int y = originToRemove.y; y < originToRemove.y + size.y; y++)
                {
                    Vector2Int currentTile = new Vector2Int(x, y);
                    if (IsInMap(currentTile))
                    {
                        occupiedTiles.Remove(currentTile); // Remove occupation mapping
                        if (map != null) map[x, y] = ZoneType.Empty; // Reset map data
                        roadTiles.Remove(currentTile); // Ensure it's not marked as a road tile
                    }
                }
            }

            // Remove the main record
            trackedObjects.Remove(originToRemove);

            // Destroy the GameObject instance
            if (recordToRemove.instance != null)
            {
                 // Check if it's part of the highlighted path and remove if so
                 if (highlightedPathInstances.Contains(recordToRemove.instance))
                 {
                      highlightedPathInstances.Remove(recordToRemove.instance);
                 }
                DestroyObjectInstance(recordToRemove.instance);
            }
        }
        else
        {
            // Inconsistency: We found an origin in occupiedTiles, but no corresponding record in trackedObjects.
            if (foundInOccupied)
            {
                // Debug.LogError($"RemoveTrackedObject({pos}): 不一致！瓦片被 {originToRemove} 占用，但在 trackedObjects 中无记录。清理瓦片...");
                // Still try to clean up the specific tile 'pos' that triggered the call
                 occupiedTiles.Remove(pos);
                 if (map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty;
                 roadTiles.Remove(pos);
            }
            else
            {
                 // Should not happen based on the logic flow, but handle defensively.
                 // Debug.LogError($"RemoveTrackedObject({pos}): 不一致！初始找到 {originToRemove}，但记录消失。清理瓦片...");
                 if (map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty;
                 roadTiles.Remove(pos);
            }

        }
    }


    // Used when converting an existing tile (potentially occupied) into a road tile
    IEnumerator TryClaimTileForRoad(Vector2Int pos, Action<bool> callback)
    {
        bool success = false;
        if (IsInMap(pos))
        {
            if (roadTiles.Contains(pos))
            {
                // Already a road tile, success.
                success = true;
            }
            else
            {
                // Not currently a road tile. Check if demolition is needed.
                bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;

                if (needsDemolition)
                {
                    RemoveTrackedObject(pos); // Remove whatever is there
                     if (asyncGeneration) yield return null; // Allow frame for destruction if needed
                }

                // After potential demolition, check if the tile is now clear
                if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
                {
                    // Tile is clear, claim it for road
                    map[pos.x, pos.y] = ZoneType.Road;
                    roadTiles.Add(pos);
                    // Note: We don't place the visual object here, InstantiateRoadVisuals handles that later.
                    success = true;
                }
                else if (roadTiles.Contains(pos))
                {
                     // Check again in case RemoveTrackedObject somehow added it? Unlikely but safe.
                     success = true;
                }
                // else: Tile still not clear after demolition attempt, or out of bounds. Failure.
            }
        }
        // else: Out of bounds. Failure.

        callback?.Invoke(success);
    }

    // Synchronous version for logic-only updates (like Voronoi edge marking before visuals)
    void TryClaimTileForRoadLogicOnly(Vector2Int pos, out bool success)
    {
        success = false;
        if (!IsInMap(pos)) return;

        if (roadTiles.Contains(pos))
        {
            success = true; // Already a road logic tile
            return;
        }

        // Check for demolition NEED but DO NOT perform visual deletion here.
        // Assume that if something occupies it, it will be handled later or overwritten.
        // Or, if strict logic is needed:
        bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;
        if (needsDemolition)
        {
             // We need to remove the *logic* of the occupying object here
             // to allow the road logic to proceed.
             RemoveTrackedObject(pos); // This will clear occupiedTiles and map data.
             // WARNING: This removes the actual object record. Be careful if using this
             // before the object placement phases are complete.
             // Maybe a different approach is needed if pre-emptive demolition is bad.
             // Alternative: just check if it's ZoneType.Empty conceptually.
             // Let's stick with RemoveTrackedObject for consistency for now.
        }

        // After potential logical removal, check if tile is clear
        if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
        {
            map[pos.x, pos.y] = ZoneType.Road; // Update map data
            roadTiles.Add(pos);            // Add to logical road set
            success = true;
        }
         else if (roadTiles.Contains(pos))
         {
              // Check again after RemoveTrackedObject
              success = true;
         }
         // else: Tile still blocked logically, or out of bounds. Failed.
    }
    #endregion // Core Logic: Placement, Removal, Claiming

    #region Instantiation & Cleanup
    // --- 实例化与清理 ---

    void StartObjectInstantiation(bool processAllImmediately = false)
    {
        if (processAllImmediately)
        {
            // Stop any ongoing async activation
            if (instantiationCoroutine != null)
            {
                StopCoroutine(instantiationCoroutine);
                instantiationCoroutine = null;
            }
            // Process the entire queue now
            int processedCount = 0;
            while (generationQueue.Count > 0)
            {
                GameObject obj = generationQueue.Dequeue();
                if (ValidateObjectForActivation(obj))
                {
                    obj.SetActive(true);
                    processedCount++;
                }
                else if (obj != null)
                {
                     // Invalid object (e.g., record removed before activation), destroy it
                     DestroyObjectInstance(obj);
                }
            }
            // // Debug.Log($"立即处理了 {processedCount} 个对象。");
        }
        else // Standard async activation
        {
            // Start the coroutine only if it's not already running and there's work to do
            if (instantiationCoroutine == null && generationQueue.Count > 0)
            {
                instantiationCoroutine = StartCoroutine(ActivateQueuedObjects());
            }
        }
    }

    IEnumerator ActivateQueuedObjects()
    {
        if (generationQueue.Count == 0)
        {
            instantiationCoroutine = null; // Ensure coroutine reference is cleared if queue is empty
            yield break;
        }

        int processedSinceYield = 0;
        int totalActivated = 0;
        System.Diagnostics.Stopwatch batchTimer = new System.Diagnostics.Stopwatch();
        float maxFrameTimeMs = 8.0f; // Target max time per frame for this coroutine

        while (generationQueue.Count > 0)
        {
            batchTimer.Restart();
            int activatedThisBatch = 0;

            // Process a batch of objects or until time limit is reached
            while (generationQueue.Count > 0 && activatedThisBatch < objectsPerFrame)
            {
                GameObject obj = generationQueue.Dequeue();
                bool isValid = ValidateObjectForActivation(obj); // Check if still valid before activating

                if (isValid)
                {
                    obj.SetActive(true);
                    activatedThisBatch++;
                    processedSinceYield++;
                    totalActivated++;
                }
                else if (obj != null)
                {
                    // Object is no longer valid (record likely removed), destroy it
                    DestroyObjectInstance(obj);
                }

                 // Check time limit if async
                 if (asyncGeneration && batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs)
                 {
                      break; // Stop this batch and yield
                 }
            }
             batchTimer.Stop();


            // Yield control back to Unity if time limit hit, batch size reached, or if not async
            if (asyncGeneration && (batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs || processedSinceYield >= yieldBatchSize))
            {
                // // Debug.Log($"ActivateQueuedObjects: Yielding. Processed {processedSinceYield} since last yield. Total: {totalActivated}. Queue: {generationQueue.Count}. Time: {batchTimer.Elapsed.TotalMilliseconds:F1}ms");
                processedSinceYield = 0;
                yield return null; // Wait for the next frame
            }
            else if (!asyncGeneration && generationQueue.Count > 0)
            {
                // If not async, yield each frame to avoid freezing if many objects
                 yield return null;
            }
             // If async and neither condition met, continue processing in the same frame
        }

        // // Debug.Log($"ActivateQueuedObjects: 完成。共激活 {totalActivated} 个对象。");
        instantiationCoroutine = null; // Clear coroutine reference when done
    }


    bool ValidateObjectForActivation(GameObject obj)
    {
        if (obj == null) return false;

        // Use the object's name to find its intended origin
        if (TryParsePositionFromName(obj.name, out Vector2Int origin))
        {
            // Check if a record still exists for this origin
            if (trackedObjects.TryGetValue(origin, out BuildingRecord record))
            {
                // Check if the instance in the record matches the object we're trying to activate
                if (record.instance == obj)
                {
                    return true; // Valid: Record exists and points to this instance
                }
                else
                {
                    // Invalid: Record exists, but points to a different instance (maybe replaced?)
                    // Debug.LogWarning($"Validate: 失败 '{obj.name}' @ {origin}。记录指向不同实例 ({record.instance?.name ?? "null"})。");
                    return false;
                }
            }
            else
            {
                // Invalid: No record found for this origin (object was likely removed/overwritten)
                // Debug.LogWarning($"Validate: 失败 '{obj.name}' @ {origin}。无记录。");
                return false;
            }
        }
        else
        {
            // Invalid: Cannot determine origin from name
            // Debug.LogError($"Validate: 失败 '{obj.name}'。无法从名称解析位置。");
            return false;
        }
    }


    void ClearPreviousGeneration()
    {
        // Debug.Log("开始清理之前的生成...");

        // Stop active coroutines
        if (mainGenerationCoroutine != null)
        {
            StopCoroutine(mainGenerationCoroutine);
            mainGenerationCoroutine = null;
        }
        if (instantiationCoroutine != null)
        {
            StopCoroutine(instantiationCoroutine);
            instantiationCoroutine = null;
        }
        StopTrafficAndPathUpdates(); // Stop traffic simulation
        ClearHighlightedPath(); // Clear path visuals and state

        // Destroy generated GameObjects
        if (mapParent != null)
        {
            // Iterate backwards as child count changes during destruction
            for (int i = mapParent.childCount - 1; i >= 0; i--)
            {
                Transform child = mapParent.GetChild(i);
                if (child != null)
                {
                    DestroyObjectInstance(child.gameObject);
                }
            }
        }
        else
        {
             // Try finding the default parent if mapParent wasn't set
             GameObject defaultParent = GameObject.Find("GeneratedCity");
             if (defaultParent != null)
             {
                  for (int i = defaultParent.transform.childCount - 1; i >= 0; i--)
                  {
                       Transform child = defaultParent.transform.GetChild(i);
                       if (child != null) DestroyObjectInstance(child.gameObject);
                  }
             }
             else
             {
                  // Debug.LogWarning("mapParent 未设置，也找不到 'GeneratedCity'。无法自动清理场景对象。");
             }
        }


        // Clear data structures
        map = null; // Let GC collect the old array
        trackedObjects.Clear();
        occupiedTiles.Clear();
        generationQueue.Clear();
        roadTiles.Clear();
        voronoiSites.Clear();
        noisePoints.Clear();
        highlightedPathInstances.Clear();
        currentHighlightedPath.Clear();


        // Reset state variables
        prefabsValidated = false;
        currentHoverInfo = "";
        if (hoverInfoTextElement != null) hoverInfoTextElement.text = "";
        pathStartPoint = null;
        pathEndPoint = null;
        // noiseOffset can be kept or reset if desired:
        // noiseOffset = Vector2.zero;

        // Debug.Log("之前的生成清理完毕。");
    }

    void DestroyObjectInstance(GameObject obj)
    {
        if (obj == null) return;

        // Use DestroyImmediate in Editor when not playing, otherwise use Destroy
        if (Application.isEditor && !Application.isPlaying)
        {
            #if UNITY_EDITOR
            DestroyImmediate(obj);
            #endif
        }
        else
        {
            Destroy(obj);
        }
    }
    #endregion // Instantiation & Cleanup

    #region Internal Fixed Cost A*
    // --- 内部固定成本 A* ---
    // Used for road generation logic where complex costs aren't needed

    private class Internal_PathNode
    {
        public Vector2Int position;
        public float gScore; // Cost from start
        public float hScore; // Heuristic cost to target
        public float fScore => gScore + hScore; // Total estimated cost
        public Internal_PathNode parent;

        public Internal_PathNode(Vector2Int pos, float g, float h, Internal_PathNode p)
        {
            position = pos;
            gScore = g;
            hScore = h;
            parent = p;
        }
    }

    // Finds a path using fixed costs, primarily for establishing road connections
    private List<Vector2Int> FindPath_InternalFixedCost(Vector2Int start, HashSet<Vector2Int> targets)
    {
        if (targets == null || targets.Count == 0 || !IsInMap(start)) return null;
        if (targets.Contains(start)) return new List<Vector2Int>() { start }; // Already at a target

        // --- Initialization ---
        List<Internal_PathNode> openSet = new List<Internal_PathNode>();     // Nodes to be evaluated
        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>(); // Nodes already evaluated
        Dictionary<Vector2Int, Internal_PathNode> nodeMap = new Dictionary<Vector2Int, Internal_PathNode>(); // Fast lookup for node data

        float startHScore = InternalHeuristic(start, targets);
        Internal_PathNode startNode = new Internal_PathNode(start, 0f, startHScore, null);
        openSet.Add(startNode);
        nodeMap[start] = startNode;

        int iterations = 0;
        int maxIterations = mapWidth * mapHeight * 4; // Safety break

        // --- Main Loop ---
        while (openSet.Count > 0)
        {
            iterations++;
            if (iterations > maxIterations)
            {
                // Debug.LogError($"内部 A* 迭代超限 ({start} -> targets)。");
                return null; // Prevent infinite loop
            }

            // Get node with lowest F score
            openSet.Sort((a, b) => a.fScore.CompareTo(b.fScore)); // Simple sort, consider PriorityQueue for large maps
            Internal_PathNode current = openSet[0];
            openSet.RemoveAt(0);

            // --- Goal Check ---
            if (targets.Contains(current.position))
            {
                return InternalReconstructPath(current); // Path found!
            }

            closedSet.Add(current.position);

            // --- Explore Neighbors ---
            foreach (var dir in directions)
            {
                Vector2Int neighborPos = current.position + dir;

                // Basic validation
                if (!IsInMap(neighborPos) || closedSet.Contains(neighborPos))
                {
                    continue;
                }

                // Calculate cost to reach neighbor
                float terrainCost = GetTerrainCost_InternalFixed(neighborPos);
                if (float.IsPositiveInfinity(terrainCost))
                {
                    continue; // Impassable terrain
                }
                float tentativeGScore = current.gScore + terrainCost; // Cost from start to neighbor via current

                // Check if neighbor is already in nodeMap (means it's in openSet or processed differently)
                nodeMap.TryGetValue(neighborPos, out Internal_PathNode neighborNode);

                // If new path to neighbor is shorter OR neighbor hasn't been seen
                if (neighborNode == null || tentativeGScore < neighborNode.gScore)
                {
                    float neighborHScore = InternalHeuristic(neighborPos, targets);

                    if (neighborNode == null) // First time seeing this neighbor
                    {
                        neighborNode = new Internal_PathNode(neighborPos, tentativeGScore, neighborHScore, current);
                        nodeMap[neighborPos] = neighborNode;
                        openSet.Add(neighborNode); // Add to open set for evaluation
                    }
                    else // Found a better path to this existing node
                    {
                        neighborNode.parent = current;
                        neighborNode.gScore = tentativeGScore;
                        // HScore remains the same, FScore updates automatically
                        // If neighborNode was already in openSet, its position in the sorted list might need update.
                        // Simple list sort handles this implicitly on the next iteration.
                    }
                }
            }
        }

        // --- No Path Found ---
        // Debug.LogWarning($"内部 A* 无法找到路径: {start} -> targets");
        return null;
    }

    // Cost function for internal A* (road generation)
    private float GetTerrainCost_InternalFixed(Vector2Int pos)
    {
        if (!IsInMap(pos)) return float.PositiveInfinity;

        // Check occupation (heavy penalty for non-road buildings)
        if (occupiedTiles.TryGetValue(pos, out Vector2Int origin) && origin != pos) // Occupied by a multi-tile object originating elsewhere
        {
             if (trackedObjects.TryGetValue(origin, out BuildingRecord occupier) && occupier.type != ZoneType.Road)
             {
                  return float.PositiveInfinity; // Avoid building over existing buildings
             }
             // If occupied by a road, treat as road cost below.
        }

        // Check map type (prefer roads, allow empty, avoid buildings)
        if (map == null) return 10f; // Default high cost if map not initialized

        switch (map[pos.x, pos.y])
        {
            case ZoneType.Road:
                return 1.0f; // Very cheap to path over existing roads
            case ZoneType.Empty:
                return 5.0f; // Moderately expensive to path over empty space (encourages using roads)
            //case ZoneType.Residential: // Treat buildings as impassable for road generation pathing
            //case ZoneType.Commercial:
            //case ZoneType.Industrial:
            default: // Includes buildings
                return float.PositiveInfinity; // Cannot path through existing buildings
        }
    }

    // Heuristic for internal A* (Manhattan distance to nearest target)
    private float InternalHeuristic(Vector2Int current, HashSet<Vector2Int> targets)
    {
        float minDistance = float.MaxValue;
        foreach (var target in targets)
        {
            minDistance = Mathf.Min(minDistance, InternalManhattanDistance(current, target));
        }
        return minDistance;
    }

    private int InternalManhattanDistance(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    // Reconstructs the path from the target node back to the start
    private List<Vector2Int> InternalReconstructPath(Internal_PathNode targetNode)
    {
        List<Vector2Int> path = new List<Vector2Int>();
        Internal_PathNode current = targetNode;
        int safetyCounter = 0;
        int maxPathLength = mapWidth * mapHeight + 1; // Safety break

        while (current != null && safetyCounter < maxPathLength)
        {
            path.Add(current.position);
            current = current.parent;
            safetyCounter++;
        }

        if (safetyCounter >= maxPathLength)
        {
            // Debug.LogError("内部 A* 重构路径超长！可能存在循环。");
            return null; // Indicate error
        }

        path.Reverse(); // Reverse to get path from start to end
        return path;
    }

    #endregion // Internal Fixed Cost A*

    #region Helper Methods
    // --- 辅助与帮助方法 ---

    bool IsInMap(Vector2Int pos)
    {
        return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight;
    }

    // Parses "PrefabName_X_Y" format
    bool TryParsePositionFromName(string name, out Vector2Int position)
    {
        position = Vector2Int.zero;
        if (string.IsNullOrEmpty(name)) return false;

        try
        {
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore >= name.Length - 1) return false; // Need underscore and chars after

            int secondLastUnderscore = name.LastIndexOf('_', lastUnderscore - 1);
            if (secondLastUnderscore < 0) return false; // Need two underscores

            string xStr = name.Substring(secondLastUnderscore + 1, lastUnderscore - secondLastUnderscore - 1);
            string yStr = name.Substring(lastUnderscore + 1);

            // Use InvariantCulture for reliable parsing regardless of system locale
            if (int.TryParse(xStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(yStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y))
            {
                position = new Vector2Int(x, y);
                return true;
            }
            else
            {
                return false; // Parsing failed
            }
        }
        catch (Exception ex)
        {
            // Debug.LogError($"TryParsePositionFromName Error: {ex.Message} for name '{name}'");
            return false;
        }
    }

    // Simple straight-line path drawer (potentially useful for debugging or simple connections)
    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet)
    {
        Vector2Int current = start;
        pathTilesSet.Add(current);

        // Move horizontally first
        int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x);
        while (current.x != end.x)
        {
            current.x += xDir;
            if (!IsInMap(current)) return; // Stop if out of bounds
            pathTilesSet.Add(current);
        }

        // Then move vertically
        int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y);
        while (current.y != end.y)
        {
            current.y += yDir;
            if (!IsInMap(current)) return; // Stop if out of bounds
            pathTilesSet.Add(current);
        }
    }


    // ---- 已修正 IsPrefabSource 方法 ----
    // Checks if an instance originates from a specific prefab asset
    bool IsPrefabSource(GameObject instance, GameObject prefab)
    {
        if (instance == null || prefab == null)
        {
            return false;
        }

        // --- 编辑器特定检查 ---
#if UNITY_EDITOR
        // 仅在编辑器非播放模式下使用 PrefabUtility，避免运行时开销
        // Correction: Can use GetCorrespondingObjectFromSource in Play mode too, but GetPrefabAssetType is Editor-only.
        // Let's stick to GetCorrespondingObjectFromSource as it works in both editor states.
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
            // Debug.LogWarning($"IsPrefabSource (Editor Check) failed for instance '{instance.name}': {e.Message}");
            // Fallback to name check in case of error? Or just return false? Let's return false.
            return false;
        }
#else
        // --- 运行时检查 (基于名称) ---
        // This is executed in builds where UNITY_EDITOR is not defined.
        // It's a fallback check, less reliable than the editor check.
        return instance.name.StartsWith(prefab.name + "_"); // Check if name starts with "PrefabName_"
#endif

    } // <-- 方法结束括号
    #endregion // Helper Methods

    #region Traffic Simulation & Auto-Update
    // --- 交通模拟与自动路径更新 ---

    void StartTrafficAndPathUpdates()
    {
        StopTrafficAndPathUpdates(); // Ensure no duplicates
        if (vehicleUpdateInterval > 0 && Application.isPlaying)
        {
            // Debug.Log($"启动交通/路径更新 (每 {vehicleUpdateInterval}s)");
            trafficAndPathUpdateCoroutine = StartCoroutine(UpdateTrafficAndPathCoroutine());
        }
        else if (vehicleUpdateInterval <= 0)
        {
            // Debug.LogWarning("自动交通/路径更新已禁用 (间隔 <= 0)。");
        }
        else if (!Application.isPlaying)
        {
            // Debug.Log("非播放模式下不启动交通/路径更新。");
        }
    }

    void StopTrafficAndPathUpdates()
    {
        if (trafficAndPathUpdateCoroutine != null)
        {
            StopCoroutine(trafficAndPathUpdateCoroutine);
            trafficAndPathUpdateCoroutine = null;
            // Debug.Log("停止交通/路径更新。");
        }
    }

    IEnumerator UpdateTrafficAndPathCoroutine()
    {
         // Initial delay before the first update
         yield return new WaitForSeconds(Mathf.Max(1.0f, vehicleUpdateInterval / 2f));

        while (true)
        {
            // 1. Simulate Traffic Changes (Simple random update for now)
            // Use ToList() to avoid modification issues while iterating
            foreach (BuildingRecord record in trackedObjects.Values.ToList())
            {
                if (record?.type == ZoneType.Road && record.size == Vector2Int.one && record.capacity > 0)
                {
                    // Randomly update vehicle count within a plausible range
                    record.currentVehicles = UnityEngine.Random.Range(0, record.capacity + 10); // Allow slight overcapacity visually
                    // Clamp if strict capacity is needed:
                    // record.currentVehicles = Mathf.Clamp(UnityEngine.Random.Range(0, record.capacity * 2), 0, record.capacity + 5);
                }
            }
            // // Debug.Log("UpdateTrafficAndPathCoroutine: 更新了车辆计数。");


            // 2. Re-calculate and Highlight Path if defined
            if (pathStartPoint.HasValue && pathEndPoint.HasValue)
            {
                 // // Debug.Log("UpdateTrafficAndPathCoroutine: 重新计算高亮路径...");
                 FindAndHighlightPath(false); // Update path without logging the start message
            }

             // 3. Wait for the next interval
             yield return new WaitForSeconds(vehicleUpdateInterval);
        }
    }

    // Calculates travel time based on traffic load
    public float CalculateTravelTime(BuildingRecord roadRecord)
    {
        // Validate input
        if (roadRecord == null || roadRecord.type != ZoneType.Road || roadRecord.size != Vector2Int.one || roadRecord.capacity <= 0)
        {
            return float.PositiveInfinity; // Impassable or invalid road segment
        }

        // Calculate load factor (n/v)
        float loadFactor = (float)roadRecord.currentVehicles / roadRecord.capacity;

        // Apply congestion penalty
        // Simple exponential increase after threshold
        float congestionMultiplier = (loadFactor <= trafficCongestionThreshold)
            ? 1.0f // No congestion penalty below threshold
            : (1.0f + Mathf.Exp(loadFactor - trafficCongestionThreshold)); // Increasing penalty above threshold
            // Alternative: smoother polynomial: 1.0f + Mathf.Pow(Mathf.Max(0, loadFactor - trafficCongestionThreshold), 2);

        // Calculate final time
        float baseCost = Mathf.Max(0.01f, travelTimeBaseCost); // Ensure base cost is slightly positive
        float finalTime = baseCost * congestionMultiplier;

        // Ensure final time is always slightly positive (minimum travel time)
        return Mathf.Max(0.01f, finalTime);
    }

    void HandleMouseHover()
    {
        if (mainCamera == null) return;

        string infoToShow = "";
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        // Raycast specifically against the road layer
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask))
        {
            // Convert hit point to grid coordinates
            // Assumes flat ground at y=0 and grid aligned with world axes
            int gridX = Mathf.FloorToInt(hit.point.x);
            int gridY = Mathf.FloorToInt(hit.point.z);
            Vector2Int gridPos = new Vector2Int(gridX, gridY);

            // Check if the grid position is valid and has a tracked object (should be the road tile itself)
            if (IsInMap(gridPos) && trackedObjects.TryGetValue(gridPos, out BuildingRecord record))
            {
                 // Check if it's a valid 1x1 road tile we can display info for
                 if (record.type == ZoneType.Road && record.size == Vector2Int.one && record.instance != null)
                 {
                     float travelTime = CalculateTravelTime(record);
                     infoToShow = $"道路 @ ({gridPos.x},{gridPos.y})\n" +
                                  $"容量 (v): {record.capacity}\n" +
                                  $"当前车辆 (n): {record.currentVehicles}\n" +
                                  $"通行时间: {travelTime:F2}";
                 }
                 // Could add info for other building types here if desired
            }
        }

        // Update the displayed text
        currentHoverInfo = infoToShow;
        if (hoverInfoTextElement != null)
        {
            hoverInfoTextElement.text = infoToShow;
        }
    }
    #endregion // Traffic Simulation & Auto-Update

    #region Pathfinding & Highlighting (User Interaction)
    // --- 寻路与高亮 - 用户交互部分 ---

    void HandleMapClick(int mouseButton)
    {
        if (mainCamera == null || map == null || aStarFinder == null) return;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

        // Raycast against the road layer only
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask))
        {
            // Determine grid position from hit point
            int gridX = Mathf.FloorToInt(hit.point.x);
            int gridY = Mathf.FloorToInt(hit.point.z);
            Vector2Int clickedGridPos = new Vector2Int(gridX, gridY);

            // Validate the clicked position: Must be in map and be a 1x1 road tile
            if (IsInMap(clickedGridPos) &&
                trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord record) &&
                record.type == ZoneType.Road &&
                record.size == Vector2Int.one)
            {
                if (mouseButton == 0) // Left Click: Set Start Point
                {
                    if (pathStartPoint != clickedGridPos) // Only update if different
                    {
                        pathStartPoint = clickedGridPos;
                        // Debug.Log($"起点: {pathStartPoint.Value}");
                        if (pathEndPoint.HasValue)
                        {
                            FindAndHighlightPath(true); // Find path if end is also set
                        }
                        else
                        {
                            ClearHighlightedPathVisualsOnly(); // Clear visuals if only start is set
                        }
                    }
                }
                else if (mouseButton == 1) // Right Click: Set End Point
                {
                    if (pathEndPoint != clickedGridPos) // Only update if different
                    {
                        pathEndPoint = clickedGridPos;
                        // Debug.Log($"终点: {pathEndPoint.Value}");
                        if (pathStartPoint.HasValue)
                        {
                            FindAndHighlightPath(true); // Find path if start is also set
                        }
                        else
                        {
                            ClearHighlightedPathVisualsOnly(); // Clear visuals if only end is set
                        }
                    }
                }
            }
            else // Clicked on something invalid (not a road, or outside map)
            {
                 string reason = "?";
                 if (!IsInMap(clickedGridPos)) reason = "出界";
                 else if (!trackedObjects.ContainsKey(clickedGridPos)) reason = "无记录";
                 else if (trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord r)) reason = $"Type={r.type},Size={r.size}";
                 else reason = "无法获取记录"; // Should not happen if ContainsKey is true
                 // Debug.LogWarning($"HandleMapClick: 点击 {clickedGridPos} 无效 ({reason}). 命中: {hit.collider?.name ?? "无碰撞体"}");
            }
        }
        // else: Raycast didn't hit anything on the road layer
    }

    void FindAndHighlightPath(bool logStartMessage = true)
    {
        // Check if path start/end points are valid
        if (!pathStartPoint.HasValue || !pathEndPoint.HasValue)
        {
            ClearHighlightedPathVisualsOnly(); // Clear any existing path if points become invalid
            return;
        }

        if (aStarFinder == null)
        {
            // Debug.LogError("寻路失败：缺少 AStar 组件！");
            ClearHighlightedPath(); // Clear state and visuals
            return;
        }

        // Handle trivial case: start and end are the same
        if (pathStartPoint.Value == pathEndPoint.Value)
        {
            if (logStartMessage)
            {
                // Debug.Log("寻路：起点终点相同。");
            }
            HighlightPath(new List<Vector2Int> { pathStartPoint.Value }); // Highlight just the single point
            return;
        }

        if (logStartMessage)
        {
            // Debug.Log($"寻路：请求 {pathStartPoint.Value} -> {pathEndPoint.Value}...");
        }

        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

        // Call the external AStar component
        List<Vector2Int> path = aStarFinder.FindPath(
            pathStartPoint.Value,
            pathEndPoint.Value,
            GetPathfindingCost,       // Function to get cost of a tile (using traffic)
            IsTileWalkableForPath,    // Function to check if a tile is walkable (must be 1x1 road)
            travelTimeBaseCost        // Base cost for heuristic calculation (optional, A* might use its own)
        );

        timer.Stop();

        // Process the result
        if (path != null && path.Count > 0)
        {
            float totalTime = CalculateTotalPathTime(path);
            if (logStartMessage)
            {
                // Debug.Log($"寻路：找到路径 ({path.Count} 瓦片)。总通行时间: {totalTime:F2}。计算耗时: {timer.ElapsedMilliseconds}ms");
            }
            HighlightPath(path);
        }
        else
        {
            if (logStartMessage)
            {
                // Debug.LogWarning($"寻路：未找到路径 {pathStartPoint.Value} -> {pathEndPoint.Value}。计算耗时: {timer.ElapsedMilliseconds}ms");
            }
            ClearHighlightedPathVisualsOnly(); // Clear visuals if no path found
        }
    }

    // --- A* Callbacks ---

    // Provides the cost for the A* algorithm (uses traffic simulation)
    private float GetPathfindingCost(Vector2Int pos)
    {
        if (!IsInMap(pos))
        {
            return float.PositiveInfinity; // Out of bounds is infinitely costly
        }
        if (trackedObjects.TryGetValue(pos, out BuildingRecord record) &&
            record.type == ZoneType.Road &&
            record.size == Vector2Int.one)
        {
            float time = CalculateTravelTime(record);
            return Mathf.Max(0.01f, time); // Return calculated travel time (ensure > 0)
        }
        // Not a 1x1 road tile
        return float.PositiveInfinity;
    }

    // Checks if a tile is walkable for the A* algorithm
    private bool IsTileWalkableForPath(Vector2Int pos)
    {
        if (!IsInMap(pos))
        {
            return false; // Cannot walk outside map
        }
        // Must be a tracked 1x1 road tile
        return trackedObjects.TryGetValue(pos, out BuildingRecord record) &&
               record.type == ZoneType.Road &&
               record.size == Vector2Int.one;
    }

    // --- Path Highlighting Logic ---

    // Calculates the total travel time for a given path (for display/debug)
    float CalculateTotalPathTime(List<Vector2Int> path)
    {
        if (path == null || path.Count == 0) return 0f;

        float totalTime = 0f;
        for (int i = 0; i < path.Count; i++) // Include cost of the start node? A* usually does cost *between* nodes. Let's sum cost *of* each node in path.
        {
            float cost = GetPathfindingCost(path[i]);
            if (float.IsPositiveInfinity(cost))
            {
                // Debug.LogError($"CalculateTotalPathTime: 路径包含不可通行瓦片 {path[i]}！");
                return float.PositiveInfinity; // Invalid path
            }
            totalTime += cost;
        }
        return totalTime;
    }

    void HighlightPath(List<Vector2Int> path)
    {
        // Ensure prefabs are available
        if (highlightedRoadPrefab == null || roadPrefab == null)
        {
            // Debug.LogError("无法高亮路径：道路或高亮道路预设体未分配！");
            return;
        }

        // 1. Clear existing highlighted visuals ONLY (don't reset start/end points)
        ClearHighlightedPathVisualsOnly();

        // 2. Store the new path
        currentHighlightedPath = (path != null) ? new List<Vector2Int>(path) : new List<Vector2Int>();
        if (currentHighlightedPath.Count == 0)
        {
            return; // Nothing to highlight
        }

        // 3. Iterate through the new path and swap prefabs
        foreach (Vector2Int pos in currentHighlightedPath)
        {
            // Find the record for the road tile
            if (trackedObjects.TryGetValue(pos, out BuildingRecord record) &&
                record.type == ZoneType.Road &&
                record.size == Vector2Int.one &&
                record.instance != null) // Ensure instance exists
            {
                GameObject currentInstance = record.instance;

                // Check if it's already the highlighted version (might happen with overlapping paths or updates)
                // Use IsPrefabSource for robustness, fallback to name check
                bool alreadyHighlighted = IsPrefabSource(currentInstance, highlightedRoadPrefab); // currentInstance.name.StartsWith("HighlightedRoad_"); // Less robust check

                if (alreadyHighlighted)
                {
                    // Already highlighted, just ensure it's in our list of highlighted instances
                     if (!highlightedPathInstances.Contains(currentInstance))
                     {
                          highlightedPathInstances.Add(currentInstance);
                     }
                }
                else // Needs to be swapped to highlighted version
                {
                    Vector3 position = currentInstance.transform.position;
                    Quaternion rotation = currentInstance.transform.rotation;

                    // Instantiate the highlighted version
                    GameObject newHighlightInstance = Instantiate(highlightedRoadPrefab, position, rotation, mapParent);
                    newHighlightInstance.name = $"HighlightedRoad_{pos.x}_{pos.y}"; // Use specific name

                    // Update the record to point to the new instance
                    record.instance = newHighlightInstance;

                    // Add to our tracking list
                    highlightedPathInstances.Add(newHighlightInstance);

                    // Destroy the original road instance
                    DestroyObjectInstance(currentInstance);
                }
            }
            else // Error case: Tile in path is not a valid road tile in trackedObjects
            {
                string reason = "?";
                 if (!trackedObjects.ContainsKey(pos)) reason = "无记录";
                 else if (trackedObjects.TryGetValue(pos, out BuildingRecord r)) reason = $"T={r.type},S={r.size},I={(r.instance == null ? "null" : "ok")}";
                 else reason = "无法获取记录";
                 // Debug.LogWarning($"HighlightPath: 无法在 {pos} 高亮，无效的道路瓦片 ({reason})");
            }
        }
         // // Debug.Log($"高亮了 {highlightedPathInstances.Count} 个路径瓦片。");
    }

    // Clears both visuals and path selection state
    void ClearHighlightedPath()
    {
        ClearHighlightedPathVisualsOnly();
        currentHighlightedPath.Clear();
        pathStartPoint = null;
        pathEndPoint = null;
        // // Debug.Log("清除高亮路径和选择点。");
    }

    // Only reverts highlighted road visuals back to normal roads
    void ClearHighlightedPathVisualsOnly()
    {
        if (roadPrefab == null)
        {
            // Debug.LogError("无法清除高亮：Road Prefab 未分配！");
            return;
        }
        if (highlightedPathInstances.Count == 0)
        {
            return; // Nothing to clear
        }

        // // Debug.Log($"开始清除 {highlightedPathInstances.Count} 个高亮瓦片的视觉效果...");
        // Create a copy to iterate over, as we modify the original list indirectly
        List<GameObject> instancesToRevert = new List<GameObject>(highlightedPathInstances);
        highlightedPathInstances.Clear(); // Clear the tracking list immediately

        foreach (GameObject highlightedInstance in instancesToRevert)
        {
            if (highlightedInstance == null) continue; // Skip if instance was destroyed elsewhere

            // Try to get position from name
            if (TryParsePositionFromName(highlightedInstance.name, out Vector2Int pos))
            {
                // Find the record for this position
                if (trackedObjects.TryGetValue(pos, out BuildingRecord record))
                {
                    // Check if the record still points to the highlighted instance we are removing
                    if (record.instance == highlightedInstance)
                    {
                        // Instantiate the original road prefab at the same location
                        Vector3 position = highlightedInstance.transform.position;
                        Quaternion rotation = highlightedInstance.transform.rotation;
                        GameObject originalInstance = Instantiate(roadPrefab, position, rotation, mapParent);
                        originalInstance.name = $"{roadPrefab.name}_{pos.x}_{pos.y}"; // Standard name

                        // Update the record to point back to the original road instance
                        record.instance = originalInstance;

                        // Destroy the highlighted instance we are replacing
                        DestroyObjectInstance(highlightedInstance);
                    }
                    else
                    {
                        // Record no longer points to this highlighted instance (maybe overwritten again?)
                        // Just destroy the orphaned highlighted instance.
                        // Debug.LogWarning($"ClearVisuals: 瓦片 {pos} 的记录不再指向预期的高亮实例 ({highlightedInstance.name})。可能已被覆盖。销毁高亮实例。");
                        DestroyObjectInstance(highlightedInstance);
                    }
                }
                else
                {
                    // No record found for this position anymore. Just destroy the highlighted instance.
                    // Debug.LogError($"ClearVisuals: 无法找到瓦片 {pos} 的记录。销毁孤立的高亮实例 '{highlightedInstance.name}'。");
                    DestroyObjectInstance(highlightedInstance);
                }
            }
            else
            {
                // Cannot parse position from name. Just destroy the instance.
                // Debug.LogWarning($"ClearVisuals: 无法从名称 '{highlightedInstance.name}' 解析位置。销毁实例。");
                DestroyObjectInstance(highlightedInstance);
            }
        }
         // // Debug.Log("高亮视觉效果清除完毕。");
    }
    #endregion // Pathfinding & Highlighting (User Interaction)


    #region Public Accessors
    // --- 公共访问器 ---

    /// <summary>
    /// 返回当前逻辑道路瓦片位置的 HashSet 副本。
    /// 这是为了让外部脚本（如 CarPlacementController）可以安全地访问道路信息，而不能修改内部状态。
    /// </summary>
    public HashSet<Vector2Int> GetRoadTiles()
    {
        // 返回副本以防止外部修改内部集合
        return new HashSet<Vector2Int>(this.roadTiles);
    }
    #endregion // Public Accessors

} 