using UnityEngine;

public class ShowBounds : MonoBehaviour
{
    void OnDrawGizmos()
    {
        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(renderer.bounds.center, renderer.bounds.size);
        }
    }
}
