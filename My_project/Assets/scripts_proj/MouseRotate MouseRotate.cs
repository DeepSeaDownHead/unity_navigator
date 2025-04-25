using Cinemachine;
using UnityEngine;

public class MouseRotate : MonoBehaviour
{
    [Header("�������")]
    public CinemachineVirtualCamera targetCamera;

    [Header("��ת����")]
    public float mouseSensitivity = 2.0f;
    public Vector2 horizontalClamp = new Vector2(-180, 180);
    public Vector2 verticalClamp = new Vector2(10, 80); // ��������ֱ��ת�Ƕȷ�Χ
    public float smoothTime = 0.1f;

    [Header("ģʽ����")]
    public KeyCode enterModeKey = KeyCode.B;
    public KeyCode exitModeKey = KeyCode.Escape;

    private Transform cameraTransform;
    private float targetRotationY;
    private float currentRotationY;
    private float rotationVelocityY;
    private float targetRotationX; // ������Ŀ�괹ֱ��ת�Ƕ�
    private float currentRotationX; // ��������ǰ��ֱ��ת�Ƕ�
    private float rotationVelocityX; // ��������ֱ��ת�ٶ�
    private bool isMouseControlActive = false;
    private bool isKeyPressed = false;

    void Start()
    {
        if (targetCamera == null)
        {
            Debug.LogError("δָ��Ŀ�������");
            enabled = false;
            return;
        }

        cameraTransform = targetCamera.VirtualCameraGameObject.transform;
        // ���ó�ʼˮƽ��תֵ
        targetRotationY = -35;
        currentRotationY = targetRotationY;
        // ���ó�ʼ��ֱ��תֵ
        targetRotationX = 70;
        currentRotationX = targetRotationX;
        cameraTransform.rotation = Quaternion.Euler(targetRotationX, targetRotationY, 0);

        // ��ʼ״̬Ϊ�ر�
        SetMouseControlMode(false);
    }

    void Update()
    {
        // ģʽ�л����
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

            targetRotationX -= mouseY; // ��ֱ��ת���봦��
            targetRotationX = Mathf.Clamp(targetRotationX, verticalClamp.x, verticalClamp.y);

            currentRotationY = Mathf.SmoothDampAngle(currentRotationY, targetRotationY, ref rotationVelocityY, smoothTime);
            currentRotationX = Mathf.SmoothDampAngle(currentRotationX, targetRotationX, ref rotationVelocityX, smoothTime);

            cameraTransform.rotation = Quaternion.Euler(currentRotationX, currentRotationY, 0);

            Debug.Log($"ˮƽ��ת�Ƕ�: {currentRotationY}, ��ֱ��ת�Ƕ�: {currentRotationX}");
        }
    }

    void SetMouseControlMode(bool active)
    {
        isMouseControlActive = active;
        Cursor.lockState = active ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !active;

        Debug.Log($"������ģʽ: {(active ? "����" : "����")}");
    }

    void OnDestroy()
    {
        SetMouseControlMode(false);
    }
}