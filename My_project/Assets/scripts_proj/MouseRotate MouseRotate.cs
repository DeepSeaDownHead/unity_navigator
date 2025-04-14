using Cinemachine;
using UnityEngine;

public class FreeLookMouseRotate : MonoBehaviour
{
    [Header("�������")]
    public CinemachineVirtualCamera targetCamera;

    [Header("��ת����")]
    public float mouseSensitivity = 2.0f;
    public Vector2 horizontalClamp = new Vector2(-180, 180);
    public float smoothTime = 0.1f;

    [Header("ģʽ����")]
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
            Debug.LogError("δָ��Ŀ�������");
            enabled = false;
            return;
        }

        cameraTransform = targetCamera.VirtualCameraGameObject.transform;
        // ���ó�ʼˮƽ��תֵ
        targetRotationY = -35;
        currentRotationY = targetRotationY;
        // �̶���ֱ������תΪ70
        cameraTransform.rotation = Quaternion.Euler(70, targetRotationY, 0);

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

            targetRotationY += mouseX;
            targetRotationY = Mathf.Clamp(targetRotationY, horizontalClamp.x, horizontalClamp.y);

            currentRotationY = Mathf.SmoothDampAngle(currentRotationY, targetRotationY, ref rotationVelocityY, smoothTime);
            // ���ִ�ֱ����70���䣬ֻ����ˮƽ��ת
            cameraTransform.rotation = Quaternion.Euler(70, currentRotationY, 0);

            Debug.Log($"ˮƽ��ת�Ƕ�: {currentRotationY}");
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