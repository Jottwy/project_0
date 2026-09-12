# Tablas generadas por summarize.py (fase 3 = grabación)

## Frametime (ms) y cuello
| run | frames | dt p50 | p95 | p99 | max | cpuMain p50 | p95 | renderThr p50 | gpu p50 | gpu p95 | frames GPU>CPU | spikes >2×med | GC.Collect frames | GC.Collect max ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| S1_census_perf_S1_census_20260910_131354 | 3242 | 11.83 | 17.31 | 19.90 | 62.1 | 11.82 | 17.30 | 5.02 | 7.17 | 8.69 | 0 % | 3 | 71 | 3.04 |
| S1_mq1_perf_S1_mq1_20260910_130853 | 3284 | 17.80 | 22.43 | 26.25 | 53.1 | 17.79 | 22.39 | 5.38 | 7.23 | 9.43 | 0 % | 2 | 88 | 3.08 |
| S1_perf_S1_20260910_130148 | 4134 | 13.92 | 20.36 | 23.50 | 39.3 | 13.90 | 20.31 | 3.38 | 7.99 | 11.96 | 3 % | 8 | 112 | 3.57 |
| S2_perf_S2_20260910_130318 | 4148 | 14.52 | 21.82 | 25.65 | 33.5 | 14.51 | 21.81 | 4.17 | 4.93 | 10.42 | 1 % | 8 | 107 | 4.84 |
| S2_raw_perf_S2_raw_20260910_131500 | 2955 | 12.68 | 19.52 | 21.98 | 64.0 | 12.67 | 19.51 | 4.34 | 6.33 | 9.71 | 1 % | 5 | 74 | 3.06 |
| S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725 | 4955 | 11.28 | 18.55 | 21.34 | 293.8 | 11.26 | 18.54 | 3.25 | 4.14 | 9.30 | 2 % | 22 | 126 | 6.58 |
| S2_rs200_perf_S2_rs200_20260910_130558 | 2476 | 24.31 | 34.35 | 38.28 | 41.0 | 14.15 | 20.90 | 4.29 | 24.15 | 30.66 | 84 % | 0 | 79 | 3.05 |
| S2_vsync1_perf_S2_vsync1_20260910_131021 | 3588 | 16.65 | 16.75 | 17.28 | 33.4 | 12.49 | 18.89 | 4.71 | 7.52 | 9.66 | 0 % | 12 | 94 | 5.54 |
| S3_uuid_perfS3-1_20260910_131948 | 2206 | 24.12 | 41.83 | 46.31 | 57.1 | 24.11 | 41.82 | 6.18 | 6.26 | 22.93 | 7 % | 9 | 169 | 4.05 |
| S5_perf_S5_20260910_130445 | 5945 | 9.44 | 14.24 | 17.58 | 356.0 | 9.40 | 14.23 | 4.75 | 4.89 | 6.54 | 0 % | 35 | 77 | 9.87 |

## Render y GC
| run | draw calls | SetPass | tris | verts | GC alloc/frame (KB) mean | p95 | remotos | chunks |
|---|---|---|---|---|---|---|---|---|
| S1_census_perf_S1_census_20260910_131354 | 24411 | 211 | 4122 k | 4273 k | 81.4 | 299.3 | 15 | 12 |
| S1_mq1_perf_S1_mq1_20260910_130853 | 24068 | 222 | 4112 k | 4275 k | 96.4 | 304.4 | 15 | 12 |
| S1_perf_S1_20260910_130148 | 11396 | 152 | 1185 k | 1395 k | 70.4 | 241.0 | 11 | 9 |
| S2_perf_S2_20260910_130318 | 12384 | 227 | 2267 k | 2398 k | 74.1 | 258.1 | 11 | 12 |
| S2_raw_perf_S2_raw_20260910_131500 | 18662 | 223 | 4357 k | 4365 k | 108.8 | 314.4 | 15 | 12 |
| S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725 | 12963 | 155 | 3751 k | 3486 k | 109.8 | 327.0 | 20 | 12 |
| S2_rs200_perf_S2_rs200_20260910_130558 | 15601 | 226 | 4516 k | 4475 k | 137.7 | 325.4 | 19 | 9 |
| S2_vsync1_perf_S2_vsync1_20260910_131021 | 22430 | 223 | 5762 k | 5677 k | 93.5 | 305.0 | 15 | 12 |
| S3_uuid_perfS3-1_20260910_131948 | 24287 | 207 | 7924 k | 7492 k | 263.9 | 637.2 | 56 | 12 |
| S5_perf_S5_20260910_130445 | 29991 | 128 | 4771 k | 5123 k | 48.7 | 149.5 | 0 | 15 |

## Memoria (MB, mediana de la grabación)
| run | Total Used | System Used (WS) | GC heap | Texturas | Mallas | Render targets | Buffers GPU |
|---|---|---|---|---|---|---|---|
| S1_census_perf_S1_census_20260910_131354 | 3262 | 1803 | 39 | 2024 | 208 | 122 | 175 |
| S1_mq1_perf_S1_mq1_20260910_130853 | 3227 | 1803 | 39 | 2024 | 205 | 122 | 173 |
| S1_perf_S1_20260910_130148 | 3176 | 1752 | 38 | 2024 | 192 | 122 | 149 |
| S2_perf_S2_20260910_130318 | 3216 | 1816 | 38 | 2041 | 209 | 139 | 164 |
| S2_raw_perf_S2_raw_20260910_131500 | 3281 | 1822 | 39 | 2024 | 214 | 122 | 189 |
| S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725 | 3153 | 1822 | 39 | 1943 | 213 | 40 | 197 |
| S2_rs200_perf_S2_rs200_20260910_130558 | 3531 | 1771 | 38 | 2344 | 199 | 441 | 190 |
| S2_vsync1_perf_S2_vsync1_20260910_131021 | 3241 | 1815 | 39 | 2024 | 214 | 122 | 180 |
| S3_uuid_perfS3-1_20260910_131948 | 3325 | 1877 | 43 | 1985 | 213 | 83 | 301 |
| S5_perf_S5_20260910_130445 | 3234 | 1855 | 40 | 2026 | 222 | 124 | 163 |

## Marcadores del hilo principal (ms, media / p95)
| run | ScriptUpdate | ScriptLateUpdate | Physics_Simulate | PhysicsFixed | Animators_Update | SRP loop | FinishFrameRendering | Culling | CullResults | WaitRenderThread | WaitPresent | WaitForTargetFPS | Gfx_PresentFrame | Semaphore_WaitForSignal | GC_Collect | Shader_CreateGPUProgram | AudioManager_Update | UpdateAllRenderers | UpdateSkinned | Loading_ReadObject | Coroutines | Canvases | PlayerLoop |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| S1_census_perf_S1_census_20260910_131354 | 2.13/3.20 | 0.46/0.66 | 0.94/1.67 | 1.05/1.86 | 0.24/0.31 | n/a | 7.20/11.86 | 0.00/0.00 | 0.45/0.51 | 0.00/0.00 | 0.00/0.00 | 0.00/0.00 | 0.47/0.98 | 8.37/19.32 | 0.05/0.00 | 0.00/0.00 | 0.05/0.07 | 0.04/0.05 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.44/0.67 | 12.33/17.30 |
| S1_mq1_perf_S1_mq1_20260910_130853 | 2.47/4.03 | 0.50/0.72 | 1.40/1.79 | 1.54/1.99 | 0.26/0.34 | n/a | 8.28/13.73 | 0.00/0.00 | 0.47/0.56 | 0.00/0.00 | 0.00/0.00 | 0.00/0.00 | 0.49/0.88 | 12.79/16.21 | 0.07/0.00 | 0.00/0.00 | 0.06/0.08 | 0.05/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.48/0.72 | 18.26/22.39 |
| S1_perf_S1_20260910_130148 | 2.79/4.36 | 0.42/0.58 | 0.81/1.67 | 0.90/1.85 | 0.29/0.45 | n/a | 8.44/13.79 | 0.00/0.00 | 0.27/0.36 | 0.00/0.00 | 0.02/0.00 | 0.00/0.00 | 0.46/0.62 | 10.55/16.06 | 0.07/0.00 | 0.00/0.00 | 0.05/0.06 | 0.05/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.01 | 0.59/0.99 | 14.51/20.35 |
| S2_perf_S2_20260910_130318 | 2.46/4.12 | 0.42/0.60 | 1.12/1.99 | 1.24/2.25 | 0.27/0.47 | n/a | 8.43/14.37 | 0.00/0.00 | 0.41/0.82 | 0.00/0.00 | 0.00/0.00 | 0.00/0.00 | 0.52/1.46 | 9.48/15.60 | 0.07/0.00 | 0.00/0.00 | 0.08/0.13 | 0.04/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.53/0.84 | 14.45/21.80 |
| S2_raw_perf_S2_raw_20260910_131500 | 2.22/3.45 | 0.51/0.75 | 1.02/1.67 | 1.14/1.85 | 0.25/0.33 | n/a | 8.09/13.26 | 0.00/0.00 | 0.39/0.56 | 0.00/0.00 | 0.00/0.00 | 0.00/0.00 | 0.39/0.92 | 10.22/20.45 | 0.06/0.00 | 0.00/0.00 | 0.05/0.07 | 0.05/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.45/0.69 | 13.53/19.51 |
| S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725 | 2.14/3.20 | 0.56/0.82 | 0.83/1.65 | 0.95/1.88 | 0.28/0.36 | n/a | 6.95/12.66 | 0.00/0.00 | 0.31/0.53 | 0.00/0.00 | 0.01/0.00 | 0.00/0.00 | 0.37/0.90 | 8.21/13.90 | 0.06/0.00 | 0.00/0.00 | 0.05/0.07 | 0.05/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.43/0.68 | 12.10/18.54 |
| S2_rs200_perf_S2_rs200_20260910_130558 | 2.44/3.78 | 0.61/0.83 | 1.19/1.49 | 1.38/1.74 | 0.30/0.37 | n/a | 18.04/28.02 | 0.00/0.00 | 0.33/0.42 | 0.00/0.00 | 9.00/16.29 | 0.00/0.00 | 2.21/17.69 | 14.38/23.75 | 0.08/0.00 | 0.00/0.00 | 0.05/0.08 | 0.05/0.07 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.49/0.80 | 24.22/34.35 |
| S2_vsync1_perf_S2_vsync1_20260910_131021 | 2.26/3.51 | 0.48/0.69 | 1.31/1.79 | 1.46/2.00 | 0.25/0.33 | n/a | 7.46/12.42 | 0.00/0.00 | 0.42/0.57 | 0.00/0.00 | 0.00/0.00 | 3.47/6.36 | 0.48/0.95 | 11.86/13.97 | 0.06/0.00 | 0.00/0.00 | 0.05/0.08 | 0.05/0.06 | 0.04/0.05 | 0.00/0.00 | 0.00/0.00 | 0.43/0.60 | 16.71/19.31 |
| S3_uuid_perfS3-1_20260910_131948 | 5.20/7.63 | 2.30/3.15 | 2.10/2.50 | 2.74/3.30 | 0.79/0.95 | n/a | 14.12/28.00 | 0.00/0.00 | 0.55/0.77 | 0.00/0.00 | 0.00/0.00 | 0.00/0.00 | 0.52/0.95 | 20.45/35.13 | 0.22/2.78 | 0.00/0.00 | 0.08/0.12 | 0.13/0.16 | 0.10/0.13 | 0.00/0.00 | 0.00/0.00 | 0.58/0.95 | 27.19/41.82 |
| S5_perf_S5_20260910_130445 | 2.13/3.05 | 0.20/0.43 | 0.39/1.63 | 0.42/1.77 | 0.09/0.24 | n/a | 6.06/9.16 | 0.00/0.00 | 0.45/0.59 | 0.00/0.00 | 0.02/0.12 | 0.00/0.00 | 0.59/1.41 | 5.28/9.74 | 0.03/0.00 | 0.00/0.00 | 0.04/0.06 | 0.02/0.04 | 0.01/0.04 | 0.00/0.00 | 0.01/0.01 | 0.42/0.66 | 10.10/14.21 |

## Tirones (dt > 2× mediana): los 8 mayores por run, con el marcador dominante
- **S1_census_perf_S1_census_20260910_131354** (3 tirones sobre mediana 11.83 ms):
  - t=18.9s dt=62.1 ms cpuMain=11.2 gpu=6.6 GCalloc=63 KB chunkΔ=0 Semaphore_WaitForSignal=70.7, PlayerLoop=62.1, ScriptUpdate=48.8
  - t=41.1s dt=26.2 ms cpuMain=16.1 gpu=8.5 GCalloc=300 KB chunkΔ=0 PlayerLoop=26.2, Semaphore_WaitForSignal=17.9, FinishFrameRendering=17.1
  - t=36.7s dt=24.4 ms cpuMain=10.8 gpu=7.6 GCalloc=157 KB chunkΔ=0 PlayerLoop=24.4, Semaphore_WaitForSignal=18.3, FinishFrameRendering=14.2
- **S1_mq1_perf_S1_mq1_20260910_130853** (2 tirones sobre mediana 17.80 ms):
  - t=38.4s dt=53.1 ms cpuMain=19.4 gpu=7.2 GCalloc=305 KB chunkΔ=0 PlayerLoop=53.1, Semaphore_WaitForSignal=45.6, ScriptUpdate=30.3
  - t=44.1s dt=42.2 ms cpuMain=17.7 gpu=12.4 GCalloc=296 KB chunkΔ=0 PlayerLoop=42.2, Semaphore_WaitForSignal=34.8, FinishFrameRendering=24.6
- **S1_perf_S1_20260910_130148** (8 tirones sobre mediana 13.92 ms):
  - t=57.0s dt=39.3 ms cpuMain=12.1 gpu=8.4 GCalloc=179 KB chunkΔ=0 PlayerLoop=39.0, Semaphore_WaitForSignal=34.2, FinishFrameRendering=14.2
  - t=72.7s dt=39.1 ms cpuMain=17.9 gpu=7.3 GCalloc=239 KB chunkΔ=0 PlayerLoop=39.1, Semaphore_WaitForSignal=32.9, FinishFrameRendering=27.2
  - t=60.0s dt=39.0 ms cpuMain=10.8 gpu=5.6 GCalloc=256 KB chunkΔ=0 PlayerLoop=27.0, Semaphore_WaitForSignal=22.3, FinishFrameRendering=15.1
  - t=61.9s dt=38.2 ms cpuMain=15.7 gpu=11.7 GCalloc=240 KB chunkΔ=0 PlayerLoop=38.2, Semaphore_WaitForSignal=31.7, FinishFrameRendering=26.4
  - t=60.3s dt=33.9 ms cpuMain=12.4 gpu=4.5 GCalloc=239 KB chunkΔ=0 PlayerLoop=34.0, Semaphore_WaitForSignal=29.7, FinishFrameRendering=24.7
  - t=61.6s dt=32.3 ms cpuMain=15.5 gpu=6.2 GCalloc=241 KB chunkΔ=0 PlayerLoop=32.3, Semaphore_WaitForSignal=28.0, FinishFrameRendering=26.9
  - t=62.0s dt=28.4 ms cpuMain=14.3 gpu=4.8 GCalloc=163 KB chunkΔ=0 PlayerLoop=28.4, Semaphore_WaitForSignal=24.2, FinishFrameRendering=20.5
  - t=62.5s dt=28.2 ms cpuMain=12.2 gpu=4.5 GCalloc=40 KB chunkΔ=0 PlayerLoop=28.2, Semaphore_WaitForSignal=24.1, FinishFrameRendering=19.1
- **S2_perf_S2_20260910_130318** (8 tirones sobre mediana 14.52 ms):
  - t=28.8s dt=33.5 ms cpuMain=14.0 gpu=6.6 GCalloc=259 KB chunkΔ=0 PlayerLoop=33.5, FinishFrameRendering=25.5, Semaphore_WaitForSignal=22.8
  - t=64.9s dt=31.7 ms cpuMain=8.9 gpu=3.7 GCalloc=260 KB chunkΔ=0 PlayerLoop=18.2, Semaphore_WaitForSignal=13.9, FinishFrameRendering=11.0
  - t=71.7s dt=31.4 ms cpuMain=23.3 gpu=6.2 GCalloc=254 KB chunkΔ=0 PlayerLoop=31.3, FinishFrameRendering=21.7, Semaphore_WaitForSignal=20.7
  - t=52.5s dt=30.7 ms cpuMain=18.5 gpu=7.9 GCalloc=40 KB chunkΔ=0 PlayerLoop=30.7, Semaphore_WaitForSignal=23.4, FinishFrameRendering=13.3
  - t=59.8s dt=30.3 ms cpuMain=16.9 gpu=9.1 GCalloc=265 KB chunkΔ=0 PlayerLoop=30.3, FinishFrameRendering=23.0, Semaphore_WaitForSignal=19.9
  - t=57.2s dt=30.0 ms cpuMain=14.1 gpu=4.2 GCalloc=258 KB chunkΔ=0 PlayerLoop=30.0, Semaphore_WaitForSignal=23.5, FinishFrameRendering=17.8
  - t=71.6s dt=29.1 ms cpuMain=16.1 gpu=6.8 GCalloc=259 KB chunkΔ=0 PlayerLoop=29.1, Semaphore_WaitForSignal=20.8, FinishFrameRendering=16.9
  - t=61.9s dt=29.1 ms cpuMain=16.7 gpu=8.6 GCalloc=260 KB chunkΔ=0 PlayerLoop=29.1, FinishFrameRendering=20.1, Semaphore_WaitForSignal=19.0
- **S2_raw_perf_S2_raw_20260910_131500** (5 tirones sobre mediana 12.68 ms):
  - t=18.6s dt=64.0 ms cpuMain=10.0 gpu=6.9 GCalloc=196 KB chunkΔ=0 PlayerLoop=64.0, Semaphore_WaitForSignal=61.3, ScriptUpdate=47.0
  - t=48.3s dt=35.0 ms cpuMain=18.0 gpu=6.2 GCalloc=705 KB chunkΔ=0 PlayerLoop=35.0, Semaphore_WaitForSignal=27.9, FinishFrameRendering=15.7
  - t=49.3s dt=32.1 ms cpuMain=11.5 gpu=7.6 GCalloc=450 KB chunkΔ=0 PlayerLoop=32.0, Semaphore_WaitForSignal=26.8, FinishFrameRendering=13.9
  - t=47.2s dt=29.2 ms cpuMain=13.6 gpu=8.2 GCalloc=1106 KB chunkΔ=0 PlayerLoop=29.2, Semaphore_WaitForSignal=22.6, FinishFrameRendering=12.9
  - t=35.4s dt=26.1 ms cpuMain=10.8 gpu=8.0 GCalloc=301 KB chunkΔ=0 PlayerLoop=26.1, Semaphore_WaitForSignal=17.8, FinishFrameRendering=17.4
- **S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725** (22 tirones sobre mediana 11.28 ms):
  - t=45.3s dt=293.8 ms cpuMain=8.9 gpu=0.0 GCalloc=14966 KB chunkΔ=3 PlayerLoop=293.8, Semaphore_WaitForSignal=280.6, ScriptUpdate=276.3
  - t=45.3s dt=39.9 ms cpuMain=8.9 gpu=7.8 GCalloc=194 KB chunkΔ=0 PlayerLoop=39.8, FinishFrameRendering=28.1, Semaphore_WaitForSignal=24.6
  - t=35.1s dt=32.1 ms cpuMain=8.7 gpu=3.5 GCalloc=1121 KB chunkΔ=0 PlayerLoop=32.1, Semaphore_WaitForSignal=28.4, ScriptUpdate=14.6
  - t=32.8s dt=31.5 ms cpuMain=8.9 gpu=2.7 GCalloc=310 KB chunkΔ=0 PlayerLoop=31.5, FinishFrameRendering=26.6, Semaphore_WaitForSignal=22.5
  - t=64.7s dt=29.3 ms cpuMain=8.8 gpu=12.2 GCalloc=238 KB chunkΔ=0 PlayerLoop=29.3, Semaphore_WaitForSignal=22.7, FinishFrameRendering=18.2
  - t=70.3s dt=29.1 ms cpuMain=22.7 gpu=6.5 GCalloc=409 KB chunkΔ=0 PlayerLoop=29.1, Semaphore_WaitForSignal=20.9, FinishFrameRendering=13.9
  - t=52.4s dt=28.6 ms cpuMain=13.7 gpu=10.3 GCalloc=328 KB chunkΔ=0 PlayerLoop=28.6, Semaphore_WaitForSignal=21.2, FinishFrameRendering=17.4
  - t=63.0s dt=26.1 ms cpuMain=18.7 gpu=2.3 GCalloc=188 KB chunkΔ=0 PlayerLoop=26.1, Semaphore_WaitForSignal=21.6, FinishFrameRendering=14.3
- S2_rs200_perf_S2_rs200_20260910_130558: sin tirones
- **S2_vsync1_perf_S2_vsync1_20260910_131021** (12 tirones sobre mediana 16.65 ms):
  - t=54.4s dt=33.4 ms cpuMain=15.6 gpu=7.5 GCalloc=303 KB chunkΔ=0 PlayerLoop=21.0, Semaphore_WaitForSignal=15.2, FinishFrameRendering=13.8
  - t=30.1s dt=33.4 ms cpuMain=29.5 gpu=9.2 GCalloc=49 KB chunkΔ=0 PlayerLoop=22.0, FinishFrameRendering=14.4, Semaphore_WaitForSignal=12.8
  - t=30.3s dt=33.4 ms cpuMain=21.9 gpu=9.0 GCalloc=203 KB chunkΔ=0 PlayerLoop=24.3, Semaphore_WaitForSignal=16.3, FinishFrameRendering=15.8
  - t=50.6s dt=33.4 ms cpuMain=12.5 gpu=7.2 GCalloc=315 KB chunkΔ=0 PlayerLoop=21.0, Semaphore_WaitForSignal=15.5, FinishFrameRendering=12.3
  - t=30.2s dt=33.4 ms cpuMain=19.1 gpu=0.0 GCalloc=49 KB chunkΔ=0 PlayerLoop=19.1, Semaphore_WaitForSignal=14.3, FinishFrameRendering=9.6
  - t=63.9s dt=33.3 ms cpuMain=13.0 gpu=6.9 GCalloc=315 KB chunkΔ=0 PlayerLoop=21.2, Semaphore_WaitForSignal=15.6, FinishFrameRendering=13.8
  - t=30.5s dt=33.3 ms cpuMain=16.2 gpu=8.9 GCalloc=297 KB chunkΔ=0 PlayerLoop=20.8, Semaphore_WaitForSignal=14.5, FinishFrameRendering=13.7
  - t=30.1s dt=33.3 ms cpuMain=17.3 gpu=9.9 GCalloc=327 KB chunkΔ=0 PlayerLoop=31.0, FinishFrameRendering=21.1, Semaphore_WaitForSignal=17.5
- **S3_uuid_perfS3-1_20260910_131948** (9 tirones sobre mediana 24.12 ms):
  - t=47.7s dt=57.1 ms cpuMain=26.1 gpu=8.1 GCalloc=491 KB chunkΔ=0 PlayerLoop=57.0, Semaphore_WaitForSignal=48.1, FinishFrameRendering=39.2
  - t=29.4s dt=54.2 ms cpuMain=23.4 gpu=4.9 GCalloc=461 KB chunkΔ=0 PlayerLoop=54.2, Semaphore_WaitForSignal=46.9, FinishFrameRendering=39.4
  - t=63.3s dt=52.4 ms cpuMain=42.0 gpu=6.3 GCalloc=610 KB chunkΔ=0 PlayerLoop=52.4, Semaphore_WaitForSignal=44.3, FinishFrameRendering=36.2
  - t=42.3s dt=51.8 ms cpuMain=45.0 gpu=7.3 GCalloc=606 KB chunkΔ=0 PlayerLoop=51.7, Semaphore_WaitForSignal=41.8, FinishFrameRendering=33.2
  - t=54.2s dt=49.8 ms cpuMain=23.3 gpu=7.5 GCalloc=833 KB chunkΔ=0 PlayerLoop=49.8, Semaphore_WaitForSignal=41.2, FinishFrameRendering=29.1
  - t=64.2s dt=49.6 ms cpuMain=38.1 gpu=8.0 GCalloc=601 KB chunkΔ=0 PlayerLoop=49.6, Semaphore_WaitForSignal=42.6, FinishFrameRendering=35.9
  - t=43.5s dt=49.1 ms cpuMain=38.5 gpu=7.4 GCalloc=693 KB chunkΔ=0 PlayerLoop=49.1, Semaphore_WaitForSignal=41.1, FinishFrameRendering=30.6
  - t=40.2s dt=48.9 ms cpuMain=39.0 gpu=6.5 GCalloc=608 KB chunkΔ=0 PlayerLoop=48.9, Semaphore_WaitForSignal=41.4, FinishFrameRendering=35.5
- **S5_perf_S5_20260910_130445** (35 tirones sobre mediana 9.44 ms):
  - t=3.9s dt=356.0 ms cpuMain=249.7 gpu=2.8 GCalloc=19376 KB chunkΔ=5 PlayerLoop=356.0, Semaphore_WaitForSignal=344.9, ScriptUpdate=333.7
  - t=3.5s dt=84.0 ms cpuMain=90.6 gpu=0.3 GCalloc=2216 KB chunkΔ=0 Semaphore_WaitForSignal=150.1, PlayerLoop=84.0, ScriptUpdate=35.4
  - t=25.8s dt=74.9 ms cpuMain=18.5 gpu=5.1 GCalloc=3759 KB chunkΔ=1 PlayerLoop=75.1, Semaphore_WaitForSignal=61.7, ScriptUpdate=55.5
  - t=28.1s dt=34.3 ms cpuMain=9.6 gpu=3.4 GCalloc=337 KB chunkΔ=0 PlayerLoop=34.6, Semaphore_WaitForSignal=30.3, ScriptUpdate=16.2
  - t=3.9s dt=34.1 ms cpuMain=860.3 gpu=0.0 GCalloc=553 KB chunkΔ=0 PlayerLoop=34.1, Semaphore_WaitForSignal=29.2, ScriptUpdate=13.6
  - t=55.2s dt=27.9 ms cpuMain=9.5 gpu=0.0 GCalloc=151 KB chunkΔ=0 PlayerLoop=12.2, Semaphore_WaitForSignal=8.9, FinishFrameRendering=7.3
  - t=4.0s dt=27.0 ms cpuMain=11.8 gpu=5.2 GCalloc=425 KB chunkΔ=0 PlayerLoop=27.0, Semaphore_WaitForSignal=26.1, FinishFrameRendering=12.2
  - t=25.0s dt=23.8 ms cpuMain=14.8 gpu=6.6 GCalloc=268 KB chunkΔ=0 PlayerLoop=24.5, FinishFrameRendering=16.6, Semaphore_WaitForSignal=15.7

## Carga y streaming (fases 1-2 = espera del mundo y warmup; eventos = frames donde builtChunks sube)
- **S1_census_perf_S1_census_20260910_131354**: frames antes de grabar=1347, dt max en carga/warmup=1380 ms; eventos de chunk durante la grabación=0
- **S1_mq1_perf_S1_mq1_20260910_130853**: frames antes de grabar=1153, dt max en carga/warmup=1197 ms; eventos de chunk durante la grabación=0
- **S1_perf_S1_20260910_130148**: frames antes de grabar=1105, dt max en carga/warmup=1559 ms; eventos de chunk durante la grabación=0
- **S2_perf_S2_20260910_130318**: frames antes de grabar=1073, dt max en carga/warmup=1271 ms; eventos de chunk durante la grabación=0
- **S2_raw_perf_S2_raw_20260910_131500**: frames antes de grabar=1295, dt max en carga/warmup=1167 ms; eventos de chunk durante la grabación=0
- **S2_rs060_sh0_perf_S2_rs060_sh0_20260910_130725**: frames antes de grabar=1464, dt max en carga/warmup=1137 ms; eventos de chunk durante la grabación=1, dt en esos frames: media 293.8 ms, max 293.8 ms, chunks por evento media 3.0
  - t=45.3s +3 chunk(s) dt=293.8 ms cpuMain=8.9 GCalloc=14966 KB ScriptUpdate=276.3 Loading=0.2
- **S2_rs200_perf_S2_rs200_20260910_130558**: frames antes de grabar=1421, dt max en carga/warmup=1160 ms; eventos de chunk durante la grabación=0
- **S2_vsync1_perf_S2_vsync1_20260910_131021**: frames antes de grabar=1266, dt max en carga/warmup=1175 ms; eventos de chunk durante la grabación=0
- **S3_uuid_perfS3-1_20260910_131948**: frames antes de grabar=1740, dt max en carga/warmup=1369 ms; eventos de chunk durante la grabación=0
- **S5_perf_S5_20260910_130445**: frames antes de grabar=119, dt max en carga/warmup=1137 ms; eventos de chunk durante la grabación=2, dt en esos frames: media 215.5 ms, max 356.0 ms, chunks por evento media 3.0
  - t=3.9s +5 chunk(s) dt=356.0 ms cpuMain=249.7 GCalloc=19376 KB ScriptUpdate=333.7 Loading=0.0
  - t=25.8s +1 chunk(s) dt=74.9 ms cpuMain=18.5 GCalloc=3759 KB ScriptUpdate=55.5 Loading=0.0

## Censo de renderers: S1_census_perf_S1_census_renderers.tsv
```
renderers=20147 visible=8251 withPropertyBlock=0 instancedMaterialSlots=19 distinctKeys=83
count	visible	type|shader|material|pb|shadows
12587	5711	MeshRenderer|Universal Render Pipeline/Lit|LayerLampEmissive|pb=0|shadows=On
3096	453	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
624	317	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s3|pb=0|shadows=On
602	303	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s3|pb=0|shadows=On
593	301	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s3|pb=0|shadows=On
553	283	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s3|pb=0|shadows=On
474	241	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s2|pb=0|shadows=On
390	233	MeshRenderer|Universal Render Pipeline/Lit|Wg3Sign|pb=0|shadows=Off
444	226	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s2|pb=0|shadows=On
444	226	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s2|pb=0|shadows=On
381	221	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s6|pb=0|shadows=On
381	221	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s6|pb=0|shadows=On
381	221	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s6|pb=0|shadows=On
381	221	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s6|pb=0|shadows=On
414	211	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s2|pb=0|shadows=On
372	176	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall|pb=0|shadows=On
345	157	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte|pb=0|shadows=On
228	139	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s4|pb=0|shadows=On
220	131	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s4|pb=0|shadows=On
268	128	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling|pb=0|shadows=On
visibleRenderers=8251 visibleMeshTriangles=236360
distinctMaterials=121
```
## Censo de renderers: S1_mq1_perf_S1_mq1_renderers.tsv
```
renderers=3670 visible=581 withPropertyBlock=0 instancedMaterialSlots=19 distinctKeys=28
count	visible	type|shader|material|pb|shadows
3096	439	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
162	45	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
80	24	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
67	19	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
72	12	MeshRenderer|Universal Render Pipeline/Lit|URP_M_WarehouseAssets|pb=0|shadows=On
25	10	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
20	6	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
15	5	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
15	5	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
6	4	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Transparent|pb=0|shadows=Off
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
6	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingChildRealForm|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Shader Graphs/Skin|MaleSurvivorSkin (Instance)|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
4	2	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
6	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Desk 1|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 1|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 2|pb=0|shadows=On
lights enabled=0 withShadows=0 total=1
distinctMaterials=66
```
## Censo de renderers: S1_perf_S1_renderers.tsv
```
renderers=2958 visible=58 withPropertyBlock=0 instancedMaterialSlots=13 distinctKeys=22
count	visible	type|shader|material|pb|shadows
2716	52	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
57	3	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
37	2	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
8	1	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
2	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
5	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
2	0	SkinnedMeshRenderer|Shader Graphs/Skin|MaleSurvivorSkin (Instance)|pb=0|shadows=On
2	0	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
2	0	MeshRenderer|Universal Render Pipeline/Lit|Leather 2|pb=0|shadows=On
11	0	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
1	0	MeshRenderer|Universal Render Pipeline/Lit|Reception Counter|pb=0|shadows=On
10	0	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Emissive|pb=0|shadows=On
4	0	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
2	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
2	0	MeshRenderer|Universal Render Pipeline/Lit|Leather 1|pb=0|shadows=On
3	0	MeshRenderer|Universal Render Pipeline/Lit|Lit|pb=0|shadows=On
4	0	MeshRenderer|Universal Render Pipeline/Lit|BR_CrankFlashlight_Mat|pb=0|shadows=On
15	0	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
11	0	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
60	0	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
lights enabled=0 withShadows=0 total=0
distinctMaterials=50
```
## Censo de renderers: S2_perf_S2_renderers.tsv
```
renderers=2833 visible=621 withPropertyBlock=0 instancedMaterialSlots=13 distinctKeys=24
count	visible	type|shader|material|pb|shadows
2448	371	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
147	114	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
67	52	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
60	36	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
19	16	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
15	9	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
10	5	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Emissive|pb=0|shadows=On
11	3	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
11	3	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
3	3	MeshRenderer|Universal Render Pipeline/Lit|Lit|pb=0|shadows=On
5	3	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
2	2	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
4	2	MeshRenderer|Universal Render Pipeline/Lit|BR_CrankFlashlight_Mat|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 2|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Desk 1|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 1|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Reception Counter|pb=0|shadows=On
2	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
4	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingChildRealForm|pb=0|shadows=On
lights enabled=0 withShadows=0 total=1
distinctMaterials=52
```
## Censo de renderers: S2_raw_perf_S2_raw_renderers.tsv
```
renderers=20174 visible=3618 withPropertyBlock=0 instancedMaterialSlots=24 distinctKeys=83
count	visible	type|shader|material|pb|shadows
12587	2720	MeshRenderer|Universal Render Pipeline/Lit|LayerLampEmissive|pb=0|shadows=On
624	186	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s3|pb=0|shadows=On
602	179	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s3|pb=0|shadows=On
593	175	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s3|pb=0|shadows=On
553	160	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s3|pb=0|shadows=On
3096	129	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
474	108	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s2|pb=0|shadows=On
381	103	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s6|pb=0|shadows=On
381	103	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s6|pb=0|shadows=On
381	103	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s6|pb=0|shadows=On
381	103	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s6|pb=0|shadows=On
444	102	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s2|pb=0|shadows=On
444	102	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s2|pb=0|shadows=On
414	96	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s2|pb=0|shadows=On
214	51	MeshRenderer|Universal Render Pipeline/Lit|Air Vent|pb=0|shadows=On
372	40	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall|pb=0|shadows=On
206	36	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s5|pb=0|shadows=On
345	35	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte|pb=0|shadows=On
390	34	MeshRenderer|Universal Render Pipeline/Lit|Wg3Sign|pb=0|shadows=Off
186	32	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s5|pb=0|shadows=On
visibleRenderers=3618 visibleMeshTriangles=307498
distinctMaterials=131
```
## Censo de renderers: S2_rs060_sh0_perf_S2_rs060_sh0_renderers.tsv
```
renderers=3673 visible=578 withPropertyBlock=0 instancedMaterialSlots=24 distinctKeys=28
count	visible	type|shader|material|pb|shadows
3096	440	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
162	45	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
80	24	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
67	19	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
25	10	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
72	8	MeshRenderer|Universal Render Pipeline/Lit|URP_M_WarehouseAssets|pb=0|shadows=On
20	6	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
20	5	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
20	5	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
6	4	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Transparent|pb=0|shadows=Off
4	2	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Shader Graphs/Skin|MaleSurvivorSkin (Instance)|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
6	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingChildRealForm|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 1|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 2|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Desk 1|pb=0|shadows=On
11	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
lights enabled=0 withShadows=0 total=1
distinctMaterials=76
```
## Censo de renderers: S2_rs200_perf_S2_rs200_renderers.tsv
```
renderers=2423 visible=284 withPropertyBlock=0 instancedMaterialSlots=23 distinctKeys=28
count	visible	type|shader|material|pb|shadows
2008	157	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
132	33	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
56	28	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
56	12	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
19	7	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
19	7	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
14	7	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
19	7	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
10	5	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Emissive|pb=0|shadows=On
4	4	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingChildRealForm|pb=0|shadows=On
3	3	MeshRenderer|Universal Render Pipeline/Lit|Lit|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Shader Graphs/Skin|MaleSurvivorSkin (Instance)|pb=0|shadows=On
12	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
4	2	MeshRenderer|Universal Render Pipeline/Lit|BR_CrankFlashlight_Mat|pb=0|shadows=On
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|TShirt_White|pb=0|shadows=On
6	1	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Transparent|pb=0|shadows=Off
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Boots|pb=0|shadows=On
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Pants_Woodland|pb=0|shadows=On
lights enabled=1 withShadows=0 total=1
distinctMaterials=74
```
## Censo de renderers: S2_vsync1_perf_S2_vsync1_renderers.tsv
```
renderers=3682 visible=169 withPropertyBlock=0 instancedMaterialSlots=19 distinctKeys=28
count	visible	type|shader|material|pb|shadows
3096	96	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
162	12	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
80	12	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
67	8	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
15	7	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Lit|pb=0|shadows=On
15	7	MeshRenderer|TextMeshPro/Mobile/Distance Field|LiberationSans SDF Material (Instance)|pb=0|shadows=Off
6	5	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingChildRealForm|pb=0|shadows=On
6	4	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Transparent|pb=0|shadows=Off
25	3	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
20	3	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Shader Graphs/Skin|MaleSurvivorSkin (Instance)|pb=0|shadows=On
4	2	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Beard|pb=0|shadows=On
74	2	MeshRenderer|Universal Render Pipeline/Lit|WoodenCrate|pb=0|shadows=On
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Boots|pb=0|shadows=On
6	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|FacelingAdultRealForm|pb=0|shadows=On
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|TShirt_White|pb=0|shadows=On
1	1	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Pants_Woodland|pb=0|shadows=On
4	0	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
2	0	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
lights enabled=0 withShadows=0 total=1
distinctMaterials=66
```
## Censo de renderers: S3_uuid_perfS3-1_renderers.tsv
```
renderers=20318 visible=9610 withPropertyBlock=0 instancedMaterialSlots=66 distinctKeys=83
count	visible	type|shader|material|pb|shadows
12587	6378	MeshRenderer|Universal Render Pipeline/Lit|LayerLampEmissive|pb=0|shadows=On
3096	470	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
624	331	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s3|pb=0|shadows=On
602	318	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s3|pb=0|shadows=On
593	315	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s3|pb=0|shadows=On
553	296	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s3|pb=0|shadows=On
372	294	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall|pb=0|shadows=On
474	282	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s2|pb=0|shadows=On
345	274	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte|pb=0|shadows=On
444	264	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s2|pb=0|shadows=On
444	264	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s2|pb=0|shadows=On
414	246	MeshRenderer|Universal Render Pipeline/Lit|LayerPropMatte_s2|pb=0|shadows=On
390	222	MeshRenderer|Universal Render Pipeline/Lit|Wg3Sign|pb=0|shadows=Off
268	213	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling|pb=0|shadows=On
256	202	MeshRenderer|Universal Render Pipeline/Lit|Wg3_FloorOffice|pb=0|shadows=On
175	165	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s1|pb=0|shadows=On
216	160	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
165	156	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Ceiling_s1|pb=0|shadows=On
165	156	MeshRenderer|Backrooms/GridWallOffset|Canon_M_Backrooms_Wall_s1|pb=0|shadows=On
228	153	MeshRenderer|Universal Render Pipeline/Lit|Canon_M_Backrooms_Floor_s4|pb=0|shadows=On
visibleRenderers=9610 visibleMeshTriangles=474074
distinctMaterials=209
```
## Censo de renderers: S5_perf_S5_renderers.tsv
```
renderers=3707 visible=446 withPropertyBlock=0 instancedMaterialSlots=1 distinctKeys=23
count	visible	type|shader|material|pb|shadows
3280	313	MeshRenderer|Universal Render Pipeline/Lit|URP_M_OfficePapers|pb=0|shadows=On
171	51	MeshRenderer|Universal Render Pipeline/Lit|Paper Tray|pb=0|shadows=On
79	30	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background|pb=0|shadows=On
76	24	MeshRenderer|Universal Render Pipeline/Lit|Wall Clock|pb=0|shadows=On
22	8	MeshRenderer|Universal Render Pipeline/Lit|Laptop Background_emissive|pb=0|shadows=On
19	6	MeshRenderer|Universal Render Pipeline/Lit|Glass|pb=0|shadows=On
10	5	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Emissive|pb=0|shadows=On
3	3	MeshRenderer|Universal Render Pipeline/Lit|Lit|pb=0|shadows=On
2	2	MeshRenderer|Universal Render Pipeline/Lit|Metal 3|pb=0|shadows=On
4	2	MeshRenderer|Universal Render Pipeline/Lit|BR_CrankFlashlight_Mat|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 1|pb=0|shadows=On
2	1	MeshRenderer|Universal Render Pipeline/Lit|Reception Counter|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Leather 2|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Wood|pb=0|shadows=On
1	1	MeshRenderer|Universal Render Pipeline/Lit|Desk 1|pb=0|shadows=On
1	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|TShirt_White|pb=0|shadows=On
1	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|MaleSurvivorEyes|pb=0|shadows=On
1	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Boots|pb=0|shadows=On
6	0	MeshRenderer|Universal Render Pipeline/Lit|Runtime_Transparent|pb=0|shadows=Off
1	0	SkinnedMeshRenderer|Universal Render Pipeline/Lit|Pants_Woodland|pb=0|shadows=On
lights enabled=0 withShadows=0 total=1
distinctMaterials=30
```