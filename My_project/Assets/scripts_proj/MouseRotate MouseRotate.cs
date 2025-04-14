using Cinemachine;
using UnityEngine;

public class FreeLookMouseRotate : MonoBehaviour
{
    [Header("相机配置")]
    public CinemachineVirtualCamera targetCamera;

    [Header("旋转设置")]
    public float mouseSensitivity = 2.0f;
    public Vector2 horizontalClamp = new Vector2(-180, 180);
    public float smoothTime = 0.1f;

    [Header("模式控制")]
    public KeyCode enterModeKey = KeyCode.B;
    public KeyCode exitModeKey = KeyCode.Escape;

    private Transform cameraTransform;
    private float targetRotationY;
    private float currentRotationY;
    private float rotationVelocityY;
    private bool isMouseControlActive = false;
    private bool isKeyPressed = false;

    void Start()
    {
        if (targetCamera == null)
        {
            Debug.LogError("未指定目标相机！");
            enabled = false;
            return;
        }

        cameraTransform = targetCamera.VirtualCameraGameObject.transform;
        // 设置初始水平旋转值
        targetRotationY = -35;
        currentRotationY = targetRotationY;
        // 固定垂直方向旋转为70
        cameraTransform.rotation = Quaternion.Euler(70, targetRotationY, 0);

        // 初始状态为关闭
        SetMouseControlMode(false);
    }

    void Update()
    {
        // 模式切换检测
        if (Input.GetKeyDown(enterModeKey) && !isMouseControlActive)
        {
            SetMouseControlMode(true);
            isKeyPressed = true;
        }
        else if (Input.GetKeyDown(exitModeKey) && isMouseControlActive)
        {
            SetMouseControlMode(false);
            isKeyPressed = true;
        }

        if (Input.GetKeyUp(enterModeKey) || Input.GetKeyUp(exitModeKey))
        {
            isKeyPressed = false;
        }
    }

    void LateUpdate()
    {
        if (!isMouseControlActive || targetCamera == null) return;

        if (!isKeyPressed)
        {
            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;

            targetRotationY += mouseX;
            targetRotationY = Mathf.Clamp(targetRotationY, horizontalClamp.x, horizontalClamp.y);

            currentRotationY = Mathf.SmoothDampAngle(currentRotationY, targetRotationY, ref rotationVelocityY, smoothTime);
            // 保持垂直方向70不变，只更新水平旋转
            cameraTransform.rotation = Quaternion.Euler(70, currentRotationY, 0);

            Debug.Log($"水平旋转角度: {currentRotationY}");
        }
    }

    void SetMouseControlMode(bool active)
    {
        isMouseControlActive = active;
        Cursor.lockState = active ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !active;

        Debug.Log($"鼠标控制模式: {(active ? "启用" : "禁用")}");
    }

    void OnDestroy()
    {
        SetMouseControlMode(false);
    }
}