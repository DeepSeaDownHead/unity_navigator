using UnityEngine;

public class kaiche : MonoBehaviour
{
    [Header("�ƶ�����")]
    [Tooltip("ǰ��/���˵��ٶ�")]
    public float moveSpeed = 5.0f; // ǰ�����˵��ٶ�

    [Header("ת������")]
    [Tooltip("ת���ٶȣ���/�룩")]
    public float turnSpeed = 90.0f; // ת���ٶȣ���λ��ÿ��

    [Header("��ʼλ������")]
    [Tooltip("���ó�ʼY��λ��")]
    public float initialYPosition = 0.08f; // Ŀ��Y��λ��
    private bool firstWPressed = false; // ����Ƿ��ǵ�һ�ΰ��� W ��

    void Update()
    {
        HandleFirstWPressAndMovement();
        HandleRotation();
    }

    void HandleFirstWPressAndMovement()
    {
        // �����״ΰ��� W
        if (!firstWPressed && Input.GetKeyDown(KeyCode.W))
        {
            Vector3 currentPosition = transform.position;
            transform.position = new Vector3(currentPosition.x, initialYPosition, currentPosition.z);
            firstWPressed = true;
            Debug.Log("��һ�ΰ��� W��Y λ��������Ϊ " + initialYPosition);
        }

        // ��������ƶ�
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