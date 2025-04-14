using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Provides a generic A* pathfinding algorithm implementation.
/// Can be used with different cost functions and walkability checks via delegates.
/// Attach this component alongside the script that needs pathfinding (e.g., MapGenerator).
/// </summary>
public class AStar : MonoBehaviour
{
    // Internal class representing a node in the A* search space
    private class PathNode
    {
        public Vector2Int position; // Grid position
        public float gScore;       // Cost from start to this node
        public float hScore;       // Heuristic cost from this node to target
        public float fScore => gScore + hScore; // Total estimated cost
        public PathNode parent;     // Node from which we reached this node

        public PathNode(Vector2Int pos, float g, float h, PathNode p)
        {
            position = pos;
            gScore = g;
            hScore = h;
            parent = p;
        }

        // Optional: Override Equals and GetHashCode if using PathNode directly in HashSets/Dictionaries
        // (Currently using Vector2Int as the key in nodeMap, so not strictly required)
        public override bool Equals(object obj) => obj is PathNode node && position.Equals(node.position);
        public override int GetHashCode() => position.GetHashCode();
    }

    // --- Public Pathfinding Method ---

    /// <summary>
    /// Finds the lowest cost path from start to end using the A* algorithm.
    /// </summary>
    /// <param name="start">Starting grid position.</param>
    /// <param name="end">Ending grid position.</param>
    /// <param name="getCostFunction">A function(Vector2Int pos) that returns the cost to ENTER the given tile. Should return float.PositiveInfinity for impassable tiles.</param>
    /// <param name="isWalkableFunction">A function(Vector2Int pos) that returns true if a given tile is potentially traversable (e.g., within bounds, is a road). Can overlap with cost function logic, but provides an early exit.</param>
    /// <param name="heuristicMultiplier">Optional multiplier for the heuristic (Manhattan distance). Useful for tuning behavior (e.g., use base tile cost).</param>
    /// <returns>A list of Vector2Int representing the path coordinates from start to end (inclusive), or null if no path is found.</returns>
    public List<Vector2Int> FindPath(
        Vector2Int start,
        Vector2Int end,
        Func<Vector2Int, float> getCostFunction,
        Func<Vector2Int, bool> isWalkableFunction,
        float heuristicMultiplier = 1.0f)
    {
        // --- Input Validation ---
        if (getCostFunction == null)
        {
            Debug.LogError("A* Error: getCostFunction delegate is null.");
            return null;
        }
        if (isWalkableFunction == null)
        {
             Debug.LogError("A* Error: isWalkableFunction delegate is null.");
             return null;
        }

        // Check walkability of start/end using the provided function.
        // It's okay if they aren't strictly "walkable" by this function
        // as long as getCostFunction handles them correctly (returns a finite cost).
        if (!isWalkableFunction(start))
        {
            Debug.LogWarning($"A* Warning: Start position {start} reported as not walkable by isWalkableFunction. Pathfinding might fail if getCostFunction also considers it impassable.");
            // Check cost explicitly for start node here if needed:
            // if (float.IsPositiveInfinity(getCostFunction(start))) { Debug.LogError("A*: Start node is impassable based on cost."); return null; }
        }
        if (!isWalkableFunction(end))
        {
             Debug.LogWarning($"A* Warning: End position {end} reported as not walkable by isWalkableFunction. Pathfinding will proceed, but check target node handling.");
        }


        // Handle trivial case: Start and End are the same
        if (start == end)
        {
            // Return a path containing only the start point if it's considered valid by cost
             if (!float.IsPositiveInfinity(getCostFunction(start)))
             {
                return new List<Vector2Int> { start };
             }
             else
             {
                 Debug.LogWarning($"A*: Start and End are the same ({start}), but the tile is impassable according to getCostFunction.");
                 return null;
             }
        }

        // --- Data Structures ---
        // Open Set: Nodes to be evaluated. Using List.Sort is simple but inefficient for large maps.
        // Consider a Min-Heap/Priority Queue (e.g., from C5 collections or a custom implementation)
        // for significant performance gains (O(log N) vs O(N) for finding minimum).
        List<PathNode> openSet = new List<PathNode>();

        // Closed Set: Positions already evaluated with the cheapest known path.
        HashSet<Vector2Int> closedSet = new HashSet<Vector2Int>();

        // Node Map: Stores the best known PathNode for each visited position (Vector2Int -> PathNode).
        // Allows efficiently finding existing nodes and updating their paths if a cheaper one is found.
        Dictionary<Vector2Int, PathNode> nodeMap = new Dictionary<Vector2Int, PathNode>();

        // Neighbor Directions (Cardinal)
        Vector2Int[] directions = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        // Add diagonals if needed: new Vector2Int(1, 1), new Vector2Int(1, -1), etc.
        // Remember to adjust cost calculation for diagonals if they aren't uniform.

        // --- Initialization ---
        // Target set for heuristic calculation (currently just the single end point)
        HashSet<Vector2Int> targets = new HashSet<Vector2Int> { end };

        // Calculate heuristic for the start node (estimated cost from start to end)
        // gScore is 0 for the start node.
        float startHScore = Heuristic(start, targets) * heuristicMultiplier;
        PathNode startNode = new PathNode(start, 0f, startHScore, null);

        openSet.Add(startNode);
        nodeMap[start] = startNode;

        // --- A* Search Loop ---
        int iterations = 0;
        // Safety break: Estimate max iterations based on map size or use a large default
        int maxIterations = (500 * 500) * 4; // Generous estimate for a 500x500 map

        while (openSet.Count > 0)
        {
            iterations++;
            if (iterations > maxIterations)
            {
                Debug.LogError($"A* pathfinding exceeded max iterations ({maxIterations}) from {start} to {end}. Aborting search. Check for inaccessible targets or cost function errors.");
                return null; // Prevent potential infinite loop
            }

            // --- Get Node with Lowest fScore ---
            // Performance Bottleneck: Sorting the list each time. Replace with Priority Queue for large maps.
            openSet.Sort((a, b) => a.fScore.CompareTo(b.fScore));
            PathNode current = openSet[0];
            openSet.RemoveAt(0); // Dequeue the node with the lowest fScore

            // --- Goal Check ---
            if (current.position == end)
            {
                // Path found! Reconstruct and return it.
                // Debug.Log($"A* Path Found: {start} -> {end} in {iterations} iterations.");
                return ReconstructPath(current);
            }

            // --- Process Current Node ---
            // Move the current node's position to the closed set (it's been fully processed).
            closedSet.Add(current.position);

            // --- Explore Neighbors ---
            foreach (var dir in directions)
            {
                Vector2Int neighborPos = current.position + dir;

                // --- Neighbor Validation ---
                // 1. Skip if already fully evaluated and in the closed set.
                if (closedSet.Contains(neighborPos))
                {
                    continue;
                }

                // 2. Basic walkability check (e.g., within map bounds, is a road tile).
                //    Provides an early out before potentially expensive cost calculation.
                if (!isWalkableFunction(neighborPos))
                {
                    continue;
                }

                // --- Calculate Cost to Enter Neighbor ---
                // 3. Get the actual cost to *enter* the neighbor tile using the provided delegate.
                float moveCost = getCostFunction(neighborPos);

                // 4. Skip if impassable based on the cost function (e.g., cost is infinity).
                if (float.IsPositiveInfinity(moveCost))
                {
                    continue;
                }

                // --- Calculate Path Cost (gScore) to Neighbor via Current Node ---
                // gScore = Cost from start to 'current' + Cost to move from 'current' to 'neighbor'
                float tentativeGScore = current.gScore + moveCost;

                // --- Compare with Existing Path (if neighbor was already seen) ---
                // Try to get the existing node for this neighbor position from our map.
                nodeMap.TryGetValue(neighborPos, out PathNode neighborNode);

                // If this neighbor hasn't been seen before (neighborNode is null), OR
                // if the path *through the current node* (tentativeGScore) is cheaper
                // than any previously known path to this neighbor (neighborNode.gScore).
                if (neighborNode == null || tentativeGScore < neighborNode.gScore)
                {
                    // This path is better (or it's the first path found). Update/Create the node.

                    // Calculate heuristic for the neighbor (estimated cost from neighbor to target).
                    float neighborHScore = Heuristic(neighborPos, targets) * heuristicMultiplier;

                    if (neighborNode == null) // Node is entirely new
                    {
                        // Create a new node with the calculated scores and set its parent to 'current'.
                        neighborNode = new PathNode(neighborPos, tentativeGScore, neighborHScore, current);
                        // Add it to the map for tracking.
                        nodeMap.Add(neighborPos, neighborNode);
                        // Add it to the open set for future evaluation.
                        openSet.Add(neighborNode);
                    }
                    else // Node already existed (was in openSet or previously reached via a more expensive path)
                    {
                        // Update the existing node with the new, lower gScore.
                        neighborNode.gScore = tentativeGScore;
                        // hScore remains the same.
                        // fScore updates automatically via the property.
                        // Crucially, update the parent to 'current' because this node provides the cheaper path.
                        neighborNode.parent = current;

                        // NOTE: If using a Min-Heap/Priority Queue, this is where you would typically
                        // call an DecreaseKey or Update operation if the node was already in the queue.
                        // Since we sort the List each time, no explicit update action is needed here,
                        // but finding the node to update would be inefficient without the nodeMap.
                    }
                }
            } // End foreach neighbor
        } // End while openSet not empty

        // --- Path Not Found ---
        // If the open set becomes empty and the target was never reached.
        Debug.LogWarning($"A* could not find a path from {start} to {end}. Open set became empty after {iterations} iterations.");
        return null;
    }

    // --- Private Helper Methods ---

    /// <summary>
    /// Heuristic function (Manhattan Distance). Estimates cost from current to nearest target.
    /// Admissible & Consistent for grid maps with cardinal movement if cost >= 1.
    /// The result is multiplied by heuristicMultiplier in the main loop.
    /// </summary>
    private float Heuristic(Vector2Int current, HashSet<Vector2Int> targets)
    {
        // Optimized for single target scenario which is most common here.
        if (targets.Count == 1)
        {
            // Calculate Manhattan distance (deltaX + deltaY)
            Vector2Int target = targets.First();
            return Mathf.Abs(current.x - target.x) + Mathf.Abs(current.y - target.y);
        }

        // Fallback for multiple targets (if needed in the future)
        if (targets == null || targets.Count == 0) return 0f; // No target, no heuristic cost.

        float minDistance = float.MaxValue;
        foreach (var target in targets)
        {
            float dist = Mathf.Abs(current.x - target.x) + Mathf.Abs(current.y - target.y);
            if (dist < minDistance)
            {
                minDistance = dist;
            }
        }
        return minDistance;
    }

    // ManhattanDistance helper (extracted for clarity, used in Heuristic)
    // private int ManhattanDistance(Vector2Int a, Vector2Int b)
    // {
    //     return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    // }

    /// <summary>
    /// Reconstructs the path from the target node back to the start using parent pointers.
    /// </summary>
    private List<Vector2Int> ReconstructPath(PathNode targetNode)
    {
        List<Vector2Int> totalPath = new List<Vector2Int>();
        PathNode current = targetNode;
        int safetyBreak = 0;
        // Estimate max path length based on map dimensions or use a large constant
        int maxLen = (500 * 500) * 2 + 1; // Generous buffer

        while (current != null && safetyBreak < maxLen)
        {
            totalPath.Add(current.position);
            current = current.parent; // Move to the previous node in the path
            safetyBreak++;
        }

        if (safetyBreak >= maxLen)
        {
            Debug.LogError("A* Path reconstruction exceeded max length! Possible cycle or error in parent pointers. Returning partial path or null.");
            // Depending on requirements, you might return the partial path or null.
            // totalPath.Reverse(); return totalPath; // Return partial
            return null; // Indicate failure
        }

        // The path is constructed backwards (end to start), so reverse it before returning.
        totalPath.Reverse();
        return totalPath;
    }
}