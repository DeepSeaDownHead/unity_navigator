using UnityEngine;
using System.Collections.Generic;
using System.Linq; // System.Linq 用于 ToList()

/// <summary>
/// 在 MapGenerator 完成后，创建地图边界墙并将预设的车辆放置在道路上。
/// 需要在 Inspector 中设置对 MapGenerator 和 Car To Place 的引用。
/// </summary>
public class CarPlacementController : MonoBehaviour
{
    [Header("Dependencies (依赖项)")]
    [Tooltip("对场景中 MapGenerator 组件的引用。")]
    [SerializeField] private MapGenerator mapGenerator;
    [Tooltip("对场景中预先放置的、需要移动到路上的小车 GameObject 的引用。")]
    [SerializeField] private GameObject carToPlace;

    [Header("Boundary Wall Settings (边界墙设置)")]
    [Tooltip("空气墙的高度。")]
    [SerializeField] private float wallHeight = 10f;
    [Tooltip("空气墙的厚度。")]
    [SerializeField] private float wallThickness = 1f;
    [Tooltip("（可选）分配给空气墙的物理材质。")]
    [SerializeField] private PhysicMaterial wallPhysicMaterial;
    [Tooltip("空气墙所在的层。建议创建一个单独的层（例如 'Environment' 或 'InvisibleWall'）")]
    [SerializeField] private LayerMask wallLayer = default; // 如果想指定层，请在 Inspector 中设置

    [Header("Car Placement Settings (小车放置设置)")]
    [Tooltip("小车放置在道路瓦片中心上方的 Y 轴偏移量。")]
    [SerializeField] private float carPlacementYOffset = 0.08f;

    private GameObject wallsParent = null; // 用于组织墙体对象的父对象

    /// <summary>
    /// 由 MapGenerator 调用，以初始化边界墙和车辆位置。
    /// </summary>
    public void InitializePlacementAndWalls()
    {
        if (mapGenerator == null)
        {
            Debug.LogError("CarPlacementController: MapGenerator reference is not set!", this);
            return;
        }
        if (carToPlace == null)
        {
            Debug.LogError("CarPlacementController: Car To Place reference is not set!", this);
            return;
        }

        Debug.Log("CarPlacementController: Initializing boundary walls and car placement...", this);
        CreateBoundaryWalls();
        PlaceCarOnRoad();
        Debug.Log("CarPlacementController: Initialization complete.", this);
    }

    /// <summary>
    /// 根据 MapGenerator 的尺寸创建四个边界墙。
    /// </summary>
    private void CreateBoundaryWalls()
    {
        int mapWidth = mapGenerator.mapWidth;
        int mapHeight = mapGenerator.mapHeight;

        if (wallsParent == null)
        {
            Transform existingParent = transform.Find("BoundaryWalls");
            if (existingParent != null) { wallsParent = existingParent.gameObject; }
            else { wallsParent = new GameObject("BoundaryWalls"); wallsParent.transform.SetParent(this.transform); wallsParent.transform.localPosition = Vector3.zero; }
        }

        for (int i = wallsParent.transform.childCount - 1; i >= 0; i--)
        {
            Destroy(wallsParent.transform.GetChild(i).gameObject);
        }
        // Debug.Log($"CarPlacementController: Cleared previous walls under '{wallsParent.name}'."); // Optional log

        float centerX = mapWidth / 2.0f - 0.5f;
        float centerZ = mapHeight / 2.0f - 0.5f;
        float wallY = wallHeight / 2.0f;

        Vector3 topPos = new Vector3(centerX, wallY, mapHeight - 0.5f + wallThickness / 2.0f);
        Vector3 topSize = new Vector3(mapWidth + wallThickness, wallHeight, wallThickness);
        Vector3 bottomPos = new Vector3(centerX, wallY, -0.5f - wallThickness / 2.0f);
        Vector3 bottomSize = new Vector3(mapWidth + wallThickness, wallHeight, wallThickness);
        Vector3 leftPos = new Vector3(-0.5f - wallThickness / 2.0f, wallY, centerZ);
        Vector3 leftSize = new Vector3(wallThickness, wallHeight, mapHeight);
        Vector3 rightPos = new Vector3(mapWidth - 0.5f + wallThickness / 2.0f, wallY, centerZ);
        Vector3 rightSize = new Vector3(wallThickness, wallHeight, mapHeight);

        CreateWall("BoundaryWall_Top", topPos, topSize);
        CreateWall("BoundaryWall_Bottom", bottomPos, bottomSize);
        CreateWall("BoundaryWall_Left", leftPos, leftSize);
        CreateWall("BoundaryWall_Right", rightPos, rightSize);

        Debug.Log("CarPlacementController: Boundary walls created.", this);
    }

    /// <summary>
    /// 创建单个墙体 GameObject。
    /// </summary>
    private void CreateWall(string wallName, Vector3 position, Vector3 size)
    {
        GameObject wall = new GameObject(wallName);
        wall.transform.SetParent(wallsParent.transform);
        wall.transform.position = position;
        BoxCollider collider = wall.AddComponent<BoxCollider>();
        collider.size = size;
        collider.material = wallPhysicMaterial;
        if (wallLayer.value != 0)
        {
            int layerIndex = GetLayerIndexFromMask(wallLayer);
            if (layerIndex != -1) { wall.layer = layerIndex; }
            else { Debug.LogWarning($"Could not find a valid layer index in Wall LayerMask for '{wallName}'.", this); }
        }
    }

    private int GetLayerIndexFromMask(LayerMask layerMask)
    {
        int layerNumber = 0; int layer = layerMask.value;
        if (layer == 0) return -1;
        while (layerNumber < 32) { if ((layer & 1) != 0) return layerNumber; layer >>= 1; layerNumber++; }
        return -1;
    }

    /// <summary>
    /// 将 carToPlace GameObject 放置在地图上随机一个道路瓦片的中心。
    /// </summary>
    private void PlaceCarOnRoad()
    {
        HashSet<Vector2Int> roadTiles = mapGenerator.GetRoadTiles();

        if (roadTiles == null || roadTiles.Count == 0)
        {
            Debug.LogError("CarPlacementController: No road tiles found! Cannot place car.", this);
            if (carToPlace != null) { carToPlace.transform.position = new Vector3(mapGenerator.mapWidth / 2.0f, carPlacementYOffset, mapGenerator.mapHeight / 2.0f); Debug.LogWarning("Car placed at map center fallback.", this); }
            return;
        }

        List<Vector2Int> roadList = roadTiles.ToList(); // 需要 using System.Linq;
        Vector2Int randomRoadTile = roadList[UnityEngine.Random.Range(0, roadList.Count)]; // 使用 UnityEngine.Random 明确指定

        Vector3 targetPosition = new Vector3(
            randomRoadTile.x + 0.5f,
            carPlacementYOffset,
            randomRoadTile.y + 0.5f
        );

        carToPlace.transform.position = targetPosition;
        // --- 使用 UnityEngine.Random 明确指定 ---
        carToPlace.transform.rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);

        Debug.Log($"CarPlacementController: Car placed on road {randomRoadTile} at {targetPosition}.", this);
    }

    void OnValidate()
    {
        // Optional: Find references in editor
        // if (mapGenerator == null) mapGenerator = FindObjectOfType<MapGenerator>();
        // if (carToPlace == null) Debug.LogWarning("Car To Place not set.", this);
    }
}