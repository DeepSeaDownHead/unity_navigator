using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 使用 WASD 输入处理指定小车的街机风格、基于格子的移动。
/// 需要对 MapGenerator 和 小车 GameObject 的引用。
/// 移动被限制在有效的道路格子上，并且会进行物理检测以避免撞墙或建筑。
/// </summary>
public class CarController : MonoBehaviour
{
    [Header("引用 (References)")]
    [Tooltip("对 MapGenerator 的引用，以访问道路数据。")]
    [SerializeField] private MapGenerator mapGenerator;
    [Tooltip("此脚本将控制的小车 GameObject。")]
    [SerializeField] private GameObject carToControl;
    [Tooltip("对 CarPlacementController 的引用，以获取 Y 轴偏移量（可选，但推荐）。")]
    [SerializeField] private CarPlacementController placementController;

    [Header("移动设置 (Movement Settings)")]
    [Tooltip("执行一个动作后，再次处理输入的冷却时间（防止过快移动）。")]
    [SerializeField] private float inputCooldown = 0.15f;
    [Tooltip("用于物理检测的检测盒尺寸（大致匹配小车碰撞体）。X 和 Z 应该是格子大小，Y 可以小一点。")]
    [SerializeField] private Vector3 collisionCheckBoxSize = new Vector3(0.8f, 0.5f, 0.8f); // 比格子稍小防止边缘误判
    [Tooltip("物理检测时要忽略的层（通常是小车自身所在的层）。")]
    [SerializeField] private LayerMask ignoreLayerMask = default; // 设置为小车所在的层

    // 内部状态
    private Vector2Int currentGridPosition;
    private float currentYRotation = 0f;
    private bool isProcessingInput = false;
    private float carYOffset = 0.1f;
    private HashSet<Vector2Int> validRoadTiles = null;
    private Collider carCollider; // 缓存小车自身的碰撞体，以便在检测时忽略它

    public void Initialize()
    {
        if (carToControl == null) { Debug.LogError("CarController: 未指定要控制的小车!", this); this.enabled = false; return; }
        if (mapGenerator == null) { Debug.LogError("CarController: 未指定 MapGenerator!", this); this.enabled = false; return; }

        if (placementController != null) { carYOffset = placementController.GetCarPlacementYOffset(); }
        else { Debug.LogWarning("CarController: 未设置 CarPlacementController 引用。使用默认 Y 偏移。", this); }

        // 获取并缓存小车碰撞体
        carCollider = carToControl.GetComponent<Collider>();
        if (carCollider == null)
        {
            Debug.LogWarning("CarController: 控制的小车上没有找到 Collider，物理检测可能不准确。", this);
        }

        currentGridPosition = WorldToGrid(carToControl.transform.position);
        float initialYRotation = carToControl.transform.rotation.eulerAngles.y;
        currentYRotation = SnapRotation(initialYRotation);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);

        validRoadTiles = mapGenerator.GetRoadTiles();
        if (validRoadTiles == null || validRoadTiles.Count == 0) { Debug.LogError("CarController: 获取有效道路格子失败!", this); this.enabled = false; return; }

        isProcessingInput = false;
        Debug.Log($"CarController 初始化完成。起始位置: {currentGridPosition}, 起始旋转: {currentYRotation}", this);
        this.enabled = true;
    }

    void Update()
    {
        if (carToControl == null || isProcessingInput || !this.enabled) return;

        if (Input.GetKeyDown(KeyCode.W)) { StartCoroutine(ProcessAction(AttemptMoveForward)); }
        else if (Input.GetKeyDown(KeyCode.A)) { StartCoroutine(ProcessAction(AttemptTurnLeft)); }
        else if (Input.GetKeyDown(KeyCode.D)) { StartCoroutine(ProcessAction(AttemptTurnRight)); }
        // else if (Input.GetKeyDown(KeyCode.S)) { StartCoroutine(ProcessAction(AttemptMoveBackward)); }
    }

    private IEnumerator ProcessAction(System.Action actionToPerform)
    {
        isProcessingInput = true;
        actionToPerform?.Invoke();
        yield return new WaitForSeconds(inputCooldown);
        isProcessingInput = false;
    }

    // --- 核心移动逻辑修改 ---
    private void AttemptMoveForward()
    {
        Vector2Int forwardDirection = GetForwardDirection(currentYRotation);
        Vector2Int targetGridPosition = currentGridPosition + forwardDirection;

        // 1. 逻辑检查：目标格子是否是道路？
        if (!IsValidRoadTile(targetGridPosition))
        {
            // Debug.Log($"目标格子 {targetGridPosition} 不是道路。"); // 可选日志
            // 可以在这里加撞墙音效或视觉效果
            return; // 不移动
        }

        // 2. 物理检查：目标世界位置是否被阻挡？
        Vector3 targetWorldCenter = GridToWorld(targetGridPosition); // 获取目标格子的世界中心点

        // 使用 Physics.CheckBox 进行检测
        // QueryTriggerInteraction.Ignore 表示不检测触发器
        // ~ignoreLayerMask 表示检测除了 ignoreLayerMask 之外的所有层
        bool blocked = Physics.CheckBox(
            targetWorldCenter,               // 检测盒的中心
            collisionCheckBoxSize / 2f,      // 检测盒的半尺寸 (Extents)
            Quaternion.identity,             // 检测盒的旋转 (通常不需要旋转)
            ~ignoreLayerMask,                // 检测的层：除了忽略层之外的所有层
            QueryTriggerInteraction.Ignore   // 忽略触发器
        );

        // --- 可视化检测盒 (仅用于调试) ---
        // DrawCollisionCheckBox(targetWorldCenter, collisionCheckBoxSize, blocked);


        if (blocked)
        {
            Debug.Log($"物理检测：目标位置 {targetGridPosition} (世界: {targetWorldCenter}) 被阻挡。");
            // 可以在这里加撞墙音效或视觉效果
            return; // 不移动
        }

        // 3. 如果逻辑和物理检查都通过，则移动
        currentGridPosition = targetGridPosition;
        // 注意：我们直接设置到目标中心点，因为是瞬时移动
        carToControl.transform.position = targetWorldCenter;
        // Debug.Log($"向前移动到 {currentGridPosition}"); // 可选日志
    }


    // 尝试左转 (转向通常不需要碰撞检测)
    private void AttemptTurnLeft()
    {
        currentYRotation = NormalizeAngle(currentYRotation - 90f);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);
    }

    // 尝试右转 (转向通常不需要碰撞检测)
    private void AttemptTurnRight()
    {
        currentYRotation = NormalizeAngle(currentYRotation + 90f);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);
    }

    // --- 辅助函数 ---

    // 仅检查是否是逻辑上的道路格子
    private bool IsValidRoadTile(Vector2Int gridPos)
    {
        return validRoadTiles != null && validRoadTiles.Contains(gridPos);
    }

    private Vector2Int GetForwardDirection(float yRotation)
    {
        float angle = NormalizeAngle(yRotation);
        if (Mathf.Abs(angle - 0f) < 1f) return Vector2Int.up;
        if (Mathf.Abs(angle - 90f) < 1f) return Vector2Int.right;
        if (Mathf.Abs(angle - 180f) < 1f) return Vector2Int.down;
        if (Mathf.Abs(angle - 270f) < 1f) return Vector2Int.left;
        return Vector2Int.up;
    }

    private float NormalizeAngle(float angle) { while (angle < 0f) angle += 360f; while (angle >= 360f) angle -= 360f; return angle; }
    private float SnapRotation(float yRotation) { float normalized = NormalizeAngle(yRotation); if (normalized >= 315f || normalized < 45f) return 0f; if (normalized >= 45f && normalized < 135f) return 90f; if (normalized >= 135f && normalized < 225f) return 180f; if (normalized >= 225f && normalized < 315f) return 270f; return 0f; }
    private Vector2Int WorldToGrid(Vector3 worldPos) { return new Vector2Int(Mathf.FloorToInt(worldPos.x), Mathf.FloorToInt(worldPos.z)); }
    private Vector3 GridToWorld(Vector2Int gridPos) { return new Vector3(gridPos.x + 0.5f, carYOffset, gridPos.y + 0.5f); }

    // --- 调试函数：在 Scene 视图中绘制检测盒 ---
    /*
    private void OnDrawGizmos() // 或者 OnDrawGizmosSelected
    {
        if (!Application.isPlaying || carToControl == null) return; // 只在运行时绘制

        // 绘制当前位置的盒子（可选）
        // Gizmos.color = Color.blue;
        // Gizmos.DrawWireCube(carToControl.transform.position, collisionCheckBoxSize);

        // 计算并绘制将要检测的目标位置的盒子
        Vector2Int forwardDirection = GetForwardDirection(currentYRotation);
        Vector2Int targetGridPosition = currentGridPosition + forwardDirection;
        Vector3 targetWorldCenter = GridToWorld(targetGridPosition);

        // 模拟物理检测逻辑，以决定 Gizmo 颜色
        bool isRoad = IsValidRoadTile(targetGridPosition);
        bool blockedByPhysics = false;
        if (isRoad) // 只有当目标是道路时才进行物理检测
        {
             blockedByPhysics = Physics.CheckBox(targetWorldCenter, collisionCheckBoxSize / 2f, Quaternion.identity, ~ignoreLayerMask, QueryTriggerInteraction.Ignore);
        }

        // 根据状态设置颜色
        if (!isRoad) {
            Gizmos.color = Color.gray; // 不是路
        } else if (blockedByPhysics) {
            Gizmos.color = Color.red; // 是路但被物理阻挡
        } else {
            Gizmos.color = Color.green; // 可以移动
        }
        Gizmos.DrawWireCube(targetWorldCenter, collisionCheckBoxSize);
    }

    // 如果需要一个单独的绘制函数
    private void DrawCollisionCheckBox(Vector3 center, Vector3 size, bool isBlocked)
    {
        // 这个函数不能直接在 Update 或 AttemptMoveForward 中调用来绘制 Gizmos
        // Gizmos 只能在 OnDrawGizmos 或 OnDrawGizmosSelected 中绘制
        // 但可以在 OnDrawGizmos 中调用这个逻辑
        Color originalColor = Gizmos.color;
        Gizmos.color = isBlocked ? Color.red : Color.green;
        Gizmos.DrawWireCube(center, size);
        Gizmos.color = originalColor;
    }
    */

    void OnValidate()
    {
        // Optional: Try to find references automatically in editor
        // if (mapGenerator == null) mapGenerator = FindObjectOfType<MapGenerator>();
        // if (carToControl == null) Debug.LogWarning("CarController: Car To Control reference not set.", this);
        // if (placementController == null) placementController = FindObjectOfType<CarPlacementController>();

        // 确保检测盒尺寸至少有一点大小
        collisionCheckBoxSize.x = Mathf.Max(0.1f, collisionCheckBoxSize.x);
        collisionCheckBoxSize.y = Mathf.Max(0.1f, collisionCheckBoxSize.y);
        collisionCheckBoxSize.z = Mathf.Max(0.1f, collisionCheckBoxSize.z);
    }
}