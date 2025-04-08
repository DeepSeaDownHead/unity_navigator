using UnityEngine;

public class CameraVerticalFollow : MonoBehaviour
{
    
    public Transform target;
    
    public float verticalOffset = 5f;
    
    public float smoothSpeed = 0.125f;

    void LateUpdate()
    {
        if (target == null)
        {
            return;
        }

        
        Vector3 targetPosition = target.position;
        targetPosition.y += verticalOffset;

       
        Vector3 smoothedPosition = Vector3.Lerp(transform.position, targetPosition, smoothSpeed);
        transform.position = smoothedPosition;

        
        transform.LookAt(target);
    }
}