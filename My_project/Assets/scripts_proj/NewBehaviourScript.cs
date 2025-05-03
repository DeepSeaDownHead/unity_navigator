using UnityEngine;

public class SimpleCarController : MonoBehaviour
{
    [Header("�ƶ�����")]
    [Tooltip("ǰ��/���˵��ٶ�")]
    public float moveSpeed = 5.0f; // ǰ�����˵��ٶ�

    [Header("ת������")]
    [Tooltip("ÿ��ת��ĽǶ�")]
    public float turnAngle = 90.0f; // ÿ��ת��ĽǶ�

    // Update is called once per frame
    void Update()
    {
        HandleMovement();
        HandleRotation();
    }

    // ����ǰ���ͺ���
    void HandleMovement()
    {
        // ���� W ��ǰ��
        if (Input.GetKey(KeyCode.W))
        {
            // ����������ǰ���ƶ�
            // Time.deltaTime ʹ���ƶ���֡���޹أ�����ƽ��
            transform.Translate(Vector3.forward * moveSpeed * Time.deltaTime);
        }
        // ���� S ������
        else if (Input.GetKey(KeyCode.S))
        {
            // ������������ƶ�
            transform.Translate(Vector3.back * moveSpeed * Time.deltaTime);
        }
    }

    // ��������ת��
    void HandleRotation()
    {
        // ���� A ������ת
        // ʹ�� GetKeyDown ȷ��ÿ�ΰ���ֻתһ�Σ������ǰ�סʱ������ת
        if (Input.GetKeyDown(KeyCode.A))
        {
            // Χ������� Y �ᣨ�����ᣩ��תָ���Ƕȣ���ֵ��ʾ����
            transform.Rotate(Vector3.up, -turnAngle);
        }
        // ���� D ������ת
        else if (Input.GetKeyDown(KeyCode.D))
        {
            // Χ������� Y �ᣨ�����ᣩ��תָ���Ƕȣ���ֵ��ʾ���ң�
            transform.Rotate(Vector3.up, turnAngle);
        }
    }
}