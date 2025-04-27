using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 在 MapGenerator 完成后，创建地图边界墙并将预设的车辆放置在道路上。
/// 需要在 Inspector 中设置对 MapGenerator 和 Car To Place 的引用。
/// 会调用 CarController 的初始化方法。
/// </summary>
public class CarPlacementController : MonoBehaviour
{
    [Header("Dependencies (依赖项)")]
    [Tooltip("对场景中 MapGenerator 组件的引用。")]
    [SerializeField] private MapGenerator mapGenerator;
    [Tooltip("对场景中预先放置的、需要移动到路上的小车 GameObject 的引用。")]
    [SerializeField] private GameObject carToPlace;
    [Tooltip("对 CarController 脚本的引用（通常在同一个 GameObject 或子对象上）。")]
    [SerializeField] private CarController carController; // 添加了对 CarController 的引用

    [Header("Boundary Wall Settings (边界墙设置)")]
    [Tooltip("空气墙的高度。")]
    [SerializeField] private float wallHeight = 10f;
    [Tooltip("空气墙的厚度。")]
    [SerializeField] private float wallThickness = 1f;
    [Tooltip("（可选）分配给空气墙的物理材质。")]
    [SerializeField] private PhysicMaterial wallPhysicMaterial;
    [Tooltip("空气墙所在的层。建议创建一个单独的层（例如 'Environment' 或 'InvisibleWall'）")]
    [SerializeField] private LayerMask wallLayer = default;

    [Header("Car Placement Settings (小车放置设置)")]
    [Tooltip("小车放置在道路瓦片中心上方的 Y 轴偏移量。")]
    [SerializeField] private float carPlacementYOffset = 0.1f;
    // --- 添加了公共 Get 方法 ---
    public float GetCarPlacementYOffset() { return carPlacementYOffset; }

    private GameObject wallsParent = null;

    public void InitializePlacementAndWalls()
    {
        if (mapGenerator == null) { Debug.LogError("CarPlacementController: MapGenerator reference is not set!", this); return; }
        if (carToPlace == null) { Debug.LogError("CarPlacementController: Car To Place reference is not set!", this); return; }

        Debug.Log("CarPlacementController: Initializing boundary walls and car placement...", this);
        CreateBoundaryWalls();
        PlaceCarOnRoad(); // 放置小车

        // --- 调用 CarController 初始化 ---
        if (carController != null)
        {
            Debug.Log("CarPlacementController: 正在初始化 CarController。", this);
            carController.Initialize();
        }
        else
        {
            if (carToPlace != null) carController = carToPlace.GetComponent<CarController>();
            if (carController != null)
            {
                Debug.Log("CarPlacementController: 在小车上找到并初始化 CarController。", this);
                carController.Initialize();
            }
            else
            {
                Debug.LogWarning("CarPlacementController: 未设置 CarController 引用，且在小车上未找到。小车将无法控制。", this);
            }
        }
        // --- 结束调用 ---

        Debug.Log("CarPlacementController: Initialization complete.", this);
    }

    private void CreateBoundaryWalls()
    {
        int mapWidth = mapGenerator.mapWidth;
        int mapHeight = mapGenerator.mapHeight;
        if (wallsParent == null) { Transform existingParent = transform.Find("BoundaryWalls"); if (existingParent != null) { wallsParent = existingParent.gameObject; } else { wallsParent = new GameObject("BoundaryWalls"); wallsParent.transform.SetParent(this.transform); wallsParent.transform.localPosition = Vector3.zero; } }
        for (int i = wallsParent.transform.childCount - 1; i >= 0; i--) { Destroy(wallsParent.transform.GetChild(i).gameObject); }
        float centerX = mapWidth / 2.0f - 0.5f; float centerZ = mapHeight / 2.0f - 0.5f; float wallY = wallHeight / 2.0f;
        Vector3 topPos = new Vector3(centerX, wallY, mapHeight - 0.5f + wallThickness / 2.0f); Vector3 topSize = new Vector3(mapWidth + wallThickness, wallHeight, wallThickness); Vector3 bottomPos = new Vector3(centerX, wallY, -0.5f - wallThickness / 2.0f); Vector3 bottomSize = new Vector3(mapWidth + wallThickness, wallHeight, wallThickness); Vector3 leftPos = new Vector3(-0.5f - wallThickness / 2.0f, wallY, centerZ); Vector3 leftSize = new Vector3(wallThickness, wallHeight, mapHeight); Vector3 rightPos = new Vector3(mapWidth - 0.5f + wallThickness / 2.0f, wallY, centerZ); Vector3 rightSize = new Vector3(wallThickness, wallHeight, mapHeight);
        CreateWall("BoundaryWall_Top", topPos, topSize); CreateWall("BoundaryWall_Bottom", bottomPos, bottomSize); CreateWall("BoundaryWall_Left", leftPos, leftSize); CreateWall("BoundaryWall_Right", rightPos, rightSize);
        Debug.Log("CarPlacementController: Boundary walls created.", this);
    }

    private void CreateWall(string wallName, Vector3 position, Vector3 size)
    {
        GameObject wall = new GameObject(wallName); wall.transform.SetParent(wallsParent.transform); wall.transform.position = position; BoxCollider collider = wall.AddComponent<BoxCollider>(); collider.size = size; collider.material = wallPhysicMaterial;
        if (wallLayer.value != 0) { int layerIndex = GetLayerIndexFromMask(wallLayer); if (layerIndex != -1) { wall.layer = layerIndex; } else { Debug.LogWarning($"Could not find a valid layer index in Wall LayerMask for '{wallName}'.", this); } }
    }

    private int GetLayerIndexFromMask(LayerMask layerMask) { int layerNumber = 0; int layer = layerMask.value; if (layer == 0) return -1; while (layerNumber < 32) { if ((layer & 1) != 0) return layerNumber; layer >>= 1; layerNumber++; } return -1; }

    private void PlaceCarOnRoad()
    {
        HashSet<Vector2Int> roadTiles = mapGenerator.GetRoadTiles();
        if (roadTiles == null || roadTiles.Count == 0) { Debug.LogError("CarPlacementController: No road tiles found!", this); if (carToPlace != null) { carToPlace.transform.position = new Vector3(mapGenerator.mapWidth / 2.0f, carPlacementYOffset, mapGenerator.mapHeight / 2.0f); Debug.LogWarning("Car placed at map center fallback.", this); } return; }
        List<Vector2Int> roadList = roadTiles.ToList(); Vector2Int randomRoadTile = roadList[UnityEngine.Random.Range(0, roadList.Count)];
        Vector3 targetPosition = new Vector3(randomRoadTile.x + 0.5f, carPlacementYOffset, randomRoadTile.y + 0.5f);
        carToPlace.transform.position = targetPosition;
        // --- 确保初始旋转被吸附 ---
        float randomY = UnityEngine.Random.Range(0, 4) * 90f; // 0, 90, 180, or 270
        carToPlace.transform.rotation = Quaternion.Euler(0, randomY, 0);
        Debug.Log($"CarPlacementController: Car placed on road {randomRoadTile} at {targetPosition}.", this);
    }

    void OnValidate() { /* Optional editor checks */ }
}