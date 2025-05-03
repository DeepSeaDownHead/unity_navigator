using Cinemachine;
using UnityEngine;

public class CameraSwitcher : MonoBehaviour
{
    public CinemachineVirtualCamera cameraHead;    // 游戏视角相机
    public CinemachineVirtualCamera cameraBack;    // 背后视角相机
    private bool isUsingHeadView = true;
    public float blendTime = 0.5f;                // 相机切换时间
    public float rotationSmoothTime = 0.8f;       // 旋转平滑时间
    public GameObject miniMap;

    public GameObject miniMapImage; // 小地图图像
    public GameObject miniMapMask; // 小地图遮罩层

    void Start()
    {
        miniMapImage = GameObject.Find("MiniMapImage");
        miniMapMask = GameObject.Find("MiniMapMask");

        CinemachineBrain brain = Camera.main.GetComponent<CinemachineBrain>();
        if (brain != null)
        {
            brain.m_DefaultBlend = new CinemachineBlendDefinition(
                CinemachineBlendDefinition.Style.EaseInOut,
                blendTime
            );
        }

        // 设置相机平滑
        SetupCameraSmoothing(cameraHead);
        SetupCameraSmoothing(cameraBack);

        if (miniMapImage != null && miniMapMask != null)
        {
            miniMapImage.SetActive(true); // 显示小地图图像
            miniMapMask.SetActive(true); // 显示小地图遮罩层
            
        }
        else
        {
            Debug.LogError("MiniMapImage or MiniMapMask not found!");
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            if (isUsingHeadView)
            {
                cameraHead.Priority = 0;
                cameraBack.Priority = 10;
                isUsingHeadView = false;
                if (miniMapImage != null && miniMapMask != null)
                {
                    miniMapImage.SetActive(false); // 隐藏小地图图像
                    miniMapMask.SetActive(false); // 隐藏小地图遮罩层
                }         
            }
            else
            {
                cameraBack.Priority = 0;
                cameraHead.Priority = 10;
                isUsingHeadView = true;
                if (miniMapImage != null && miniMapMask != null)
                {
                    miniMapImage.SetActive(true); // 显示小地图图像
                    miniMapMask.SetActive(true); // 显示小地图遮罩层
                    
                }
            }
        }
    }

    // 设置相机旋转平滑
    private void SetupCameraSmoothing(CinemachineVirtualCamera vcam)
    {
        // 获取 CinemachineOrbitalTransposer 组件
        var orbitalTransposer = vcam.GetCinemachineComponent<CinemachineOrbitalTransposer>();
        if (orbitalTransposer != null)
        {
            orbitalTransposer.m_XAxis.m_MaxSpeed = 0f;
            orbitalTransposer.m_RecenterToTargetHeading.m_RecenteringTime = rotationSmoothTime;
        }

        // 获取 CinemachineTransposer 组件
        var transposer = vcam.GetCinemachineComponent<CinemachineTransposer>();
        if (transposer != null)
        {
            transposer.m_XDamping = rotationSmoothTime;
            transposer.m_YDamping = rotationSmoothTime;
            transposer.m_ZDamping = rotationSmoothTime;
        }
    }
}