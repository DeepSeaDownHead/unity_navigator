
using UnityEngine;
using System.Collections.Generic;

public class CubeStateRetrace : MonoBehaviour
{
    
    private List<State> stateHistory = new List<State>();
   
    private const float recordInterval = 0.1f;
    private float timeSinceLastRecord = 0f;

    
    private struct State
    {
        public Vector3 position;
        public Quaternion rotation;
        public float time;

        public State(Vector3 pos, Quaternion rot, float t)
        {
            position = pos;
            rotation = rot;
            time = t;
        }
    }

    void Update()
    {
        
        timeSinceLastRecord += Time.deltaTime;
        if (timeSinceLastRecord >= recordInterval)
        {
            RecordState();
            timeSinceLastRecord = 0f;
        }

        
        if (Input.GetKeyDown(KeyCode.R))
        {
            RewindToPast(5f);
        }
    }

    
    private void RecordState()
    {
        State currentState = new State(transform.position, transform.rotation, Time.time);
        stateHistory.Add(currentState);

        
        while (stateHistory.Count > 0 && Time.time - stateHistory[0].time > 5f)
        {
            stateHistory.RemoveAt(0);
        }
    }

    
    private void RewindToPast(float seconds)
    {
        float targetTime = Time.time - seconds;
        for (int i = stateHistory.Count - 1; i >= 0; i--)
        {
            if (stateHistory[i].time <= targetTime)
            {
                transform.position = stateHistory[i].position;
                transform.rotation = stateHistory[i].rotation;
                return;
            }
        }
    }
}