# Offline Memory Read Policy Simulation Report (Fully Corrected Model)
Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2 (Exact-First + Hierarchical Promotion)

Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (TotalFetchedBytes * 1.0)`

## 1. Worst-Case Scattered Workload (1 Scalar Read per 4KB Page)

### Scale: 10 items (10 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 80 B | 80 B | 10 | 80 B | 0 B | 0 B | 0 B | 80 B | 1.00x | 10,080 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 80 B | 80 B | 10 | 0 B | 0 B | 0 B | 40,960 B | 40,960 B | 512.00x | 50,960 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 80 B | 80 B | 10 | 80 B | 0 B | 0 B | 0 B | 80 B | 1.00x | 10,080 |

### Scale: 100 items (100 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 800 B | 800 B | 100 | 800 B | 0 B | 0 B | 0 B | 800 B | 1.00x | 100,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 800 B | 800 B | 100 | 0 B | 0 B | 0 B | 409,600 B | 409,600 B | 512.00x | 509,600 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 800 B | 800 B | 100 | 800 B | 0 B | 0 B | 0 B | 800 B | 1.00x | 100,800 |

### Scale: 500 items (500 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 4,000 B | 4,000 B | 500 | 4,000 B | 0 B | 0 B | 0 B | 4,000 B | 1.00x | 504,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 4,000 B | 4,000 B | 500 | 0 B | 0 B | 0 B | 2,048,000 B | 2,048,000 B | 512.00x | 2,548,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 4,000 B | 4,000 B | 500 | 4,000 B | 0 B | 0 B | 0 B | 4,000 B | 1.00x | 504,000 |

### Scale: 1000 items (1000 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 8,000 B | 8,000 B | 1,000 | 8,000 B | 0 B | 0 B | 0 B | 8,000 B | 1.00x | 1,008,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 8,000 B | 8,000 B | 1,000 | 0 B | 0 B | 0 B | 4,096,000 B | 4,096,000 B | 512.00x | 5,096,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 8,000 B | 8,000 B | 1,000 | 8,000 B | 0 B | 0 B | 0 B | 8,000 B | 1.00x | 1,008,000 |

## 2. Skill Workload (Actor + ActiveSkills + Cooldowns)

### Scale: 10 items (134 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,156 B | 1,156 B | 134 | 1,156 B | 0 B | 0 B | 0 B | 1,156 B | 1.00x | 135,156 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,156 B | 1,156 B | 4 | 0 B | 0 B | 0 B | 16,384 B | 16,384 B | 14.17x | 20,384 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 1,156 B | 1,156 B | 19 | 88 B | 640 B | 3,584 B | 12,288 B | 16,600 B | 14.36x | 35,600 |

### Scale: 100 items (1304 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,516 B | 10,516 B | 1,304 | 10,516 B | 0 B | 0 B | 0 B | 10,516 B | 1.00x | 1,314,516 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 10,516 B | 10,516 B | 14 | 0 B | 0 B | 0 B | 57,344 B | 57,344 B | 5.45x | 71,344 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 10,516 B | 10,516 B | 75 | 140 B | 1,920 B | 17,920 B | 45,056 B | 65,036 B | 6.18x | 140,036 |

### Scale: 500 items (6504 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 52,116 B | 52,116 B | 6,504 | 52,116 B | 0 B | 0 B | 0 B | 52,116 B | 1.00x | 6,556,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 52,116 B | 52,116 B | 57 | 0 B | 0 B | 0 B | 233,472 B | 233,472 B | 4.48x | 290,472 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 52,116 B | 52,116 B | 336 | 384 B | 7,424 B | 84,992 B | 225,280 B | 318,080 B | 6.10x | 654,080 |

### Scale: 1000 items (13004 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 104,116 B | 104,116 B | 13,004 | 104,116 B | 0 B | 0 B | 0 B | 104,116 B | 1.00x | 13,108,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 104,116 B | 104,116 B | 112 | 0 B | 0 B | 0 B | 458,752 B | 458,752 B | 4.41x | 570,752 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 104,116 B | 104,116 B | 666 | 700 B | 14,464 B | 169,984 B | 446,464 B | 631,612 B | 6.07x | 1,297,612 |

## 3. Entity / Component Workload (Dense Component Clusters)

### Scale: 10 items (180 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 3,000 B | 3,000 B | 180 | 3,000 B | 0 B | 0 B | 0 B | 3,000 B | 1.00x | 183,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 3,000 B | 3,000 B | 15 | 0 B | 0 B | 0 B | 61,440 B | 61,440 B | 20.48x | 76,440 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 3,000 B | 3,000 B | 50 | 240 B | 2,560 B | 5,120 B | 20,480 B | 28,400 B | 9.47x | 78,400 |

### Scale: 100 items (1800 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 B | 30,000 B | 1,800 | 30,000 B | 0 B | 0 B | 0 B | 30,000 B | 1.00x | 1,830,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 30,000 B | 30,000 B | 150 | 0 B | 0 B | 0 B | 614,400 B | 614,400 B | 20.48x | 764,400 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 30,000 B | 30,000 B | 500 | 2,400 B | 25,600 B | 51,200 B | 204,800 B | 284,000 B | 9.47x | 784,000 |

### Scale: 500 items (9000 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 B | 150,000 B | 9,000 | 150,000 B | 0 B | 0 B | 0 B | 150,000 B | 1.00x | 9,150,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 150,000 B | 150,000 B | 750 | 0 B | 0 B | 0 B | 3,072,000 B | 3,072,000 B | 20.48x | 3,822,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 150,000 B | 150,000 B | 2,500 | 12,000 B | 128,000 B | 256,000 B | 1,024,000 B | 1,420,000 B | 9.47x | 3,920,000 |

### Scale: 1000 items (18000 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 300,000 B | 300,000 B | 18,000 | 300,000 B | 0 B | 0 B | 0 B | 300,000 B | 1.00x | 18,300,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 300,000 B | 300,000 B | 1,500 | 0 B | 0 B | 0 B | 6,144,000 B | 6,144,000 B | 20.48x | 7,644,000 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 300,000 B | 300,000 B | 5,000 | 24,000 B | 256,000 B | 512,000 B | 2,048,000 B | 2,840,000 B | 9.47x | 7,840,000 |

## 4. UI-Tree Workload (Hierarchical UI Traversal & String Reads)

### Scale: 10 items (71 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,640 B | 1,480 B | 71 | 1,640 B | 0 B | 0 B | 0 B | 1,640 B | 1.00x | 72,640 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,640 B | 1,480 B | 4 | 0 B | 0 B | 0 B | 16,384 B | 16,384 B | 9.99x | 20,384 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 1,640 B | 1,480 B | 24 | 32 B | 1,024 B | 4,608 B | 12,288 B | 17,952 B | 10.95x | 41,952 |

### Scale: 100 items (671 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 15,800 B | 14,440 B | 671 | 15,800 B | 0 B | 0 B | 0 B | 15,800 B | 1.00x | 686,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 15,800 B | 14,440 B | 26 | 0 B | 0 B | 0 B | 106,496 B | 106,496 B | 6.74x | 132,496 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 15,800 B | 14,440 B | 178 | 208 B | 6,656 B | 38,400 B | 102,400 B | 147,664 B | 9.35x | 325,664 |

### Scale: 500 items (3337 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 78,720 B | 72,040 B | 3,337 | 78,720 B | 0 B | 0 B | 0 B | 78,720 B | 1.00x | 3,415,720 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 78,720 B | 72,040 B | 126 | 0 B | 0 B | 0 B | 516,096 B | 516,096 B | 6.56x | 642,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 78,720 B | 72,040 B | 878 | 1,008 B | 32,256 B | 192,000 B | 512,000 B | 737,264 B | 9.37x | 1,615,264 |

### Scale: 1000 items (6671 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 157,400 B | 144,040 B | 6,671 | 157,400 B | 0 B | 0 B | 0 B | 157,400 B | 1.00x | 6,828,400 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 157,400 B | 144,040 B | 251 | 0 B | 0 B | 0 B | 1,028,096 B | 1,028,096 B | 6.53x | 1,279,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 157,400 B | 144,040 B | 1,753 | 2,008 B | 64,256 B | 384,000 B | 1,024,000 B | 1,474,264 B | 9.37x | 3,227,264 |

## 5. Repeated Single-Pointer Workload (Same 8-byte pointer read repeatedly)

### Scale: 10 items (10 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 80 B | 8 B | 10 | 80 B | 0 B | 0 B | 0 B | 80 B | 1.00x | 10,080 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 80 B | 8 B | 1 | 0 B | 0 B | 0 B | 4,096 B | 4,096 B | 51.20x | 5,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 80 B | 8 B | 1 | 8 B | 0 B | 0 B | 0 B | 8 B | 0.10x | 1,008 |

### Scale: 100 items (100 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 800 B | 8 B | 100 | 800 B | 0 B | 0 B | 0 B | 800 B | 1.00x | 100,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 800 B | 8 B | 1 | 0 B | 0 B | 0 B | 4,096 B | 4,096 B | 5.12x | 5,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 800 B | 8 B | 1 | 8 B | 0 B | 0 B | 0 B | 8 B | 0.01x | 1,008 |

### Scale: 500 items (500 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 4,000 B | 8 B | 500 | 4,000 B | 0 B | 0 B | 0 B | 4,000 B | 1.00x | 504,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 4,000 B | 8 B | 1 | 0 B | 0 B | 0 B | 4,096 B | 4,096 B | 1.02x | 5,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 4,000 B | 8 B | 1 | 8 B | 0 B | 0 B | 0 B | 8 B | 0.00x | 1,008 |

### Scale: 1000 items (1000 requests)
| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 8,000 B | 8 B | 1,000 | 8,000 B | 0 B | 0 B | 0 B | 8,000 B | 1.00x | 1,008,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 8,000 B | 8 B | 1 | 0 B | 0 B | 0 B | 4,096 B | 4,096 B | 0.51x | 5,096 |
| **Proposed Hybrid V2 (Hierarchical Exact-First)** | 8,000 B | 8 B | 1 | 8 B | 0 B | 0 B | 0 B | 8 B | 0.00x | 1,008 |

## Cost Model Sensitivity Sweep
Evaluation on Mixed Realistic Workload (100 Skills + 100 Entities + 100 UI elements + 100 Scattered):

| CallCost Ratio | Legacy Exact-Read | Current NewMemoryRead | Proposed Hybrid V2 | Optimal Policy |
| :---: | :---: | :---: | :---: | :---: |
| **250** | 1,025,866 | 1,260,340 | 710,750 | **Hybrid V2** |
| **500** | 1,994,616 | 1,332,840 | 924,000 | **Hybrid V2** |
| **1,000** | 3,932,116 | 1,477,840 | 1,350,500 | **Hybrid V2** |
| **2,000** | 7,807,116 | 1,767,840 | 2,203,500 | **Current NewMemory** |
| **5,000** | 19,432,116 | 2,637,840 | 4,762,500 | **Current NewMemory** |

## Pareto-Optimal Configuration Analysis
Comparison of Candidate Configurations on Mixed Workload:

| Candidate Configuration | Native Calls | Fetched Bytes | Amplification | Cost (CallCost=1000) | Pareto-Optimal Status |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 3,875 | 57,116 B | 1.00x | 3,932,116 | Lowest Fetched / Highest Calls |
| **Current NewMemoryRead (4KB/8KB)** | 290 | 1,187,840 B | 20.80x | 1,477,840 | Lowest Calls / Highest Wasted |
| **Hybrid V2 (Exact + 128B + 512B + 4KB)** | 853 | 497,500 B | 8.71x | 1,350,500 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 64B + 256B + 4KB)** | 972 | 455,388 B | 7.97x | 1,427,388 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 256B + 1024B + 4KB)** | 687 | 526,812 B | 9.22x | 1,213,812 | Pareto-Optimal (Balanced) |
