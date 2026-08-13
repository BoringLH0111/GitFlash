# GitFlash

一个**面向新手的中文图形化 Git 管理工具**（Windows 桌面软件）。

仍在开发中，功能不成熟，或存在问题，使用过程中注意

传统 Git 需要在命令行敲指令，对不熟悉 Git 的人不太友好。GitFlash 把常用操作都做成了图形界面，点一点就能完成：打开仓库、暂存提交、查看对比、切换分支、拉取推送等。

> 软件本身不实现 Git，而是调用电脑上已安装的 Git 命令完成所有操作。

## 功能特性

- **三栏布局，可拖拽调整宽度**，左侧栏可收起
  - **左栏**：我的仓库列表（可随时切换）+ 仓库文件浏览（懒加载文件树）
  - **中栏**：文件变更区 —— 暂存 / 取消暂存 / 撤回修改 / 提交暂存
  - **右栏**：历史 / 对比 —— 提交历史、提交详情、双栏代码对比（删除红、新增绿）、分支管理、拉取 / 推送
- **文件内容查看与编辑**：左侧文件树点击文件即可查看内容，支持编辑并保存（保留原编码与 BOM），未保存修改在切换文件 / 仓库 / 分支 / 拉取前会提醒
- **双栏 diff 对比**：删除的行红色显示在左侧，新增的行绿色显示在右侧，一目了然
- **分支管理**：本地分支 / 远程分支分组展示，点击远程分支自动创建本地跟踪分支；支持新建分支
- **危险操作二次确认**：撤回修改、切换分支前有未提交修改时都会先询问
- **异步操作**：克隆 / 拉取 / 推送不卡界面
- **仓库记录自动保存**：下次启动还记得你打开过的仓库

## 运行环境

- Windows 10 / 11
- **必须安装 Git**（软件的所有操作依赖 git 命令）：https://git-scm.com
- 源码运行 / 构建：Visual Studio 2022（17.10+）或 .NET 9 SDK

## 构建与运行

使用 Visual Studio 2022 打开 `GitFlash.sln`，直接按 F5 运行。

命令行构建：

```bash
dotnet build GitFlash.csproj
```

## 打包发布

### 小白包（推荐发给别人，双击即用，无需装 .NET）

```bash
dotnet publish GitFlash.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-standalone
```

产物：`publish-standalone\GitFlash.exe`（约 128 MB 单文件）

### 框架依赖版（体积小，但目标电脑需安装 .NET 9 桌面运行时）

```bash
dotnet publish GitFlash.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 数据存储位置

仓库列表记录保存在（不随 exe 移动，同一台电脑上的所有 GitFlash 共用）：

```
C:\Users\<用户名>\AppData\Roaming\GitFlash\repos.json
```

删除该文件即可清空仓库记录。

## 目录结构

```
GitFlash/
├── MainWindow.xaml / .xaml.cs   # 主界面与全部逻辑
├── GitHelper.cs                 # git 命令调用封装
├── ChangeFile.cs                # git status 输出解析
├── CommitInfo.cs                # git log 输出解析
├── CloneDialog.xaml / .xaml.cs  # 克隆仓库对话框
├── NewBranchDialog.xaml / .xaml.cs  # 新建分支对话框
└── app.ico                      # 软件图标
```
