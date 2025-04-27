using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// ʹ�� WASD ���봦��ָ��С���Ľֻ���񡢻��ڸ��ӵ��ƶ���
/// ��Ҫ�� MapGenerator �� С�� GameObject �����á�
/// �ƶ�����������Ч�ĵ�·�����ϣ����һ�����������Ա���ײǽ������
/// </summary>
public class CarController : MonoBehaviour
{
    [Header("���� (References)")]
    [Tooltip("�� MapGenerator �����ã��Է��ʵ�·���ݡ�")]
    [SerializeField] private MapGenerator mapGenerator;
    [Tooltip("�˽ű������Ƶ�С�� GameObject��")]
    [SerializeField] private GameObject carToControl;
    [Tooltip("�� CarPlacementController �����ã��Ի�ȡ Y ��ƫ��������ѡ�����Ƽ�����")]
    [SerializeField] private CarPlacementController placementController;

    [Header("�ƶ����� (Movement Settings)")]
    [Tooltip("ִ��һ���������ٴδ����������ȴʱ�䣨��ֹ�����ƶ�����")]
    [SerializeField] private float inputCooldown = 0.15f;
    [Tooltip("����������ļ��гߴ磨����ƥ��С����ײ�壩��X �� Z Ӧ���Ǹ��Ӵ�С��Y ����Сһ�㡣")]
    [SerializeField] private Vector3 collisionCheckBoxSize = new Vector3(0.8f, 0.5f, 0.8f); // �ȸ�����С��ֹ��Ե����
    [Tooltip("������ʱҪ���ԵĲ㣨ͨ����С���������ڵĲ㣩��")]
    [SerializeField] private LayerMask ignoreLayerMask = default; // ����ΪС�����ڵĲ�

    // �ڲ�״̬
    private Vector2Int currentGridPosition;
    private float currentYRotation = 0f;
    private bool isProcessingInput = false;
    private float carYOffset = 0.1f;
    private HashSet<Vector2Int> validRoadTiles = null;
    private Collider carCollider; // ����С���������ײ�壬�Ա��ڼ��ʱ������

    public void Initialize()
    {
        if (carToControl == null) { Debug.LogError("CarController: δָ��Ҫ���Ƶ�С��!", this); this.enabled = false; return; }
        if (mapGenerator == null) { Debug.LogError("CarController: δָ�� MapGenerator!", this); this.enabled = false; return; }

        if (placementController != null) { carYOffset = placementController.GetCarPlacementYOffset(); }
        else { Debug.LogWarning("CarController: δ���� CarPlacementController ���á�ʹ��Ĭ�� Y ƫ�ơ�", this); }

        // ��ȡ������С����ײ��
        carCollider = carToControl.GetComponent<Collider>();
        if (carCollider == null)
        {
            Debug.LogWarning("CarController: ���Ƶ�С����û���ҵ� Collider����������ܲ�׼ȷ��", this);
        }

        currentGridPosition = WorldToGrid(carToControl.transform.position);
        float initialYRotation = carToControl.transform.rotation.eulerAngles.y;
        currentYRotation = SnapRotation(initialYRotation);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);

        validRoadTiles = mapGenerator.GetRoadTiles();
        if (validRoadTiles == null || validRoadTiles.Count == 0) { Debug.LogError("CarController: ��ȡ��Ч��·����ʧ��!", this); this.enabled = false; return; }

        isProcessingInput = false;
        Debug.Log($"CarController ��ʼ����ɡ���ʼλ��: {currentGridPosition}, ��ʼ��ת: {currentYRotation}", this);
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

    // --- �����ƶ��߼��޸� ---
    private void AttemptMoveForward()
    {
        Vector2Int forwardDirection = GetForwardDirection(currentYRotation);
        Vector2Int targetGridPosition = currentGridPosition + forwardDirection;

        // 1. �߼���飺Ŀ������Ƿ��ǵ�·��
        if (!IsValidRoadTile(targetGridPosition))
        {
            // Debug.Log($"Ŀ����� {targetGridPosition} ���ǵ�·��"); // ��ѡ��־
            // �����������ײǽ��Ч���Ӿ�Ч��
            return; // ���ƶ�
        }

        // 2. �����飺Ŀ������λ���Ƿ��赲��
        Vector3 targetWorldCenter = GridToWorld(targetGridPosition); // ��ȡĿ����ӵ��������ĵ�

        // ʹ�� Physics.CheckBox ���м��
        // QueryTriggerInteraction.Ignore ��ʾ����ⴥ����
        // ~ignoreLayerMask ��ʾ������ ignoreLayerMask ֮������в�
        bool blocked = Physics.CheckBox(
            targetWorldCenter,               // ���е�����
            collisionCheckBoxSize / 2f,      // ���еİ�ߴ� (Extents)
            Quaternion.identity,             // ���е���ת (ͨ������Ҫ��ת)
            ~ignoreLayerMask,                // ���Ĳ㣺���˺��Բ�֮������в�
            QueryTriggerInteraction.Ignore   // ���Դ�����
        );

        // --- ���ӻ����� (�����ڵ���) ---
        // DrawCollisionCheckBox(targetWorldCenter, collisionCheckBoxSize, blocked);


        if (blocked)
        {
            Debug.Log($"�����⣺Ŀ��λ�� {targetGridPosition} (����: {targetWorldCenter}) ���赲��");
            // �����������ײǽ��Ч���Ӿ�Ч��
            return; // ���ƶ�
        }

        // 3. ����߼��������鶼ͨ�������ƶ�
        currentGridPosition = targetGridPosition;
        // ע�⣺����ֱ�����õ�Ŀ�����ĵ㣬��Ϊ��˲ʱ�ƶ�
        carToControl.transform.position = targetWorldCenter;
        // Debug.Log($"��ǰ�ƶ��� {currentGridPosition}"); // ��ѡ��־
    }


    // ������ת (ת��ͨ������Ҫ��ײ���)
    private void AttemptTurnLeft()
    {
        currentYRotation = NormalizeAngle(currentYRotation - 90f);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);
    }

    // ������ת (ת��ͨ������Ҫ��ײ���)
    private void AttemptTurnRight()
    {
        currentYRotation = NormalizeAngle(currentYRotation + 90f);
        carToControl.transform.rotation = Quaternion.Euler(0, currentYRotation, 0);
    }

    // --- �������� ---

    // ������Ƿ����߼��ϵĵ�·����
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

    // --- ���Ժ������� Scene ��ͼ�л��Ƽ��� ---
    /*
    private void OnDrawGizmos() // ���� OnDrawGizmosSelected
    {
        if (!Application.isPlaying || carToControl == null) return; // ֻ������ʱ����

        // ���Ƶ�ǰλ�õĺ��ӣ���ѡ��
        // Gizmos.color = Color.blue;
        // Gizmos.DrawWireCube(carToControl.transform.position, collisionCheckBoxSize);

        // ���㲢���ƽ�Ҫ����Ŀ��λ�õĺ���
        Vector2Int forwardDirection = GetForwardDirection(currentYRotation);
        Vector2Int targetGridPosition = currentGridPosition + forwardDirection;
        Vector3 targetWorldCenter = GridToWorld(targetGridPosition);

        // ģ���������߼����Ծ��� Gizmo ��ɫ
        bool isRoad = IsValidRoadTile(targetGridPosition);
        bool blockedByPhysics = false;
        if (isRoad) // ֻ�е�Ŀ���ǵ�·ʱ�Ž���������
        {
             blockedByPhysics = Physics.CheckBox(targetWorldCenter, collisionCheckBoxSize / 2f, Quaternion.identity, ~ignoreLayerMask, QueryTriggerInteraction.Ignore);
        }

        // ����״̬������ɫ
        if (!isRoad) {
            Gizmos.color = Color.gray; // ����·
        } else if (blockedByPhysics) {
            Gizmos.color = Color.red; // ��·���������赲
        } else {
            Gizmos.color = Color.green; // �����ƶ�
        }
        Gizmos.DrawWireCube(targetWorldCenter, collisionCheckBoxSize);
    }

    // �����Ҫһ�������Ļ��ƺ���
    private void DrawCollisionCheckBox(Vector3 center, Vector3 size, bool isBlocked)
    {
        // �����������ֱ���� Update �� AttemptMoveForward �е��������� Gizmos
        // Gizmos ֻ���� OnDrawGizmos �� OnDrawGizmosSelected �л���
        // �������� OnDrawGizmos �е�������߼�
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

        // ȷ�����гߴ�������һ���С
        collisionCheckBoxSize.x = Mathf.Max(0.1f, collisionCheckBoxSize.x);
        collisionCheckBoxSize.y = Mathf.Max(0.1f, collisionCheckBoxSize.y);
        collisionCheckBoxSize.z = Mathf.Max(0.1f, collisionCheckBoxSize.z);
    }
}