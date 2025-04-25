using Cinemachine;
using UnityEngine;

public class MouseRotate : MonoBehaviour
{
    [Header("相机配置")]
    public CinemachineVirtualCamera targetCamera;

    [Header("旋转设置")]
    public float mouseSensitivity = 2.0f;
    public Vector2 horizontalClamp = new Vector2(-180, 180);
    public Vector2 verticalClamp = new Vector2(10, 80); // 新增：垂直旋转角度范围
    public float smoothTime = 0.1f;

    [Header("模式控制")]
    public KeyCode enterModeKey = KeyCode.B;
    public KeyCode exitModeKey = KeyCode.Escape;

    private Transform cameraTransform;
    private float targetRotationY;
    private float currentRotationY;
    private float rotationVelocityY;
    private float targetRotationX; // 新增：目标垂直旋转角度
    private float currentRotationX; // 新增：当前垂直旋转角度
    private float rotationVelocityX; // 新增：垂直旋转速度
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
        // 设置初始垂直旋转值
        targetRotationX = 70;
        currentRotationX = targetRotationX;
        cameraTransform.rotation = Quaternion.Euler(targetRotationX, targetRotationY, 0);

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
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            targetRotationY += mouseX;
            targetRotationY = Mathf.Clamp(targetRotationY, horizontalClamp.x, horizontalClamp.y);

            targetRotationX -= mouseY; // 垂直旋转输入处理
            targetRotationX = Mathf.Clamp(targetRotationX, verticalClamp.x, verticalClamp.y);

            currentRotationY = Mathf.SmoothDampAngle(currentRotationY, targetRotationY, ref rotationVelocityY, smoothTime);
            currentRotationX = Mathf.SmoothDampAngle(currentRotationX, targetRotationX, ref rotationVelocityX, smoothTime);

            cameraTransform.rotation = Quaternion.Euler(currentRotationX, currentRotationY, 0);

            Debug.Log($"水平旋转角度: {currentRotationY}, 垂直旋转角度: {currentRotationX}");
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