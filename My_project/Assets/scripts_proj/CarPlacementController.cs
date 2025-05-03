using UnityEngine;
using System.Collections.Generic;
using System.Linq; // System.Linq ���� ToList()

/// <summary>
/// �� MapGenerator ��ɺ󣬴�����ͼ�߽�ǽ����Ԥ��ĳ��������ڵ�·�ϡ�
/// ��Ҫ�� Inspector �����ö� MapGenerator �� Car To Place �����á�
/// </summary>
public class CarPlacementController : MonoBehaviour
{
    [Header("Dependencies (������)")]
    [Tooltip("�Գ����� MapGenerator ��������á�")]
    [SerializeField] private MapGenerator mapGenerator;
    [Tooltip("�Գ�����Ԥ�ȷ��õġ���Ҫ�ƶ���·�ϵ�С�� GameObject �����á�")]
    [SerializeField] private GameObject carToPlace;

    [Header("Boundary Wall Settings (�߽�ǽ����)")]
    [Tooltip("����ǽ�ĸ߶ȡ�")]
    [SerializeField] private float wallHeight = 10f;
    [Tooltip("����ǽ�ĺ�ȡ�")]
    [SerializeField] private float wallThickness = 1f;
    [Tooltip("����ѡ�����������ǽ��������ʡ�")]
    [SerializeField] private PhysicMaterial wallPhysicMaterial;
    [Tooltip("����ǽ���ڵĲ㡣���鴴��һ�������Ĳ㣨���� 'Environment' �� 'InvisibleWall'��")]
    [SerializeField] private LayerMask wallLayer = default; // �����ָ���㣬���� Inspector ������

    [Header("Car Placement Settings (С����������)")]
    [Tooltip("С�������ڵ�·��Ƭ�����Ϸ��� Y ��ƫ������")]
    [SerializeField] private float carPlacementYOffset = 0.08f;

    private GameObject wallsParent = null; // ������֯ǽ�����ĸ�����

    /// <summary>
    /// �� MapGenerator ���ã��Գ�ʼ���߽�ǽ�ͳ���λ�á�
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
    /// ���� MapGenerator �ĳߴ紴���ĸ��߽�ǽ��
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
    /// ��������ǽ�� GameObject��
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
    /// �� carToPlace GameObject �����ڵ�ͼ�����һ����·��Ƭ�����ġ�
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

        List<Vector2Int> roadList = roadTiles.ToList(); // ��Ҫ using System.Linq;
        Vector2Int randomRoadTile = roadList[UnityEngine.Random.Range(0, roadList.Count)]; // ʹ�� UnityEngine.Random ��ȷָ��

        Vector3 targetPosition = new Vector3(
            randomRoadTile.x + 0.5f,
            carPlacementYOffset,
            randomRoadTile.y + 0.5f
        );

        carToPlace.transform.position = targetPosition;
        // --- ʹ�� UnityEngine.Random ��ȷָ�� ---
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