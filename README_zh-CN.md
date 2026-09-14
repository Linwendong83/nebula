# Nebula 多人联机 Mod [![Build - Win x64](https://github.com/Linwendong83/nebula/actions/workflows/build-winx64.yml/badge.svg)](https://github.com/Linwendong83/nebula/actions/workflows/build-winx64.yml)

[English](README.md) | [简体中文](README_zh-CN.md)

一款适用于游戏 [《戴森球计划》(Dyson Sphere Program)](https://store.steampowered.com/app/1366540/Dyson_Sphere_Program/) 的开源多人联机 Mod。

## 版本发布与下载

- 您可以从本仓库的 [Releases](https://github.com/Linwendong83/nebula/releases) 或 [Actions](https://github.com/Linwendong83/nebula/actions) 页面下载最新的构建版本。
- 原版稳定版本也可以在 [Thunderstore](https://dsp.thunderstore.io/package/nebula/NebulaMultiplayerMod/) 获取。
- 有关手动安装的详细步骤，请参阅[安装指南](https://github.com/Linwendong83/nebula/wiki/Installation#manual-installation)。

## 常见问题 (FAQ)

### 在哪里可以获得支持或反馈问题？

欢迎前往本仓库的 [GitHub Issues](https://github.com/Linwendong83/nebula/issues) 页面提交反馈或 Bug 报告。  
游戏本体更新后，Mod 往往会出现不兼容的情况，可能需要暂时回退游戏版本。  
部分 Mod 与多人联机不兼容，详情请查看 [NebulaCompatibilityAssist](https://thunderstore.io/c/dyson-sphere-program/p/starfi5h/NebulaCompatibilityAssist/) 页面。  

### 如何游玩此 Mod？

请注意，为了紧跟游戏本体的更新，该 Mod 目前仍在积极开发中，可能仍存在一些问题与缺陷。

- 您可以从本仓库的 [Releases](https://github.com/Linwendong83/nebula/releases) 获取开发构建版本。
- 关于安装步骤，请参考[手动安装指南](https://github.com/Linwendong83/nebula/wiki/Installation#manual-installation)。
- 关于局域网/互联网联机连接方式，请查阅[建主与加入联机指南](https://github.com/Linwendong83/nebula/wiki/Hosting-and-Joining)。本 Mod 采用 TCP 直连协议，默认通信端口为 `8469`。

### 聊天系统 (Chat)

聊天窗口可以通过快捷键 `Alt + ~`（反引号键）打开或关闭（可在游戏内“设置 - 多人模式 - 聊天”中自定义配置）。设置中还提供了“收到新消息时是否自动弹出聊天窗口”的选项开关。  
在聊天框中输入 `/help` 可查看所有可用指令，或参阅 [Chat Commands](https://github.com/Linwendong83/nebula/wiki/Chat-Commands) Wiki 页面了解更多信息。  

### 独立/专用服务器 (Dedicated Server)

该 Mod 支持在无 GPU 的纯服务端环境下运行（Headless Server）。请查阅 [Wiki 页面](https://github.com/Linwendong83/nebula/wiki/Setup-Headless-Server)了解如何配置以及可用的命令行启动参数。  

### 目前的开发状态与同步进度？

完整功能概览请查阅 [Wiki](https://github.com/Linwendong83/nebula/wiki/About-Nebula)。  

目前该多人联机 Mod 已支持最新游戏版本（0.10.34.x）的**黑雾崛起战斗模式**。  
战斗模式下绝大多数内容已实现同步，仅少数特性仍在完善中。  

<details>
<summary>和平模式同步特性列表（点击展开）</summary>

- [x] 服务端 / 客户端通信
- [x] 游戏内自定义多人联机菜单
- [x] 行星地表玩家移动同步
- [x] 太空环境中玩家移动同步
- [x] 玩家视觉特效同步 (喷气背包、手电筒等)
- [x] 玩家音效同步 (脚步声、手电筒声等)
- [x] 玩家外观与机甲模型同步
- [x] 游戏逻辑帧率 (UPS) 同步
- [x] 宇宙生成与星系设置同步
- [x] 客户端从服务端加载行星数据
- [x] 行星地表植被采集同步
- [x] 行星矿物资源同步
- [x] 建筑建造虚影/预览同步
- [x] 建筑建造同步
- [x] 建筑拆除同步
- [x] 建筑升级同步
- [x] 戴森球数据与进度同步
- [x] 科技研发同步
- [x] 工厂生产统计数据同步 (部分新增统计项暂未同步)
- [x] 储物仓/容器物品栏同步
- [x] 建筑交互与配置面板同步
- [x] 传送带物品交互同步 (拾取/放置)
- [x] 丢弃物 (掉落物垃圾) 同步
- [x] 星际物流运输站同步
- [x] 物流运输机事件同步
- [x] 地基铺设同步 (地形变形)
- [x] 服务端世界状态持久化
- [x] 电网同步 (向戴森球请求电力)
- [x] 预警与警报系统同步
- [x] 全局广播通知同步 (带指引图标的事件)
- [x] 物流控制面板 (I 键) 同步 (条目列表与详细面板)
- [x] 行星备忘录便签同步
- [ ] 里程碑/目标系统 (客户端暂不可用)
- [ ] 自定义监控面板 (客户端离开星系后会丢失自定义统计)
- [x] 无线输电塔 (机甲充电时功率暂未完全同步)

</details>


<details>
<summary>战斗模式（黑雾崛起）同步特性列表（点击展开）</summary>

- [x] 新建筑设置同步 (战场分析基站 BAB、各类防御塔)
- [x] 战斗相关设置同步
- [x] 黑雾地面敌人生成/消亡事件同步 (factory.enemyPool)
- [x] 黑雾地面单位激活/休眠状态同步
- [x] 黑雾太空敌人生成/消亡事件同步 (spaceSector.enemyPool)
- [x] 黑雾太空单位激活/休眠状态同步
- [x] 黑雾行星基地经验等级与威胁度同步
- [x] 黑雾太空巢穴经验等级与威胁度同步
- [x] 掉落物及掉落物拾取过滤配置表同步
- [x] 机甲武器射击同步
- [x] 机甲投弹轰炸同步
- [x] 机甲阵亡与重生动画同步
- [x] 机甲护盾格挡弹道投射物同步
- [x] 黑雾基地唤醒/惊动事件同步 (武器锁定、玩家靠近、受到攻击)
- [x] 黑雾基地威胁度与发起进攻事件同步
- [x] 修正黑雾单位索敌逻辑：搜寻最近的存活机甲 (雷达感知范围)
- [x] 修正黑雾防御塔索敌逻辑：攻击最近的存活机甲 (射程内攻击或反击)
- [x] 同步仇恨目标切换，确保黑雾单位攻击同一目标
- [x] 建筑维修无人机同步 (仍在完善中)
- [x] 建筑被摧毁事件同步 (服务端完全鉴权判定)
- [x] 建筑重建事件同步
- [x] 黑雾中继站（Relay）事件同步 (到达基地/到达停靠泊位/离开基地/离开停靠泊位)
- [x] 清理黑雾地穴事件同步 (填埋封穴)
- [x] 尝试创建新巢穴（TryCreateNewHive）与从巢穴分遣单位（DispatchFromHive）事件同步
- [x] 巢穴实体化加载及打开/关闭预览事件同步
- [x] 黑雾巢穴唤醒/惊动事件同步 (武器锁定、玩家靠近、受到攻击)
- [x] 黑雾巢穴威胁等级与发起进攻事件同步
- [x] 修正黑雾太空单位寻敌逻辑：搜寻最近的存活机甲 (雷达感知范围)
- [x] 修正黑雾太空防御塔寻敌逻辑：攻击最近的存活机甲 (射程内攻击或反击)
- [x] 聊天栏展示基地/巢穴/中继站入侵事件提示
- [x] 黑雾通讯器设置同步 (好斗度与休战协议)
- [ ] 击杀统计数据同步
- [ ] 远端玩家机甲战斗无人机编队动画显示
- [ ] 远端玩家机甲太空战舰编队动画显示
- [ ] 客户端显示远端行星的地对空攻击动画 (导弹防御塔、等离子炮)
- [ ] 显示远端行星的空对地攻击动画 (枪骑兵激光扫射与轰炸机)

</details>

### 开发者接口 (API Documentation)

本 Mod 提供了专有 API，方便其他 Mod 开发者使其 Mod 与 Nebula 兼容。如果您是一名 Mod 开发者并希望与 Nebula 适配，请遵循[这里的指南](https://github.com/Linwendong83/nebula/wiki/Nebula-mod-API)。

### 如何参与贡献？

非常欢迎任何形式的代码贡献！您可以直接提交 Issue 或 Pull Request。环境搭建与贡献指南请参阅：[Wiki 开发环境配置](https://github.com/Linwendong83/nebula/wiki/Setting-up-a-development-environment)。
