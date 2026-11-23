# CourseReminder · 课表提醒

一个面向 Windows 的**桌面课表提醒**小工具：导入 Excel 课表后，在每节课开始前按设定的提前量弹出提醒卡片并播放提示音，同时提供整周课表与今日课表视图。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6)](#运行环境)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

## 功能特性

- **课前提醒**：每个大节开始前 N 分钟（默认 40，可设 1~180）在屏幕右下角弹出提醒卡片并播放提示音；连堂的每个大节各提醒一次。
- **整周课表**：周一~周日 × 节次的完整表格，标出今天、当前正在上的节次，以及按周次规则「本周不上」的课。
- **今日课表**：当天课程按时间排序，已过课程标注「【已过】」；点击提醒卡片可直接跳转查看。
- **Excel 导入**：读取 `.xlsx` 模板，逐行校验并预览，确认后才写入；导入失败不覆盖原课表。
- **手动录入**：没有 Excel 也能逐门新增 / 修改 / 删除课程，与导入共用同一份数据。
- **周次规则**：支持每周、单周、双周、`1-16`、`1,3,5` 等写法，自动换算教学周。
- **提醒去重**：同一门课同一大节当天只提醒一次，记录跨重启有效并保留 60 天。
- **绿色便携**：免安装，全部数据保存在程序目录的 `data/`，不写系统目录，整个文件夹可拷贝换机。
- **单实例运行**：重复启动会激活已有窗口，不会开出第二个进程。
- **零第三方依赖**：不引用任何 NuGet 包，Excel 由内置的 OOXML 读取器直接解析。

## 运行环境

- Windows 10 / 11（x64）
- [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)

## 下载与使用

程序以 **GitHub Release 附件** 形式发布，仓库内不提交编译产物：

1. 在本仓库的 **Releases** 页面下载 `CourseReminder-v0.1.7-win-x64.zip`。
2. 解压到任意目录，双击 `CourseReminder.exe`。
3. 在「设置」页填写学期第一周的周一日期与结束日期，再到「导入课表」页导入 `.xlsx` 课表。

详细步骤、模板格式、提醒规则与常见问题见 **[使用说明](docs/使用说明.md)**。

> 提示：点窗口右上角 X 只会最小化到任务栏；要退出程序，请用「设置」页的「退出课表提醒」。

## Excel 模板

模板文件：[`templates/课表模板.xlsx`](templates/课表模板.xlsx)，包含两个工作表：

| 工作表 | 列 | 说明 |
|---|---|---|
| 课程 | 星期 / 大节 / 课程名 / 教室 / 教师 / 周次规则 | 一行一门课；星期支持 `一`~`日` 或 `1`~`7` |
| 节次时间 | 大节 / 开始时间 / 结束时间 | 定义每个大节的起止时间，时间格式 `HH:mm` |

- 周次规则可填 `每周`、`单周`、`双周`、`1-16`、`1,3,5`，留空视为每周，范围 1~60。
- 表头可位于前 10 个非空行内；只支持 `.xlsx` 格式。
- 模板另含「填写说明」工作表，仅作注释，导入时忽略。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
git clone https://github.com/2wweq/CourseReminder.git
cd CourseReminder
dotnet build src/CourseReminder.sln -c Release

# 生成免安装文件夹版到 dist/
dotnet publish src/CourseReminder/CourseReminder.csproj -c Release -r win-x64 --self-contained false -o dist
```

构建产物 `dist/` 已被 `.gitignore` 排除。发布新版本时，把 `dist/` 打包为 `CourseReminder-v<版本>-win-x64.zip`，附上 `docs/使用说明.md`，上传到 GitHub Releases 即可。

## 项目结构

```text
CourseReminder/
├── docs/                       # 需求、设计、计划与使用文档
├── templates/课表模板.xlsx      # Excel 导入模板
└── src/
    ├── CourseReminder.sln
    └── CourseReminder/         # WPF 应用源码
        ├── Models/             # 学期、节次、课程、周次规则、设置等模型
        ├── Data/               # 便携路径、JSON 原子读写与备份、日志
        ├── Excel/              # xlsx 解析与逐行校验
        ├── Services/           # 课表服务、提醒计算
        ├── Notifications/      # 提醒卡片文案与提示音
        ├── Sys/                # 单实例控制
        └── Views/              # 提醒卡片、课程编辑窗口
```

## 技术栈

- C# / .NET 8 / WPF（`net8.0-windows`）
- 无第三方 NuGet 依赖；`.xlsx` 由自研 OOXML 读取器解析
- 提醒卡片为 WPF 自绘窗口（`Views/ToastWindow`），提示音使用 `SystemSounds`

## 文档

| 文档 | 内容 |
|---|---|
| [使用说明](docs/使用说明.md) | 面向使用者的安装、模板格式、提醒规则与 FAQ |
| [需求文档](docs/需求文档.md) | 背景、功能需求 FR、非功能需求 NFR 与验收标准 |
| [用户故事](docs/用户故事.md) | 按史诗拆分的故事与 Given/When/Then 验收标准 |
| [技术方案](docs/技术方案.md) | 架构、模块职责、数据模型、关键流程与发布方式 |
| [编码实施计划](docs/编码实施计划.md) | 文件结构、模块职责、实施路线与验证记录 |
| [需求澄清记录](docs/需求澄清记录.md) | 需求访谈、取舍与校订记录（已脱敏） |

## 数据与隐私

- 所有数据仅保存在程序目录的 `data/`（课表、设置、提醒去重记录、备份与日志）。
- 程序不联网、不上传任何数据，也没有遥测。
- 仓库不包含任何个人课表、截图或运行日志；`data/` 已在 `.gitignore` 中排除。

## 贡献

欢迎提交 Issue 反馈问题或提出建议；提交 PR 前请先开 Issue 说明改动意图，并保持零第三方依赖与便携式数据存储的约束。

## 许可

本项目基于 [MIT License](LICENSE) 开源。
