# Unity-PCG-Navigator
**基于分层生成算法 (Voronoi & Perlin) 与自适应 A* 寻路的动态导航系统**

[![Unity](https://img.shields.io/badge/Unity-2022.3+-black.svg?style=flat-square&logo=unity)](https://unity.com/)
[![C#](https://img.shields.io/badge/Language-C%23-blue.svg?style=flat-square&logo=c-sharp)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Algorithm](https://img.shields.io/badge/Algorithm-A*--Optimized-orange.svg?style=flat-square)](https://en.wikipedia.org/wiki/A*_search_algorithm)

## 🚀 项目概述
本项目是一个集成在 Unity 环境中的高级导航解决方案。它展示了如何通过过程生成内容 (PCG) 构建复杂的城市拓扑，并实现了一套能够感知实时路况变化、具备自适应搜索边界的高性能寻路引擎。

---

## 🛠️ 技术核心 (Technical Excellence)

### 1. 分层 PCG 地图生成管线 (Layered PCG Pipeline)
放弃了单一的随机生成，构建了符合城市规划逻辑的双层建模系统：
*   **骨骼层 (Voronoi Topology)**：利用 **Voronoi（泰森多边形）** 空间划分算法切割街区，自动生成城市主干道网络。
*   **功能层 (Perlin Functional Zones)**：在街区内部叠加 **Perlin Noise**，根据噪声阈值程序化分布“工厂（障碍）”、“公园（高代价区）”与“次级路径（小巷）”。
*   **连通性校验**：集成 **Flood Fill（种子填充）** 算法，在生成阶段自动检测并消除逻辑死锁的孤岛区域。

### 2. 深度优化的 A* 寻路引擎 (Optimized Pathfinding)
针对大规模动态网格（150x150+）进行了计算性能与搜索逻辑的深度双优：
*   **空间复杂度优化**：针对 `OpenList` 引入 **最小二叉堆 (Binary Heap)**，将最优节点检索复杂度从 $O(n)$ 降低至 $O(\log n)$。
*   **自适应代价剪枝 (Adaptive Bounded Pruning)**：
    *   **动态基准探路**：系统预先计算最优基准代价 (Base Cost)。
    *   **搜索截断**：在搜索过程中，当某分支的实际累积代价 (G-Value) 超过基准值的容忍倍数时，强制执行 **即时剪枝**，显著降低无效分支的 CPU 遍历开销。
*   **实时动态重规划**：支持实时注入“路况快照”（如事故拥堵、道路损坏）。算法能够感知通行代价的瞬间飙升，并实时计算避障绕行方案。

### 3. 数据驱动的配置体系 (Data-Driven Design)
*   **ScriptableObject 架构**：通过 Unity 的 `ScriptableObject` 将地形代价值（Terrain Costs）、生成权重与算法参数序列化，实现了业务配置与底层寻路逻辑的完全解耦。
