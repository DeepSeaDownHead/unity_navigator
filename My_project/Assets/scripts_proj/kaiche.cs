using UnityEngine;

public class kaiche : MonoBehaviour
{
    [Header("移动设置")]
    [Tooltip("前进/后退的速度")]
    public float moveSpeed = 5.0f; // 前进后退的速度

    [Header("转向设置")]
    [Tooltip("转向速度（度/秒）")]
    public float turnSpeed = 90.0f; // 转向速度，单位度每秒

    [Header("初始位置设置")]
    [Tooltip("设置初始Y轴位置")]
    public float initialYPosition = 0.08f; // 目标Y轴位置
    private bool firstWPressed = false; // 标记是否是第一次按下 W 键

    void Update()
    {
        HandleFirstWPressAndMovement();
        HandleRotation();
    }

    void HandleFirstWPressAndMovement()
    {
        // 处理首次按下 W
        if (!firstWPressed && Input.GetKeyDown(KeyCode.W))
        {
            Vector3 currentPosition = transform.position;
            transform.position = new Vector3(currentPosition.x, initialYPosition, currentPosition.z);
            firstWPressed = true;
            Debug.Log("第一次按下 W，Y 位置已设置为 " + initialYPosition);
        }

        // 处理持续移动
        float moveAmount = moveSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.W))
        {
            transform.Translate(Vector3.forward * moveAmount);
        }
        else if (Input.GetKey(KeyCode.S))
        {
            transform.Translate(Vector3.back * moveAmount);
        }
    }

    void HandleRotation()
    {
        float rotationAmount = turnSpeed * Time.deltaTime;

        if (Input.GetKey(KeyCode.A))
        {
            transform.Rotate(Vector3.up, -rotationAmount);
        }
        else if (Input.GetKey(KeyCode.D))
        {
            transform.Rotate(Vector3.up, rotationAmount);
        }
    }
}