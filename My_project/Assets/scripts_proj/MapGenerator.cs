using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class BuildingRecord
{
    public GameObject instance;
    public Vector2Int size;
    public MapGenerator.ZoneType type;
    public Quaternion rotation;

    public BuildingRecord(GameObject instance, Vector2Int size, MapGenerator.ZoneType type, Quaternion rotation)
    {
        this.instance = instance;
        this.size = size;
        this.type = type;
        this.rotation = rotation;
    }
}

/// <summary>
/// Generates a city layout in stages, with manual triggers for building placement.
/// Stage 1: Generates road network (logical & visual) using ONE road prefab.
/// Stage 2-4 (Manual Triggers): Fills available non-road areas (3x3, 2x2, 1x1).
/// Placement attempts to align the collider's bottom-left corner with the grid,
/// compensating for potential pivot point inaccuracies (assumes BoxCollider).
/// CRITICAL ASSUMPTION: Prefabs have *some* Collider (ideally BoxCollider) and
/// their visual/collider size *matches* their logical grid size (1x1, 2x2, 3x3).
/// </summary>
public class MapGenerator : MonoBehaviour
{
    #region Public Inspector Variables

    [Header("Prefabs & Parent")]
    // --- PREFAB NOTES ---
    // - Pivot: While the script tries to compensate, correct corner pivot (0,0,0) is best.
    // - Collider: Must have a Collider, BoxCollider preferred for accurate alignment.
    // - Visual/Collider Size: MUST exactly match the logical grid size (1x1, 2x2, 3x3 world units).
    // ---

    [Tooltip("重要: 用于所有道路瓦片的预设体。应有 BoxCollider 且尺寸严格匹配 1x1。")]
    [SerializeField] private GameObject roadPrefab;

    [Tooltip("3x3 建筑预设体列表。应有 BoxCollider 且尺寸严格匹配 3x3。")]
    public List<GameObject> buildingPrefabs3x3;
    [Tooltip("2x2 建筑预设体列表。应有 BoxCollider 且尺寸严格匹配 2x2。")]
    public List<GameObject> buildingPrefabs2x2;
    [Tooltip("1x1 建筑预设体列表。应有 BoxCollider 且尺寸严格匹配 1x1。")]
    public List<GameObject> buildingPrefabs1x1;

    [Tooltip("所有生成对象的父级变换。")]
    public Transform mapParent;

    [Header("Map Dimensions")]
    [Min(10)] public int mapWidth = 50;
    [Min(10)] public int mapHeight = 50;

    [Header("Voronoi Road Network Options")]
    [Min(2)] public int voronoiSiteSpacing = 15;
    [Range(0, 10)] public int voronoiSiteJitter = 3;

    [Header("Noise Branch Connection Options")]
    public float branchNoiseScale = 10.0f;
    [Range(0.0f, 1.0f)] public float noiseBranchThreshold = 0.8f;

    [Header("Map Edge Connection")]
    public bool ensureEdgeConnections = true;

    [Header("Performance & Features")]
    public Vector2 noiseOffset = Vector2.zero;
    [Tooltip("异步生成时每帧激活多少非活动的游戏对象。")]
    [Min(1)] public int objectsPerFrame = 200;
    [Tooltip("异步模式下，循环中处理多少次迭代/瓦片后让出控制权。")]
    [Range(100, 10000)] public int yieldBatchSize = 500;
    public bool asyncGeneration = true;

    #endregion

    #region Private Variables
    public enum ZoneType { Empty, Road, Residential, Commercial, Industrial }
    private ZoneType[,] map;
    private Queue<GameObject> generationQueue = new Queue<GameObject>();

    private Dictionary<Vector2Int, BuildingRecord> trackedObjects = new Dictionary<Vector2Int, BuildingRecord>();
    private Dictionary<Vector2Int, Vector2Int> occupiedTiles = new Dictionary<Vector2Int, Vector2Int>();

    private readonly List<Vector2Int> directions = new List<Vector2Int> { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };

    // Generation Data
    private List<Vector2Int> voronoiSites = new List<Vector2Int>();
    private HashSet<Vector2Int> roadTiles = new HashSet<Vector2Int>();
    private List<Vector2Int> noisePoints = new List<Vector2Int>();

    private bool prefabsValidated = false;
    private Coroutine instantiationCoroutine = null;
    private Coroutine mainGenerationCoroutine = null;

    #endregion

    #region Unity Lifecycle Methods
    void Start()
    {
        if (mainGenerationCoroutine == null)
        {
            Debug.Log("按 Play 按钮旁边的 '开始生成' 按钮启动，或右键点击脚本 Inspector 选择 '开始生成'。");
        }
    }

    [ContextMenu("开始生成")]
    public void StartGenerationProcess()
    {
        if (mainGenerationCoroutine != null)
        {
            Debug.LogWarning("生成已在进行中！");
            return;
        }
        mainGenerationCoroutine = StartCoroutine(RunGenerationPipeline());
    }

    IEnumerator RunGenerationPipeline()
    {
        if (mapParent == null)
        {
            GameObject parentObj = GameObject.Find("GeneratedCity");
            if (parentObj == null) parentObj = new GameObject("GeneratedCity");
            mapParent = parentObj.transform;
        }

        ClearPreviousGeneration();

        ValidatePrefabs();
        if (!prefabsValidated)
        {
            Debug.LogError("必需的预设体缺失或配置不正确。请在 Inspector 中分配并修复。中止生成。");
            mainGenerationCoroutine = null;
            yield break;
        }
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            Debug.LogError("地图尺寸必须大于 0。");
            mainGenerationCoroutine = null;
            yield break;
        }

        map = new ZoneType[mapWidth, mapHeight];
        if (noiseOffset == Vector2.zero)
            noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));

        Debug.Log("--- 开始生成 ---");
        yield return StartCoroutine(GenerateRoadsPhase());

        Debug.Log("--- 道路生成完毕 --- 等待输入以放置 3x3 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 3x3 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(3));
        StartObjectInstantiation(true); if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 3x3 放置完毕 --- 等待输入以放置 2x2 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 2x2 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(2));
        StartObjectInstantiation(true); if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 2x2 放置完毕 --- 等待输入以放置 1x1 建筑...");
        yield return WaitForInput();
        Debug.Log("--- 开始放置 1x1 建筑 ---");
        yield return StartCoroutine(FillSpecificSizeGreedy(1));
        StartObjectInstantiation(true); if (instantiationCoroutine != null) yield return instantiationCoroutine;

        Debug.Log("--- 所有阶段完成！---");
        mainGenerationCoroutine = null;
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
        bool essentialRoadsOk = roadPrefab != null;
        bool essentialBuildingsOk = (buildingPrefabs1x1 != null && buildingPrefabs1x1.Any(p => p != null)) ||
                                    (buildingPrefabs2x2 != null && buildingPrefabs2x2.Any(p => p != null)) ||
                                    (buildingPrefabs3x3 != null && buildingPrefabs3x3.Any(p => p != null));

        if (!essentialRoadsOk) Debug.LogError("道路预设体 (Road Prefab) 缺失！");
        if (!essentialBuildingsOk) Debug.LogError("必须至少分配一种尺寸（1x1、2x2 或 3x3）的有效建筑预设体！");

        Action<GameObject, string, Vector2Int> checkPrefabSetup = (prefab, name, expectedSize) =>
        {
            if (prefab != null)
            {
                if (prefab.transform.position != Vector3.zero)
                    Debug.LogWarning($"预设体 '{name}' ({prefab.name}) 根变换位置非零 ({prefab.transform.position})。虽然脚本尝试补偿，但推荐轴心点为局部 (0,0,0) 且根位置为 (0,0,0)。", prefab);
                if (prefab.transform.localScale != Vector3.one)
                    Debug.LogWarning($"预设体 '{name}' ({prefab.name}) 根变换缩放不是单位尺寸 ({prefab.transform.localScale})。根缩放应为 (1,1,1)。", prefab);

                Collider col = prefab.GetComponent<Collider>();
                if (col == null)
                {
                    Debug.LogError($"预设体 '{name}' ({prefab.name}) 缺少 Collider 组件！脚本无法对其进行精确对齐。", prefab);
                    return;
                }

                if (col is BoxCollider boxCol)
                {
                    if (Mathf.Abs(boxCol.size.x - expectedSize.x) > 0.01f || Mathf.Abs(boxCol.size.z - expectedSize.y) > 0.01f)
                        Debug.LogWarning($"预设体 '{name}' ({prefab.name}) 的 BoxCollider Size ({boxCol.size}) 可能未精确匹配其逻辑尺寸 ({expectedSize.x}x{expectedSize.y})。", prefab);

                    float expectedCenterX = expectedSize.x / 2.0f;
                    float expectedCenterZ = expectedSize.y / 2.0f;
                    if (Mathf.Abs(boxCol.center.x - expectedCenterX) > 0.01f || Mathf.Abs(boxCol.center.z - expectedCenterZ) > 0.01f)
                        Debug.LogWarning($"预设体 '{name}' ({prefab.name}) 的 BoxCollider Center ({boxCol.center}) 可能未精确匹配其逻辑尺寸 (对于角点轴心，应为 ({expectedCenterX}, y, {expectedCenterZ}))。", prefab);
                }
                else
                {
                    Debug.LogWarning($"预设体 '{name}' ({prefab.name}) 使用的不是 BoxCollider。脚本的基于 BoxCollider 的角点对齐可能不精确。", prefab);
                }
            }
        };

        checkPrefabSetup(roadPrefab, "Road Prefab", Vector2Int.one);
        buildingPrefabs1x1?.ForEach(p => checkPrefabSetup(p, "Building 1x1", Vector2Int.one));
        buildingPrefabs2x2?.ForEach(p => checkPrefabSetup(p, "Building 2x2", Vector2Int.one * 2));
        buildingPrefabs3x3?.ForEach(p => checkPrefabSetup(p, "Building 3x3", Vector2Int.one * 3));

        prefabsValidated = essentialRoadsOk && essentialBuildingsOk;

        if (buildingPrefabs3x3 == null || !buildingPrefabs3x3.Any(p => p != null)) Debug.LogWarning("未分配有效的 3x3 建筑预设体。");
        if (buildingPrefabs2x2 == null || !buildingPrefabs2x2.Any(p => p != null)) Debug.LogWarning("未分配有效的 2x2 建筑预设体。");
        if (buildingPrefabs1x1 == null || !buildingPrefabs1x1.Any(p => p != null)) Debug.LogWarning("未分配有效的 1x1 建筑预设体。");
    }
    #endregion

    #region Road Generation Phase
    IEnumerator GenerateRoadsPhase()
    {
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        Debug.Log("--- 开始道路生成阶段 ---");

        Debug.Log("步骤 1 & 2: 生成逻辑道路网络...");
        yield return StartCoroutine(GenerateVoronoiSites());
        yield return StartCoroutine(SelectNoisePoints());
        if (voronoiSites.Count >= 2) yield return StartCoroutine(ComputeVoronoiEdgesAndMarkRoads());
        if (noisePoints.Count > 0 && (roadTiles.Count > 0 || voronoiSites.Count > 0)) yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());
        if (ensureEdgeConnections && (roadTiles.Count > 0 || voronoiSites.Count > 0)) yield return StartCoroutine(EnsureMapEdgeConnections());

        Debug.Log("步骤 3: 优化逻辑道路网络...");
        yield return StartCoroutine(EnsureRoadConnectivity());

        StartObjectInstantiation();

        Debug.Log("步骤 4: 实例化道路视觉效果...");
        yield return StartCoroutine(InstantiateRoadVisuals());
        StartObjectInstantiation(true);
        if (instantiationCoroutine != null) yield return instantiationCoroutine;

        timer.Stop();
        Debug.Log($"--- 道路生成阶段完成 ({timer.ElapsedMilliseconds} 毫秒) ---");
    }

    #region Step 1: Voronoi & Noise Point Generation
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

                if (!voronoiSites.Any(site => Mathf.Abs(site.x - p.x) <= 1 && Mathf.Abs(site.y - p.y) <= 1))
                {
                    if (IsInMap(p) && map[p.x, p.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(p))
                    {
                        voronoiSites.Add(p);
                    }
                }
                count++;
                if (asyncGeneration && count % yieldBatchSize == 0) yield return null;
            }
        }

        if (voronoiSites.Count < 4 && mapWidth > 10 && mapHeight > 10)
        {
            Debug.Log("添加默认角点站点。");
            List<Vector2Int> corners = new List<Vector2Int> {
                new Vector2Int(Mathf.Clamp(spacing/2, 0, mapWidth - 1), Mathf.Clamp(spacing/2, 0, mapHeight - 1)),
                new Vector2Int(Mathf.Clamp(mapWidth-1-spacing/2, 0, mapWidth - 1), Mathf.Clamp(spacing/2, 0, mapHeight - 1)),
                new Vector2Int(Mathf.Clamp(spacing/2, 0, mapWidth - 1), Mathf.Clamp(mapHeight-1-spacing/2, 0, mapHeight - 1)),
                new Vector2Int(Mathf.Clamp(mapWidth-1-spacing/2, 0, mapWidth - 1), Mathf.Clamp(mapHeight-1-spacing/2, 0, mapHeight - 1))
            };
            foreach (var corner in corners)
            {
                if (!voronoiSites.Any(site => (site - corner).sqrMagnitude < spacing * spacing * 0.25f) &&
                    IsInMap(corner) && map[corner.x, corner.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(corner))
                {
                    voronoiSites.Add(corner);
                }
            }
            voronoiSites = voronoiSites.Distinct().ToList();
        }
        Debug.Log($"生成了 {voronoiSites.Count} 个 Voronoi 站点。");
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
                    float nV = Mathf.PerlinNoise(nX, nY);

                    if (nV > noiseBranchThreshold)
                    {
                        if (map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentPos))
                            noisePoints.Add(currentPos);
                    }
                }
                checkedCount++;
                if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null;
            }
        }
        Debug.Log($"选择了 {noisePoints.Count} 个噪声点。");
    }
    #endregion

    #region Step 2: Initial Logical Road Network
    IEnumerator ComputeVoronoiEdgesAndMarkRoads()
    {
        if (voronoiSites.Count < 2) { yield break; }
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
                    if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
                    continue;
                }

                int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites);
                if (nearestSiteIndex < 0) continue;

                foreach (var dir in directions)
                {
                    Vector2Int neighborPos = currentPos + dir;
                    if (!IsInMap(neighborPos)) continue;
                    int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites);
                    if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex)
                    {
                        bool claimed = false;
                        TryClaimTileForRoadLogicOnly(currentPos, out claimed);
                        if (claimed) marked++;
                        break;
                    }
                }
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
            }
        }
        Debug.Log($"标记了 {marked} 个初始 Voronoi 道路瓦片。");
    }

    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites)
    {
        if (sites == null || sites.Count == 0) return -1;
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
        if (noisePoints.Count == 0) yield break;
        HashSet<Vector2Int> anchorNodes = new HashSet<Vector2Int>(roadTiles);
        anchorNodes.UnionWith(voronoiSites.Where(site => IsInMap(site)));
        if (anchorNodes.Count == 0)
        {
            Debug.LogWarning("无法连接噪声点 - 未找到锚点。");
            yield break;
        }

        int pathsDrawn = 0;
        int totalClaimed = 0;

        foreach (var noiseStart in noisePoints)
        {
            if (!IsInMap(noiseStart) || roadTiles.Contains(noiseStart) || occupiedTiles.ContainsKey(noiseStart) || map[noiseStart.x, noiseStart.y] != ZoneType.Empty) continue;

            Vector2Int? nearestAnchor = FindNearestPointInSet(noiseStart, anchorNodes);
            if (nearestAnchor.HasValue)
            {
                List<Vector2Int> path = FindPath(noiseStart, new HashSet<Vector2Int> { nearestAnchor.Value });
                int claimedOnThisPath = 0;
                if (path != null && path.Count > 0)
                {
                    foreach (var roadPos in path)
                    {
                        if (!IsInMap(roadPos)) continue;
                        bool claimed = false;
                        yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                        if (claimed)
                        {
                            claimedOnThisPath++;
                            anchorNodes.Add(roadPos);
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"A* 未能连接噪声点 {noiseStart} 到 {nearestAnchor.Value}。");
                }
                if (claimedOnThisPath > 0) totalClaimed += claimedOnThisPath;
            }
            pathsDrawn++;
            if (asyncGeneration && pathsDrawn % 20 == 0) yield return null;
        }
        Debug.Log($"尝试连接 {pathsDrawn} 个噪声点，新增 {totalClaimed} 个道路瓦片 (A*)。");
    }

    Vector2Int? FindNearestPointInList(Vector2Int startPoint, List<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) return null;
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

    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) return null;
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
        List<Vector2Int> edgeAnchors = new List<Vector2Int> {
            new Vector2Int(mapWidth / 2, mapHeight - 1), new Vector2Int(mapWidth / 2, 0),
            new Vector2Int(0, mapHeight / 2), new Vector2Int(mapWidth - 1, mapHeight / 2),
            new Vector2Int(1, 1), new Vector2Int(mapWidth - 2, 1),
            new Vector2Int(1, mapHeight - 2), new Vector2Int(mapWidth - 2, mapHeight - 2)
        };
        edgeAnchors = edgeAnchors.Where(IsInMap).Distinct().ToList();
        int connectionsMade = 0;
        HashSet<Vector2Int> currentNetworkPoints = new HashSet<Vector2Int>(roadTiles);
        currentNetworkPoints.UnionWith(voronoiSites.Where(site => IsInMap(site)));
        if (currentNetworkPoints.Count == 0)
        {
            Debug.LogWarning("跳过边缘连接：无网络。");
            yield break;
        }

        foreach (var edgePoint in edgeAnchors)
        {
            if (currentNetworkPoints.Contains(edgePoint)) continue;
            Vector2Int? connectFrom = FindNearestPointInSet(edgePoint, currentNetworkPoints);
            if (connectFrom.HasValue)
            {
                if (!roadTiles.Contains(edgePoint))
                {
                    bool success = false;
                    yield return StartCoroutine(ConnectTwoPoints(connectFrom.Value, edgePoint, result => success = result));
                    if (success) connectionsMade++;
                }
            }
            else
            {
                Debug.LogWarning($"未找到连接边缘点 {edgePoint} 的网络点。");
            }
            if (asyncGeneration) yield return null;
        }
        Debug.Log($"尝试/完成 {connectionsMade} 个边缘连接。");
    }

    IEnumerator ConnectTwoPoints(Vector2Int start, Vector2Int end, System.Action<bool> callback)
    {
        List<Vector2Int> path = FindPath(start, new HashSet<Vector2Int> { end });
        int appliedCount = 0;
        bool success = false;
        bool pathBlockedMidway = false;

        if (path != null && path.Count > 1)
        {
            for (int i = 0; i < path.Count; ++i)
            {
                Vector2Int roadPos = path[i];
                if (!IsInMap(roadPos)) continue;
                bool claimed = false;
                if (!roadTiles.Contains(roadPos))
                {
                    yield return StartCoroutine(TryClaimTileForRoad(roadPos, result => claimed = result));
                }
                else
                {
                    claimed = true;
                }
                if (claimed)
                {
                    appliedCount++;
                }
                else
                {
                    if (roadPos != start && roadPos != end)
                    {
                        Debug.LogWarning($"路径段 {roadPos} 无法声明。路径阻塞？");
                        pathBlockedMidway = true;
                    }
                }
                if (asyncGeneration && i > 0 && i % 100 == 0) yield return null;
            }
            success = (appliedCount > 0 || roadTiles.Contains(end)) && !pathBlockedMidway;
        }
        else if (path != null && path.Count == 1 && path[0] == end && roadTiles.Contains(end))
        {
            success = true;
        }
        else
        {
            Debug.LogWarning($"A* 路径在 {start} 和 {end} 之间失败。");
            success = false;
        }

        if (asyncGeneration && appliedCount > 0) yield return null;
        callback?.Invoke(success);
    }

    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet)
    {
        Vector2Int current = start;
        pathTilesSet.Add(current);
        int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x);
        while (current.x != end.x)
        {
            current.x += xDir;
            if (!IsInMap(current)) return;
            pathTilesSet.Add(current);
        }
        int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y);
        while (current.y != end.y)
        {
            current.y += yDir;
            if (!IsInMap(current)) return;
            pathTilesSet.Add(current);
        }
    }
    #endregion

    #region Step 3: Road Network Refinement
    IEnumerator EnsureRoadConnectivity()
    {
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();
        if (roadComponents.Count <= 1) { yield break; }
        Debug.Log($"道路网络连通性检查：找到 {roadComponents.Count} 个组件。尝试连接...");
        roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList();
        HashSet<Vector2Int> mainNetwork = roadComponents[0];
        int connectionsMade = 0;
        int componentsMerged = 1;
        for (int i = 1; i < roadComponents.Count; i++)
        {
            HashSet<Vector2Int> currentComponent = roadComponents[i];
            if (currentComponent.Count == 0) continue;
            bool connected = false;
            yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork, result => connected = result));
            if (connected)
            {
                connectionsMade++;
                componentsMerged++;
                mainNetwork.UnionWith(currentComponent);
            }
            else
            {
                Debug.LogWarning($"未能将组件 {i + 1} ({currentComponent.Count} 瓦片) 连接到主网络。");
            }
            if (asyncGeneration && i % 5 == 0) yield return null;
        }
        Debug.Log($"连通性检查完成。合并了 {componentsMerged}/{roadComponents.Count} 个组件 ({connectionsMade} 连接)。");
    }

    List<HashSet<Vector2Int>> FindAllRoadComponents()
    {
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>();
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        HashSet<Vector2Int> currentRoadTilesSnapshot = new HashSet<Vector2Int>(roadTiles);
        foreach (Vector2Int startPos in currentRoadTilesSnapshot)
        {
            if (!visited.Contains(startPos) && roadTiles.Contains(startPos))
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
                        if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            newComponent.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
                if (newComponent.Count > 0) components.Add(newComponent);
            }
        }
        return components;
    }

    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork, System.Action<bool> callback)
    {
        if (componentToConnect == null || targetNetwork == null || componentToConnect.Count == 0 || targetNetwork.Count == 0)
        {
            callback?.Invoke(false);
            yield break;
        }
        Vector2Int? bestStart = null;
        Vector2Int? bestTarget = null;
        float minDistanceSq = float.MaxValue;
        HashSet<Vector2Int> searchSet = (componentToConnect.Count < targetNetwork.Count) ? componentToConnect : targetNetwork;
        HashSet<Vector2Int> destinationSet = (searchSet == componentToConnect) ? targetNetwork : componentToConnect;
        int maxSearchPoints = 500;
        var pointsToSearch = searchSet.Count <= maxSearchPoints ? searchSet : searchSet.OrderBy(p => UnityEngine.Random.value).Take(maxSearchPoints);
        int searched = 0;

        foreach (var startCandidate in pointsToSearch)
        {
            Vector2Int? currentNearestTarget = FindNearestPointInSet(startCandidate, destinationSet);
            if (currentNearestTarget.HasValue)
            {
                float distSq = (startCandidate - currentNearestTarget.Value).sqrMagnitude;
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    bestStart = (searchSet == componentToConnect) ? startCandidate : currentNearestTarget.Value;
                    bestTarget = (searchSet == componentToConnect) ? currentNearestTarget.Value : startCandidate;
                }
            }
            searched++;
            if (minDistanceSq <= 2.0f) break; // Adjacent optimization
            if (asyncGeneration && searched % 50 == 0) yield return null;
        }

        if (!bestStart.HasValue || !bestTarget.HasValue)
        {
            Debug.LogError($"无法找到组件和目标网络间的连接点。");
            callback?.Invoke(false);
            yield break;
        }
        if (minDistanceSq <= 2.0f)
        { // Already adjacent
            callback?.Invoke(true);
            yield break;
        }

        bool connected = false;
        yield return StartCoroutine(ConnectTwoPoints(bestStart.Value, bestTarget.Value, result => connected = result));
        callback?.Invoke(connected);
    }
    #endregion

    #region Step 4: Instantiate Road Visuals
    IEnumerator InstantiateRoadVisuals()
    {
        int roadsProcessed = 0;
        List<Vector2Int> currentRoadTilesSnapshot = new List<Vector2Int>(roadTiles);

        if (roadPrefab == null)
        {
            Debug.LogError("道路预设体 (Road Prefab) 未在 Inspector 中分配！无法实例化道路视觉效果。");
            yield break;
        }

        foreach (Vector2Int pos in currentRoadTilesSnapshot)
        {
            if (!roadTiles.Contains(pos)) continue;
            PlaceObject(pos, Vector2Int.one, roadPrefab, ZoneType.Road, Quaternion.identity);
            roadsProcessed++;
            if (asyncGeneration && roadsProcessed % yieldBatchSize == 0) yield return null;
        }
        Debug.Log($"为 {roadsProcessed} 个道路瓦片处理了视觉效果 (使用单一预设体)。");
    }
    #endregion
    #endregion // End Road Generation Phase

    #region Building Placement Phase
    IEnumerator FillSpecificSizeGreedy(int sizeToFill)
    {
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
            default: Debug.LogError($"无效的建筑尺寸请求: {sizeToFill}"); yield break;
        }

        if (prefabList == null || !prefabList.Any(p => p != null))
        {
            Debug.LogWarning($"没有为尺寸 {sizeToFill}x{sizeToFill} 分配有效的预设体，跳过此阶段。");
            yield break;
        }

        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                tilesChecked++;
                Vector2Int currentOrigin = new Vector2Int(x, y);

                if (IsInMap(currentOrigin) && map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(currentOrigin))
                {
                    if (CanPlaceBuildingHere(currentOrigin, buildingSize))
                    {
                        GameObject prefab = GetRandomValidPrefab(prefabList);
                        if (prefab != null)
                        {
                            PlaceObject(currentOrigin, buildingSize, prefab, DetermineBuildingType(), Quaternion.identity);
                            buildingsPlaced++;
                            // Potential optimization: Skip columns
                            // x += (sizeToFill - 1);
                        }
                    }
                }

                if (asyncGeneration && tilesChecked % (yieldBatchSize * 2) == 0)
                {
                    yield return null;
                }
            }
            if (asyncGeneration && y % 10 == 0)
            {
                yield return null;
            }
        }

        timer.Stop();
        Debug.Log($"填充 {sizeToFill}x{sizeToFill} 建筑：放置了 {buildingsPlaced} 个，耗时 {timer.ElapsedMilliseconds} 毫秒。");
    }

    bool CanPlaceBuildingHere(Vector2Int origin, Vector2Int size)
    {
        if (!IsInMap(origin) || !IsInMap(origin + size - Vector2Int.one)) return false;
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentTile = new Vector2Int(x, y);
                if (!IsInMap(currentTile) || map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentTile))
                {
                    return false;
                }
            }
        }
        return true;
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
        int r = UnityEngine.Random.Range(0, 3);
        if (r == 0) return ZoneType.Residential;
        if (r == 1) return ZoneType.Commercial;
        return ZoneType.Industrial;
    }
    #endregion

    #region Core Logic: Placement, Removal, Claiming
    void PlaceObject(Vector2Int origin, Vector2Int size, GameObject prefab, ZoneType type, Quaternion rotation)
    {
        if (prefab == null) { Debug.LogError($"PlaceObject Error: Prefab is null @ {origin}"); return; }
        if (!IsInMap(origin) || (size != Vector2Int.one && !IsInMap(origin + size - Vector2Int.one)))
        {
            if (size == Vector2Int.one && !IsInMap(origin)) { Debug.LogError($"PlaceObject Error: Origin {origin} for 1x1 object is out of bounds."); return; }
            else if (size != Vector2Int.one) { Debug.LogError($"PlaceObject Error: Footprint {origin} size {size} out of bounds."); return; }
        }

        // 1. Demolition
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int tile = new Vector2Int(x, y);
                if (IsInMap(tile) && (occupiedTiles.ContainsKey(tile) || map[x, y] != ZoneType.Empty))
                {
                    RemoveTrackedObject(tile);
                }
            }
        }

        // Calculate Placement Position based on Collider
        Vector3 instantiationPosition;
        Vector3 targetCornerWorldPos = new Vector3(origin.x, 0, origin.y);
        Vector3 colliderOffsetXZ = Vector3.zero;

        BoxCollider boxCol = prefab.GetComponent<BoxCollider>();
        if (boxCol != null)
        {
            float colliderBottomLeft_X = boxCol.center.x - boxCol.size.x / 2.0f;
            float colliderBottomLeft_Z = boxCol.center.z - boxCol.size.z / 2.0f;
            colliderOffsetXZ = new Vector3(colliderBottomLeft_X, 0, colliderBottomLeft_Z);
        }
        else
        {
            Collider generalCol = prefab.GetComponent<Collider>();
            if (generalCol != null)
            {
                Vector3 boundsMin = generalCol.bounds.min - prefab.transform.position;
                colliderOffsetXZ = new Vector3(boundsMin.x, 0, boundsMin.z);
                Debug.LogWarning($"Prefab {prefab.name} @ {origin}: No BoxCollider. Using general Collider bounds for alignment (Offset={colliderOffsetXZ}). May be inaccurate.", prefab);
            }
            else
            {
                Debug.LogWarning($"Prefab {prefab.name} @ {origin}: No Collider! Placing using pivot only.", prefab);
            }
        }
        instantiationPosition = targetCornerWorldPos - colliderOffsetXZ;

        // 2. Instantiation
        GameObject instance = Instantiate(prefab, instantiationPosition, rotation, mapParent);
        instance.name = $"{prefab.name}_{origin.x}_{origin.y}";
        instance.SetActive(false);

        // 3. Tracking Update
        BuildingRecord record = new BuildingRecord(instance, size, type, rotation);
        trackedObjects[origin] = record;
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentTile = new Vector2Int(x, y);
                if (IsInMap(currentTile))
                {
                    map[x, y] = type;
                    occupiedTiles[currentTile] = origin;
                    roadTiles.Remove(currentTile);
                }
            }
        }

        // 4. Activation Queue
        generationQueue.Enqueue(instance);
        StartObjectInstantiation();
    }

    void RemoveTrackedObject(Vector2Int pos)
    {
        if (!IsInMap(pos)) return;
        Vector2Int originToRemove = pos;
        bool wasOccupied = occupiedTiles.TryGetValue(pos, out Vector2Int foundOrigin);

        if (wasOccupied) originToRemove = foundOrigin;
        else if (!trackedObjects.ContainsKey(pos))
        {
            if (IsInMap(pos) && map[pos.x, pos.y] != ZoneType.Empty) map[pos.x, pos.y] = ZoneType.Empty;
            roadTiles.Remove(pos);
            return;
        }

        if (trackedObjects.TryGetValue(originToRemove, out BuildingRecord recordToRemove))
        {
            Vector2Int size = recordToRemove.size;
            for (int x = originToRemove.x; x < originToRemove.x + size.x; x++)
            {
                for (int y = originToRemove.y; y < originToRemove.y + size.y; y++)
                {
                    Vector2Int currentTile = new Vector2Int(x, y);
                    if (IsInMap(currentTile))
                    {
                        occupiedTiles.Remove(currentTile);
                        map[x, y] = ZoneType.Empty;
                        roadTiles.Remove(currentTile);
                    }
                }
            }
            trackedObjects.Remove(originToRemove);
            if (recordToRemove.instance != null) DestroyObjectInstance(recordToRemove.instance);
        }
        else if (wasOccupied)
        {
            Debug.LogError($"Inconsistency: Tile {pos} occupied by {originToRemove}, but no record found.");
            occupiedTiles.Remove(pos);
            if (IsInMap(pos) && map[pos.x, pos.y] != ZoneType.Empty) map[pos.x, pos.y] = ZoneType.Empty;
            roadTiles.Remove(pos);
        }
    }

    IEnumerator TryClaimTileForRoad(Vector2Int pos, System.Action<bool> callback)
    {
        bool success = false;
        if (IsInMap(pos))
        {
            if (roadTiles.Contains(pos)) success = true;
            else
            {
                bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;
                if (needsDemolition) { RemoveTrackedObject(pos); if (asyncGeneration) yield return null; }
                if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
                {
                    map[pos.x, pos.y] = ZoneType.Road;
                    roadTiles.Add(pos);
                    occupiedTiles[pos] = pos; // Road occupies its own tile logically
                    success = true;
                }
                else if (roadTiles.Contains(pos)) { success = true; } // Became road after demolition?
            }
        }
        callback?.Invoke(success);
    }

    void TryClaimTileForRoadLogicOnly(Vector2Int pos, out bool success)
    {
        success = false;
        if (!IsInMap(pos)) return;
        if (roadTiles.Contains(pos)) { success = true; return; }
        bool needsDemolition = occupiedTiles.ContainsKey(pos) || map[pos.x, pos.y] != ZoneType.Empty;
        if (needsDemolition) RemoveTrackedObject(pos);
        if (IsInMap(pos) && map[pos.x, pos.y] == ZoneType.Empty && !occupiedTiles.ContainsKey(pos))
        {
            map[pos.x, pos.y] = ZoneType.Road;
            roadTiles.Add(pos);
            occupiedTiles[pos] = pos; // Road occupies its own tile logically
            success = true;
        }
        else if (roadTiles.Contains(pos)) { success = true; }
    }
    #endregion

    #region Instantiation & Cleanup
    void StartObjectInstantiation(bool processAllImmediately = false)
    {
        if (instantiationCoroutine != null && !processAllImmediately) return;
        if (instantiationCoroutine != null && processAllImmediately) { StopCoroutine(instantiationCoroutine); instantiationCoroutine = null; }
        bool useAsyncBatching = asyncGeneration && !processAllImmediately;
        instantiationCoroutine = StartCoroutine(ActivateQueuedObjects(!useAsyncBatching));
    }

    IEnumerator ActivateQueuedObjects(bool activateAllImmediately)
    {
        if (generationQueue.Count == 0) { instantiationCoroutine = null; yield break; }
        int processedSinceYield = 0;
        int batchActivationCount = activateAllImmediately ? int.MaxValue : objectsPerFrame;
        int safetyYieldThreshold = activateAllImmediately ? 10000 : yieldBatchSize * 5;
        System.Diagnostics.Stopwatch batchTimer = new System.Diagnostics.Stopwatch();
        float maxFrameTimeMs = 10.0f;

        while (generationQueue.Count > 0)
        {
            batchTimer.Restart();
            int activatedThisBatch = 0;
            while (generationQueue.Count > 0 && activatedThisBatch < batchActivationCount)
            {
                GameObject obj = generationQueue.Dequeue();
                if (ValidateObjectForActivation(obj))
                {
                    obj.SetActive(true);
                    activatedThisBatch++;
                    processedSinceYield++;
                }
                else if (obj != null)
                {
                    DestroyObjectInstance(obj);
                }
                if (!activateAllImmediately && batchTimer.ElapsedMilliseconds > maxFrameTimeMs) break;
            }
            if (!activateAllImmediately || processedSinceYield >= safetyYieldThreshold)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }
        instantiationCoroutine = null;
    }

    bool ValidateObjectForActivation(GameObject obj)
    {
        if (obj == null) return false;
        if (TryParsePositionFromName(obj.name, out Vector2Int origin))
        {
            if (trackedObjects.TryGetValue(origin, out BuildingRecord record) && record.instance == obj) return true;
            else return false;
        }
        else
        {
            Debug.LogWarning($"无法从名称解析位置：{obj.name}。");
            return false;
        }
    }

    void ClearPreviousGeneration()
    {
        Debug.Log("清除上一代...");
        if (mainGenerationCoroutine != null) StopCoroutine(mainGenerationCoroutine);
        if (instantiationCoroutine != null) StopCoroutine(instantiationCoroutine);
        mainGenerationCoroutine = null;
        instantiationCoroutine = null;

        if (mapParent != null)
        {
            for (int i = mapParent.childCount - 1; i >= 0; i--)
            {
                Transform child = mapParent.GetChild(i);
                if (child != null) DestroyObjectInstance(child.gameObject);
            }
        }
        else
        {
            GameObject defaultParent = GameObject.Find("GeneratedCity");
            if (defaultParent != null && defaultParent.transform != null)
            {
                for (int i = defaultParent.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = defaultParent.transform.GetChild(i);
                    if (child != null) DestroyObjectInstance(child.gameObject);
                }
            }
        }

        map = null;
        trackedObjects.Clear();
        occupiedTiles.Clear();
        generationQueue.Clear();
        roadTiles.Clear();
        voronoiSites.Clear();
        noisePoints.Clear();
        prefabsValidated = false;
        Debug.Log("清除完成。");
    }

    void DestroyObjectInstance(GameObject obj)
    {
        if (obj == null) return;
        if (this == null || !this.gameObject.scene.isLoaded)
        {
            #if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () => { if (obj != null) DestroyImmediate(obj); };
                return;
            }
            #endif
            return;
        }
        if (Application.isPlaying)
        {
            Destroy(obj);
        }
        else
        {
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () => { if (obj != null) DestroyImmediate(obj); };
            #else
            Destroy(obj);
            #endif
        }
    }
    #endregion

    #region A* Pathfinding Algorithm
    private class PathNode
    {
        public Vector2Int position;
        public float gScore;
        public float hScore;
        public float fScore => gScore + hScore;
        public PathNode parent;
        public PathNode(Vector2Int pos, float g, float h, PathNode p) { position = pos; gScore = g; hScore = h; parent = p; }
    }

    List<Vector2Int> FindPath(Vector2Int start, HashSet<Vector2Int> targets)
    {
        if (targets == null || targets.Count == 0 || !IsInMap(start)) return null;
        if (targets.Contains(start)) return new List<Vector2Int>() { start };

        List<PathNode> openSet = new List<PathNode>();
        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>();
        Dictionary<Vector2Int, PathNode> cameFrom = new Dictionary<Vector2Int, PathNode>();

        float startHScore = Heuristic(start, targets);
        PathNode startNode = new PathNode(start, 0f, startHScore, null);
        openSet.Add(startNode);
        cameFrom[start] = startNode;

        int iterations = 0;
        int maxIterations = mapWidth * mapHeight * 4;

        while (openSet.Count > 0)
        {
            iterations++;
            if (iterations > maxIterations)
            {
                Debug.LogError($"A* 超出最大迭代 ({maxIterations})。");
                return null;
            }

            openSet.Sort((a, b) => a.fScore.CompareTo(b.fScore));
            PathNode current = openSet[0];
            openSet.RemoveAt(0);

            if (targets.Contains(current.position)) return ReconstructPath(current);

            closedSet.Add(current.position);

            foreach (var dir in directions)
            {
                Vector2Int neighborPos = current.position + dir;
                if (!IsInMap(neighborPos) || closedSet.Contains(neighborPos)) continue;

                float terrainCost = GetTerrainCost(neighborPos);
                if (float.IsPositiveInfinity(terrainCost)) continue;

                float tentativeGScore = current.gScore + terrainCost;
                PathNode neighborNode;
                bool isInOpenSet = cameFrom.TryGetValue(neighborPos, out neighborNode);

                if (!isInOpenSet || tentativeGScore < neighborNode.gScore)
                {
                    float neighborHScore = Heuristic(neighborPos, targets);
                    if (neighborNode == null)
                    {
                        neighborNode = new PathNode(neighborPos, tentativeGScore, neighborHScore, current);
                        cameFrom[neighborPos] = neighborNode;
                        openSet.Add(neighborNode);
                    }
                    else
                    {
                        neighborNode.gScore = tentativeGScore;
                        neighborNode.parent = current;
                    }
                }
            }
        }
        Debug.LogWarning($"A* 未找到路径 {start} -> 目标。");
        return null;
    }

    float GetTerrainCost(Vector2Int pos)
    {
        if (!IsInMap(pos)) return float.PositiveInfinity;
        bool isOccupiedNonOrigin = occupiedTiles.TryGetValue(pos, out Vector2Int origin) && origin != pos;
        if (isOccupiedNonOrigin) return float.PositiveInfinity;

        switch (map[pos.x, pos.y])
        {
            case ZoneType.Road: return 1.0f;
            case ZoneType.Empty: return 5.0f; // A* runs before building placement
            case ZoneType.Residential:
            case ZoneType.Commercial:
            case ZoneType.Industrial:
                // Should not encounter these during road pathfinding if logic is correct
                return float.PositiveInfinity;
            default:
                Debug.LogError($"A* 未知 ZoneType @ {pos}");
                return float.PositiveInfinity;
        }
    }

    float Heuristic(Vector2Int current, HashSet<Vector2Int> targets)
    {
        if (targets == null || targets.Count == 0) return 0;
        float minDistance = float.MaxValue;
        foreach (var target in targets) minDistance = Mathf.Min(minDistance, ManhattanDistance(current, target));
        return minDistance;
    }

    int ManhattanDistance(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    List<Vector2Int> ReconstructPath(PathNode targetNode)
    {
        List<Vector2Int> totalPath = new List<Vector2Int>();
        PathNode current = targetNode;
        int safetyBreak = 0;
        int maxLen = mapWidth * mapHeight + 1;
        while (current != null && safetyBreak < maxLen)
        {
            totalPath.Add(current.position);
            current = current.parent;
            safetyBreak++;
        }
        if (safetyBreak >= maxLen) Debug.LogError($"A* 路径重构超出最大长度！");
        totalPath.Reverse();
        return totalPath;
    }
    #endregion

    #region Auxiliary & Helper Methods
    bool IsInMap(Vector2Int pos)
    {
        return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight;
    }

    bool TryParsePositionFromName(string name, out Vector2Int position)
    {
        position = Vector2Int.zero;
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore < 1 || lastUnderscore == name.Length - 1) return false;
            int secondLastUnderscore = name.LastIndexOf('_', lastUnderscore - 1);
            if (secondLastUnderscore < 0) return false;
            string xStr = name.Substring(secondLastUnderscore + 1, lastUnderscore - secondLastUnderscore - 1);
            string yStr = name.Substring(lastUnderscore + 1);
            if (int.TryParse(xStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(yStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int y))
            {
                position = new Vector2Int(x, y);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"从名称 '{name}' 解析位置出错：{ex.Message}");
        }
        return false;
    }

    void DrawStraightRoadPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet)
    {
        Vector2Int current = start;
        pathTilesSet.Add(current);
        int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x);
        while (current.x != end.x)
        {
            current.x += xDir;
            if (!IsInMap(current)) return;
            pathTilesSet.Add(current);
        }
        int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y);
        while (current.y != end.y)
        {
            current.y += yDir;
            if (!IsInMap(current)) return;
            pathTilesSet.Add(current);
        }
    }
    #endregion

} // End of MapGenerator class