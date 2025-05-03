using UnityEngine;

public class SimpleCarController : MonoBehaviour
{
    [Header("移动设置")]
    [Tooltip("前进/后退的速度")]
    public float moveSpeed = 5.0f; // 前进后退的速度

    [Header("转向设置")]
    [Tooltip("每次转向的角度")]
    public float turnAngle = 90.0f; // 每次转向的角度

    // Update is called once per frame
    void Update()
    {
        HandleMovement();
        HandleRotation();
    }

    // 处理前进和后退
    void HandleMovement()
    {
        // 按下 W 键前进
        if (Input.GetKey(KeyCode.W))
        {
            // 向物体自身前方移动
            // Time.deltaTime 使得移动与帧率无关，更加平滑
            transform.Translate(Vector3.forward * moveSpeed * Time.deltaTime);
        }
        // 按下 S 键后退
        else if (Input.GetKey(KeyCode.S))
        {
            // 向物体自身后方移动
            transform.Translate(Vector3.back * moveSpeed * Time.deltaTime);
        }
    }

    // 处理左右转向
    void HandleRotation()
    {
        // 按下 A 键向左转
        // 使用 GetKeyDown 确保每次按下只转一次，而不是按住时持续旋转
        if (Input.GetKeyDown(KeyCode.A))
        {
            // 围绕自身的 Y 轴（向上轴）旋转指定角度（负值表示向左）
            transform.Rotate(Vector3.up, -turnAngle);
        }
        // 按下 D 键向右转
        else if (Input.GetKeyDown(KeyCode.D))
        {
            // 围绕自身的 Y 轴（向上轴）旋转指定角度（正值表示向右）
            transform.Rotate(Vector3.up, turnAngle);
        }
    }
}