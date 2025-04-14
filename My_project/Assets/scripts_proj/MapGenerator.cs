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
        // 容量(v)和当前车辆数(n)将在 PlaceObject 中为道路特别设置
    }
}


/// <summary>
/// 生成城市布局，模拟交通，处理路径选择和高亮。
/// 使用独立的 AStar 组件基于通行时间进行寻路计算。
/// 定期根据交通状况自动更新高亮路径。
/// </summary>
[RequireComponent(typeof(AStar))] // 确保 AStar 组件存在于同一个 GameObject 上
public class MapGenerator : MonoBehaviour
{
    #region Public Inspector Variables (公共检视面板变量)

    [Header("Prefabs & Parent (预设体与父对象)")]
    [Tooltip("重要: 用于所有道路瓦片的预设体。应有 BoxCollider 且尺寸严格匹配 1x1。必须在 Road Layer 上。")]
    [SerializeField] private GameObject roadPrefab; // 道路预设体
    [Tooltip("重要: 用于高亮显示路径的道路预设体。应有 BoxCollider 且尺寸/轴心点与 Road Prefab 匹配。")]
    [SerializeField] private GameObject highlightedRoadPrefab; // 高亮道路预设体

    [Tooltip("3x3 建筑预设体列表。")] public List<GameObject> buildingPrefabs3x3;
    [Tooltip("2x2 建筑预设体列表。")] public List<GameObject> buildingPrefabs2x2;
    [Tooltip("1x1 建筑预设体列表。")] public List<GameObject> buildingPrefabs1x1;
    [Tooltip("所有生成对象的父级变换。")] public Transform mapParent; // 生成对象的父节点

    [Header("Map Dimensions (地图尺寸)")]
    [Min(10)] public int mapWidth = 50; // 地图宽度
    [Min(10)] public int mapHeight = 50; // 地图高度

    [Header("Voronoi Road Network Options (Voronoi 道路网络选项)")]
    [Min(2)] public int voronoiSiteSpacing = 15; // Voronoi 站点间距
    [Range(0, 10)] public int voronoiSiteJitter = 3; // Voronoi 站点抖动

    [Header("Noise Branch Connection Options (噪声分支连接选项)")]
    public float branchNoiseScale = 10.0f; // 分支噪声缩放
    [Range(0.0f, 1.0f)] public float noiseBranchThreshold = 0.8f; // 噪声分支阈值

    [Header("Map Edge Connection (地图边缘连接)")]
    public bool ensureEdgeConnections = true; // 是否确保连接到地图边缘

    [Header("Performance & Features (性能与特性)")]
    public Vector2 noiseOffset = Vector2.zero; // 噪声偏移量
    [Min(1)] public int objectsPerFrame = 200; // 每帧实例化的对象数 (异步)
    [Range(100, 10000)] public int yieldBatchSize = 500; // 异步生成时多少操作后让出控制权
    public bool asyncGeneration = true; // 是否启用异步生成

    [Header("Traffic Simulation (交通模拟)")]
    [Min(0.1f)] public float travelTimeBaseCost = 1.0f; // 基础通行时间成本 (f(x)中的基础值1对应的现实时间)
    [Range(0.0f, 2.0f)] public float trafficCongestionThreshold = 1.0f; // 交通拥堵阈值 T (n/v > T 时，f(x)开始变化)
    [Tooltip("车辆计数和路径重新计算的更新频率（秒）。")]
    [Min(1.0f)] public float vehicleUpdateInterval = 15.0f; // 交通更新间隔
    [Tooltip("可选: 用于显示悬停信息的 TextMeshProUGUI 元素。")] public TextMeshProUGUI hoverInfoTextElement; // 用于显示悬停信息的 UI 文本

    [Header("Pathfinding & Highlighting (寻路与高亮)")]
    [Tooltip("用于射线投射以仅击中道路瓦片的层。确保道路预设体在此层上。")]
    public LayerMask roadLayerMask = default; // 道路层遮罩 (!!必须在 Inspector 中设置!!)

    #endregion

    #region Private Variables (私有变量)
    // 在 MapGenerator 类内部定义区域类型枚举
    public enum ZoneType
    {
        Empty, Road, Residential, Commercial, Industrial
    }
    private ZoneType[,] map; // 地图逻辑网格
    private Queue<GameObject> generationQueue = new Queue<GameObject>(); // 对象实例化队列 (用于异步)

    // 使用上面定义的 BuildingRecord 类
    private Dictionary<Vector2Int, BuildingRecord> trackedObjects = new Dictionary<Vector2Int, BuildingRecord>(); // 跟踪已放置的对象 (Key: 对象左下角原点)
    private Dictionary<Vector2Int, Vector2Int> occupiedTiles = new Dictionary<Vector2Int, Vector2Int>(); // 跟踪每个瓦片被哪个对象的原点所占据 (Key: 瓦片坐标, Value: 对象原点坐标)

    private readonly List<Vector2Int> directions = new List<Vector2Int> { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left }; // 四个基本方向

    // 生成过程数据
    private List<Vector2Int> voronoiSites = new List<Vector2Int>();   // Voronoi 站点
    private HashSet<Vector2Int> roadTiles = new HashSet<Vector2Int>(); // 逻辑上的道路瓦片位置集合
    private List<Vector2Int> noisePoints = new List<Vector2Int>();    // 噪声点

    private bool prefabsValidated = false; // 预设体验证标志
    private Coroutine instantiationCoroutine = null; // 对象实例化协程句柄
    private Coroutine mainGenerationCoroutine = null; // 主生成流程协程句柄

    // --- 交通/悬停/路径更新变量 ---
    private Coroutine trafficAndPathUpdateCoroutine = null; // 组合更新协程句柄
    private string currentHoverInfo = "";                   // 当前悬停信息 (用于 OnGUI 显示)
    private Camera mainCamera;                              // 主摄像机缓存

    // 寻路状态与引用
    private AStar aStarFinder; // 对 AStar 组件的引用
    private Vector2Int? pathStartPoint = null; // 寻路起点 (可空类型)
    private Vector2Int? pathEndPoint = null;   // 寻路终点 (可空类型)
    private List<GameObject> highlightedPathInstances = new List<GameObject>(); // 跟踪当前高亮路径的 GameObject 实例
    private List<Vector2Int> currentHighlightedPath = new List<Vector2Int>();   // 存储当前高亮路径的逻辑位置列表

    #endregion

    #region Unity Lifecycle Methods (Unity 生命周期方法)

    void Awake()
    {
        // 获取挂载在同一 GameObject 上的 AStar 组件
        aStarFinder = GetComponent<AStar>();
        if (aStarFinder == null)
        {
            Debug.LogError("在 MapGenerator 所在的 GameObject 上找不到 AStar 组件！寻路功能将无法工作。");
        }
    }

    void Start()
    {
        // 缓存主摄像机
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            // 确保场景中有一个标签为 "MainCamera" 的摄像机
            Debug.LogError("MapGenerator 需要场景中有一个标签为 'MainCamera' 的主摄像机！");
        }

        // 初始化 UI 文本
        if (hoverInfoTextElement != null)
        {
            hoverInfoTextElement.text = "";
        }

        // 提示如何开始生成
        if (mainGenerationCoroutine == null)
        {
            Debug.Log("请按 Play 按钮旁的 'Start Generation' 按钮，或右键点击脚本 Inspector > 'Start Generation' 开始生成。");
            // 如果需要在构建版本中自动开始，取消下面的注释:
            // StartGenerationProcess();
        }
    }

    void Update()
    {
        // --- 处理寻路输入 ---
        if (Input.GetMouseButtonDown(0)) // 鼠标左键点击
        {
            HandleMapClick(0); // 0 代表左键
        }
        else if (Input.GetMouseButtonDown(1)) // 鼠标右键点击
        {
            HandleMapClick(1); // 1 代表右键
        }

        // --- 更新鼠标悬停信息 ---
        if (map != null) // 仅当地图数据存在时更新
        {
            HandleMouseHover();
        }
    }

    void OnDestroy()
    {
        // 对象销毁时停止所有相关协程
        StopTrafficAndPathUpdates();
        if (mainGenerationCoroutine != null) StopCoroutine(mainGenerationCoroutine);
        if (instantiationCoroutine != null) StopCoroutine(instantiationCoroutine);
        // 清理工作由 ClearPreviousGeneration 完成
    }

    void OnGUI()
    {
        // --- 悬停信息显示 (如果没有设置 TextMeshProUGUI，则使用 IMGUI 作为后备) ---
        if (hoverInfoTextElement == null && !string.IsNullOrEmpty(currentHoverInfo))
        {
            GUI.backgroundColor = new Color(0, 0, 0, 0.7f); // 半透明黑色背景
            GUI.contentColor = Color.white; // 白色文字
            // 在屏幕左下角绘制信息框
            GUI.Box(new Rect(10, Screen.height - 80, 250, 70), currentHoverInfo);
        }

        // --- 路径选择信息显示 (如果没有设置 TextMeshProUGUI) ---
        string startText = pathStartPoint.HasValue ? pathStartPoint.Value.ToString() : "未选择";
        string endText = pathEndPoint.HasValue ? pathEndPoint.Value.ToString() : "未选择";

        string pathInfo = "路径选择:\n";
        pathInfo += $"起点: {startText}\n";
        pathInfo += $"终点: {endText}";

        if (hoverInfoTextElement == null) // 仅在未使用 TMP UI 时显示
        {
            // 调整 Y 坐标，避免与悬停信息重叠
            float yPos = string.IsNullOrEmpty(currentHoverInfo) ? Screen.height - 65 : Screen.height - 140;
             GUI.Box(new Rect(10, yPos, 200, 55), pathInfo);
        }
    }

    #endregion

    #region Generation Pipeline & Control (生成流程与控制)

    [ContextMenu("Start Generation")] // 允许在 Inspector 中右键启动
    public void StartGenerationProcess()
    {
         Debug.Log("StartGenerationProcess: 开始生成流程。");
        if (mainGenerationCoroutine != null)
        {
            Debug.LogWarning("生成已经在进行中！");
            return;
        }
        StopTrafficAndPathUpdates(); // 停止之前的交通和路径更新
        ClearHighlightedPath();     // 清除之前的高亮路径和选择状态
         Debug.Log("StartGenerationProcess: 启动 RunGenerationPipeline 协程。");
        mainGenerationCoroutine = StartCoroutine(RunGenerationPipeline());
    }

    // 主生成流程协程
    IEnumerator RunGenerationPipeline()
    {
         Debug.Log("RunGenerationPipeline: 协程已启动。");
        // --- 设置 ---
        if (mapParent == null) // 如果未指定父对象，尝试查找或创建
        {
            GameObject parentObj = GameObject.Find("GeneratedCity");
            if (parentObj == null) parentObj = new GameObject("GeneratedCity");
            mapParent = parentObj.transform;
             Debug.Log($"RunGenerationPipeline: 设置 mapParent 为 '{mapParent.name}'。");
        }

        ClearPreviousGeneration(); // 清理之前的生成结果和数据
         Debug.Log("RunGenerationPipeline: 已清理之前的生成。");
        ValidatePrefabs(); // 验证必要的预设体是否设置正确
         Debug.Log($"RunGenerationPipeline: 预设体验证结果: {prefabsValidated}");
        if (!prefabsValidated)
        {
            Debug.LogError("必要的预设体缺失或配置错误。请在 Inspector 中分配并修复。中止生成。");
            mainGenerationCoroutine = null;
            yield break; // 中止协程
        }
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            Debug.LogError("地图尺寸必须大于 0。");
            mainGenerationCoroutine = null;
            yield break;
        }

        map = new ZoneType[mapWidth, mapHeight]; // 初始化地图逻辑网格
        if (noiseOffset == Vector2.zero) // 如果噪声偏移为 0，随机设置一个
        {
            noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));
        }

        // --- 生成阶段 ---
        Debug.Log("--- 开始生成阶段 ---");
         Debug.Log("RunGenerationPipeline: 开始 GenerateRoadsPhase。");
        yield return StartCoroutine(GenerateRoadsPhase()); // 生成道路
         Debug.Log("RunGenerationPipeline: 完成 GenerateRoadsPhase。");

        // --- 放置建筑 (可选，带暂停等待输入) ---
        Debug.Log("--- 道路生成完毕 --- 等待输入以放置 3x3 建筑...");
        yield return WaitForInput(); // 等待用户输入继续
        Debug.Log("--- 开始放置 3x3 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(3)); // 放置 3x3 建筑
        StartObjectInstantiation(true); // 立即实例化队列中的对象
        if (instantiationCoroutine != null) yield return instantiationCoroutine; // 等待实例化完成

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

        // --- 启动交通模拟与路径更新 ---
        StartTrafficAndPathUpdates(); // 在所有对象都放置并激活后启动

        mainGenerationCoroutine = null; // 标记主生成协程结束
        Debug.Log("生成完成。请在道路上点击鼠标左键设置起点，右键设置终点。路径将自动更新。");
    }

    // 等待用户输入的协程 (鼠标左键或空格键)
    IEnumerator WaitForInput()
    {
        Debug.Log("请在 Game 视图中单击鼠标左键或按空格键以继续...");
        while (!Input.GetMouseButtonDown(0) && !Input.GetKeyDown(KeyCode.Space))
        {
            yield return null; // 每帧检查一次输入
        }
        Debug.Log("收到输入，继续...");
        yield return null; // 再等待一帧确保输入处理完成
    }

    // 在 Inspector 值改变时调用 (用于限制范围)
    void OnValidate()
    {
        // 限制 Inspector 中的值在合理范围内
        if (voronoiSiteSpacing < 1) voronoiSiteSpacing = 1;
        if (mapWidth < 1) mapWidth = 1;
        if (mapHeight < 1) mapHeight = 1;
        if (yieldBatchSize < 10) yieldBatchSize = 10;
        if (objectsPerFrame < 1) objectsPerFrame = 1;
        voronoiSiteJitter = Mathf.Clamp(voronoiSiteJitter, 0, voronoiSiteSpacing / 2);
    }

    // 验证必要的预设体是否设置正确
    void ValidatePrefabs()
    {
        bool essentialRoadsOk = true;
        if (roadPrefab == null) { Debug.LogError("Road Prefab (道路预设体) 缺失！"); essentialRoadsOk = false; }
        if (highlightedRoadPrefab == null) { Debug.LogError("Highlighted Road Prefab (高亮道路预设体) 缺失！"); essentialRoadsOk = false; }

        // 检查 Collider 和 Renderer 是否存在
        Action<GameObject, string, bool> checkComponents = (prefab, name, isCritical) => {
            if (prefab != null)
            {
                if (prefab.GetComponent<Collider>() == null) {
                     Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Collider 组件！", prefab);
                     if (isCritical) essentialRoadsOk = false;
                }
                if (prefab.GetComponentInChildren<Renderer>() == null) { // GetComponentInChildren 检查自身和子对象
                     Debug.LogError($"预设体 '{name}' ({prefab.name}) 必须有一个 Renderer (例如 MeshRenderer) 组件！", prefab);
                     if (isCritical) essentialRoadsOk = false;
                }
            }
        };
        checkComponents(roadPrefab, "Road Prefab", true);
        checkComponents(highlightedRoadPrefab, "Highlighted Road Prefab", true); // 高亮版本也需要这些

        // 检查建筑预设体（可选）
        bool essentialBuildingsOk = buildingPrefabs1x1.Any(p => p != null) ||
                                     buildingPrefabs2x2.Any(p => p != null) ||
                                     buildingPrefabs3x3.Any(p => p != null);
        if (!essentialBuildingsOk) Debug.LogWarning("所有建筑预设体列表都为空或无效。将只生成道路。");

        prefabsValidated = essentialRoadsOk; // 建筑是可选的，只要道路预设体 OK 就行
    }

    #endregion

    #region Road Generation Phase (道路生成阶段)
    // --- 包含生成道路网络的所有子方法 ---
    // --- 注意：这里的寻路使用内部固定成本的 A*，与用户交互的寻路不同 ---
    IEnumerator GenerateRoadsPhase()
    {
         Debug.Log("GenerateRoadsPhase: 开始。");
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        roadTiles.Clear(); voronoiSites.Clear(); noisePoints.Clear();

        // 1. 生成基础点
        yield return StartCoroutine(GenerateVoronoiSites());
        yield return StartCoroutine(SelectNoisePoints());

        // 2. 生成 Voronoi 边界道路
        if (voronoiSites.Count >= 2)
            yield return StartCoroutine(ComputeVoronoiEdgesAndMarkRoads());

        // 3. 连接噪声点到现有网络 (如果存在)
        if (noisePoints.Count > 0 && (roadTiles.Count > 0 || voronoiSites.Count > 0))
            yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());

        // 4. 确保连接到地图边缘 (如果启用)
        if (ensureEdgeConnections && (roadTiles.Count > 0 || voronoiSites.Count > 0))
            yield return StartCoroutine(EnsureMapEdgeConnections());

        // 5. 确保所有道路组件都连通
        yield return StartCoroutine(EnsureRoadConnectivity());
         Debug.Log($"GenerateRoadsPhase: 道路连通性检查完毕。逻辑道路瓦片数量: {roadTiles.Count}");
        if(roadTiles.Count == 0) Debug.LogWarning("GenerateRoadsPhase: 没有生成任何逻辑道路瓦片！");

        // 6. 实例化道路的视觉对象
         Debug.Log("GenerateRoadsPhase: 开始 InstantiateRoadVisuals。");
        yield return StartCoroutine(InstantiateRoadVisuals());
         Debug.Log("GenerateRoadsPhase: 完成 InstantiateRoadVisuals。");

        // 7. 激活所有排队的道路对象 (立即处理)
         Debug.Log("GenerateRoadsPhase: 开始立即实例化对象。");
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null)
        {
             Debug.Log("GenerateRoadsPhase: 等待实例化协程 (如果仍在运行)。");
             yield return instantiationCoroutine;
        }
         Debug.Log("GenerateRoadsPhase: 完成立即实例化对象。");

        timer.Stop();
        Debug.Log($"--- 道路生成阶段完成 ({roadTiles.Count} 个道路瓦片, {timer.ElapsedMilliseconds}ms) ---");
    }

    // --- 道路生成步骤 1: Voronoi & 噪声点 ---
    /// <summary>
    /// 在地图上生成 Voronoi 站点，用于后续生成主干道。
    /// </summary>
    IEnumerator GenerateVoronoiSites()
    {
        voronoiSites.Clear();
        int spacing = Mathf.Max(1, voronoiSiteSpacing);
        int jitter = Mathf.Clamp(voronoiSiteJitter, 0, spacing / 2);
        int count = 0; // 用于异步让步计数

        // 在网格上以一定间距生成站点，并加入随机抖动
        for (int x = spacing / 2; x < mapWidth; x += spacing)
        {
            for (int y = spacing / 2; y < mapHeight; y += spacing)
            {
                int jX = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                int jY = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                Vector2Int p = new Vector2Int(
                    Mathf.Clamp(x + jX, 0, mapWidth - 1),
                    Mathf.Clamp(y + jY, 0, mapHeight - 1)
                );

                // 避免站点过于接近 (至少间隔1格)
                bool tooClose = voronoiSites.Any(site => Mathf.Abs(site.x - p.x) <= 1 && Mathf.Abs(site.y - p.y) <= 1);

                // 确保点在地图内，且该位置为空，并且未被占用
                if (!tooClose && IsInMap(p) && map[p.x, p.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(p))
                {
                    voronoiSites.Add(p);
                }

                // 异步处理：达到一定数量后让出控制权
                count++;
                if (asyncGeneration && count % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }

        // 如果生成的站点太少 (少于4个)，并且地图足够大，则尝试在角落添加一些点
        if (voronoiSites.Count < 4 && mapWidth > 10 && mapHeight > 10)
        {
            List<Vector2Int> corners = new List<Vector2Int>
            {
                new Vector2Int(Mathf.Clamp(spacing/2, 1, mapWidth - 2), Mathf.Clamp(spacing/2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(mapWidth-1-spacing/2, 1, mapWidth - 2), Mathf.Clamp(spacing/2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(spacing/2, 1, mapWidth - 2), Mathf.Clamp(mapHeight-1-spacing/2, 1, mapHeight - 2)),
                new Vector2Int(Mathf.Clamp(mapWidth-1-spacing/2, 1, mapWidth - 2), Mathf.Clamp(mapHeight-1-spacing/2, 1, mapHeight - 2))
            };
            corners = corners.Where(IsInMap).Distinct().ToList(); // 过滤无效和重复的点

            foreach (var corner in corners)
            {
                // 检查新加的点是否离现有站点太近，以及位置是否有效
                if (!voronoiSites.Any(site => (site - corner).sqrMagnitude < spacing * spacing * 0.1f) &&
                    IsInMap(corner) && map[corner.x, corner.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(corner))
                {
                    voronoiSites.Add(corner);
                }
            }
            voronoiSites = voronoiSites.Distinct().ToList(); // 确保最终列表无重复
        }
        Debug.Log($"生成了 {voronoiSites.Count} 个 Voronoi 站点。");
    }

    /// <summary>
    /// 使用 Perlin 噪声在地图上选择一些点，用于生成分支道路。
    /// </summary>
    IEnumerator SelectNoisePoints()
    {
        noisePoints.Clear();
        float noiseOffsetX = noiseOffset.x;
        float noiseOffsetY = noiseOffset.y;
        int checkedCount = 0; // 用于异步让步计数

        // 遍历地图上的每个瓦片
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                // 检查位置是否有效且为空
                if (IsInMap(currentPos) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos))
                {
                    // 计算归一化坐标和噪声坐标
                    float normX = (float)x / mapWidth;
                    float normY = (float)y / mapHeight;
                    float nX = noiseOffsetX + normX * branchNoiseScale;
                    float nY = noiseOffsetY + normY * branchNoiseScale;
                    float noiseValue = Mathf.PerlinNoise(nX, nY); // 获取 Perlin 噪声值

                    // 如果噪声值超过阈值，则将此点选为噪声点
                    if (noiseValue > noiseBranchThreshold)
                    {
                        // 再次确认该点仍为空且未被占用 (以防万一)
                        if (map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos))
                        {
                            noisePoints.Add(currentPos);
                        }
                    }
                }
                // 异步处理
                checkedCount++;
                if (asyncGeneration && checkedCount % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }
        Debug.Log($"选择了 {noisePoints.Count} 个噪声点。");
    }


    // --- 道路生成步骤 2: 初始逻辑道路网络 ---
    /// <summary>
    /// 计算 Voronoi 图的边界，并将这些边界标记为逻辑道路。
    /// Voronoi 边界是距离两个或多个最近站点等距的点集。
    /// </summary>
    IEnumerator ComputeVoronoiEdgesAndMarkRoads()
    {
        if (voronoiSites.Count < 2) // 至少需要两个站点才能形成边界
        {
            Debug.LogWarning("Voronoi 站点少于 2 个，无法计算 Voronoi 边界。");
            yield break;
        }

        int processed = 0; // 用于异步让步计数
        int marked = 0;    // 标记为道路的瓦片计数

        // 遍历地图上的每个瓦片
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);

                // 如果已经是道路或被占用，则跳过
                if (roadTiles.Contains(currentPos) || occupiedTiles.ContainsKey(currentPos))
                {
                    processed++;
                    if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
                    continue;
                }

                // 找到离当前点最近的 Voronoi 站点
                int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites);
                if (nearestSiteIndex < 0) continue; // 如果找不到最近站点 (理论上不应发生)，则跳过

                // 检查邻居瓦片
                foreach (var dir in directions)
                {
                    Vector2Int neighborPos = currentPos + dir;
                    if (!IsInMap(neighborPos)) continue; // 跳过地图外的邻居

                    // 找到离邻居点最近的 Voronoi 站点
                    int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites);

                    // 如果邻居点有效，并且它最近的站点与当前点最近的站点不同，
                    // 则当前点位于 Voronoi 边界上。
                    if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex)
                    {
                        bool claimed;
                        // 尝试将此瓦片在逻辑上声明为道路 (不立即实例化视觉对象)
                        TryClaimTileForRoadLogicOnly(currentPos, out claimed);
                        if (claimed)
                        {
                            marked++; // 增加标记计数
                        }
                        break; // 找到一个不同邻居就足够确定是边界点，跳出邻居循环
                    }
                }
                // 异步处理
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0)
                {
                    yield return null;
                }
            }
        }
        Debug.Log($"标记了 {marked} 个 Voronoi 道路瓦片。");
    }

    /// <summary>
    /// 查找给定点在站点列表中最近的站点的索引。
    /// </summary>
    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites)
    {
        if (sites == null || sites.Count == 0) return -1; // 无站点可找

        int nearestIndex = -1;
        float minDistSq = float.MaxValue; // 使用距离平方避免开方运算

        for (int i = 0; i < sites.Count; i++)
        {
            float distSq = (sites[i] - point).sqrMagnitude; // 计算距离平方
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                nearestIndex = i;
            }
        }
        return nearestIndex;
    }

    /// <summary>
    /// 查找给定点在站点列表中最近的站点的坐标。
    /// </summary>
    Vector2Int? FindNearestSite(Vector2Int point, List<Vector2Int> sites)
    {
        int index = FindNearestSiteIndex(point, sites);
        return index >= 0 ? sites[index] : (Vector2Int?)null; // 如果找到索引，返回对应坐标，否则返回 null
    }

    /// <summary>
    /// 将噪声点连接到最近的现有道路网络锚点 (Voronoi 站点或已生成的道路)。
    /// 使用内部固定成本 A* 查找路径并声明路径上的瓦片。
    /// </summary>
    IEnumerator ConnectNoiseAndRoadsWithMST()
    {
        if (noisePoints.Count == 0) yield break; // 没有噪声点需要连接

        // 定义锚点集合：包括 Voronoi 站点和所有已标记的道路瓦片
        HashSet<Vector2Int> anchorNodes = new HashSet<Vector2Int>(roadTiles);
        anchorNodes.UnionWith(voronoiSites.Where(site => IsInMap(site))); // 添加在地图内的 Voronoi 站点

        if (anchorNodes.Count == 0)
        {
            Debug.LogWarning("无法连接噪声点 - 未找到锚点 (没有 Voronoi 站点或初始道路)。");
            yield break;
        }

        int pathsDrawn = 0; // 处理的噪声点计数
        int totalClaimedOnPaths = 0; // 通过连接噪声点添加的道路瓦片总数

        // 遍历每个噪声点
        foreach (var noiseStart in noisePoints)
        {
            // 跳过无效或已被占用的噪声点
            if (!IsInMap(noiseStart) || roadTiles.Contains(noiseStart) || occupiedTiles.ContainsKey(noiseStart) || map[noiseStart.x, noiseStart.y] != ZoneType.Empty)
            {
                continue;
            }

            // 找到离此噪声点最近的锚点
            Vector2Int? nearestAnchor = FindNearestPointInSet(noiseStart, anchorNodes);

            if (nearestAnchor.HasValue)
            {
                // 使用内部固定成本 A* 查找从噪声点到最近锚点的路径
                List<Vector2Int> path = FindPath_InternalFixedCost(noiseStart, new HashSet<Vector2Int> { nearestAnchor.Value });

                int claimedOnThisPath = 0; // 此路径上成功声明的瓦片数
                if (path != null && path.Count > 0)
                {
                    // 遍历路径上的每个点
                    foreach (var roadPos in path)
                    {
                        if (!IsInMap(roadPos)) continue; // 跳过地图外的点

                        bool claimed = false;
                        // 尝试将此瓦片声明为道路 (这会处理拆除和协程等待)
                        yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                        if (claimed)
                        {
                            claimedOnThisPath++;
                            // 将新声明的道路瓦片也添加到锚点集合中，以便后续噪声点可以连接到它
                            anchorNodes.Add(roadPos);
                        }
                    }
                    totalClaimedOnPaths += claimedOnThisPath; // 累加总数
                }
                else
                {
                    Debug.LogWarning($"内部 A* 未能连接噪声点 {noiseStart} 到锚点 {nearestAnchor.Value}。");
                }
            }
            else
            {
                Debug.LogWarning($"噪声点 {noiseStart} 未找到最近的锚点。");
            }

            // 异步处理
            pathsDrawn++;
            if (asyncGeneration && pathsDrawn % 20 == 0) // 每处理 20 个点让步一次
            {
                yield return null;
            }
        }
        Debug.Log($"尝试连接 {pathsDrawn} 个噪声点，总共添加了 {totalClaimedOnPaths} 个道路瓦片。");
    }


    /// <summary>
    /// 在目标点列表 (List) 中查找距离起点最近的点。
    /// </summary>
    Vector2Int? FindNearestPointInList(Vector2Int startPoint, List<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) return null; // 列表为空

        Vector2Int? nearest = null;
        float minDistanceSq = float.MaxValue; // 使用距离平方

        foreach (Vector2Int target in targetPoints)
        {
            float distSq = (startPoint - target).sqrMagnitude;
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                nearest = target;
                // 优化：如果距离已经非常近 (例如，是邻居)，可以直接返回
                if (minDistanceSq <= 1.1f) break;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 在目标点集合 (HashSet) 中查找距离起点最近的点。
    /// </summary>
    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) return null; // 集合为空

        Vector2Int? nearest = null;
        float minDistanceSq = float.MaxValue; // 使用距离平方

        foreach (Vector2Int target in targetPoints)
        {
            float distSq = (startPoint - target).sqrMagnitude;
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                nearest = target;
                // 优化：如果距离已经非常近
                if (minDistanceSq <= 1.1f) break;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 确保道路网络连接到地图的边缘（如果选项启用）。
    /// 选择一些靠近边缘的预设点，并将它们连接到最近的现有道路网络。
    /// </summary>
    IEnumerator EnsureMapEdgeConnections()
    {
        // 定义一些靠近边缘的候选连接点
        List<Vector2Int> edgeAnchors = new List<Vector2Int> {
            new Vector2Int(mapWidth / 2, mapHeight - 2), // 上边缘中间
            new Vector2Int(mapWidth / 2, 1),             // 下边缘中间
            new Vector2Int(1, mapHeight / 2),             // 左边缘中间
            new Vector2Int(mapWidth - 2, mapHeight / 2), // 右边缘中间
            new Vector2Int(2, 2),                          // 左下角附近
            new Vector2Int(mapWidth - 3, 2),               // 右下角附近
            new Vector2Int(2, mapHeight - 3),               // 左上角附近
            new Vector2Int(mapWidth - 3, mapHeight - 3)    // 右上角附近
        };
        // 过滤掉不在地图内或太靠近边界的点 (距离边界至少1格)
        edgeAnchors = edgeAnchors.Where(p => IsInMap(p) && p.x > 0 && p.x < mapWidth - 1 && p.y > 0 && p.y < mapHeight - 1)
                                .Distinct().ToList();

        int connectionsAttempted = 0; // 尝试连接的次数
        int connectionsMade = 0;      // 成功连接的次数

        // 获取当前的道路网络点 (包括站点和道路瓦片)
        HashSet<Vector2Int> currentNetworkPoints = new HashSet<Vector2Int>(roadTiles);
        currentNetworkPoints.UnionWith(voronoiSites.Where(site => IsInMap(site)));

        if (currentNetworkPoints.Count == 0)
        {
            Debug.LogWarning("跳过边缘连接：道路网络为空。");
            yield break;
        }

        // 遍历每个边缘锚点
        foreach (var edgePoint in edgeAnchors)
        {
            connectionsAttempted++;
            // 如果该点已经是网络的一部分，则跳过
            if (currentNetworkPoints.Contains(edgePoint)) continue;

            // 找到离此边缘点最近的现有网络点
            Vector2Int? connectFrom = FindNearestPointInSet(edgePoint, currentNetworkPoints);

            if (connectFrom.HasValue)
            {
                // 尝试连接找到的网络点和边缘点
                bool success = false;
                // ConnectTwoPoints 会使用内部 A* 寻找路径并尝试声明瓦片
                yield return StartCoroutine(ConnectTwoPoints(connectFrom.Value, edgePoint, result => success = result));
                if (success) connectionsMade++;
            }
            else
            {
                Debug.LogWarning($"未能找到用于连接边缘点 {edgePoint} 的网络点。");
            }
            // 异步处理
            if (asyncGeneration && connectionsAttempted % 2 == 0) yield return null;
        }
        Debug.Log($"尝试了 {connectionsAttempted} 次边缘连接，成功 {connectionsMade} 次。");
    }

    /// <summary>
    /// 使用内部 A* 查找两个点之间的路径，并尝试声明路径上的瓦片为道路。
    /// </summary>
    /// <param name="start">起始点</param>
    /// <param name="end">目标点</param>
    /// <param name="callback">完成后调用的回调，参数为是否成功连接 (至少部分路径被声明或目标已是道路)</param>
    IEnumerator ConnectTwoPoints(Vector2Int start, Vector2Int end, Action<bool> callback)
    {
        // 使用内部固定成本 A* 查找路径
        List<Vector2Int> path = FindPath_InternalFixedCost(start, new HashSet<Vector2Int> { end });

        int appliedCount = 0; // 成功声明的瓦片数
        bool success = false;
        bool pathBlockedMidway = false; // 路径是否在中间被阻塞

        if (path != null && path.Count > 1) // 路径有效且包含多个点
        {
            for (int i = 0; i < path.Count; ++i)
            {
                Vector2Int roadPos = path[i];
                if (!IsInMap(roadPos)) continue; // 跳过地图外的点

                bool claimed = false;
                if (!roadTiles.Contains(roadPos)) // 如果它还不是逻辑道路
                {
                    // 尝试声明它 (可能涉及拆除)
                    yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                }
                else // 如果它已经是逻辑道路
                {
                    claimed = true;
                }

                if (claimed)
                {
                    appliedCount++; // 增加成功计数
                }
                else
                {
                    // 如果路径中间的点无法声明 (不是起点或终点)，则标记为阻塞
                    if (roadPos != start && roadPos != end)
                    {
                        Debug.LogWarning($"路径段 {roadPos} (在 {start} 到 {end} 的路径上) 无法声明。");
                        pathBlockedMidway = true;
                    }
                }
                // 异步处理
                if (asyncGeneration && i > 0 && i % 100 == 0) yield return null;
            }
            // 如果至少声明了一个瓦片，或者终点本身已经是道路的一部分，并且路径未在中间阻塞，则认为成功
            success = (appliedCount > 0 || roadTiles.Contains(end)) && !pathBlockedMidway;
        }
        else if (path != null && path.Count == 1 && path[0] == end && roadTiles.Contains(end))
        {
            // 路径只包含终点，且终点已是道路 (说明起点和终点是邻居且终点是路)
            success = true;
        }
        else if (path == null)
        {
            Debug.LogWarning($"内部 A* 路径在 {start} 和 {end} 之间失败。");
            success = false;
        }

        // 异步处理
        if (asyncGeneration && appliedCount > 0) yield return null;
        callback?.Invoke(success); // 调用回调函数并传递结果
    }

    // --- 道路生成步骤 3: 道路网络优化 (连通性) ---
    /// <summary>
    /// 确保所有生成的道路瓦片形成一个单一的连通组件。
    /// 如果存在多个组件，则尝试将较小的组件连接到最大的组件上。
    /// </summary>
    IEnumerator EnsureRoadConnectivity()
    {
        // 查找当前所有独立的道路组件 (使用 BFS 或 DFS)
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();

        // 如果只有一个或零个组件，则无需连接，网络已连通
        if (roadComponents.Count <= 1)
        {
             Debug.Log($"道路网络连通性检查：找到 {roadComponents.Count} 个组件。无需连接。");
            yield break; // 结束协程
        }

        Debug.Log($"发现 {roadComponents.Count} 个道路组件，尝试连接...");
        // 按组件大小降序排序，最大的作为主网络
        roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList();
        HashSet<Vector2Int> mainNetwork = roadComponents[0]; // 第一个 (最大的) 是主网络
        Debug.Log($"主网络组件大小: {mainNetwork.Count}");

        int connectionsMade = 0; // 成功连接次数
        int componentsMerged = 1; // 已合并的组件数 (从主网络开始计数)

        // 遍历其余的组件 (从索引 1 开始)
        for (int i = 1; i < roadComponents.Count; i++)
        {
            HashSet<Vector2Int> currentComponent = roadComponents[i];
            if (currentComponent.Count == 0) continue; // 跳过空组件 (理论上不应出现)

            Debug.Log($"尝试连接组件 {i + 1} (大小: {currentComponent.Count}) 到主网络...");
            bool connected = false;
            // 尝试将当前组件连接到主网络
            // ConnectComponentToNetwork 会查找最近点并调用 ConnectTwoPoints
            yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork, result => connected = result));

            if (connected)
            {
                connectionsMade++;
                componentsMerged++;
                 Debug.Log($"组件 {i + 1} 成功连接并合并到主网络。");
                // 将成功连接的组件的瓦片合并到主网络集合中 (逻辑上确认合并)
                // 注意：ConnectTwoPoints 内部的 TryClaimTileForRoad 已经更新了 roadTiles 集合，
                // 所以这里的 UnionWith 主要是为了在后续迭代中让 mainNetwork 包含所有已连接的点。
                mainNetwork.UnionWith(currentComponent);
            }
            else
            {
                Debug.LogWarning($"未能将组件 {i + 1} (大小: {currentComponent.Count}) 连接到主网络。");
            }
            // 异步处理：每处理几个组件让步一次
            if (asyncGeneration && i % 5 == 0) yield return null;
        }
        Debug.Log($"连通性检查完成。合并了 {componentsMerged}/{roadComponents.Count} 个组件 (尝试了 {connectionsMade} 次有效连接)。");
    }

    /// <summary>
    /// 使用广度优先搜索 (BFS) 查找当前 roadTiles 集合中的所有连通组件。
    /// 连通是基于四方向邻接。
    /// </summary>
    /// <returns>一个列表，其中每个元素是一个代表连通组件的 HashSet<Vector2Int>。</returns>
    List<HashSet<Vector2Int>> FindAllRoadComponents()
    {
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>(); // 存储找到的组件
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>(); // 跟踪已访问的瓦片，防止重复处理
        // 在 roadTiles 的快照上操作，防止在迭代过程中 roadTiles 被修改导致错误
        HashSet<Vector2Int> currentRoadTilesSnapshot = new HashSet<Vector2Int>(roadTiles);

        // 遍历所有逻辑道路瓦片作为潜在的起点
        foreach (Vector2Int startPos in currentRoadTilesSnapshot)
        {
            // 如果该瓦片尚未被访问过，并且确实是当前逻辑上的道路瓦片
            if (!visited.Contains(startPos) && roadTiles.Contains(startPos)) // 双重检查 roadTiles，以防快照期间被删除
            {
                // 发现了一个新组件的起点，开始 BFS
                HashSet<Vector2Int> newComponent = new HashSet<Vector2Int>(); // 存储当前组件的瓦片
                Queue<Vector2Int> queue = new Queue<Vector2Int>(); // BFS 使用的队列

                // 初始化 BFS
                queue.Enqueue(startPos);
                visited.Add(startPos);
                newComponent.Add(startPos);

                // 执行 BFS 直到队列为空
                while (queue.Count > 0)
                {
                    Vector2Int node = queue.Dequeue(); // 取出队首节点

                    // 检查所有四个方向的邻居
                    foreach (var dir in directions)
                    {
                        Vector2Int neighbor = node + dir;
                        // 检查邻居是否有效：在地图内、是道路瓦片、且尚未被访问过
                        if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);       // 标记邻居为已访问
                            newComponent.Add(neighbor);   // 将邻居加入当前组件
                            queue.Enqueue(neighbor);     // 将邻居加入队列以便继续搜索
                        }
                    }
                }

                // BFS 完成后，如果找到的组件非空，则将其添加到结果列表
                if (newComponent.Count > 0)
                {
                    components.Add(newComponent);
                }
            }
        }
        // 返回所有找到的连通组件的列表
        return components;
    }


    /// <summary>
    /// 尝试将一个道路组件 (componentToConnect) 连接到目标网络 (targetNetwork)。
    /// 它会找到两个集合之间最近的点对，并尝试使用 ConnectTwoPoints 连接它们。
    /// </summary>
    /// <param name="componentToConnect">要连接的组件 (瓦片集合)</param>
    /// <param name="targetNetwork">目标网络 (瓦片集合)</param>
    /// <param name="callback">完成后调用的回调，参数为是否成功连接</param>
    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork, Action<bool> callback)
    {
        // 基本有效性检查
        if (componentToConnect == null || targetNetwork == null || componentToConnect.Count == 0 || targetNetwork.Count == 0)
        {
            Debug.LogWarning("ConnectComponentToNetwork: 输入的组件或目标网络为空。");
            callback?.Invoke(false); // 调用回调，传递失败
            yield break; // 结束协程
        }

        Vector2Int? bestStart = null; // 找到的最佳起点 (来自 componentToConnect)
        Vector2Int? bestTarget = null; // 找到的最佳目标点 (来自 targetNetwork)
        float minDistanceSq = float.MaxValue; // 记录找到的最小距离平方，初始化为最大值

        // 优化策略：从点数较少的集合开始搜索，以减少外层循环次数
        HashSet<Vector2Int> searchSet = (componentToConnect.Count < targetNetwork.Count) ? componentToConnect : targetNetwork;
        HashSet<Vector2Int> destinationSet = (searchSet == componentToConnect) ? targetNetwork : componentToConnect;

        // 性能优化：限制搜索点的数量，特别是对于非常大的组件，避免 O(N*M) 的完全搜索
        // 设定一个基础搜索点数，并根据组件大小增加一些点数
        int maxSearchPoints = 300 + (int)Mathf.Sqrt(searchSet.Count);
        // 如果搜索集合小于限制，则搜索所有点；否则，随机选取一部分点进行搜索
        var pointsToSearchFrom = searchSet.Count <= maxSearchPoints ?
                                 searchSet :
                                 searchSet.OrderBy(p => UnityEngine.Random.value).Take(maxSearchPoints); // 随机取样

        int searchedCount = 0; // 搜索计数器，用于异步让步

        // 遍历选定的搜索点
        foreach (var startCandidate in pointsToSearchFrom)
        {
            // 在目标集合中查找离当前候选点最近的点
            Vector2Int? currentNearestTarget = FindNearestPointInSet(startCandidate, destinationSet);
            if (currentNearestTarget.HasValue)
            {
                // 计算距离平方
                float distSq = (startCandidate - currentNearestTarget.Value).sqrMagnitude;
                // 如果这个距离比当前记录的最小距离还要小
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq; // 更新最小距离
                    // 根据哪个集合是搜索集，正确分配起点和目标点
                    bestStart = (searchSet == componentToConnect) ? startCandidate : currentNearestTarget.Value;
                    bestTarget = (searchSet == componentToConnect) ? currentNearestTarget.Value : startCandidate;
                }
            }

            searchedCount++;
            // 早期退出优化：如果找到非常近的点对 (例如，已经是邻居)，可以认为已连接或连接成本极低，提前结束搜索
            if (minDistanceSq <= 2.0f) break;
            // 异步处理：每搜索一定数量的点就让步
            if (asyncGeneration && searchedCount % 50 == 0) yield return null;
        }

        // 检查是否成功找到了连接点对
        if (!bestStart.HasValue || !bestTarget.HasValue)
        {
            // 如果遍历完所有候选点后仍然没有找到最佳点对 (理论上不应发生，除非集合为空)
            Debug.LogError($"无法在组件之间找到连接点。Component size: {componentToConnect.Count}, Target size: {targetNetwork.Count}");
            callback?.Invoke(false); // 回调失败
            yield break; // 结束协程
        }

        // 如果找到的最佳点对本身已经非常接近 (例如是邻居)，则认为它们已经连接或无需连接
        if (minDistanceSq <= 2.0f)
        {
             Debug.Log($"组件似乎已通过点 {bestStart.Value} 和 {bestTarget.Value} 连接 (距离平方 <= 2)，无需额外连接。");
            callback?.Invoke(true); // 回调成功
            yield break; // 结束协程
        }

        // 如果找到的点对距离较远，则尝试使用 ConnectTwoPoints 来实际连接它们
        bool connected = false;
         Debug.Log($"尝试连接组件：从 {bestStart.Value} (来自 component) 到 {bestTarget.Value} (来自 target) (距离平方: {minDistanceSq:F1})");
        // ConnectTwoPoints 会处理 A* 寻路和瓦片声明
        yield return StartCoroutine(ConnectTwoPoints(bestStart.Value, bestTarget.Value, result => connected = result));
        callback?.Invoke(connected); // 将 ConnectTwoPoints 的结果传递给回调
    }

    // --- 道路生成步骤 4: 实例化道路视觉对象 ---
    IEnumerator InstantiateRoadVisuals()
    {
        int roadsProcessed = 0; // 成功触发 PlaceObject 的次数
        int placeAttempts = 0;  // 尝试检查的次数
        List<Vector2Int> currentRoadTilesSnapshot = new List<Vector2Int>(roadTiles); // 在副本上操作，避免迭代时修改集合
        Debug.Log($"InstantiateRoadVisuals: 基于 roadTiles 集合，处理 {currentRoadTilesSnapshot.Count} 个潜在的道路瓦片。");

        if (roadPrefab == null) {
            Debug.LogError("Road Prefab 未分配！无法实例化视觉对象。");
            yield break; // 无法继续
        }

        foreach (Vector2Int pos in currentRoadTilesSnapshot)
        {
            placeAttempts++; // 记录尝试次数

            // 基础检查：它是否仍然在逻辑道路集合中？
            if (!roadTiles.Contains(pos))
            {
                // Debug.Log($"InstantiateRoadVisuals: 跳过 {pos} 因为它不再 roadTiles 集合中。"); // 可选日志
                continue;
            }

            // 详细检查：地图网格是否一致？该瓦片是否真正可用？
            // 可用条件:
            // 1. 地图网格标记为 Road 类型。
            // 2. occupiedTiles 中完全没有这个位置的记录，或者有记录但记录的 Value (对象原点) 就是它自己 (说明是一个 1x1 对象的原点)。
            // 3. trackedObjects 中没有以这个位置为原点的记录 (防止重复创建记录和对象)。
            bool isMarkedAsRoad = map[pos.x, pos.y] == ZoneType.Road;
            bool isAvailable = !occupiedTiles.ContainsKey(pos) || occupiedTiles[pos] == pos;
            bool alreadyHasTrackedObjectOrigin = trackedObjects.ContainsKey(pos); // 检查是否已有以此为原点的对象

            // Debug.Log($"InstantiateRoadVisuals: 检查 {pos}: IsMarkedRoad={isMarkedAsRoad}, IsAvailable={isAvailable}, AlreadyTrackedOrigin={alreadyHasTrackedObjectOrigin}"); // 详细日志

            // 仅在以下情况下放置道路预设体：
            // - 地图网格标记为 Road (一致性)
            // - 并且瓦片当前可用 (未被其他对象占用)
            // - 并且当前没有以此位置为原点的已跟踪对象 (防止重复)
            if (isMarkedAsRoad && isAvailable && !alreadyHasTrackedObjectOrigin)
            {
                 Debug.Log($"InstantiateRoadVisuals: >>> 尝试为道路在 {pos} 调用 PlaceObject");
                 PlaceObject(pos, Vector2Int.one, roadPrefab, ZoneType.Road, Quaternion.identity); // PlaceObject 会处理添加到 trackedObjects 和 occupiedTiles
                 roadsProcessed++; // 记录成功触发放置
            }
            // --- 以下是跳过放置时的警告/信息日志 ---
            else if (!isMarkedAsRoad && roadTiles.Contains(pos))
            {
                 Debug.LogWarning($"InstantiateRoadVisuals: 瓦片 {pos} 在 roadTiles 集合中, 但地图网格类型为 '{map[pos.x, pos.y]}'. 跳过放置。");
            }
            else if (!isAvailable)
            {
                 Vector2Int occupierOrigin = occupiedTiles.ContainsKey(pos) ? occupiedTiles[pos] : pos; // 找出占用者
                 Debug.LogWarning($"InstantiateRoadVisuals: 瓦片 {pos} 被位于 {occupierOrigin} 的对象占用, 无法在此处放置道路视觉对象。");
            }
             else if (alreadyHasTrackedObjectOrigin)
            {
                 // 理论上，如果 isAvailable 为 true (即 occupiedTiles[pos] == pos)，这里不应该触发，因为 PlaceObject 会先检查 occupiedTiles。
                 // 但作为双重保险和调试信息。
                 Debug.LogWarning($"InstantiateRoadVisuals: 瓦片 {pos} 已经有一个以此为原点的已跟踪对象 ('{trackedObjects[pos].instance?.name}'). 跳过重复放置。");
            }

            // 异步处理：如果处理了一定数量，让出控制权
            if (asyncGeneration && placeAttempts > 0 && placeAttempts % yieldBatchSize == 0)
                yield return null;
        }
        Debug.Log($"InstantiateRoadVisuals: 循环结束。在 {placeAttempts} 次尝试中为 {currentRoadTilesSnapshot.Count} 个候选位置触发了 {roadsProcessed} 次 PlaceObject。");
    }
    #endregion // 结束道路生成阶段

    #region Building Placement Phase (建筑放置阶段)
    // (保持之前的代码不变, 但增加了日志)
    IEnumerator FillSpecificSizeGreedy(int sizeToFill) { Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 开始。"); int buildingsPlaced = 0; int tilesChecked = 0; System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew(); Vector2Int buildingSize = Vector2Int.one * sizeToFill; List<GameObject> prefabList; switch (sizeToFill) { case 3: prefabList = buildingPrefabs3x3; break; case 2: prefabList = buildingPrefabs2x2; break; case 1: prefabList = buildingPrefabs1x1; break; default: Debug.LogError($"无效的建筑尺寸请求: {sizeToFill}"); yield break; } if (prefabList == null || !prefabList.Any(p => p != null)) { Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 列表中没有找到有效的预设体。跳过此阶段。"); yield break; } Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 找到 {prefabList.Count(p => p != null)} 个有效预设体。"); for (int y = 0; y <= mapHeight - sizeToFill; y++) { for (int x = 0; x <= mapWidth - sizeToFill; x++) { tilesChecked++; Vector2Int currentOrigin = new Vector2Int(x, y); if (IsInMap(currentOrigin) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentOrigin)) { if (CanPlaceBuildingHere(currentOrigin, buildingSize)) { GameObject prefab = GetRandomValidPrefab(prefabList); if (prefab != null) { Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): >>> 在 {currentOrigin} 放置建筑 '{prefab.name}'。"); PlaceObject(currentOrigin, buildingSize, prefab, DetermineBuildingType(), Quaternion.identity); buildingsPlaced++; x += (sizeToFill - 1); } else { Debug.LogError($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): GetRandomValidPrefab 意外返回 null！");} } } if (asyncGeneration && tilesChecked % (yieldBatchSize * 2) == 0) yield return null; } if (asyncGeneration && y % 10 == 0) yield return null; } timer.Stop(); Debug.Log($"FillSpecificSizeGreedy ({sizeToFill}x{sizeToFill}): 完成。放置了 {buildingsPlaced} 个建筑。耗时: {timer.ElapsedMilliseconds} ms。"); yield return null; }
    bool CanPlaceBuildingHere(Vector2Int origin, Vector2Int size) { if (!IsInMap(origin) || !IsInMap(origin + size - Vector2Int.one)) return false; for (int x = origin.x; x < origin.x + size.x; x++) { for (int y = origin.y; y < origin.y + size.y; y++) { Vector2Int currentTile = new Vector2Int(x, y); if (!IsInMap(currentTile) || map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentTile)) return false; } } return true; }
    GameObject GetRandomValidPrefab(List<GameObject> list) { if (list == null) return null; List<GameObject> validPrefabs = list.Where(p => p != null).ToList(); if (validPrefabs.Count == 0) return null; return validPrefabs[UnityEngine.Random.Range(0, validPrefabs.Count)]; }
    ZoneType DetermineBuildingType() { int r = UnityEngine.Random.Range(0, 3); if (r == 0) return ZoneType.Residential; if (r == 1) return ZoneType.Commercial; return ZoneType.Industrial; }
    #endregion

    #region Core Logic: Placement, Removal, Claiming (核心逻辑：放置、移除、声明)

    // PlaceObject - 处理创建记录、实例化(非激活)、更新地图/占用、排队等待激活
    // ** 修改版，包含交通数据初始化和详细日志 **
    void PlaceObject(Vector2Int origin, Vector2Int size, GameObject prefab, ZoneType type, Quaternion rotation)
    {
         Debug.Log($"PlaceObject 开始: 类型={type}, 原点={origin}, 尺寸={size}, 预设体='{prefab?.name}'");
        if (prefab == null) { Debug.LogError($"PlaceObject 错误 @ {origin}: 预设体为 null。中止 PlaceObject。"); return; }
        Vector2Int extent = origin + size - Vector2Int.one;
        if (!IsInMap(origin) || !IsInMap(extent)) { Debug.LogError($"PlaceObject 错误: 占地区域 {origin} 尺寸 {size} 超出地图边界 [{mapWidth}x{mapHeight}]。中止 PlaceObject。"); return; }

        // 检查并移除目标瓦片上当前占用的任何东西
         Debug.Log($"PlaceObject ({origin}): 检查区域以进行清理...");
        for (int x = origin.x; x < origin.x + size.x; x++) {
            for (int y = origin.y; y < origin.y + size.y; y++) {
                Vector2Int tile = new Vector2Int(x, y);
                if (IsInMap(tile))
                {
                    bool isOccupied = occupiedTiles.ContainsKey(tile);
                    bool mapNotEmpty = map[x, y] != ZoneType.Empty;
                    if (isOccupied || mapNotEmpty) {
                         Debug.Log($"PlaceObject ({origin}): 在 {tile} 发现障碍物 (Occupied={isOccupied}, MapType={map[x, y]})。调用 RemoveTrackedObject...");
                        RemoveTrackedObject(tile); // 移除那里的东西
                    }
                }
            }
        }
         Debug.Log($"PlaceObject ({origin}): 清理检查完成。");

        // --- 计算实例化位置 ---
        Vector3 instantiationPosition;
        Vector3 targetCornerWorldPos = new Vector3(origin.x, 0, origin.y);
        Vector3 colliderOffsetFromPivotXZ = Vector3.zero;
        BoxCollider boxCol = prefab.GetComponent<BoxCollider>();
        if (boxCol != null) {
             Vector3 localBottomLeftOffset = boxCol.center - new Vector3(boxCol.size.x / 2.0f, 0, boxCol.size.z / 2.0f);
             colliderOffsetFromPivotXZ = new Vector3(localBottomLeftOffset.x, 0, localBottomLeftOffset.z);
        } else {
             Collider generalCol = prefab.GetComponent<Collider>();
             if (generalCol != null) { colliderOffsetFromPivotXZ = Vector3.zero; Debug.LogWarning($"预设体 {prefab.name} @ {origin}: 没有 BoxCollider。使用轴心点进行放置。", prefab); }
             else { colliderOffsetFromPivotXZ = Vector3.zero; Debug.LogError($"预设体 {prefab.name} @ {origin}: 找不到 Collider！放置可能不准确。", prefab); }
        }
        instantiationPosition = targetCornerWorldPos - colliderOffsetFromPivotXZ;
         Debug.Log($"PlaceObject ({origin}): 计算得到的实例化位置: {instantiationPosition}");


        // --- 实例化对象 (非激活状态) ---
        GameObject instance = null;
         try
         {
            instance = Instantiate(prefab, instantiationPosition, rotation, mapParent); // 实例化并设置父对象
         }
         catch (Exception e)
         {
             Debug.LogError($"PlaceObject ({origin}): 实例化预设体 '{prefab.name}' 失败！错误: {e.Message}\n{e.StackTrace}");
             return; // 如果实例化失败则停止
         }
         if (instance == null)
         {
             Debug.LogError($"PlaceObject ({origin}): Instantiate 返回了 NULL 对于预设体 '{prefab.name}'！中止 PlaceObject。");
             return; // 如果实例化返回 null 则停止
         }

        instance.name = $"{prefab.name}_{origin.x}_{origin.y}"; // 重要: 设置命名约定
        instance.SetActive(false); // 初始状态为非激活
         Debug.Log($"PlaceObject ({origin}): 实例化了 '{instance.name}' (非激活)。父对象: '{instance.transform.parent?.name}'。");


        // --- 创建记录 ---
        BuildingRecord record = new BuildingRecord(instance, size, type, rotation);
        trackedObjects[origin] = record; // 按原点跟踪
         Debug.Log($"PlaceObject ({origin}): 创建了 BuildingRecord 并添加到 trackedObjects。当前数量: {trackedObjects.Count}");

        // --- **为道路设置交通数据** ---
        if (type == ZoneType.Road && size == Vector2Int.one) {
            record.capacity = UnityEngine.Random.Range(5, 21); // v: 容量 (5-20)
            record.currentVehicles = UnityEngine.Random.Range(0, 31); // n: 当前车辆数 (0-30)
             Debug.Log($"PlaceObject ({origin}): 设置道路交通数据: 容量(v)={record.capacity}, 当前车辆(n)={record.currentVehicles}");
        }

        // --- 更新地图网格和占用瓦片映射 ---
        for (int x = origin.x; x < origin.x + size.x; x++) {
            for (int y = origin.y; y < origin.y + size.y; y++) {
                Vector2Int currentTile = new Vector2Int(x, y);
                if (IsInMap(currentTile)) {
                    map[x, y] = type;
                    occupiedTiles[currentTile] = origin; // 将此瓦片映射回对象的原点
                    if (type != ZoneType.Road) roadTiles.Remove(currentTile); // 确保非道路瓦片不在 roadTiles 集合中
                }
            }
        }
         Debug.Log($"PlaceObject ({origin}): 更新了地图网格和 occupiedTiles。Occupied 数量: {occupiedTiles.Count}");

        // --- 特殊处理 1x1 道路，将其原点添加到 roadTiles 集合 ---
        if (type == ZoneType.Road && size == Vector2Int.one) {
            roadTiles.Add(origin);
             Debug.Log($"PlaceObject ({origin}): 添加到 roadTiles 集合。当前数量: {roadTiles.Count}");
        }

        // --- 排队等待激活 ---
        generationQueue.Enqueue(instance);
         Debug.Log($"PlaceObject ({origin}): 将 '{instance.name}' 加入队列。队列大小: {generationQueue.Count}");
        StartObjectInstantiation(); // 触发激活协程 (如果尚未运行)
         Debug.Log($"PlaceObject 为 {origin} 执行完毕。");
    }

    // RemoveTrackedObject - 处理查找记录、销毁实例、清理地图/占用
    void RemoveTrackedObject(Vector2Int pos)
    {
        // 基本检查：坐标是否在地图内
        if (!IsInMap(pos))
        {
            // Debug.Log($"RemoveTrackedObject: Position {pos} is outside map bounds."); // 可选日志
            return;
        }

        Vector2Int originToRemove = pos; // 假设要移除的对象原点就是 pos (适用于 1x1 对象或直接指定原点的情况)
        bool foundInOccupied = false;

        // 优先检查 occupiedTiles，因为它可以找到占据该瓦片的多格对象的原点
        if (occupiedTiles.TryGetValue(pos, out Vector2Int foundOrigin))
        {
            originToRemove = foundOrigin; // 找到了占据此瓦片的对象原点
            foundInOccupied = true;
             // Debug.Log($"RemoveTrackedObject({pos}): Found object origin {originToRemove} via occupiedTiles."); // 可选日志
        }
        // 如果 occupiedTiles 中没有，再检查 trackedObjects 中是否直接以 pos 为原点 (处理 1x1 对象或特殊情况)
        else if (!trackedObjects.ContainsKey(pos))
        {
            // 如果两个字典都没有找到与 pos 相关的信息，那么此位置可能没有需要移除的“跟踪对象”
            // 只需要确保地图逻辑网格被清理即可
            if (map != null && map[pos.x, pos.y] != ZoneType.Empty)
            {
                 Debug.LogWarning($"RemoveTrackedObject({pos}): Tile not in occupiedTiles or trackedObjects origin, but map grid is not Empty ({map[pos.x, pos.y]}). Clearing map grid."); // 警告日志
                map[pos.x, pos.y] = ZoneType.Empty; // 清理地图类型
            }
            roadTiles.Remove(pos); // 无论如何，确保它不在道路集合中
            // Debug.Log($"RemoveTrackedObject({pos}): No tracked object found via occupiedTiles or as origin."); // 可选日志
            return; // 没有找到可追踪的对象记录，结束
        }
        // 如果在 occupiedTiles 中没找到，但在 trackedObjects 中以 pos 为 key 找到了，则 originToRemove 保持为 pos


        // 现在我们有了要移除对象的原点 (originToRemove)，尝试从 trackedObjects 获取记录
        if (trackedObjects.TryGetValue(originToRemove, out BuildingRecord recordToRemove))
        {
             Debug.Log($"RemoveTrackedObject: Removing object '{recordToRemove.instance?.name}' at origin {originToRemove} (triggered by pos {pos}). Size: {recordToRemove.size}"); // 日志

            Vector2Int size = recordToRemove.size; // 获取对象的尺寸
            // 清理该对象占用的所有瓦片状态
            for (int x = originToRemove.x; x < originToRemove.x + size.x; x++)
            {
                for (int y = originToRemove.y; y < originToRemove.y + size.y; y++)
                {
                    Vector2Int currentTile = new Vector2Int(x, y);
                    if (IsInMap(currentTile)) // 再次检查边界
                    {
                        occupiedTiles.Remove(currentTile); // 从占用瓦片映射中移除
                        if (map != null) map[x, y] = ZoneType.Empty; // 重置地图网格类型为空
                        roadTiles.Remove(currentTile); // 从逻辑道路集合中移除 (无论它是什么类型)
                    }
                }
            }

            // 从 trackedObjects 字典中移除记录
            trackedObjects.Remove(originToRemove);
             Debug.Log($"RemoveTrackedObject: Removed record for origin {originToRemove}. Tracked objects count: {trackedObjects.Count}"); // 日志

            // 销毁游戏对象实例
            if (recordToRemove.instance != null)
            {
                // 如果实例在当前高亮列表中，也从中移除
                if(highlightedPathInstances.Contains(recordToRemove.instance))
                {
                    highlightedPathInstances.Remove(recordToRemove.instance);
                     Debug.Log($"RemoveTrackedObject: Removed instance '{recordToRemove.instance.name}' from highlightedPathInstances."); // 日志
                }
                 Debug.Log($"RemoveTrackedObject: Destroying instance '{recordToRemove.instance.name}'."); // 日志
                DestroyObjectInstance(recordToRemove.instance); // 销毁 GameObject
            }
             else {
                  Debug.LogWarning($"RemoveTrackedObject: Record for {originToRemove} had a null instance."); // 警告日志
             }
        }
        else // 处理不一致的情况
        {
            // occupiedTiles 说这里有东西 (如果 foundInOccupied 为 true)，但 trackedObjects 里却没有对应的记录
            if(foundInOccupied)
            {
                 Debug.LogError($"RemoveTrackedObject({pos}): Inconsistency! Tile occupied by {originToRemove}, but no record found in trackedObjects. Cleaning map/occupied for {pos} based on assumption it was 1x1."); // 错误日志
                 // 作为后备，至少清理触发调用的这个瓦片
                 occupiedTiles.Remove(pos);
                 if(map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty;
                 roadTiles.Remove(pos);
            } else {
                 // 如果是通过 trackedObjects[pos] 找到的 originToRemove=pos，但再次查找时又没了，也是不一致
                 Debug.LogError($"RemoveTrackedObject({pos}): Inconsistency! Found origin {originToRemove} initially, but record disappeared from trackedObjects before full removal. Cleaning map/occupied for {pos}."); // 错误日志
                 if(map != null && IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty;
                 roadTiles.Remove(pos);
            }
        }
    }


    /// <summary>
    /// (协程) 尝试将指定瓦片声明为道路。如果该位置已被占用，会先调用 RemoveTrackedObject 进行清理。
    /// 这个方法主要在生成过程中需要处理异步拆除时使用。
    /// 它只负责更新逻辑状态 (map, roadTiles)，视觉对象的实例化由 InstantiateRoadVisuals 统一处理。
    /// </summary>
    /// <param name="pos">要声明为道路的瓦片坐标。</param>
    /// <param name="callback">完成后调用的回调，bool 参数表示是否成功声明。</param>
    IEnumerator TryClaimTileForRoad(Vector2Int pos, Action<bool> callback)
    {
        bool success = false; // 初始化成功状态为 false
        if (IsInMap(pos)) // 检查坐标是否有效
        {
            // 情况 1: 该瓦片已经是逻辑道路
            if (roadTiles.Contains(pos))
            {
                success = true; // 已经是路，无需操作，视为成功
            }
            // 情况 2: 该瓦片不是逻辑道路，需要尝试声明
            else
            {
                // 检查是否需要拆除 (是否被占用 或 地图网格不是 Empty)
                bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;
                if (needsDemolition)
                {
                     Debug.Log($"TryClaimTileForRoad({pos}): Needs demolition. Calling RemoveTrackedObject..."); // 日志
                    RemoveTrackedObject(pos); // 调用移除方法
                    // 如果启用了异步生成，等待一帧以允许销毁操作完成
                    if (asyncGeneration) yield return null;
                }

                // 在可能进行了拆除后，再次检查瓦片是否确实变为空闲状态
                if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
                {
                    // 状态确认：现在是空的，可以声明为道路
                    map[pos.x, pos.y] = ZoneType.Road; // 在地图网格上标记为道路
                    roadTiles.Add(pos);              // 添加到逻辑道路集合
                    success = true;                  // 标记为成功
                     Debug.Log($"TryClaimTileForRoad({pos}): Successfully claimed as road."); // 日志
                }
                else if (roadTiles.Contains(pos))
                {
                    // 意外情况：拆除后发现它已经是路了？也视为成功。
                     Debug.LogWarning($"TryClaimTileForRoad({pos}): Tile became a road after demolition check? Marking as success anyway."); // 警告日志
                    success = true;
                }
                 else {
                     Debug.LogWarning($"TryClaimTileForRoad({pos}): Failed to claim. MapType={map[pos.x, pos.y]}, Occupied={occupiedTiles.ContainsKey(pos)}"); // 警告日志
                 }
            }
        }
        callback?.Invoke(success); // 调用回调函数，传递最终的成功状态
    }

    /// <summary>
    /// (立即执行) 尝试将指定瓦片在逻辑上声明为道路。如果该位置已被占用，会立即调用 RemoveTrackedObject 进行清理。
    /// 这个方法用于生成过程中不需要等待异步拆除的场景。
    /// 只更新逻辑状态 (map, roadTiles)。
    /// </summary>
    /// <param name="pos">要声明为道路的瓦片坐标。</param>
    /// <param name="success">输出参数，指示是否成功声明。</param>
    void TryClaimTileForRoadLogicOnly(Vector2Int pos, out bool success)
    {
        success = false; // 初始化输出参数
        if (!IsInMap(pos)) return; // 检查边界，无效则直接返回

        // 情况 1: 已经是逻辑道路
        if (roadTiles.Contains(pos))
        {
            success = true;
            return; // 无需操作
        }

        // 情况 2: 不是逻辑道路，检查是否需要拆除
        bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;
        if (needsDemolition)
        {
             // Debug.Log($"TryClaimTileForRoadLogicOnly({pos}): Needs demolition. Calling RemoveTrackedObject..."); // 可选日志
            RemoveTrackedObject(pos); // 立即执行移除
        }

        // 检查拆除后（或原本就不需要拆除）瓦片是否可用
        if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
        {
            // 可用，进行逻辑声明
            map[pos.x, pos.y] = ZoneType.Road; // 更新地图网格
            roadTiles.Add(pos);              // 添加到逻辑道路集合
            success = true;                  // 设置成功状态
             // Debug.Log($"TryClaimTileForRoadLogicOnly({pos}): Successfully claimed as road logically."); // 可选日志
        }
        else if (roadTiles.Contains(pos)) // 再次检查是否已经是路
        {
             // Debug.LogWarning($"TryClaimTileForRoadLogicOnly({pos}): Tile became a road after demolition check? Marking as success anyway."); // 可选日志
            success = true;
        }
         // else { Debug.LogWarning($"TryClaimTileForRoadLogicOnly({pos}): Failed to claim logically.");} // 可选日志
    }

    #endregion // 结束核心逻辑区域

    #region Instantiation & Cleanup (实例化与清理)
    // (保持之前的代码不变, 但增加了日志)
    void StartObjectInstantiation(bool processAllImmediately = false) { Debug.Log($"调用 StartObjectInstantiation。processAllImmediately={processAllImmediately}, 队列大小={generationQueue.Count}"); if (processAllImmediately) { if (instantiationCoroutine != null) { Debug.Log("StartObjectInstantiation: 停止现有的异步实例化协程。"); StopCoroutine(instantiationCoroutine); instantiationCoroutine = null; } Debug.Log("StartObjectInstantiation: 立即处理整个队列..."); int processedCount = 0; while (generationQueue.Count > 0) { GameObject obj = generationQueue.Dequeue(); if (ValidateObjectForActivation(obj)) { obj.SetActive(true); processedCount++; } else if (obj != null) { Debug.LogWarning($"StartObjectInstantiation (立即): 无效对象 '{obj.name}' 出队。正在销毁。"); DestroyObjectInstance(obj); } } Debug.Log($"StartObjectInstantiation: 立即处理完成。激活了 {processedCount} 个对象。"); } else { if (instantiationCoroutine == null && generationQueue.Count > 0) { Debug.Log("StartObjectInstantiation: 启动异步 ActivateQueuedObjects 协程。"); instantiationCoroutine = StartCoroutine(ActivateQueuedObjects()); } } }
    IEnumerator ActivateQueuedObjects() { Debug.Log($"ActivateQueuedObjects 协程开始。初始队列大小: {generationQueue.Count}"); if (generationQueue.Count == 0) { Debug.Log("ActivateQueuedObjects 协程: 启动时队列为空。退出。"); instantiationCoroutine = null; yield break; } int processedSinceYield = 0; int totalActivated = 0; System.Diagnostics.Stopwatch batchTimer = new System.Diagnostics.Stopwatch(); float maxFrameTimeMs = 8.0f; while (generationQueue.Count > 0) { batchTimer.Restart(); int activatedThisBatch = 0; while (generationQueue.Count > 0 && activatedThisBatch < objectsPerFrame) { GameObject obj = generationQueue.Dequeue(); bool isValid = ValidateObjectForActivation(obj); if (isValid) { obj.SetActive(true); activatedThisBatch++; processedSinceYield++; totalActivated++; } else if (obj != null) { Debug.LogWarning($"ActivateQueuedObjects: 异步激活期间发现无效对象 '{obj.name}' 出队。正在销毁。"); DestroyObjectInstance(obj); } if (asyncGeneration && batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs) { break; } } if (asyncGeneration && (batchTimer.Elapsed.TotalMilliseconds > maxFrameTimeMs || processedSinceYield >= yieldBatchSize)) { processedSinceYield = 0; yield return null; } else if (!asyncGeneration && generationQueue.Count > 0) { yield return null; } } Debug.Log($"ActivateQueuedObjects 协程完成。总共激活了 {totalActivated} 个对象。最终队列大小: {generationQueue.Count}"); instantiationCoroutine = null; }
    bool ValidateObjectForActivation(GameObject obj) { if (obj == null) { Debug.LogWarning("ValidateObjectForActivation: 收到 null 对象。"); return false; } if (TryParsePositionFromName(obj.name, out Vector2Int origin)) { if (trackedObjects.TryGetValue(origin, out BuildingRecord record)) { if (record.instance == obj) { return true; } else { Debug.LogWarning($"ValidateObjectForActivation: 失败 对于 '{obj.name}' @ {origin}。记录存在但指向不同的实例 ('{record.instance?.name}')。丢弃 '{obj.name}'。"); return false; } } else { Debug.LogWarning($"ValidateObjectForActivation: 失败 对于 '{obj.name}' @ {origin}。在 trackedObjects 中找不到记录。丢弃。"); return false; } } else { Debug.LogError($"ValidateObjectForActivation: 失败 对于对象 '{obj.name}'。无法从名称解析位置。丢弃。"); return false; } }

    /// <summary>
    /// 清理所有之前生成的地图数据和对象。
    /// 停止所有相关协程，销毁所有实例化的 GameObject，并重置所有数据结构和状态变量。
    /// </summary>
    void ClearPreviousGeneration()
    {
        Debug.Log("开始清理之前的生成..."); // 日志：开始清理

        // 1. 停止所有可能仍在运行的相关协程
        if (mainGenerationCoroutine != null)
        {
            StopCoroutine(mainGenerationCoroutine);
            mainGenerationCoroutine = null; // 重置句柄
             // Debug.Log("已停止 mainGenerationCoroutine。"); // 可选日志
        }
        if (instantiationCoroutine != null)
        {
            StopCoroutine(instantiationCoroutine);
            instantiationCoroutine = null; // 重置句柄
             // Debug.Log("已停止 instantiationCoroutine。"); // 可选日志
        }
        StopTrafficAndPathUpdates(); // 调用方法停止交通模拟协程 (内部会重置句柄)
         // Debug.Log("已停止 trafficAndPathUpdateCoroutine (如果正在运行)。"); // 可选日志


        // 2. 清除路径高亮和选择状态
        // ClearHighlightedPath 会调用 ClearHighlightedPathVisualsOnly 来处理视觉恢复和实例列表
        ClearHighlightedPath(); // 重置 pathStartPoint, pathEndPoint, currentHighlightedPath, 并清理视觉效果
         // Debug.Log("已清除路径高亮和选择状态。"); // 可选日志


        // 3. 销毁所有已实例化的子对象
        if (mapParent != null)
        {
             Debug.Log($"开始销毁 '{mapParent.name}' 下的 {mapParent.childCount} 个子对象..."); // 日志
            // 从后往前遍历并销毁子对象，这样更安全，不会在迭代时改变索引
            for (int i = mapParent.childCount - 1; i >= 0; i--)
            {
                Transform child = mapParent.GetChild(i);
                if (child != null)
                {
                    // Debug.Log($"Destroying child: {child.name}"); // 可选详细日志
                    DestroyObjectInstance(child.gameObject); // 调用销毁方法
                }
            }
             Debug.Log("销毁 mapParent 的子对象完成。"); // 日志
        }
        else // 如果 mapParent 未设置，尝试查找默认父对象并清理
        {
             GameObject defaultParent = GameObject.Find("GeneratedCity");
             if(defaultParent != null)
             {
                 Debug.LogWarning($"mapParent 未设置，尝试清理默认父对象 'GeneratedCity' 下的 {defaultParent.transform.childCount} 个子对象..."); // 警告日志
                 for (int i = defaultParent.transform.childCount - 1; i >= 0; i--) {
                     Transform child = defaultParent.transform.GetChild(i);
                     if (child != null) DestroyObjectInstance(child.gameObject);
                 }
                 Debug.Log("清理默认父对象的子对象完成。"); // 日志
             } else {
                 Debug.LogWarning("mapParent 未设置，也找不到 'GeneratedCity' 对象。可能无法销毁所有生成的对象。"); // 警告日志
             }
        }


        // 4. 清理所有数据结构
        map = null; // 将大型数组设为 null，以便垃圾回收器回收内存
        trackedObjects.Clear();
        occupiedTiles.Clear();
        generationQueue.Clear();
        roadTiles.Clear();
        voronoiSites.Clear();
        noisePoints.Clear();
        // 确保高亮列表也被清空 (虽然 ClearHighlightedPath 应该已经做了)
        highlightedPathInstances.Clear();
        currentHighlightedPath.Clear();
         Debug.Log("已清理所有核心数据结构 (trackedObjects, occupiedTiles, roadTiles 等)。"); // 日志


        // 5. 重置状态变量
        prefabsValidated = false;
        currentHoverInfo = "";
        // 清除 UI 文本 (如果存在)
        if (hoverInfoTextElement != null) hoverInfoTextElement.text = "";
        // 确保路径点也被重置 (虽然 ClearHighlightedPath 应该已经做了)
        pathStartPoint = null;
        pathEndPoint = null;
         Debug.Log("已重置状态变量。"); // 日志

        Debug.Log("之前的生成清理完毕。"); // 日志：清理完成
    }

    /// <summary>
    /// 销毁一个 GameObject 实例。
    /// 在编辑器非播放模式下使用 DestroyImmediate，在播放模式下使用 Destroy。
    /// </summary>
    /// <param name="obj">要销毁的游戏对象。</param>
    void DestroyObjectInstance(GameObject obj)
    {
        if (obj == null) return; // 如果对象已经是 null，则无需操作

        // 检查当前是在编辑器模式下还是播放模式下
        if (Application.isEditor && !Application.isPlaying)
        {
            // 在编辑器非播放模式下，必须使用 DestroyImmediate 才能立即看到效果
            // Debug.Log($"DestroyImmediate called on: {obj.name}"); // 可选日志
            DestroyImmediate(obj);
        }
        else
        {
            // 在播放模式下或构建版本中，使用标准的 Destroy
            // Destroy 不会立即销毁，而是在当前帧的末尾处理
            // Debug.Log($"Destroy called on: {obj.name}"); // 可选日志
            Destroy(obj);
        }
    }

    #endregion // 结束 Instantiation & Cleanup 区域

    #region A* Pathfinding (Internal Fixed Cost - For Road Gen) (内部固定成本 A* - 用于道路生成)
    // --- 此部分使用简单的 A* 用于 **生成** 目的 ---
    // --- 与用户选择的路径高亮 **无关** ---
    // --- 它使用固定成本，忽略交通状况 ---

    /// <summary>
    /// 内部 A* 算法中使用的路径节点类。
    /// </summary>
    private class Internal_PathNode
    {
        public Vector2Int position; // 节点在网格上的位置
        public float gScore;       // 从起点到此节点的实际成本
        public float hScore;       // 从此节点到目标点的估算成本 (启发式)
        public float fScore => gScore + hScore; // 总评估成本 (g + h)
        public Internal_PathNode parent; // 指向路径上的前一个节点

        public Internal_PathNode(Vector2Int pos, float g, float h, Internal_PathNode p)
        {
            position = pos;
            gScore = g;
            hScore = h;
            parent = p;
        }
        // 为简单起见，不覆盖 Equals 和 GetHashCode，因为我们使用 Vector2Int 作为字典键
    }

    /// <summary>
    /// 内部使用的 A* 寻路函数，基于固定成本查找从起点到目标集合中任意一点的最短路径。
    /// 用于道路生成阶段连接不同的点。
    /// </summary>
    /// <param name="start">起始点坐标。</param>
    /// <param name="targets">一个包含一个或多个目标点坐标的 HashSet。</param>
    /// <returns>从起点到最近目标点的路径（Vector2Int 列表），如果找不到路径则返回 null。</returns>
    private List<Vector2Int> FindPath_InternalFixedCost(Vector2Int start, HashSet<Vector2Int> targets)
    {
        // 基本检查
        if (targets == null || targets.Count == 0 || !IsInMap(start))
        {
             Debug.LogWarning($"FindPath_InternalFixedCost: 输入无效 (targets null/empty, or start out of map: {start})");
             return null;
        }
        // 如果起点本身就是目标之一
        if (targets.Contains(start))
        {
            return new List<Vector2Int>() { start }; // 路径只包含起点
        }

        // --- 数据结构 ---
        List<Internal_PathNode> openSet = new List<Internal_PathNode>(); // 待评估节点列表 (使用 List + Sort，效率不高但简单)
        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>();       // 已评估节点位置集合
        Dictionary<Vector2Int, Internal_PathNode> nodeMap = new Dictionary<Vector2Int, Internal_PathNode>(); // 存储每个位置的最佳已知节点

        // --- 初始化 ---
        float startHScore = InternalHeuristic(start, targets); // 计算起点到目标的启发式成本
        Internal_PathNode startNode = new Internal_PathNode(start, 0f, startHScore, null); // 起点 gScore 为 0
        openSet.Add(startNode);
        nodeMap[start] = startNode;

        // 迭代次数和最大迭代限制 (防止死循环)
        int iterations = 0;
        int maxIterations = mapWidth * mapHeight * 4; // 设置一个慷慨的最大迭代次数

        // --- A* 搜索循环 ---
        while (openSet.Count > 0)
        {
            iterations++;
            if (iterations > maxIterations)
            {
                Debug.LogError($"内部 A* 寻路迭代次数超过限制 ({maxIterations}) 从 {start} 到目标。中止搜索。");
                return null; // 达到迭代限制，返回 null
            }

            // 1. 从 openSet 中找到 fScore 最低的节点
            //    (使用 List.Sort 效率较低，对于大型地图应换用优先队列)
            openSet.Sort((a, b) => a.fScore.CompareTo(b.fScore));
            Internal_PathNode current = openSet[0]; // fScore 最低的节点
            openSet.RemoveAt(0); // 将其移出 openSet

            // 2. 检查是否到达目标
            if (targets.Contains(current.position))
            {
                // Debug.Log($"Internal A* path found from {start} to {current.position} in {iterations} iterations."); // 可选日志
                return InternalReconstructPath(current); // 找到路径，重构并返回
            }

            // 3. 将当前节点加入 closedSet
            closedSet.Add(current.position);

            // 4. 遍历当前节点的邻居
            foreach (var dir in directions) // 使用预定义的四方向邻居
            {
                Vector2Int neighborPos = current.position + dir;

                // 5. 检查邻居的有效性
                // a) 是否在地图内
                if (!IsInMap(neighborPos)) continue;
                // b) 是否已在 closedSet 中 (已找到最优路径)
                if (closedSet.Contains(neighborPos)) continue;

                // c) 获取进入邻居的成本 (使用内部固定成本函数)
                float terrainCost = GetTerrainCost_InternalFixed(neighborPos);
                // d) 如果成本为无穷大 (不可通行)，则跳过
                if (float.IsPositiveInfinity(terrainCost)) continue;

                // 6. 计算通过当前节点到达邻居的总成本 (gScore)
                float tentativeGScore = current.gScore + terrainCost;

                // 7. 检查是否需要更新邻居节点
                nodeMap.TryGetValue(neighborPos, out Internal_PathNode neighborNode); // 尝试获取邻居节点

                // 如果邻居节点是新的 (从未访问过)，或者找到了更短的路径到达该邻居
                if (neighborNode == null || tentativeGScore < neighborNode.gScore)
                {
                    // 计算邻居到目标的启发式成本
                    float neighborHScore = InternalHeuristic(neighborPos, targets);

                    // 更新或创建邻居节点
                    if (neighborNode == null) // 如果是新节点
                    {
                        neighborNode = new Internal_PathNode(neighborPos, tentativeGScore, neighborHScore, current);
                        nodeMap[neighborPos] = neighborNode; // 加入 nodeMap
                        openSet.Add(neighborNode);          // 加入 openSet
                    }
                    else // 如果是已存在节点 (在 openSet 中)
                    {
                        neighborNode.gScore = tentativeGScore; // 更新 gScore
                        neighborNode.parent = current;         // 更新父节点
                        // hScore 不变, fScore 会自动更新
                        // (如果使用优先队列，这里需要调用 DecreaseKey 操作)
                    }
                }
            } // 结束邻居循环
        } // 结束主循环 (openSet 为空)

        // 如果 openSet 为空但未找到目标，说明路径不存在
        Debug.LogWarning($"内部 A* 无法找到从 {start} 到目标集合的路径。");
        return null;
    }

    /// <summary>
    /// 获取内部固定成本 A* 算法中用于评估瓦片通行成本的函数。
    /// </summary>
    /// <param name="pos">要评估的瓦片坐标。</param>
    /// <returns>通行成本。道路成本低，空地成本高，其他类型或被占用的非道路瓦片成本为无穷大。</returns>
    private float GetTerrainCost_InternalFixed(Vector2Int pos)
    {
        // 检查是否在地图边界内
        if (!IsInMap(pos)) return float.PositiveInfinity; // 地图外不可通行

        // 检查瓦片是否被非道路对象占据
        // 如果 occupiedTiles 包含此位置，并且占据者原点不是此位置本身，
        // 并且该占据者不是道路类型，则视为不可通行。
        if (occupiedTiles.TryGetValue(pos, out Vector2Int origin) && origin != pos)
        {
            if (trackedObjects.TryGetValue(origin, out BuildingRecord occupier) && occupier.type != ZoneType.Road)
            {
                 return float.PositiveInfinity; // 被非道路建筑占据
            }
            // 如果占据者是道路 (例如，多格道路的一部分，虽然当前设计都是 1x1)，可以考虑给一个成本
            // else return 1.0f; // 或者其他成本
        }


        // 检查地图逻辑网格的状态 (确保 map 已初始化)
        if (map == null) {
            Debug.LogError("GetTerrainCost_InternalFixed called before map was initialized!");
            return 10f; // 返回一个默认较高成本
        }
        switch (map[pos.x, pos.y])
        {
            case ZoneType.Road:
                return 1.0f; // 道路成本最低
            case ZoneType.Empty:
                return 5.0f; // 空地成本较高，鼓励走道路
            case ZoneType.Residential:
            case ZoneType.Commercial:
            case ZoneType.Industrial:
            default:
                return float.PositiveInfinity; // 建筑区域或其他未知区域不可通行
        }
    }

    /// <summary>
    /// 内部固定成本 A* 使用的启发式函数。
    /// 计算当前点到目标集合中最近点的曼哈顿距离。
    /// </summary>
    /// <param name="current">当前评估的节点位置。</param>
    /// <param name="targets">目标点集合。</param>
    /// <returns>到最近目标的估算距离 (曼哈顿距离)。</returns>
    private float InternalHeuristic(Vector2Int current, HashSet<Vector2Int> targets)
    {
        float minDistance = float.MaxValue; // 初始化最小距离为最大值
        // 遍历所有目标点
        foreach(var target in targets)
        {
            // 计算当前点与目标点的曼哈顿距离，并更新最小值
            minDistance = Mathf.Min(minDistance, InternalManhattanDistance(current, target));
        }
        return minDistance; // 返回找到的最小距离
    }

    /// <summary>
    /// 计算两个二维整数向量之间的曼哈顿距离 (|x1-x2| + |y1-y2|)。
    /// </summary>
    private int InternalManhattanDistance(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    /// <summary>
    /// 从目标节点开始，通过 parent 指针反向回溯，构建最终路径。
    /// </summary>
    /// <param name="targetNode">找到的目标节点。</param>
    /// <returns>从起点到目标点的路径列表 (Vector2Int)，列表顺序为 Start -> ... -> End。</returns>
    private List<Vector2Int> InternalReconstructPath(Internal_PathNode targetNode)
    {
        List<Vector2Int> path = new List<Vector2Int>(); // 初始化路径列表
        Internal_PathNode current = targetNode; // 从目标节点开始回溯
        int safetyCounter = 0; // 安全计数器，防止因 parent 链问题导致死循环
        int maxPathLength = mapWidth * mapHeight + 1; // 设定一个最大合理路径长度

        // 循环回溯，直到回到起点 (parent 为 null)
        while(current != null && safetyCounter < maxPathLength)
        {
            path.Add(current.position); // 将当前节点位置加入路径列表
            current = current.parent;    // 移动到父节点
            safetyCounter++;
        }

        // 检查是否因为达到安全限制而退出循环
        if (safetyCounter >= maxPathLength)
        {
            Debug.LogError("内部 A* 路径重构超过最大长度！可能存在循环或 parent 指针错误。");
            // 可以选择返回部分路径或 null
            // path.Reverse(); return path; // 返回部分路径
             return null; // 返回 null 表示失败
        }

        // 因为是从目标回溯到起点，所以需要将列表反转得到正确的 Start -> End 顺序
        path.Reverse();
        return path; // 返回重构好的路径
    }

    #endregion // 结束内部 A* 寻路区域

    #region Auxiliary & Helper Methods (辅助与帮助方法)

    /// <summary>
    /// 检查给定的二维整数坐标是否在当前地图的边界内。
    /// </summary>
    /// <param name="pos">要检查的坐标。</param>
    /// <returns>如果坐标在地图边界内，则返回 true；否则返回 false。</returns>
    bool IsInMap(Vector2Int pos)
    {
        return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight;
    }

    /// <summary>
    /// 尝试从遵循特定命名约定（例如 "PrefabName_X_Y"）的字符串中解析出末尾的二维整数坐标。
    /// </summary>
    /// <param name="name">包含坐标信息的字符串。</param>
    /// <param name="position">输出参数，如果解析成功，则包含解析出的坐标；否则为 Vector2Int.zero。</param>
    /// <returns>如果成功解析出坐标，则返回 true；否则返回 false。</returns>
    bool TryParsePositionFromName(string name, out Vector2Int position)
    {
        position = Vector2Int.zero; // 初始化输出参数
        if (string.IsNullOrEmpty(name)) // 检查输入字符串是否为空
        {
            return false;
        }

        try
        {
            // 1. 查找最后一个下划线的位置
            int lastUnderscore = name.LastIndexOf('_');

            // 检查最后一个下划线是否存在，并且后面至少有一个字符 (Y 坐标)，前面至少有一个字符（X 坐标或之前的字符）
            if (lastUnderscore <= 0 || lastUnderscore >= name.Length - 1)
            {
                // Debug.LogWarning($"TryParsePositionFromName: Could not find valid last underscore in '{name}'."); // 可选日志
                return false;
            }

            // 2. 查找倒数第二个下划线的位置（在最后一个下划线之前）
            int secondLastUnderscore = name.LastIndexOf('_', lastUnderscore - 1);

            // 检查倒数第二个下划线是否存在，它必须在字符串的开头之后
            if (secondLastUnderscore < 0)
            {
                // Debug.LogWarning($"TryParsePositionFromName: Could not find valid second last underscore in '{name}'."); // 可选日志
                return false; // 必须至少有两个下划线才能形成 "_X_Y" 结构
            }

            // 3. 提取 X 和 Y 坐标的字符串部分
            // X 字符串在倒数第二个和最后一个下划线之间
            string xStr = name.Substring(secondLastUnderscore + 1, lastUnderscore - secondLastUnderscore - 1);
            // Y 字符串在最后一个下划线之后
            string yStr = name.Substring(lastUnderscore + 1);

            // 4. 尝试将提取的字符串解析为整数
            // 使用 NumberStyles.Integer 和 CultureInfo.InvariantCulture 确保解析的一致性
            if (int.TryParse(xStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(yStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y))
            {
                // 解析成功，设置输出参数并返回 true
                position = new Vector2Int(x, y);
                // Debug.Log($"TryParsePositionFromName: Successfully parsed '{name}' to position {position}."); // 可选日志
                return true;
            }
            else
            {
                 Debug.LogWarning($"TryParsePositionFromName: Failed to parse integer values from substrings '{xStr}' or '{yStr}' in '{name}'."); // 可选日志
                 return false; // 解析整数失败
            }
        }
        catch (Exception ex) // 捕获可能的异常 (例如 Substring 索引越界，虽然前面的检查应该能避免)
        {
            Debug.LogError($"TryParsePositionFromName: Error parsing position from name '{name}': {ex.Message}");
            return false; // 发生异常，返回 false
        }
        // 默认返回 false (虽然理论上所有路径都应在 try 块内返回)
        // return false;
    }

    /// <summary>
    /// (可选辅助方法) 在给定的 HashSet 中绘制从起点到终点的直线路径（只包含水平和垂直段）。
    /// 注意：这不是 A* 寻路，只是简单的直线连接，主要用于某些生成算法的辅助。
    /// </summary>
    /// <param name="start">路径起点。</param>
    /// <param name="end">路径终点。</param>
    /// <param name="pathTilesSet">用于存储路径瓦片坐标的 HashSet。</param>
    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet)
    {
        Vector2Int current = start; // 从起点开始
        pathTilesSet.Add(current); // 将起点加入集合

        // 先移动 X 方向
        int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x); // 计算 X 方向的移动步长 (0, 1 或 -1)
        while (current.x != end.x) // 循环直到 X 坐标到达终点
        {
            current.x += xDir; // 移动一步
            if (!IsInMap(current)) return; // 如果移出地图，停止绘制
            pathTilesSet.Add(current); // 将当前点加入集合
        }

        // 再移动 Y 方向
        int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y); // 计算 Y 方向的移动步长 (0, 1 或 -1)
        while (current.y != end.y) // 循环直到 Y 坐标到达终点
        {
            current.y += yDir; // 移动一步
            if (!IsInMap(current)) return; // 如果移出地图，停止绘制
            pathTilesSet.Add(current); // 将当前点加入集合
        }
        // 注意：这种方法生成的路径是先水平移动再垂直移动（或反之），形成 L 形路径。
    }

    #endregion // 结束 Auxiliary & Helper Methods 区域

    #region Traffic Simulation & Automatic Path Updates (交通模拟与自动路径更新)

    // 启动交通和路径更新协程 (仅在 Play 模式下)
    void StartTrafficAndPathUpdates()
    {
        StopTrafficAndPathUpdates(); // 确保没有重复的协程
        if (vehicleUpdateInterval > 0 && Application.isPlaying)
        {
            Debug.Log($"每 {vehicleUpdateInterval} 秒启动交通和路径更新。");
            trafficAndPathUpdateCoroutine = StartCoroutine(UpdateTrafficAndPathCoroutine());
        }
        else if (vehicleUpdateInterval <= 0)
        {
            Debug.LogWarning("车辆更新间隔 <= 0。自动交通/路径更新已禁用。");
        }
         else if (!Application.isPlaying)
        {
             Debug.Log("不在编辑器模式下启动交通/路径更新。");
        }
    }

    // 停止交通和路径更新协程
    void StopTrafficAndPathUpdates()
    {
        if (trafficAndPathUpdateCoroutine != null)
        {
            StopCoroutine(trafficAndPathUpdateCoroutine);
            trafficAndPathUpdateCoroutine = null;
        }
    }

    // 更新交通和路径的协程
    IEnumerator UpdateTrafficAndPathCoroutine()
    {
        // 首次更新前的初始延迟
        yield return new WaitForSeconds(Mathf.Max(1.0f, vehicleUpdateInterval / 2f));

        while (true) // 无限循环，直到协程被停止
        {
            // 1. 模拟交通变化 (当前是简单随机更新)
            int updatedCount = 0;
            // 在值的副本上迭代，以防 trackedObjects 在迭代过程中被修改 (虽然在这里不太可能，但更安全)
            foreach (BuildingRecord record in trackedObjects.Values.ToList())
            {
                // 只更新 1x1 的道路，且容量有效
                if (record?.type == ZoneType.Road && record.size == Vector2Int.one && record.capacity > 0)
                {
                    // **重要修改：将当前车辆数随机设置为 0 到 30**
                    record.currentVehicles = UnityEngine.Random.Range(0, 31); // n (0-30)
                    updatedCount++;
                }
            }
             // if(updatedCount > 0) Debug.Log($"更新了 {updatedCount} 条道路段的交通。"); // 可选日志

            // 2. 如果起点和终点都已设置，则重新计算并高亮路径
            if (pathStartPoint.HasValue && pathEndPoint.HasValue)
            {
                // Debug.Log("交通已更新，重新计算路径..."); // 可选日志
                // 调用 FindAndHighlightPath，logStartMessage 设为 false 以避免控制台刷屏
                FindAndHighlightPath(false);
            }

            // 3. 等待下一个更新间隔
            yield return new WaitForSeconds(vehicleUpdateInterval);
        }
    }

    // CalculateTravelTime - 计算单格道路的通行时间 (供 A* 成本函数和悬停信息使用)
    // ** 修改版，使用指定的分段函数 **
    public float CalculateTravelTime(BuildingRecord roadRecord)
    {
        // 验证输入记录是否有效
        if (roadRecord == null || roadRecord.type != ZoneType.Road || roadRecord.size != Vector2Int.one || roadRecord.capacity <= 0)
        {
            return float.PositiveInfinity; // 无效或非道路瓦片视为不可通行
        }

        // 计算负载因子 x = n / v
        // 必须进行浮点数除法
        float loadFactor = (float)roadRecord.currentVehicles / roadRecord.capacity;

        // 计算拥堵乘数 congestionMultiplier = f(x)
        float congestionMultiplier;
        if (loadFactor <= trafficCongestionThreshold) // x <= T
        {
            congestionMultiplier = 1.0f; // f(x) = 1
        }
        else // x > T
        {
            // f(x) = 1 + e^x (其中 x 是 loadFactor)
            // 注意：这里原文是 e^x，如果希望是 e^(n/v) 或其他形式，需要相应修改
            congestionMultiplier = 1.0f + Mathf.Exp(loadFactor);
        }

        // 最终通行时间 = 基础成本 * 拥堵乘数
        float baseCost = Mathf.Max(0.01f, travelTimeBaseCost); // 确保基础成本大于 0
        float finalTime = baseCost * congestionMultiplier;

         // Debug.Log($"CalculateTravelTime({roadRecord.instance.name}): n={roadRecord.currentVehicles}, v={roadRecord.capacity}, load={loadFactor:F2}, threshold={trafficCongestionThreshold:F2}, multiplier={congestionMultiplier:F2}, finalTime={finalTime:F2}"); // 详细调试日志

        return Mathf.Max(0.01f, finalTime); // 确保最终时间也大于 0，防止 A* 出错
    }

    // HandleMouseHover - 处理鼠标悬停事件，显示信息
    // ** 修改版，显示 v, n, 和通行时间 **
    void HandleMouseHover()
    {
        if (mainCamera == null) return; // 确保摄像机有效

        string infoToShow = ""; // 准备要显示的信息字符串
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition); // 从摄像机发射射线

        // 使用 roadLayerMask 进行射线检测，确保只检测道路层
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask))
        {
            // 从碰撞点计算网格坐标
            int gridX = Mathf.FloorToInt(hit.point.x);
            int gridY = Mathf.FloorToInt(hit.point.z);
            Vector2Int gridPos = new Vector2Int(gridX, gridY);

            // 检查该坐标是否在地图内，并且 trackedObjects 中有对应的道路记录
            if (IsInMap(gridPos) && trackedObjects.TryGetValue(gridPos, out BuildingRecord record))
            {
                // 进一步确认是 1x1 的道路且实例存在
                if (record.type == ZoneType.Road && record.size == Vector2Int.one && record.instance != null)
                {
                    // 获取通行时间
                    float travelTime = CalculateTravelTime(record);
                    // 格式化输出字符串
                    infoToShow = $"道路 @ ({gridPos.x},{gridPos.y})\n" +
                                 $"容量 (v): {record.capacity}\n" +         // 显示容量 v
                                 $"当前车辆 (n): {record.currentVehicles}\n" + // 显示当前车辆 n
                                 $"通行时间: {travelTime:F2}";             // 显示计算出的通行时间 (保留两位小数)
                }
                // 如果需要显示其他类型建筑的信息，可以在这里添加 else if
            }
            // 如果 trackedObjects 中没有直接以 gridPos 为 key 的记录 (例如多格建筑)，
            // 可以尝试通过 occupiedTiles 查找其原点，再从 trackedObjects 查找。
            // else if (occupiedTiles.TryGetValue(gridPos, out Vector2Int origin) && trackedObjects.TryGetValue(origin, out record)) { ... }
        }

        // 更新内部信息字符串和 UI 文本 (如果已设置)
        currentHoverInfo = infoToShow;
        if (hoverInfoTextElement != null)
        {
            hoverInfoTextElement.text = infoToShow;
        }
    }

    #endregion

    #region Pathfinding and Highlighting (寻路与高亮 - 用户交互部分)

    // HandleMapClick - 处理鼠标点击，设置起点/终点
    // (此方法已在之前的回答中提供，包含日志)
    void HandleMapClick(int mouseButton) // 0 = 左键, 1 = 右键
    {
        if (mainCamera == null || map == null || aStarFinder == null)
        {
             Debug.LogWarning("HandleMapClick: 无法处理点击，缺少 Camera, map 或 aStarFinder。");
             return;
        }

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        // 重要: 使用 roadLayerMask 确保只命中道路的 Collider
        bool hitDetected = Physics.Raycast(ray, out RaycastHit hit, 2000f, roadLayerMask);

        if (hitDetected)
        {
            // 从命中点获取网格坐标
            int gridX = Mathf.FloorToInt(hit.point.x);
            int gridY = Mathf.FloorToInt(hit.point.z);
            Vector2Int clickedGridPos = new Vector2Int(gridX, gridY);

             Debug.Log($"HandleMapClick: 射线命中对象 '{hit.collider.name}' 在层 '{LayerMask.LayerToName(hit.collider.gameObject.layer)}'，坐标 {clickedGridPos}。"); // 日志

            // 验证点击位置是否对应于我们跟踪的 1x1 道路瓦片
            if (IsInMap(clickedGridPos) &&
                trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord record) &&
                record.type == ZoneType.Road &&
                record.size == Vector2Int.one)
            {
                 Debug.Log($"HandleMapClick: 在 {clickedGridPos} 处找到有效的 1x1 道路记录。"); // 日志
                // 成功点击了有效的道路瓦片
                if (mouseButton == 0) // 左键: 设置起点
                {
                    if (pathStartPoint != clickedGridPos) // 仅当点改变时更新
                    {
                        pathStartPoint = clickedGridPos;
                        Debug.Log($"路径起点设置为: {pathStartPoint.Value}");
                        // 仅当终点也设置时才触发寻路
                         if (pathEndPoint.HasValue) FindAndHighlightPath(true);
                         else ClearHighlightedPathVisualsOnly(); // 如果只设置了起点，清除旧高亮
                    }
                }
                else if (mouseButton == 1) // 右键: 设置终点
                {
                     if (pathEndPoint != clickedGridPos) // 仅当点改变时更新
                     {
                        pathEndPoint = clickedGridPos;
                        Debug.Log($"路径终点设置为: {pathEndPoint.Value}");
                        // 仅当起点也设置时才触发寻路
                        if (pathStartPoint.HasValue) FindAndHighlightPath(true);
                        else ClearHighlightedPathVisualsOnly(); // 如果只设置了终点，清除旧高亮
                     }
                }
            }
            else
            {
                 // 记录为什么点击无效
                 string reason = "未知原因";
                 if (!IsInMap(clickedGridPos)) reason = "点击位置超出地图范围";
                 else if (!trackedObjects.ContainsKey(clickedGridPos)) reason = "在 trackedObjects 中找不到记录";
                 else if (trackedObjects.TryGetValue(clickedGridPos, out BuildingRecord r)) // 再次尝试获取记录以查看类型/大小
                 {
                     if (r.type != ZoneType.Road) reason = $"对象类型不是道路 (类型={r.type})";
                     else if (r.size != Vector2Int.one) reason = $"对象尺寸不是 1x1 (尺寸={r.size})";
                 }
                 Debug.LogWarning($"HandleMapClick: 点击位置 {clickedGridPos} 不是有效的已跟踪 1x1 道路瓦片。原因: {reason}。命中的是: {hit.collider.name}"); // 警告日志
            }
        }
         else {
             // Debug.Log("HandleMapClick: 射线未命中任何带有 Collider 且在 roadLayerMask 上的对象。"); // 可选日志
         }
    }

    // FindAndHighlightPath - 使用 AStar 组件基于交通成本查找路径
    void FindAndHighlightPath(bool logStartMessage = true)
    {
        // 检查起点和终点是否已设置
        if (!pathStartPoint.HasValue || !pathEndPoint.HasValue)
        {
            // 理想情况下，这应由 HandleMapClick 在某个点变为空时处理清除视觉效果
            // if(logStartMessage) Debug.LogWarning("无法查找路径：起点或终点未设置。");
            ClearHighlightedPathVisualsOnly(); // 确保在状态无效时清除视觉效果
            return;
        }

        // 检查 AStar 组件是否存在
        if (aStarFinder == null)
        {
            Debug.LogError("无法查找路径：缺少 AStar 组件引用！");
            ClearHighlightedPath(); // 清除状态和视觉效果
            return;
        }

        // 处理起点和终点相同的情况
        if (pathStartPoint.Value == pathEndPoint.Value)
        {
            if (logStartMessage) Debug.Log("寻路：起点和终点相同。");
            // 高亮单个点
            HighlightPath(new List<Vector2Int> { pathStartPoint.Value });
            return;
        }

        if (logStartMessage) Debug.Log($"寻路：请求从 {pathStartPoint.Value} 到 {pathEndPoint.Value} 使用交通成本的路径...");

        // 计时寻路过程
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

        // --- 调用 AStar 组件进行寻路 ---
        List<Vector2Int> path = aStarFinder.FindPath(
            pathStartPoint.Value,       // 起点
            pathEndPoint.Value,         // 终点
            GetPathfindingCost,         // 获取成本的委托 (通行时间)
            IsTileWalkableForPath,      // 检查基本可步行性的委托 (是否是道路瓦片)
            travelTimeBaseCost          // 启发式乘数 (基础成本有助于估计距离)
        );

        timer.Stop(); // 停止计时

        // --- 处理寻路结果 ---
        if (path != null && path.Count > 0)
        {
            // 计算路径总时间 (用于日志)
            float totalTime = CalculateTotalPathTime(path);
            if (logStartMessage) Debug.Log($"寻路：找到路径 ({path.Count} 个瓦片)。估计总时间: {totalTime:F2}。计算耗时: {timer.ElapsedMilliseconds}ms");
            HighlightPath(path); // 更新视觉高亮
        }
        else
        {
            if (logStartMessage) Debug.LogWarning($"寻路：未找到从 {pathStartPoint.Value} 到 {pathEndPoint.Value} 的路径。计算耗时: {timer.ElapsedMilliseconds}ms");
            ClearHighlightedPathVisualsOnly(); // 如果现在找不到路径，清除任何之前的高亮
        }
    }

    // GetPathfindingCost - A* 的委托: 返回道路瓦片的通行时间
    private float GetPathfindingCost(Vector2Int pos)
    {
        // 基础边界检查 (A* 可能会查询其初始检查范围之外的瓦片)
        if (!IsInMap(pos)) return float.PositiveInfinity;

        // 检查是否是有效的、已跟踪的 1x1 道路瓦片
        if (trackedObjects.TryGetValue(pos, out BuildingRecord record) &&
            record.type == ZoneType.Road &&
            record.size == Vector2Int.one)
        {
            // 调用 CalculateTravelTime 获取基于交通的成本
            float time = CalculateTravelTime(record);
            // --- 调试日志 ---
            // 取消注释以在 A* 搜索期间验证成本计算
            // Debug.Log($"GetPathfindingCost({pos}): Road Found. Capacity={record.capacity}, Current={record.currentVehicles}, Time={time:F2}");
            // --- 结束调试日志 ---
            // 确保成本永远不为零或负数，以保证 A* 稳定性
            return Mathf.Max(0.01f, time);
        }

        // 如果在 trackedObjects 中找不到，或者不是 1x1 道路，则对于寻路来说是不可通行的
        // Debug.Log($"GetPathfindingCost({pos}): 不可通行 (不是已跟踪的 1x1 道路)。");
        return float.PositiveInfinity;
    }


    // IsTileWalkableForPath - A* 的委托: 基础检查瓦片是否可能是道路网络的一部分
    private bool IsTileWalkableForPath(Vector2Int pos)
    {
        // 首先检查边界
        if (!IsInMap(pos))
        {
            // Debug.Log($"IsTileWalkableForPath({pos}): False (超出边界)");
            return false;
        }

        // 检查根据 trackedObjects，它是否明确是一个 1x1 的道路瓦片
        // 这是寻路可步行性的主要检查。
        bool isRoad = trackedObjects.TryGetValue(pos, out BuildingRecord record)
                      && record.type == ZoneType.Road
                      && record.size == Vector2Int.one;

        // --- 调试日志 ---
        // 取消注释以在 A* 搜索期间验证可步行性检查
        // if (!isRoad && trackedObjects.ContainsKey(pos)) Debug.Log($"IsTileWalkableForPath({pos}): False (对象存在但不是 1x1 道路: 类型={record.type}, 尺寸={record.size})");
        // else if (!isRoad) Debug.Log($"IsTileWalkableForPath({pos}): False (没有跟踪的对象或不是道路)");
        // else Debug.Log($"IsTileWalkableForPath({pos}): True");
        // --- 结束调试日志 ---

        return isRoad;
    }

    // CalculateTotalPathTime - 辅助方法，计算找到的路径的总成本 (用于日志记录)
    float CalculateTotalPathTime(List<Vector2Int> path)
    {
        if (path == null || path.Count == 0) return 0f; // 处理空路径或单点路径

        float totalTime = 0f;
        // 从第一个节点开始累加成本，因为 GetPathfindingCost 代表进入该节点的成本
        for (int i = 0; i < path.Count; i++)
        {
            // 使用 A* 使用的相同函数获取成本
            float cost = GetPathfindingCost(path[i]);
            if (float.IsPositiveInfinity(cost))
            {
                // 这理论上不应该发生，因为 A* 找到的路径应该只包含可通行的节点
                Debug.LogError($"CalculateTotalPathTime: 路径包含不可通行的瓦片 {path[i]}！这表明 A* 算法或成本函数可能存在问题。");
                return float.PositiveInfinity; // 返回无穷大表示错误
            }
            totalTime += cost;
        }
        // 注意: 如果你只想计算从起点移动到终点的成本总和（不包括起点的进入成本），
        // 可以从 i = 1 开始循环。当前实现包含了路径上所有节点的进入成本。
        return totalTime;
    }


    // HighlightPath - 将标准道路预设体替换为给定路径上的高亮预设体
    void HighlightPath(List<Vector2Int> path)
    {
        // --- 必要的预设体检查 ---
        if (highlightedRoadPrefab == null) {
            Debug.LogError("无法高亮：Highlighted Road Prefab 未分配！");
            return;
        }
        if (roadPrefab == null) {
            // 需要它在 ClearHighlightedPathVisualsOnly 中恢复，提前检查。
            Debug.LogError("无法高亮：Standard Road Prefab 未分配 (后续清除高亮时需要)！");
            return;
        }

        // 1. 首先清除任何 *先前* 的视觉高亮。
        //    这将之前高亮的瓦片恢复为标准道路预设体。
        //    它使用上次调用时跟踪的 'highlightedPathInstances' 列表。
        ClearHighlightedPathVisualsOnly(); // 确保视觉上从干净状态开始

        // 2. 更新内部列表，存储 *当前* 期望路径的位置。
        currentHighlightedPath = (path != null) ? new List<Vector2Int>(path) : new List<Vector2Int>();

        // 如果新路径为空，则无需进行视觉高亮
        if (currentHighlightedPath.Count == 0) {
            // Debug.Log("HighlightPath: 新路径为空，无需视觉高亮。");
            return;
        }

        // Debug.Log($"HighlightPath: 正在高亮 {currentHighlightedPath.Count} 个瓦片。");

        // 3. 遍历 *新* 路径并应用高亮。
        //    'highlightedPathInstances' 列表在此处从头开始重新构建。
        foreach (Vector2Int pos in currentHighlightedPath)
        {
            // 检查是否存在对应的道路记录，并且实例有效
            if (trackedObjects.TryGetValue(pos, out BuildingRecord record) &&
                record.type == ZoneType.Road && record.size == Vector2Int.one &&
                record.instance != null)
            {
                GameObject currentInstance = record.instance;

                // 通过名称约定检查它是否 *已经是* 高亮预设体
                // (假设 HighlightPath 将实例命名为 "HighlightedRoad_X_Y")
                bool alreadyHighlighted = currentInstance.name.StartsWith("HighlightedRoad_");

                if (alreadyHighlighted)
                {
                    // 它已经是高亮的 (可能来自上一次更新)。
                    // 只需确保它被跟踪在 *新的* 高亮实例列表中。
                    if (!highlightedPathInstances.Contains(currentInstance))
                    {
                         highlightedPathInstances.Add(currentInstance);
                    }
                    // 无需视觉更改。
                }
                else // 它当前是标准道路预设体，需要替换。
                {
                    Vector3 position = currentInstance.transform.position;
                    Quaternion rotation = currentInstance.transform.rotation;

                    // 实例化高亮版本
                    GameObject newHighlightInstance = Instantiate(highlightedRoadPrefab, position, rotation, mapParent);
                    // 使用一致的命名方案以便稍后识别
                    newHighlightInstance.name = $"HighlightedRoad_{pos.x}_{pos.y}";

                    // --- 关键顺序 ---
                    // 1. 更新记录以指向 *新* 的实例
                    record.instance = newHighlightInstance;
                    // 2. 将 *新* 的实例添加到我们用于 *当前* 高亮状态的跟踪列表
                    highlightedPathInstances.Add(newHighlightInstance);
                    // 3. 在记录更新 *之后* 销毁 *旧* 的 (标准道路) 实例
                    DestroyObjectInstance(currentInstance);
                    // --- 结束关键顺序 ---

                    // Debug.Log($"HighlightPath: 将 {pos} 处的道路替换为高亮版本 '{newHighlightInstance.name}'。");
                }
            }
            else
            {
                // 记录无法高亮的原因
                 string reason = "未知原因";
                 if(!trackedObjects.ContainsKey(pos)) reason = "找不到跟踪记录";
                 else if(trackedObjects.TryGetValue(pos, out BuildingRecord r)) {
                     if(r.type != ZoneType.Road) reason = "类型不是道路";
                     else if(r.size != Vector2Int.one) reason = "尺寸不是 1x1";
                     else if(r.instance == null) reason = "实例为空";
                 }
                Debug.LogWarning($"HighlightPath: 无法在 {pos} 处找到有效的道路记录或实例来高亮。原因: {reason}");
            }
        }
         // Debug.Log($"HighlightPath: 完成。正在跟踪 {highlightedPathInstances.Count} 个高亮实例。");
    }


    // ClearHighlightedPath - 清除视觉效果和路径选择状态
    void ClearHighlightedPath()
    {
        // 清除视觉高亮和路径选择状态。
        // Debug.Log("ClearHighlightedPath: 清除视觉效果和路径选择状态。");
        ClearHighlightedPathVisualsOnly(); // 首先恢复视觉效果
        currentHighlightedPath.Clear();    // 清除逻辑路径列表
        pathStartPoint = null;             // 清除选择状态
        pathEndPoint = null;
        // OnGUI 会根据空的起点/终点自动更新
    }

    // ClearHighlightedPathVisualsOnly - 仅将高亮道路恢复为标准道路，保留选择状态
    void ClearHighlightedPathVisualsOnly()
    {
        // 仅根据 *上一次* 高亮操作的 'highlightedPathInstances' 列表将高亮道路视觉效果恢复为标准道路。
        // 不会清除 pathStartPoint/pathEndPoint 或 currentHighlightedPath。
        if (roadPrefab == null) {
            Debug.LogError("无法清除高亮视觉效果：Road Prefab 未分配！");
            return;
        }
        if (highlightedPathInstances.Count == 0) {
            // Debug.Log("ClearHighlightedPathVisualsOnly: 没有跟踪的高亮实例需要清除。");
            return; // 无事可做
        }

        // Debug.Log($"ClearHighlightedPathVisualsOnly: 正在恢复 {highlightedPathInstances.Count} 个跟踪的高亮实例。");

        // 创建一个副本进行迭代，因为我们可能会间接修改原始列表 (通过记录更改/潜在删除)
        List<GameObject> instancesToRevert = new List<GameObject>(highlightedPathInstances);
        highlightedPathInstances.Clear(); // 立即清除主跟踪列表 - 如果需要，HighlightPath 会重新构建它

        foreach (GameObject highlightedInstance in instancesToRevert)
        {
            if (highlightedInstance == null) continue; // 实例可能已被其他地方销毁

            // 使用名称解析来查找此实例所 *代表* 的位置。
            if (TryParsePositionFromName(highlightedInstance.name, out Vector2Int pos))
            {
                // 检查此位置是否仍存在记录
                if (trackedObjects.TryGetValue(pos, out BuildingRecord record))
                {
                    // 重要：检查记录是否 *仍然* 指向我们即将移除的高亮实例。
                    // 可能 HighlightPath 被快速再次调用，或者对象被生成过程替换了。
                    if (record.instance == highlightedInstance)
                    {
                        Vector3 position = highlightedInstance.transform.position;
                        Quaternion rotation = highlightedInstance.transform.rotation;

                        // 实例化原始道路预设体
                        GameObject originalInstance = Instantiate(roadPrefab, position, rotation, mapParent);
                        // 使用标准命名方案
                        originalInstance.name = $"{roadPrefab.name}_{pos.x}_{pos.y}";

                        // --- 关键顺序 ---
                        // 1. 更新记录以指回标准道路实例
                        record.instance = originalInstance;
                        // 2. 在记录更新 *之后* 销毁高亮实例
                        DestroyObjectInstance(highlightedInstance);
                        // --- 结束关键顺序 ---

                        // Debug.Log($"ClearHighlightedPathVisualsOnly: 将 {pos} 处的高亮道路恢复为标准 '{originalInstance.name}'。");
                    }
                    else
                    {
                        // 记录指向其他东西。此高亮实例是孤立的/过时的。直接销毁它。
                        Debug.LogWarning($"ClearHighlightedPathVisualsOnly: {pos} 处的记录不再指向高亮实例 '{highlightedInstance.name}' (现在指向 '{record.instance?.name}')。销毁孤立高亮。");
                        DestroyObjectInstance(highlightedInstance);
                    }
                }
                else
                {
                    // 找不到此位置的记录？如果高亮有效，这不应该发生。销毁孤立对象。
                     Debug.LogError($"ClearHighlightedPathVisualsOnly: 无法找到从 '{highlightedInstance.name}' 派生的位置 {pos} 的 BuildingRecord。销毁孤立高亮。");
                     DestroyObjectInstance(highlightedInstance);
                }
            }
            else
            {
                // 无法从名称解析位置。这是意外情况。直接销毁对象。
                Debug.LogWarning($"ClearHighlightedPathVisualsOnly: 无法从名称 '{highlightedInstance.name}' 解析位置。销毁对象。");
                DestroyObjectInstance(highlightedInstance);
            }
        }
         // 此时 highlightedPathInstances 列表已为空。
         // Debug.Log("ClearHighlightedPathVisualsOnly: 完成实例恢复。");
    }


    // IsPrefabSource - 辅助方法 (现在通过名称检查不太重要，主要用于编辑器检查)
    bool IsPrefabSource(GameObject instance, GameObject prefab)
    {
        if (instance == null || prefab == null) return false;

#if UNITY_EDITOR
        // 编辑器检查使用 PrefabUtility (在编辑器中可靠)
         if (!Application.isPlaying) // 仅在非播放模式下使用，避免运行时开销和错误
         {
             // Try-catch block added for safety, as GetCorrespondingObjectFromSource can sometimes throw exceptions
             try
             {
                 GameObject sourcePrefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(instance);
                 return sourcePrefab == prefab;
             }
             catch (Exception e)
             {
                 Debug.LogWarning($"IsPrefabSource (Editor Check) failed for instance '{instance.name}': {e.Message}");
                 return false; // Assume false if an error occurs during check
             }
         }
#endif
        // 运行时检查不太可靠。Highlight/Clear 方法中的名称检查是首选。
        // 如果在其他地方需要，可以根据名称前缀提供基本猜测。
        // Debug.LogWarning("运行时 IsPrefabSource 检查可能不可靠。尽可能使用直接名称检查。");
        return instance.name.StartsWith(prefab.name); // 保留基于名称的基本检查
    }

#endregion // 结束 Pathfinding and Highlighting 区域

} // --- MapGenerator 类结束 ---

