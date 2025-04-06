using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

// Helper class to store info about placed objects (buildings or roads)
public class BuildingRecord
{
    public GameObject instance;
    public Vector2Int size;
    public MapGenerator.ZoneType type; // Store the type (Res, Com, Ind, Road)

    public BuildingRecord(GameObject instance, Vector2Int size, MapGenerator.ZoneType type)
    {
        this.instance = instance;
        this.size = size;
        this.type = type;
    }
}


/// <summary>
/// Generates a city layout using Voronoi roads, MST branches, and places multi-tile buildings.
/// Optimized with Coroutines for smoother generation.
/// </summary>
public class MapGenerator : MonoBehaviour
{
    #region Public Inspector Variables

    [Header("Prefabs & Parent")]
    public GameObject roadPrefab;
    // --- NEW: Building Prefab Lists ---
    [Tooltip("List of 3x3 building prefabs.")]
    public List<GameObject> buildingPrefabs3x3;
    [Tooltip("List of 2x2 building prefabs.")]
    public List<GameObject> buildingPrefabs2x2;
    [Tooltip("List of 1x1 building prefabs.")]
    public List<GameObject> buildingPrefabs1x1;
    // ------------------------------------
    public Transform mapParent;

    [Header("Map Dimensions")]
    public int mapWidth = 50;
    public int mapHeight = 50;

    [Header("Voronoi Road Network Options")]
    public int voronoiSiteSpacing = 15;
    [Range(0, 10)] public int voronoiSiteJitter = 3;

    [Header("Noise Branch Connection Options")]
    public float branchNoiseScale = 10.0f;
    [Range(0.0f, 1.0f)] public float noiseBranchThreshold = 0.8f;

    [Header("Map Edge Connection")]
    public bool ensureEdgeConnections = true;
    public int edgeConnectionSearchRadius = 5;

    [Header("Performance & Features")]
    public Vector2 noiseOffset = Vector2.zero;
    public int objectsPerFrame = 200;
    [Range(100, 5000)] public int yieldBatchSize = 500;
    public bool asyncGeneration = true;

    #endregion

    #region Private Variables
    // --- ZoneType potentially simplified, BuildingPart might not be needed ---
    // Map now primarily stores Road/Empty, or the type *at the origin* of a building.
    // occupiedTiles dictionary becomes the main source for building occupancy info.
    public enum ZoneType { Empty, Road, Residential, Commercial, Industrial } // Removed BuildingPart for now
    private ZoneType[,] map; // Stores fundamental type (Empty, Road, Building Origin Type)
    private Queue<GameObject> generationQueue = new Queue<GameObject>();

    // --- MODIFIED: Stores BuildingRecord for buildings/roads at their origin ---
    private Dictionary<Vector2Int, BuildingRecord> trackedObjects = new Dictionary<Vector2Int, BuildingRecord>();
    // --- NEW: Tracks which building origin occupies any given tile ---
    private Dictionary<Vector2Int, Vector2Int> occupiedTiles = new Dictionary<Vector2Int, Vector2Int>(); // Key: Tile Pos, Value: Building Origin Pos

    private readonly List<Vector2Int> directions;
    private readonly List<Vector2Int> allDirections;

    private List<Vector2Int> voronoiSites = new List<Vector2Int>();
    private HashSet<Vector2Int> roadTiles = new HashSet<Vector2Int>(); // Still useful for quick road checks

    private List<Vector2Int> noisePoints = new List<Vector2Int>();

    #endregion

    public MapGenerator() // Constructor remains the same
    {
        directions = new List<Vector2Int> { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };
        allDirections = new List<Vector2Int> {
            Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down,
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
        };
    }

    #region Unity Lifecycle Methods
    IEnumerator Start()
    {
        // Basic setup (Parent, Clear, Map Init, Noise) remains similar
        if (mapParent == null) { GameObject p = new GameObject("GeneratedCity"); mapParent = p.transform; Debug.LogWarning("Created Map Parent 'GeneratedCity'."); }
        ClearPreviousGeneration();
        if (mapWidth <= 0 || mapHeight <= 0) { Debug.LogError("Map dimensions must be positive."); yield break; }
        map = new ZoneType[mapWidth, mapHeight]; // Initializes to Empty
        if (noiseOffset == Vector2.zero) noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));
        Debug.Log($"Using Noise Offset: {noiseOffset}");

        float startTime = Time.realtimeSinceStartup;
        Debug.Log($"Starting City Generation ({mapWidth}x{mapHeight}). Async: {asyncGeneration}");

        // --- Start Generation Pipeline ---
        yield return StartCoroutine(GenerateCity());

        float endTime = Time.realtimeSinceStartup;
        Debug.Log($"City Generation Complete. Tracked object origins: {trackedObjects.Count}. Occupied tiles: {occupiedTiles.Count}");
        Debug.Log($"Total Generation Time: {(endTime - startTime) * 1000:F2} ms");
    }

    void OnValidate() // Validation remains similar
    {
        if (voronoiSiteSpacing < 1) voronoiSiteSpacing = 1;
        if (mapWidth < 1) mapWidth = 1;
        if (mapHeight < 1) mapHeight = 1;
        if (edgeConnectionSearchRadius < 1) edgeConnectionSearchRadius = 1;
        if (yieldBatchSize < 10) yieldBatchSize = 10;
        if (objectsPerFrame < 1) objectsPerFrame = 1;
    }
    #endregion

    #region Main Generation Pipeline (Coroutine)
    IEnumerator GenerateCity()
    {
        // Step 1: Voronoi Sites (Unchanged)
        Debug.Log("Step 1: Generating Voronoi Sites...");
        yield return StartCoroutine(GenerateVoronoiSites());
        Debug.Log($"Step 1 Complete. Found {voronoiSites.Count} Voronoi sites.");
        if (voronoiSites.Count < 2) { Debug.LogError("Insufficient Voronoi sites."); yield break; }

        // Step 2: Voronoi Edges (Compute & Place Roads)
        Debug.Log("Step 2: Computing Voronoi Edges...");
        yield return StartCoroutine(ComputeVoronoiEdgesAndPlaceRoads()); // Renamed for clarity
        Debug.Log($"Step 2 Complete. Voronoi road tiles: {roadTiles.Count}");
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // Step 3: Select Noise Points (Unchanged)
        Debug.Log($"Step 3: Selecting Noise Points (Threshold: {noiseBranchThreshold})...");
        yield return StartCoroutine(SelectNoisePoints());
        Debug.Log($"Step 3 Complete. Found {noisePoints.Count} noise points.");

        // Step 4: Connect Noise Points via MST (Road Placement logic updated)
        if (noisePoints.Count > 0 && roadTiles.Count > 0)
        {
            Debug.Log("Step 4: Connecting Noise Points via MST...");
            yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());
            Debug.Log($"Step 4 Complete. Road tiles after MST: {roadTiles.Count}");
            if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());
        }
        else Debug.Log("Step 4: Skipped MST Connection.");

        // Step 5: Ensure Map Edge Connections (Road Placement logic updated)
        if (ensureEdgeConnections && (roadTiles.Count > 0 || voronoiSites.Count > 0))
        {
            Debug.Log("Step 5: Ensuring Map Edge Connections...");
            yield return StartCoroutine(EnsureMapEdgeConnections());
            Debug.Log($"Step 5 Complete. Road tiles after edge connection: {roadTiles.Count}");
            if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());
        }
        else Debug.Log("Step 5: Skipped Map Edge Connections.");

        // Step 6: Connectivity Pass 1 (Road Placement logic updated)
        Debug.Log("Step 6: Ensuring Road Connectivity (Pass 1)...");
        yield return StartCoroutine(EnsureRoadConnectivity());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // Step 7: Prune Wide Roads (Removal logic updated)
        Debug.Log("Step 7: Pruning Wide Roads...");
        yield return StartCoroutine(PruneWideRoads());
        Debug.Log($"Step 7 Complete. Road tiles after pruning: {roadTiles.Count}");

        // Step 8: Connectivity Pass 2 (Road Placement logic updated)
        Debug.Log("Step 8: Ensuring Road Connectivity (Pass 2 - Post Pruning)...");
        yield return StartCoroutine(EnsureRoadConnectivity());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 9: Building Placement (Major Change) ---
        Debug.Log("Step 9a: Placing Initial Buildings (Greedy Largest First)...");
        yield return StartCoroutine(PlaceBuildingsGreedy()); // New placement coroutine
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // Step 9b: Ensure Zone Accessibility (Road Placement logic updated)
        Debug.Log("Step 9b: Ensuring Zone Accessibility...");
        yield return StartCoroutine(EnsureZoneAccessibility()); // Logic needs to target building origins/tiles
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 10: REMOVED Remove Isolated Tiles ---
        Debug.Log("Step 10: Skipping Isolated Tile Removal (Not suitable for multi-tile buildings).");

        // --- Step 11: Fill Remaining (Major Change) ---
        Debug.Log("Step 11: Filling Remaining Empty Tiles (Greedy Largest First)...");
        yield return StartCoroutine(FillRemainingEmptyTilesGreedy()); // New placement coroutine
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects()); // Instantiate before final batch

        // --- Step 12: Final Instantiation (Validation Updated) ---
        Debug.Log("Step 12: Instantiating All Remaining Objects...");
        yield return StartCoroutine(InstantiateQueuedObjects(true));

        Debug.Log("--- Generation Pipeline Finished ---");
    }
    #endregion

    #region Voronoi & Noise Point Generation (Largely Unchanged)
    IEnumerator GenerateVoronoiSites() // No changes needed
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
                    voronoiSites.Add(p);
                }
                count++;
                if (asyncGeneration && count % yieldBatchSize == 0) yield return null;
            }
        }
        if (voronoiSites.Count < 4 && mapWidth > 5 && mapHeight > 5)
        {
            // Add default sites (same logic)
            voronoiSites.Add(new Vector2Int(Mathf.Clamp(mapWidth / 4, 0, mapWidth - 1), Mathf.Clamp(mapHeight / 4, 0, mapHeight - 1)));
            voronoiSites.Add(new Vector2Int(Mathf.Clamp(3 * mapWidth / 4, 0, mapWidth - 1), Mathf.Clamp(mapHeight / 4, 0, mapHeight - 1)));
            voronoiSites.Add(new Vector2Int(Mathf.Clamp(mapWidth / 4, 0, mapWidth - 1), Mathf.Clamp(3 * mapHeight / 4, 0, mapHeight - 1)));
            voronoiSites.Add(new Vector2Int(Mathf.Clamp(3 * mapWidth / 4, 0, mapWidth - 1), Mathf.Clamp(3 * mapHeight / 4, 0, mapHeight - 1)));
            voronoiSites = voronoiSites.Distinct().ToList();
            Debug.LogWarning($"Added default Voronoi sites. Count: {voronoiSites.Count}");
        }
    }

    // Combined Compute & Place roads to simplify flow
    IEnumerator ComputeVoronoiEdgesAndPlaceRoads()
    {
        if (voronoiSites.Count < 2) yield break;
        int processed = 0;
        HashSet<Vector2Int> potentialRoads = new HashSet<Vector2Int>();
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites);
                if (nearestSiteIndex < 0) continue;
                foreach (var dir in directions)
                {
                    Vector2Int neighborPos = currentPos + dir;
                    if (!IsInMap(neighborPos)) continue;
                    int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites);
                    if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex)
                    {
                        potentialRoads.Add(currentPos);
                        break;
                    }
                }
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
            }
        }

        // Place roads using the helper
        foreach (var roadPos in potentialRoads)
        {
            yield return StartCoroutine(TryPlaceRoad(roadPos)); // Use helper that handles building demolition
        }
    }

    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites) // No changes needed
    {
        if (sites == null || sites.Count == 0) return -1;
        int nearestIndex = -1; float minDistSq = float.MaxValue;
        for (int i = 0; i < sites.Count; i++) { float distSq = (sites[i] - point).sqrMagnitude; if (distSq < minDistSq) { minDistSq = distSq; nearestIndex = i; } }
        return nearestIndex;
    }
    Vector2Int? FindNearestSite(Vector2Int point, List<Vector2Int> sites) // No changes needed
    {
        int index = FindNearestSiteIndex(point, sites);
        return index >= 0 ? sites[index] : (Vector2Int?)null;
    }

    IEnumerator SelectNoisePoints() // No changes needed
    {
        noisePoints.Clear();
        float noiseOffsetX = noiseOffset.x; float noiseOffsetY = noiseOffset.y; int checkedCount = 0;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                // Check if tile is truly empty (not even part of another building)
                if (map[x, y] == ZoneType.Empty && !occupiedTiles.ContainsKey(new Vector2Int(x, y)))
                {
                    float nX = (noiseOffsetX + (float)x / mapWidth * branchNoiseScale);
                    float nY = (noiseOffsetY + (float)y / mapHeight * branchNoiseScale);
                    float nV = Mathf.PerlinNoise(nX, nY);
                    if (nV > noiseBranchThreshold) { noisePoints.Add(new Vector2Int(x, y)); }
                }
                checkedCount++;
                if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null;
            }
        }
    }
    #endregion

    #region MST Connection (Road Placement Updated)
    IEnumerator ConnectNoiseAndRoadsWithMST()
    {
        // Find components, build graph nodes (same logic)
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();
        if (roadComponents.Count == 0) { Debug.LogWarning("MST: No road components."); yield break; }
        List<Vector2Int> mstNodes = new List<Vector2Int>(noisePoints);
        Dictionary<Vector2Int, HashSet<Vector2Int>> componentLookup = new Dictionary<Vector2Int, HashSet<Vector2Int>>();
        foreach (var component in roadComponents) { if (component.Count > 0) { Vector2Int rep = component.First(); mstNodes.Add(rep); componentLookup[rep] = component; } }
        if (mstNodes.Count < 2) { Debug.Log("MST: Not enough nodes."); yield break; }

        // Calculate potential edges (same logic)
        List<GraphEdge> potentialEdges = new List<GraphEdge>(); int edgeCalcCount = 0;
        for (int i = 0; i < mstNodes.Count; i++)
        {
            for (int j = i + 1; j < mstNodes.Count; j++)
            {
                Vector2Int nodeA = mstNodes[i]; Vector2Int nodeB = mstNodes[j];
                bool nodeAIsRoadRep = componentLookup.ContainsKey(nodeA); bool nodeBIsRoadRep = componentLookup.ContainsKey(nodeB);
                Vector2Int connectionPointA = nodeA; Vector2Int connectionPointB = nodeB; int distance;
                if (nodeAIsRoadRep && !nodeBIsRoadRep) { connectionPointA = FindNearestPointInSet(nodeB, componentLookup[nodeA]) ?? nodeA; distance = ManhattanDistance(connectionPointA, nodeB); }
                else if (!nodeAIsRoadRep && nodeBIsRoadRep) { connectionPointB = FindNearestPointInSet(nodeA, componentLookup[nodeB]) ?? nodeB; distance = ManhattanDistance(nodeA, connectionPointB); }
                else if (nodeAIsRoadRep && nodeBIsRoadRep) { connectionPointA = FindNearestPointInSet(nodeB, componentLookup[nodeA]) ?? nodeA; connectionPointB = FindNearestPointInSet(nodeA, componentLookup[nodeB]) ?? nodeB; distance = ManhattanDistance(connectionPointA, connectionPointB); }
                else { distance = ManhattanDistance(nodeA, nodeB); }
                potentialEdges.Add(new GraphEdge(nodeA, nodeB, distance));
                edgeCalcCount++;
                if (asyncGeneration && edgeCalcCount % (yieldBatchSize * 5 + mstNodes.Count) == 0) yield return null; // Adjusted yield frequency
            }
        }
        Debug.Log($"MST: Calculated {potentialEdges.Count} potential edges.");

        // Compute MST (same logic)
        List<GraphEdge> mstEdges = ComputePrimMST(mstNodes, potentialEdges);
        Debug.Log($"MST: Computed MST with {mstEdges.Count} edges.");

        // --- Draw Roads (Updated) ---
        int edgesDrawn = 0;
        foreach (GraphEdge edge in mstEdges)
        {
            // Find start/end points (same logic)
            Vector2Int startNode = edge.nodeA; Vector2Int endNode = edge.nodeB; Vector2Int drawStart = startNode; Vector2Int drawEnd = endNode;
            bool startIsRoadRep = componentLookup.ContainsKey(startNode); bool endIsRoadRep = componentLookup.ContainsKey(endNode);
            if (startIsRoadRep && !endIsRoadRep) { drawStart = FindNearestPointInSet(endNode, componentLookup[startNode]) ?? startNode; }
            else if (!startIsRoadRep && endIsRoadRep) { drawEnd = FindNearestPointInSet(startNode, componentLookup[endNode]) ?? endNode; }
            else if (startIsRoadRep && endIsRoadRep)
            {
                // Refined connection point finding
                Vector2Int tempA = FindNearestPointInSet(endNode, componentLookup[startNode]) ?? startNode;
                Vector2Int tempB = FindNearestPointInSet(startNode, componentLookup[endNode]) ?? endNode;
                drawStart = FindNearestPointInSet(tempB, componentLookup[startNode]) ?? tempA;
                drawEnd = FindNearestPointInSet(drawStart, componentLookup[endNode]) ?? tempB;
            }

            // Draw path using helper
            HashSet<Vector2Int> pathTiles = new HashSet<Vector2Int>();
            DrawStraightRoadPath(drawStart, drawEnd, pathTiles);

            // --- Apply path using TryPlaceRoad ---
            foreach (var roadPos in pathTiles)
            {
                if (!IsInMap(roadPos)) continue;
                // TryPlaceRoad handles demolition and adding to queue/sets
                yield return StartCoroutine(TryPlaceRoad(roadPos));
            }

            edgesDrawn++;
            if (asyncGeneration && edgesDrawn % (yieldBatchSize / 5 + 1) == 0) yield return null; // Adjusted yield frequency
        }
    }

    List<GraphEdge> ComputePrimMST(List<Vector2Int> nodes, List<GraphEdge> edges) // No changes needed
    {
        List<GraphEdge> mstResult = new List<GraphEdge>(); if (nodes.Count == 0) return mstResult;
        HashSet<Vector2Int> inMST = new HashSet<Vector2Int>(); SimplePriorityQueue<GraphEdge> edgeQueue = new SimplePriorityQueue<GraphEdge>();
        Vector2Int startNode = nodes[0]; inMST.Add(startNode);
        foreach (var edge_iter in edges) { if ((edge_iter.nodeA == startNode && !inMST.Contains(edge_iter.nodeB)) || (edge_iter.nodeB == startNode && !inMST.Contains(edge_iter.nodeA))) edgeQueue.Enqueue(edge_iter, edge_iter.cost); }
        while (inMST.Count < nodes.Count && !edgeQueue.IsEmpty)
        {
            GraphEdge cheapestEdge = edgeQueue.Dequeue(); Vector2Int nodeToAdd = Vector2Int.zero; bool edgeCrossesCut = false;
            if (inMST.Contains(cheapestEdge.nodeA) && !inMST.Contains(cheapestEdge.nodeB)) { nodeToAdd = cheapestEdge.nodeB; edgeCrossesCut = true; }
            else if (!inMST.Contains(cheapestEdge.nodeA) && inMST.Contains(cheapestEdge.nodeB)) { nodeToAdd = cheapestEdge.nodeA; edgeCrossesCut = true; }
            if (edgeCrossesCut)
            {
                mstResult.Add(cheapestEdge); inMST.Add(nodeToAdd);
                foreach (var edge_iter in edges)
                {
                    Vector2Int otherNode = Vector2Int.zero; bool connectsToNew = false;
                    if (edge_iter.nodeA == nodeToAdd && !inMST.Contains(edge_iter.nodeB)) { otherNode = edge_iter.nodeB; connectsToNew = true; }
                    else if (edge_iter.nodeB == nodeToAdd && !inMST.Contains(edge_iter.nodeA)) { otherNode = edge_iter.nodeA; connectsToNew = true; }
                    if (connectsToNew) edgeQueue.Enqueue(edge_iter, edge_iter.cost);
                }
            }
        }
        if (inMST.Count != nodes.Count) { Debug.LogWarning($"MST incomplete. Nodes: {inMST.Count}/{nodes.Count}"); }
        return mstResult;
    }

    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints) // No changes needed
    {
        if (targetPoints == null || targetPoints.Count == 0) return null; Vector2Int? nearest = null; int minDistance = int.MaxValue; foreach (Vector2Int target in targetPoints) { int distance = ManhattanDistance(startPoint, target); if (distance < minDistance) { minDistance = distance; nearest = target; } if (minDistance <= 1) break; }
        return nearest;
    }
    #endregion

    #region Edge Connection (Road Placement Updated)
    IEnumerator EnsureMapEdgeConnections()
    {
        List<Vector2Int> edgeAnchors = new List<Vector2Int> {
            new Vector2Int(mapWidth / 2, mapHeight - 1), new Vector2Int(mapWidth / 2, 0),
            new Vector2Int(0, mapHeight / 2), new Vector2Int(mapWidth - 1, mapHeight / 2) };
        int connectedCount = 0;
        foreach (var edgePoint in edgeAnchors)
        {
            bool success = false;
            // ConnectToEdge now uses TryPlaceRoad internally via its helper
            yield return StartCoroutine(ConnectToEdge(edgePoint, result => success = result));
            if (success) connectedCount++;
            // if(asyncGeneration) yield return null; // Optional extra yield
        }
        Debug.Log($"Edge Connection: Completed attempts. Successfully connected {connectedCount} edges.");
    }

    IEnumerator ConnectToEdge(Vector2Int edgePoint, System.Action<bool> callback)
    {
        Vector2Int? startPoint = FindNearestRoadNearPoint(edgePoint, edgeConnectionSearchRadius);
        if (!startPoint.HasValue) { startPoint = FindNearestSite(edgePoint, voronoiSites); }
        if (!startPoint.HasValue) { callback?.Invoke(false); yield break; }

        HashSet<Vector2Int> pathTiles = new HashSet<Vector2Int>();
        DrawGridPath(startPoint.Value, edgePoint, pathTiles);

        int appliedCount = 0;
        foreach (var roadPos in pathTiles)
        {
            if (!IsInMap(roadPos)) continue;
            bool placed = false;
            // TryPlaceRoad returns true if a road was newly placed or already existed
            yield return StartCoroutine(TryPlaceRoad(roadPos, result => placed = result));
            if (placed) appliedCount++; // Count successful placements/overwrites
        }

        // Yield if async and we potentially placed roads
        if (asyncGeneration && appliedCount > 0) yield return null;
        callback?.Invoke(appliedCount > 0 || pathTiles.Any(p => roadTiles.Contains(p))); // Success if path exists or was created
    }


    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet) // No changes needed
    {
        Vector2Int current = start; pathTilesSet.Add(current);
        while (current != end)
        {
            int dx = end.x - current.x; int dy = end.y - current.y; Vector2Int step = Vector2Int.zero;
            if (Mathf.Abs(dx) > Mathf.Abs(dy)) { step = new Vector2Int((int)Mathf.Sign(dx), 0); } else if (Mathf.Abs(dy) > Mathf.Abs(dx)) { step = new Vector2Int(0, (int)Mathf.Sign(dy)); } else if (dx != 0) { step = new Vector2Int((int)Mathf.Sign(dx), 0); } else if (dy != 0) { step = new Vector2Int(0, (int)Mathf.Sign(dy)); } else { break; }
            current += step; if (IsInMap(current)) { pathTilesSet.Add(current); } else { break; }
        }
        if (IsInMap(end)) pathTilesSet.Add(end);
    }

    Vector2Int? FindNearestRoadNearPoint(Vector2Int center, int radius) // No changes needed
    {
        Vector2Int? nearest = null; float minDistanceSq = float.MaxValue;
        for (int x = Mathf.Max(0, center.x - radius); x <= Mathf.Min(mapWidth - 1, center.x + radius); x++)
        {
            for (int y = Mathf.Max(0, center.y - radius); y <= Mathf.Min(mapHeight - 1, center.y + radius); y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                if (roadTiles.Contains(currentPos)) { float distSq = (currentPos - center).sqrMagnitude; if (distSq < minDistanceSq) { minDistanceSq = distSq; nearest = currentPos; } }
            }
        }
        return nearest;
    }
    #endregion

    #region Building Placement Step (NEW Greedy Logic)

    /// <summary>
    /// Places initial buildings using a greedy approach (largest first).
    /// </summary>
    IEnumerator PlaceBuildingsGreedy()
    {
        Debug.Log("Placing initial buildings (greedy)...");
        // Iterate through map checking for empty spots suitable for buildings
        int placedCount = 0;
        int checkedTiles = 0;

        // Iterate potential bottom-left corners
        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                Vector2Int currentOrigin = new Vector2Int(x, y);
                checkedTiles++;

                // Is this tile already occupied?
                if (map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentOrigin))
                {
                    continue; // Skip if not empty or already part of another building
                }

                // --- Try placing largest first (3x3) ---
                bool placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(3, 3), buildingPrefabs3x3);
                if (placed)
                {
                    placedCount++;
                    // Optimization: Skip ahead horizontally past the placed building
                    x += (3 - 1); // Move x to the last column of the placed 3x3 building
                    continue; // Move to next potential spot in the row
                }

                // --- Try placing 2x2 ---
                placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(2, 2), buildingPrefabs2x2);
                if (placed)
                {
                    placedCount++;
                    x += (2 - 1);
                    continue;
                }

                // --- Try placing 1x1 ---
                // Add a random chance for 1x1s to avoid filling *every* single gap initially
                float placementChance1x1 = 0.5f; // 50% chance to place if 1x1 space is free
                if (UnityEngine.Random.value < placementChance1x1)
                {
                    placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(1, 1), buildingPrefabs1x1);
                    if (placed)
                    {
                        placedCount++;
                        // x += (1-1); // No skip needed for 1x1
                        continue;
                    }
                }


                // --- Yielding ---
                if (asyncGeneration && checkedTiles % (yieldBatchSize * 2) == 0) // Yield less often during placement checks
                {
                    yield return null;
                }
            } // End X loop
            if (asyncGeneration && y % 10 == 0) yield return null; // Optional yield per row
        } // End Y loop
        Debug.Log($"Placed {placedCount} initial buildings.");
    }

    /// <summary>
    /// Fills remaining empty tiles using the greedy placement approach.
    /// </summary>
    IEnumerator FillRemainingEmptyTilesGreedy()
    {
        Debug.Log("Filling remaining empty tiles (greedy)...");
        int filledCount = 0;
        int checkedTiles = 0;

        // Similar iteration and greedy logic as PlaceInitialBuildings
        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                Vector2Int currentOrigin = new Vector2Int(x, y);
                checkedTiles++;

                if (map[x, y] != ZoneType.Empty || occupiedTiles.ContainsKey(currentOrigin))
                {
                    continue;
                }

                bool placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(3, 3), buildingPrefabs3x3);
                if (placed) { filledCount++; x += (3 - 1); continue; }

                placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(2, 2), buildingPrefabs2x2);
                if (placed) { filledCount++; x += (2 - 1); continue; }

                // Place 1x1 unconditionally when filling remaining gaps
                placed = TryPlaceBuildingOfSize(currentOrigin, new Vector2Int(1, 1), buildingPrefabs1x1);
                if (placed) { filledCount++; /* x += (1-1); */ continue; }

                if (asyncGeneration && checkedTiles % yieldBatchSize == 0) yield return null;
            }
            if (asyncGeneration && y % 5 == 0) yield return null; // Optional yield per row
        }
        Debug.Log($"Filled {filledCount} remaining empty tiles.");
    }


    /// <summary>
    /// Helper to attempt placing a building of a specific size at an origin.
    /// </summary>
    /// <returns>True if placement was successful, false otherwise.</returns>
    bool TryPlaceBuildingOfSize(Vector2Int origin, Vector2Int size, List<GameObject> prefabList)
    {
        // Check if prefab list is valid
        if (prefabList == null || prefabList.Count == 0 || prefabList[0] == null)
        {
            // Only log warning once per size to avoid spam
            // if (!loggedMissingPrefabWarning.Contains(size.x)) {
            //    Debug.LogWarning($"No {size.x}x{size.y} prefabs assigned or list is empty. Cannot place buildings of this size.");
            //    loggedMissingPrefabWarning.Add(size.x);
            // }
            return false;
        }


        // Check if the area is clear
        if (IsAreaClear(origin, size))
        {
            // Select a random prefab from the list
            GameObject selectedPrefab = GetRandomPrefab(prefabList);
            if (selectedPrefab == null) return false; // Should not happen if list check passed, but safety first

            // Determine building type (can base on prefab name/tag or just random for now)
            ZoneType buildingType = DetermineBuildingType(); // Use existing random type logic

            // Place the building
            PlaceBuilding(origin, size, selectedPrefab, buildingType);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a rectangular area defined by origin and size is clear of roads and other buildings.
    /// </summary>
    bool IsAreaClear(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                // Check map bounds first
                if (!IsInMap(currentPos)) return false; // Area goes out of bounds

                // Check if tile is occupied by a road or another building part
                if (map[x, y] == ZoneType.Road || occupiedTiles.ContainsKey(currentPos))
                {
                    return false; // Area is blocked
                }
            }
        }
        return true; // Area is clear
    }

    /// <summary>
    /// Selects a random prefab from a list. Handles empty/null lists.
    /// </summary>
    GameObject GetRandomPrefab(List<GameObject> list)
    {
        if (list == null || list.Count == 0) return null;
        int randomIndex = UnityEngine.Random.Range(0, list.Count);
        return list[randomIndex]; // Return null if the selected element is somehow null
    }


    /// <summary>
    /// Places a building instance, updates map data, occupancy, tracking, and queues the object.
    /// Assumes IsAreaClear check has already passed.
    /// </summary>
    void PlaceBuilding(Vector2Int origin, Vector2Int size, GameObject prefab, ZoneType type)
    {
        if (prefab == null) return;

        // --- Instantiate ---
        GameObject instance = Instantiate(prefab, new Vector3(origin.x, 0, origin.y), Quaternion.identity, mapParent);
        // Adjust position slightly if needed based on prefab pivot (e.g., center pivot vs corner pivot)
        // instance.transform.position += new Vector3(size.x * 0.5f, 0, size.y * 0.5f); // Example for center pivot

        instance.name = $"{prefab.name}_{origin.x}_{origin.y}"; // Name based on origin
        instance.SetActive(false);

        // --- Record Keeping ---
        BuildingRecord record = new BuildingRecord(instance, size, type);
        trackedObjects.Add(origin, record); // Track by origin

        // Mark all occupied tiles
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                if (IsInMap(currentPos))
                {
                    // Mark the origin tile with the specific building type in the 'map' array
                    if (currentPos == origin)
                    {
                        map[x, y] = type;
                    }
                    else
                    {
                        // Other tiles belonging to the building are marked as occupied, but map type remains Empty technically.
                        // Or could introduce ZoneType.BuildingPart if needed elsewhere. Let's keep it simple for now.
                        // map[x, y] = ZoneType.BuildingPart; // Optional
                    }
                    occupiedTiles.Add(currentPos, origin); // Link tile back to building origin
                }
            }
        }

        // --- Queue for Activation ---
        generationQueue.Enqueue(instance);
    }


    #endregion

    #region Common Post-Processing Steps (Updated for Multi-Tile)

    // Step 6 & 8: Global Road Connectivity (Road Placement Updated)
    IEnumerator EnsureRoadConnectivity()
    {
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();
        if (roadComponents.Count <= 1) { yield break; }
        Debug.Log($"Connectivity: Found {roadComponents.Count} road components. Connecting...");
        roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList();
        HashSet<Vector2Int> mainNetwork = new HashSet<Vector2Int>(roadComponents[0]);
        int connectionsMade = 0;

        for (int i = 1; i < roadComponents.Count; i++)
        {
            HashSet<Vector2Int> currentComponent = roadComponents[i];
            if (currentComponent.Count == 0 || mainNetwork.Contains(currentComponent.First())) continue; // Skip empty or already connected

            // ConnectComponentToNetwork uses A* and TryPlaceRoad
            yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork));
            connectionsMade++;
            // if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects()); // Optional instantiate here
        }
        Debug.Log($"Connectivity: Finished. Made {connectionsMade} connections.");
    }

    List<HashSet<Vector2Int>> FindAllRoadComponents() // No changes needed
    {
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>(); HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        foreach (Vector2Int startPos in roadTiles) { if (!visited.Contains(startPos)) { HashSet<Vector2Int> newComponent = new HashSet<Vector2Int>(); Queue<Vector2Int> queue = new Queue<Vector2Int>(); queue.Enqueue(startPos); visited.Add(startPos); newComponent.Add(startPos); while (queue.Count > 0) { Vector2Int node = queue.Dequeue(); foreach (var dir in directions) { Vector2Int neighbor = node + dir; if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor)) { visited.Add(neighbor); newComponent.Add(neighbor); queue.Enqueue(neighbor); } } } if (newComponent.Count > 0) components.Add(newComponent); } }
        return components;
    }

    // ConnectComponentToNetwork uses TryPlaceRoad via A* path application
    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork)
    {
        if (componentToConnect.Count == 0 || targetNetwork.Count == 0) yield break;
        Vector2Int startNode = componentToConnect.First();
        List<Vector2Int> path = FindPath(startNode, targetNetwork); // A* can be slow

        if (path != null && path.Count > 1)
        {
            foreach (var point in path)
            {
                if (!IsInMap(point)) continue;
                // TryPlaceRoad handles demolition and adding to queue/sets
                yield return StartCoroutine(TryPlaceRoad(point));
                // Ensure the path point is added to the target network for subsequent A* searches within this connectivity pass
                if (roadTiles.Contains(point)) targetNetwork.Add(point);
            }
            // Merge the component logically after connecting
            targetNetwork.UnionWith(componentToConnect);
        }
        else
        {
            Debug.LogWarning($"EnsureConnectivity: A* path failed near {startNode}.");
        }
        // No yield here, handled by caller
    }

    // Step 7: Prune Wide Roads (Removal Updated)
    IEnumerator PruneWideRoads()
    {
        HashSet<Vector2Int> tilesToPrune = new HashSet<Vector2Int>(); int processed = 0;
        for (int x = 0; x < mapWidth - 1; x++)
        {
            for (int y = 0; y < mapHeight - 1; y++)
            {
                Vector2Int p00 = new Vector2Int(x, y), p10 = new Vector2Int(x + 1, y), p01 = new Vector2Int(x, y + 1), p11 = new Vector2Int(x + 1, y + 1);
                if (roadTiles.Contains(p00) && roadTiles.Contains(p10) && roadTiles.Contains(p01) && roadTiles.Contains(p11))
                {
                    // Prefer pruning non-junctions if possible? More complex. Simple prune for now.
                    tilesToPrune.Add(p11); // Mark top-right for removal
                }
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
            }
        }

        if (tilesToPrune.Count > 0)
        {
            Debug.Log($"Pruning {tilesToPrune.Count} road tiles.");
            foreach (var point in tilesToPrune)
            {
                // RemoveTrackedObject handles roads (size 1x1) correctly now
                RemoveTrackedObject(point);
            }
        }
    }

    // Step 9: Ensure Zone Accessibility (Road Placement Updated)
    IEnumerator EnsureZoneAccessibility()
    {
        int processedZones = 0; List<Vector2Int> buildingOrigins = trackedObjects.Where(kvp => kvp.Value.type != ZoneType.Road).Select(kvp => kvp.Key).ToList();
        List<Vector2Int> zonesNeedingAccess = new List<Vector2Int>();

        // Check each building origin
        foreach (var origin in buildingOrigins)
        {
            if (trackedObjects.TryGetValue(origin, out BuildingRecord record))
            {
                if (!HasAdjacentRoadToBuilding(origin, record.size))
                {
                    zonesNeedingAccess.Add(origin);
                }
            }
        }


        if (zonesNeedingAccess.Count == 0) { Debug.Log("Zone Accessibility: All buildings have access."); yield break; }
        Debug.Log($"Zone Accessibility: Found {zonesNeedingAccess.Count} buildings needing access. Connecting...");
        HashSet<Vector2Int> currentRoadNetwork = new HashSet<Vector2Int>(roadTiles);

        foreach (var zoneOrigin in zonesNeedingAccess)
        {
            // Check again, might have gained access indirectly
            if (trackedObjects.TryGetValue(zoneOrigin, out BuildingRecord record))
            {
                if (!HasAdjacentRoadToBuilding(zoneOrigin, record.size))
                {
                    // ConnectWithAStar uses TryPlaceRoad
                    yield return StartCoroutine(ConnectWithAStar(zoneOrigin, currentRoadNetwork));
                }
            }
            processedZones++;
            // if (asyncGeneration && processedZones % (yieldBatchSize / 10 + 1) == 0) yield return null;
        }
        Debug.Log("Finished ensuring zone accessibility.");
    }

    // ConnectWithAStar uses TryPlaceRoad
    IEnumerator ConnectWithAStar(Vector2Int start, HashSet<Vector2Int> targetNetwork)
    {
        if (targetNetwork.Count == 0) yield break;
        List<Vector2Int> path = FindPath(start, targetNetwork); // A*

        if (path != null && path.Count > 1)
        {
            foreach (var p in path)
            {
                if (!IsInMap(p)) continue;
                if (p == start) continue; // Don't overwrite building origin with road
                bool placed = false;
                yield return StartCoroutine(TryPlaceRoad(p, result => placed = result));
                if (placed) targetNetwork.Add(p); // Add to network for subsequent checks
            }
        }
        else
        {
            Debug.LogWarning($"EnsureAccessibility: A* failed path from {start}.");
        }
    }

    /// <summary>
    /// Checks if any tile occupied by the building has an adjacent road.
    /// </summary>
    bool HasAdjacentRoadToBuilding(Vector2Int origin, Vector2Int size)
    {
        for (int x = origin.x; x < origin.x + size.x; x++)
        {
            for (int y = origin.y; y < origin.y + size.y; y++)
            {
                Vector2Int currentBuildingTile = new Vector2Int(x, y);
                if (!IsInMap(currentBuildingTile)) continue; // Should not happen if placed correctly

                // Check neighbors of this building tile
                foreach (var dir in allDirections) // Check diagonals too for access
                {
                    Vector2Int neighborPos = currentBuildingTile + dir;
                    if (IsInMap(neighborPos) && roadTiles.Contains(neighborPos))
                    {
                        return true; // Found adjacent road
                    }
                }
            }
        }
        return false; // No adjacent road found for any part of the building
    }

    // Step 10: Remove Isolated Tiles - REMOVED

    // Step 11: Fill Remaining Empty Tiles - Handled by FillRemainingEmptyTilesGreedy

    // Step 12: Instantiate Final Objects (Validation Updated)
    IEnumerator InstantiateQueuedObjects(bool instantiateAll = false)
    {
        int frameActivationCount = 0; List<GameObject> batchToActivate = new List<GameObject>();
        while (generationQueue.Count > 0)
        {
            GameObject obj = generationQueue.Dequeue();
            if (obj == null) continue;
            bool shouldActivate = true;

            // --- Validation ---
            if (TryParsePositionFromName(obj.name, out Vector2Int origin)) // Name convention assumes origin coords
            {
                // Is the object still tracked at this origin?
                if (trackedObjects.TryGetValue(origin, out BuildingRecord record) && record.instance == obj)
                {
                    // Check if the origin tile type matches
                    ZoneType expectedTypeAtOrigin = record.type; // Road or building type
                    if (!IsInMap(origin) || map[origin.x, origin.y] != expectedTypeAtOrigin)
                    {
                        // Origin tile type mismatch (e.g., road replaced building origin)
                        Debug.LogWarning($"Validation: Origin {origin} map type mismatch for {obj.name}. Expected {expectedTypeAtOrigin}, got {map[origin.x, origin.y]}. Object likely destroyed.");
                        shouldActivate = false;
                        // No need to destroy here, RemoveTrackedObject should have done it
                        if (trackedObjects.ContainsKey(origin)) trackedObjects.Remove(origin); // Clean up tracking if needed
                    }
                    else
                    {
                        // Check if all occupied tiles still point back to this origin (for buildings)
                        if (expectedTypeAtOrigin != ZoneType.Road)
                        {
                            for (int x = origin.x; x < origin.x + record.size.x; x++)
                            {
                                for (int y = origin.y; y < origin.y + record.size.y; y++)
                                {
                                    Vector2Int currentPos = new Vector2Int(x, y);
                                    if (!occupiedTiles.TryGetValue(currentPos, out Vector2Int recordedOrigin) || recordedOrigin != origin)
                                    {
                                        Debug.LogWarning($"Validation: Tile {currentPos} for building {obj.name} is no longer occupied by it. Object likely destroyed.");
                                        shouldActivate = false;
                                        if (trackedObjects.ContainsKey(origin)) trackedObjects.Remove(origin); // Clean tracking
                                        goto EndValidationLoop; // Exit nested loops
                                    }
                                }
                            }
                        EndValidationLoop:;
                        }
                    }
                }
                else // Not tracked or different instance tracked
                {
                    Debug.LogWarning($"Validation: Queued object {obj.name} at {origin} is no longer tracked or is stale. Destroying.");
                    shouldActivate = false;
                    Destroy(obj); // Destroy this stale instance
                }
            }
            else // Cannot parse name
            {
                Debug.LogError($"Validation Failed: Cannot parse position from name: {obj.name}. Destroying object.");
                shouldActivate = false;
                Destroy(obj);
            }

            // --- Activation ---
            if (shouldActivate)
            {
                batchToActivate.Add(obj);
                frameActivationCount++;
                if (asyncGeneration && !instantiateAll && frameActivationCount >= objectsPerFrame)
                {
                    foreach (var o in batchToActivate) if (o != null) o.SetActive(true);
                    batchToActivate.Clear(); yield return null; frameActivationCount = 0;
                }
            }
            else
            {
                // If validation failed but we didn't destroy obj (e.g. mismatch found), destroy now.
                if (obj != null) Destroy(obj);
            }

        } // End while queue

        if (batchToActivate.Count > 0) { foreach (var o in batchToActivate) if (o != null) o.SetActive(true); batchToActivate.Clear(); }
    }


    #endregion

    #region A* Pathfinding Algorithm (Updated Terrain Cost)
    List<Vector2Int> FindPath(Vector2Int start, HashSet<Vector2Int> targets) // Search logic same
    {
        if (targets == null || targets.Count == 0) return null;
        if (targets.Contains(start)) return new List<Vector2Int>() { start };
        if (!IsInMap(start)) return null;

        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>();
        SimplePriorityQueue<Vector2Int> openSet = new SimplePriorityQueue<Vector2Int>();
        Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        Dictionary<Vector2Int, float> gScore = new Dictionary<Vector2Int, float>();
        gScore[start] = 0; openSet.Enqueue(start, Heuristic(start, targets));
        int iterations = 0; int maxIterations = mapWidth * mapHeight * 3;

        while (!openSet.IsEmpty)
        {
            iterations++; if (iterations > maxIterations) { Debug.LogError($"A* limit exceeded from {start}"); return null; }
            Vector2Int current = openSet.Dequeue();
            if (targets.Contains(current)) { return ReconstructPath(cameFrom, current); }
            closedSet.Add(current);
            foreach (var dir in directions)
            {
                Vector2Int neighbor = current + dir;
                if (!IsInMap(neighbor) || closedSet.Contains(neighbor)) continue;

                // --- Updated Cost Check ---
                float terrainCost = GetTerrainCost(neighbor); // Uses updated logic
                if (float.IsPositiveInfinity(terrainCost)) continue;
                // -------------------------

                float tentativeGScore = GetGScore(gScore, current) + terrainCost;
                if (tentativeGScore < GetGScore(gScore, neighbor))
                {
                    cameFrom[neighbor] = current; gScore[neighbor] = tentativeGScore;
                    openSet.Enqueue(neighbor, tentativeGScore + Heuristic(neighbor, targets));
                }
            }
        }
        Debug.LogWarning($"A* Pathfinding failed: No path found from {start}. Iterations: {iterations}");
        return null;
    }

    float GetGScore(Dictionary<Vector2Int, float> gScoreDict, Vector2Int node) { return gScoreDict.TryGetValue(node, out float score) ? score : float.PositiveInfinity; }

    // --- UPDATED Terrain Cost ---
    float GetTerrainCost(Vector2Int pos)
    {
        if (!IsInMap(pos)) return float.PositiveInfinity;

        // Check if it's part of any building first
        if (occupiedTiles.ContainsKey(pos))
        {
            // It's part of a building - high cost to demolish
            return 100.0f;
        }

        // Check map type (Road or Empty)
        switch (map[pos.x, pos.y])
        {
            case ZoneType.Road:
                return 1.0f; // Cheap to traverse existing roads
            case ZoneType.Empty:
                return 5.0f; // Moderate cost to build road on empty land
            case ZoneType.Residential: // Should only be at origin, but check anyway
            case ZoneType.Commercial:
            case ZoneType.Industrial:
                // This implies an origin tile that somehow wasn't in occupiedTiles (data inconsistency?)
                Debug.LogWarning($"GetTerrainCost: Found building type {map[pos.x, pos.y]} at {pos} but not in occupiedTiles. Treating as high cost.");
                return 100.0f; // Treat as building
            default:
                return float.PositiveInfinity; // Should not happen
        }
    }
    // ----------------------------

    float Heuristic(Vector2Int a, HashSet<Vector2Int> targets) // No changes needed
    {
        float minDistance = float.MaxValue; if (targets.Count == 0) return 0;
        foreach (var target in targets) { minDistance = Mathf.Min(minDistance, ManhattanDistance(a, target)); }
        return minDistance;
    }
    int ManhattanDistance(Vector2Int a, Vector2Int b) // No changes needed
    { return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y); }
    List<Vector2Int> ReconstructPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int current) // No changes needed
    { List<Vector2Int> totalPath = new List<Vector2Int> { current }; while (cameFrom.ContainsKey(current)) { current = cameFrom[current]; totalPath.Add(current); } totalPath.Reverse(); return totalPath; }
    #endregion

    #region Auxiliary & Helper Methods (Updated/New)

    ZoneType DetermineBuildingType() // No changes needed (can be expanded later)
    { int r = UnityEngine.Random.Range(0, 100); if (r < 50) return ZoneType.Residential; if (r < 80) return ZoneType.Commercial; return ZoneType.Industrial; }

    // GetZonePrefab is removed

    // AddToQueue is replaced by PlaceBuilding / PlaceRoad logic

    /// <summary>
    /// NEW: Helper coroutine to place a single road tile.
    /// Handles demolition of buildings if necessary.
    /// Returns true via callback if the tile is/becomes a road.
    /// </summary>
    IEnumerator TryPlaceRoad(Vector2Int pos, System.Action<bool> callback = null)
    {
        if (!IsInMap(pos)) { callback?.Invoke(false); yield break; }

        // Already a road? Nothing to do.
        if (map[pos.x, pos.y] == ZoneType.Road) { callback?.Invoke(true); yield break; }

        // Occupied by a building? Demolish it.
        if (occupiedTiles.ContainsKey(pos))
        {
            Debug.Log($"Road placement demolishing building at {pos}");
            // This remove call might take time if async, but we need it done now.
            // Consider if RemoveTrackedObject needs to be sync or async. Assume sync for now.
            RemoveTrackedObject(pos); // This removes the *entire* building
                                      // Optional: yield return null here if RemoveTrackedObject becomes async or very slow.
        }
        else if (map[pos.x, pos.y] != ZoneType.Empty)
        {
            // It's not empty, not occupied, not road -> maybe a building origin missed by occupiedTiles? Clean up.
            RemoveTrackedObject(pos); // Treat like a 1x1 building/object at this point
        }


        // Now place the road
        PlaceRoad(pos);
        callback?.Invoke(true);
        yield break; // Keep it simple, no yield needed after placement itself
    }


    /// <summary>
    /// Places a road instance, updates map data, tracking, and queues the object.
    /// Assumes demolition/clearing is already handled.
    /// </summary>
    void PlaceRoad(Vector2Int pos)
    {
        if (roadPrefab == null || !IsInMap(pos) || map[pos.x, pos.y] == ZoneType.Road) return; // Safety checks

        // --- Instantiate ---
        GameObject instance = Instantiate(roadPrefab, new Vector3(pos.x, 0, pos.y), Quaternion.identity, mapParent);
        instance.name = $"{roadPrefab.name}_{pos.x}_{pos.y}";
        instance.SetActive(false);

        // --- Record Keeping ---
        BuildingRecord record = new BuildingRecord(instance, Vector2Int.one, ZoneType.Road); // Size 1x1 for roads
        trackedObjects.Add(pos, record); // Track road by its position
        map[pos.x, pos.y] = ZoneType.Road;
        roadTiles.Add(pos);
        // Note: Roads do NOT use the occupiedTiles dictionary

        // --- Queue for Activation ---
        generationQueue.Enqueue(instance);
    }


    /// <summary>
    /// UPDATED: Removes the tracked object (building or road) at a given position.
    /// If it's part of a building, removes the entire building.
    /// </summary>
    void RemoveTrackedObject(Vector2Int pos)
    {
        if (!IsInMap(pos)) return; // Ignore out of bounds calls

        Vector2Int originToRemove = pos; // Assume removing object at pos initially
        bool isBuildingPart = occupiedTiles.TryGetValue(pos, out originToRemove); // Is it part of a building? Get the origin.

        // If it's a building part OR a building origin itself:
        if (isBuildingPart || (trackedObjects.TryGetValue(pos, out var directRecord) && directRecord.type != ZoneType.Road))
        {
            // Ensure originToRemove is correctly set even if 'pos' was the origin
            if (!isBuildingPart) originToRemove = pos;

            // Get the building record from the origin
            if (trackedObjects.TryGetValue(originToRemove, out BuildingRecord buildingRecord))
            {
                // Remove all occupied tiles for this building
                Vector2Int size = buildingRecord.size;
                for (int x = originToRemove.x; x < originToRemove.x + size.x; x++)
                {
                    for (int y = originToRemove.y; y < originToRemove.y + size.y; y++)
                    {
                        Vector2Int currentPos = new Vector2Int(x, y);
                        if (IsInMap(currentPos))
                        {
                            occupiedTiles.Remove(currentPos); // Remove from occupancy map
                            map[x, y] = ZoneType.Empty;      // Reset map tile
                        }
                    }
                }

                // Destroy the instance and remove tracking entry for the origin
                if (buildingRecord.instance != null)
                {
                    if (Application.isPlaying) Destroy(buildingRecord.instance);
                    else DestroyImmediate(buildingRecord.instance);
                }
                trackedObjects.Remove(originToRemove);
                // Debug.Log($"Removed building at origin {originToRemove} (triggered by pos {pos})");
            }
            else
            {
                // Data inconsistency: Tile was in occupiedTiles but no record at origin?
                Debug.LogWarning($"RemoveTrackedObject: Inconsistency! Tile {pos} linked to origin {originToRemove}, but no record found there. Cleaning up tile.");
                occupiedTiles.Remove(pos);
                map[pos.x, pos.y] = ZoneType.Empty;
            }
        }
        // If it wasn't a building part, check if it's a tracked road
        else if (trackedObjects.TryGetValue(pos, out BuildingRecord roadRecord) && roadRecord.type == ZoneType.Road)
        {
            // It's a road
            if (roadRecord.instance != null)
            {
                if (Application.isPlaying) Destroy(roadRecord.instance);
                else DestroyImmediate(roadRecord.instance);
            }
            trackedObjects.Remove(pos);
            roadTiles.Remove(pos);
            map[pos.x, pos.y] = ZoneType.Empty;
            // Debug.Log($"Removed road at {pos}");
        }
        else
        {
            // Not tracked as building origin, not a building part, not a tracked road.
            // Might be an empty tile, or data is inconsistent. Ensure it's marked empty.
            // Debug.Log($"RemoveTrackedObject: Pos {pos} was not a tracked object or building part. Ensuring map is Empty.");
            map[pos.x, pos.y] = ZoneType.Empty;
            occupiedTiles.Remove(pos); // Clean up just in case
            roadTiles.Remove(pos);
        }
    }


    bool IsInMap(Vector2Int pos) { return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight; }

    void DrawStraightRoadPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet) // No changes needed
    {
        int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x); for (int x = start.x; ; x += xDir) { Vector2Int c = new Vector2Int(x, start.y); if (IsInMap(c)) pathTilesSet.Add(c); else break; if (xDir == 0 || x == end.x) break; }
        int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y); for (int y = start.y; ; y += yDir) { Vector2Int c = new Vector2Int(end.x, y); if (IsInMap(c)) pathTilesSet.Add(c); else break; if (yDir == 0 || y == end.y) break; }
        if (IsInMap(end)) pathTilesSet.Add(end); if (IsInMap(start)) pathTilesSet.Add(start);
    }

    // Updated to handle new naming convention if needed, but simple origin parse is fine
    bool TryParsePositionFromName(string name, out Vector2Int position)
    {
        position = Vector2Int.zero;
        try
        {
            string[] parts = name.Split('_');
            if (parts.Length >= 3)
            {
                if (int.TryParse(parts[parts.Length - 2], out int x) && int.TryParse(parts[parts.Length - 1], out int y))
                {
                    position = new Vector2Int(x, y); return true;
                }
            }
        }
        catch { }
        return false;
    }

    void ClearPreviousGeneration()
    { // Updated
        Debug.Log("Clearing generation...");
        if (mapParent != null) { foreach (Transform child in mapParent.Cast<Transform>().ToList()) { if (child != null) { if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject); } } }
        else { GameObject dp = GameObject.Find("GeneratedCity"); if (dp != null) foreach (Transform child in dp.transform.Cast<Transform>().ToList()) { if (child != null) { if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject); } } }

        trackedObjects.Clear();
        occupiedTiles.Clear(); // Clear new dictionary
        generationQueue.Clear();
        if (map != null) System.Array.Clear(map, 0, map.Length); map = null;
        voronoiSites.Clear(); noisePoints.Clear(); roadTiles.Clear();
        Debug.Log("Clear complete.");
    }
    #endregion

    #region Helper Classes (GraphEdge, SimplePriorityQueue - Unchanged)
    // GraphEdge class remains the same
    private class GraphEdge { public Vector2Int nodeA, nodeB; public int cost; public GraphEdge(Vector2Int a, Vector2Int b, int c) { nodeA = a; nodeB = b; cost = c; } public override bool Equals(object obj) { if (obj == null || GetType() != obj.GetType()) { return false; } GraphEdge other = (GraphEdge)obj; bool same = nodeA.Equals(other.nodeA) && nodeB.Equals(other.nodeB) && cost == other.cost; bool swapped = nodeA.Equals(other.nodeB) && nodeB.Equals(other.nodeA) && cost == other.cost; return same || swapped; } public override int GetHashCode() { int hA = nodeA.GetHashCode(); int hB = nodeB.GetHashCode(); int h1 = hA < hB ? hA : hB; int h2 = hA < hB ? hB : hA; unchecked { int hash = 17; hash = hash * 23 + h1; hash = hash * 23 + h2; hash = hash * 23 + cost.GetHashCode(); return hash; } } }

    // SimplePriorityQueue class remains the same
    public class SimplePriorityQueue<T> { private List<(T item, float priority)> elements = new List<(T item, float priority)>(); public int Count => elements.Count; public bool IsEmpty => elements.Count == 0; public void Enqueue(T item, float priority) { elements.Add((item, priority)); elements.Sort((a, b) => a.priority.CompareTo(b.priority)); } public T Dequeue() { if (IsEmpty) throw new System.InvalidOperationException("Queue empty."); T item = elements[0].item; elements.RemoveAt(0); return item; } public bool Contains(T item) => elements.Any(e => e.item.Equals(item)); }
    #endregion
}