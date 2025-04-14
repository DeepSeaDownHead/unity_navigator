using Cinemachine;
using UnityEngine;

public class CameraSwitcher : MonoBehaviour
{
    public CinemachineVirtualCamera cameraHead;    // ���Ϸ��ӽ�
    public CinemachineVirtualCamera cameraBack;    // б���ӽ�
    private bool isUsingHeadView = true;
    public float blendTime = 0.5f;                // ��������ʱ��
    public float rotationSmoothTime = 0.8f;       // ��תƽ��ʱ��

    void Start()
    {
        
        CinemachineBrain brain = Camera.main.GetComponent<CinemachineBrain>();
        if (brain != null)
        {
            brain.m_DefaultBlend = new CinemachineBlendDefinition(
                CinemachineBlendDefinition.Style.EaseInOut,
                blendTime
            );
        }

        
        SetupCameraSmoothing(cameraHead);
        SetupCameraSmoothing(cameraBack);
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
            }
            else
            {
                cameraBack.Priority = 0;
                cameraHead.Priority = 10;
                isUsingHeadView = true;
            }
        }
    }

    // ���������תƽ��
    private void SetupCameraSmoothing(CinemachineVirtualCamera vcam)
    {
        
        var orbitalTransposer = vcam.GetCinemachineComponent<CinemachineOrbitalTransposer>();
        if (orbitalTransposer != null)
        {
            orbitalTransposer.m_XAxis.m_MaxSpeed = 0f; 
            orbitalTransposer.m_RecenterToTargetHeading.m_RecenteringTime = rotationSmoothTime;
        }

        //
        var transposer = vcam.GetCinemachineComponent<CinemachineTransposer>();
        if (transposer != null)
        {
            transposer.m_XDamping = rotationSmoothTime;
            transposer.m_YDamping = rotationSmoothTime;
            transposer.m_ZDamping = rotationSmoothTime;
        }
    }
}