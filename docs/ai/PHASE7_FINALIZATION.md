# Phase 7 Finalization / Release Candidate Preparation

## 范围与运行前验收表（2026-10-08）

本轮起点：`b1acff4`，应用冻结 `14d8ede`；独立分支 `codex/phase7-finalization`。保留已批准视觉、publication修复、C1和旧证据。不重复Phase7.2验收，不访问Production，不迁移Electron，不替换正式程序，不自动进入Phase8。

路线权威：Phase6原生Production初始化及6.2日用Gate已通过。Phase8只准备原生快照、版本兼容、受控真实日用、回退和正式发布验收。

| 项目 | 本轮目标 / 判据 |
|---|---|
| Portable x64 Release | self-contained运行时/WinAppSDK/VC运行时/资源/许可证齐全；非开发目录启动不依赖SDK/helper/seed。干净Windows证据独立必需 |
| 材质设置 | 独立版本化可选文件，旧native-ui schema不变；atomic commit、identity binding、未知/损坏/中断/重启可控；普通入口保存Advanced仍只生效Standard |
| Advanced OFF | 冷Standard不得加载光学DLL/创建renderer；受控ON→OFF专属纹理归零、创建/退休平衡；无图像pending复活；普通无250ms诊断 |
| 系统策略 | 保守读取失败降级；通知失败有变化感知替代；实际OS accent/HC/transparency/animations/DPI/text/Narrator证据与注入区分 |
| Upgrade / removal / rollback | 隔离原生profile、旧候选实际读写、新版本写入后旧版本读回；便携移除不删除资料；快照不静默覆盖；真实Production不访问 |
| 回归 | 先相关测试；代码稳定后最多一次完整native Release suite，保留全部首次失败 |
| Gate | 必需干净环境/真实系统证据缺失为RC BLOCKED，不用有限开发集成例外豁免发行 |

## 运行前冻结的资源短验协议

只记录本轮候选身份与合成Profile；累计资源负载硬预算30分钟。目标两组各约4分钟（固定预热30秒、24次同节奏Library/Details、静置30秒、第二次24、静置30秒）；同384条目、相同窗口/DPI/theme。组A始终Standard；组B受控Advanced预热后切Standard，后续操作完全相同。不截图循环、不GPU readback、不forcedGC、不清缓存。

硬正确性：OFF的scene/draw/frost增量=0，专属renderer/texture=0、retirementPending=false；图片/导航/详情pending边界0；句柄无连续单向累积；功能完成/无错误/正常退出；库hash不变。Stage共享fallback封面仍是Standard需要的资源，不要求全部图像/WinUI资源为0。

保护停止：Private>1.5GiB、系统可用内存<3GiB、响应停止>15秒、设备错误/crash。不是验收通过门槛。容量复用判断采用两个重复段峰值与各30秒静置末15秒中位数，避免单点GC抖动。正式资源合格需两组终点不呈有实际影响的持续增长：相对预热/第一静置增量<=64MiB且第二段峰值增量<=64MiB，handles静置增量<=64。理由：控制2段同内容生命周期的额外保留，64MiB是既有整个decode cache预算而非把偶然GC低点当零；超过即FAIL/需要明确类别，不自动推论泄漏。单组或丢失边界INCONCLUSIVE；共享增长不被ON−OFF抵消。阈值与负载不随结果修改；旧32MiB代理FAIL不改写。

## 已知环境缺口

初始未发现可用WindowsSandbox/虚拟机入口；用户确认没有现成隔离Windows环境。不得启用宿主组件或修改个性化/辅助功能。继续独立工程及本机隔离Profile验证；干净机启动、实际系统矩阵、Narrator及VM结束会话不能被本机或Test注入替代。本轮预期保留这些RC阻断，除非后续出现真实可用环境。
