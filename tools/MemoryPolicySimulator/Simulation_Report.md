# Offline Memory Read Policy Simulation Report (Corrected Hierarchical Model)
Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2 (Exact-First)

Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (FetchedBytes * 1)`

## 1. Skill Workload (Actor + ActiveSkills + Cooldowns)

### Scale: 10 items (134 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,156 B | 134 | 1,156 B | 1.00x | 0 B | 135,156 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,156 B | 4 | 16,384 B | 14.17x | 15,228 B | 20,384 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 1,156 B | 19 | 6,232 B | 5.39x | 5,076 B | 25,232 |

### Scale: 100 items (1304 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,516 B | 1,304 | 10,516 B | 1.00x | 0 B | 1,314,516 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 10,516 B | 14 | 57,344 B | 5.45x | 46,828 B | 71,344 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 10,516 B | 117 | 47,500 B | 4.52x | 36,984 B | 164,500 |

### Scale: 500 items (6504 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 52,116 B | 6,504 | 52,116 B | 1.00x | 0 B | 6,556,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 52,116 B | 57 | 233,472 B | 4.48x | 181,356 B | 290,472 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 52,116 B | 553 | 232,448 B | 4.46x | 180,332 B | 785,448 |

### Scale: 1000 items (13004 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 104,116 B | 13,004 | 104,116 B | 1.00x | 0 B | 13,108,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 104,116 B | 112 | 458,752 B | 4.41x | 354,636 B | 570,752 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 104,116 B | 1,100 | 463,548 B | 4.45x | 359,432 B | 1,563,548 |

## 2. Entity / Component Workload (Dense Component Clusters)

### Scale: 10 items (180 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 3,000 B | 180 | 3,000 B | 1.00x | 0 B | 183,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 3,000 B | 15 | 61,440 B | 20.48x | 58,440 B | 76,440 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 3,000 B | 60 | 17,520 B | 5.84x | 14,520 B | 77,520 |

### Scale: 100 items (1800 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 B | 1,800 | 30,000 B | 1.00x | 0 B | 1,830,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 30,000 B | 150 | 614,400 B | 20.48x | 584,400 B | 764,400 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 30,000 B | 600 | 175,200 B | 5.84x | 145,200 B | 775,200 |

### Scale: 500 items (9000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 B | 9,000 | 150,000 B | 1.00x | 0 B | 9,150,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 150,000 B | 750 | 3,072,000 B | 20.48x | 2,922,000 B | 3,822,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 150,000 B | 3,000 | 876,000 B | 5.84x | 726,000 B | 3,876,000 |

### Scale: 1000 items (18000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 300,000 B | 18,000 | 300,000 B | 1.00x | 0 B | 18,300,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 300,000 B | 1,500 | 6,144,000 B | 20.48x | 5,844,000 B | 7,644,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 300,000 B | 6,000 | 1,752,000 B | 5.84x | 1,452,000 B | 7,752,000 |

## 3. UI-Tree Workload (Hierarchical UI Traversal & String Reads)

### Scale: 10 items (71 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,640 B | 71 | 1,640 B | 1.00x | 0 B | 72,640 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,640 B | 4 | 16,384 B | 9.99x | 14,744 B | 20,384 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 1,640 B | 27 | 17,440 B | 10.63x | 15,800 B | 44,440 |

### Scale: 100 items (671 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 15,800 B | 671 | 15,800 B | 1.00x | 0 B | 686,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 15,800 B | 26 | 106,496 B | 6.74x | 90,696 B | 132,496 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 15,800 B | 228 | 183,248 B | 11.60x | 167,448 B | 411,248 |

### Scale: 500 items (3337 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 78,720 B | 3,337 | 78,720 B | 1.00x | 0 B | 3,415,720 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 78,720 B | 126 | 516,096 B | 6.56x | 437,376 B | 642,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 78,720 B | 1,128 | 913,648 B | 11.61x | 834,928 B | 2,041,648 |

### Scale: 1000 items (6671 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 157,400 B | 6,671 | 157,400 B | 1.00x | 0 B | 6,828,400 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 157,400 B | 251 | 1,028,096 B | 6.53x | 870,696 B | 1,279,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 157,400 B | 2,253 | 1,826,648 B | 11.61x | 1,669,248 B | 4,079,648 |

## 4. Worst-Case Scattered Workload (1 Scalar Read per 4KB Page)

### Scale: 10 items (10 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 80 B | 10 | 80 B | 1.00x | 0 B | 10,080 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 80 B | 10 | 40,960 B | 512.00x | 40,880 B | 50,960 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 80 B | 10 | 80 B | 1.00x | 0 B | 10,080 |

### Scale: 100 items (100 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 800 B | 100 | 800 B | 1.00x | 0 B | 100,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 800 B | 100 | 409,600 B | 512.00x | 408,800 B | 509,600 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 800 B | 100 | 800 B | 1.00x | 0 B | 100,800 |

### Scale: 500 items (500 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 4,000 B | 500 | 4,000 B | 1.00x | 0 B | 504,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 4,000 B | 500 | 2,048,000 B | 512.00x | 2,044,000 B | 2,548,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 4,000 B | 500 | 4,000 B | 1.00x | 0 B | 504,000 |

### Scale: 1000 items (1000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 8,000 B | 1,000 | 8,000 B | 1.00x | 0 B | 1,008,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 8,000 B | 1,000 | 4,096,000 B | 512.00x | 4,088,000 B | 5,096,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 8,000 B | 1,000 | 8,000 B | 1.00x | 0 B | 1,008,000 |

## Cost Model Sensitivity Sweep
Evaluation on Mixed Realistic Workload (100 Skills + 100 Entities + 100 UI elements + 100 Scattered):

| CallCost Ratio | Legacy Exact-Read | Current NewMemoryRead | Proposed Hybrid V2 | Optimal Policy |
| :---: | :---: | :---: | :---: | :---: |
| **250** | 1,025,866 | 1,260,340 | 667,998 | **Hybrid V2** |
| **500** | 1,994,616 | 1,332,840 | 929,248 | **Hybrid V2** |
| **1,000** | 3,932,116 | 1,477,840 | 1,451,748 | **Hybrid V2** |
| **2,000** | 7,807,116 | 1,767,840 | 2,496,748 | **Current NewMemory** |
| **5,000** | 19,432,116 | 2,637,840 | 5,631,748 | **Current NewMemory** |

## Pareto-Optimal Configuration Analysis
Comparison of Candidate Configurations on Mixed Workload:

| Candidate Configuration | Native Calls | Fetched Bytes | Amplification | Cost (CallCost=1000) | Pareto-Optimal Status |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 3,875 | 57,116 B | 1.00x | 3,932,116 | Lowest Fetched / Highest Calls |
| **Current NewMemoryRead (4KB/8KB)** | 290 | 1,187,840 B | 20.80x | 1,477,840 | Lowest Calls / Highest Wasted |
| **Hybrid V2 (Exact + 128B + 512B + 4KB)** | 1,045 | 406,748 B | 7.12x | 1,451,748 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 64B + 256B + 4KB)** | 1,280 | 512,348 B | 8.97x | 1,792,348 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 256B + 1024B + 4KB)** | 827 | 407,516 B | 7.13x | 1,234,516 | Pareto-Optimal (Balanced) |
