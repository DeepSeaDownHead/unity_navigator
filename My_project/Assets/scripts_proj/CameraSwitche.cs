using Cinemachine;
using UnityEngine;

public class CameraSwitcher : MonoBehaviour
{
    public CinemachineVirtualCamera camera1;
    public CinemachineVirtualCamera camera2;
    
    public GameObject miniMap = GameObject.Find("MiniMap");
    private bool isUsingCamera1 = true;

    void Update()
    {

        if (Input.GetKeyDown(KeyCode.C))
        {
            if (isUsingCamera1)
            {
                camera1.Priority = 0;
                camera2.Priority = 10;
                isUsingCamera1 = false;
                miniMap.SetActive(false);
            }
            else
            {
                camera2.Priority = 0;
                camera1.Priority = 10;
                isUsingCamera1 = true;
                miniMap.SetActive(true);
            }
        }
    }
}