# WeChatAutomation 发布指南

## 快速发布

### 方法一：使用PowerShell脚本（推荐）

```powershell
# 发布x64版本（最常用）
cd WeChatAutomation
.\publish-quick.ps1
```

### 方法二：使用批处理文件

```batch
# 发布x64版本
cd WeChatAutomation
.\publish.bat
```

### 方法三：手动发布

```powershell
cd WeChatAutomation

# 发布x64版本
dotnet publish src\WeChatAutomation.App\WeChatAutomation.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o releases\x64
```

## 支持的架构

| 架构 | Runtime Identifier | 适用系统 |
|------|-------------------|----------|
| x64 | win-x64 | 大多数现代PC (Intel/AMD) |
| x86 | win-x86 | 老旧32位系统 |
| ARM64 | win-arm64 | Surface Pro X, Snapdragon等 |

## 发布参数说明

- `-c Release`: 使用Release配置
- `-r win-x64`: 指定运行时标识符
- `--self-contained true`: 自包含发布，无需.NET运行时
- `-p:PublishSingleFile=true`: 单文件发布
- `-p:EnableCompressionInSingleFile=true`: 启用压缩
- `-p:IncludeNativeLibrariesForSelfExtract=true`: 包含原生库
- `-p:DebugType=None`: 不生成调试符号
- `-p:DebugSymbols=false`: 不生成调试符号文件

## 输出文件

发布后会在 `releases` 目录下生成：

```
releases/
  v1.1.0_YYYYMMDD_HHMMSS/
    WeChatAutomation_1.1.0_x64/
      WeChatAutomation.App.exe    # 主程序
      appsettings.json            # 配置文件
      启动.bat                     # 启动脚本
      README.txt                  # 说明文档
    WeChatAutomation_1.1.0_x64.zip  # 压缩包
```

## 自动化发布

### GitHub Actions

项目已配置GitHub Actions工作流，支持自动发布：

1. 推送版本标签 `v*` 会自动触发发布
2. 或手动触发workflow_dispatch
3. 自动构建三个架构版本并上传到GitHub Releases

### 本地批量发布

```powershell
# 发布所有架构版本
.\publish-all.ps1
```

## 版本号管理

版本号在以下位置定义：

- `WeChatAutomation.Core.csproj` - Version属性
- `WeChatAutomation.App.csproj` - Version属性
- 发布脚本中的 `$version` 变量

更新版本时需要同步修改这些位置。

## 故障排除

### 编译警告

发布时可能出现一些警告，这些警告不影响功能：
- UIAutomation引用警告 - 正常现象
- 空引用警告 - 代码可进一步优化

### 发布失败

1. 确保已安装 .NET 9 SDK
2. 检查项目是否能正常编译
3. 查看详细错误信息

### 文件过大

自包含版本包含.NET运行时，所以文件较大（约60-80MB）是正常的。

## 最佳实践

1. **发布前测试**：确保程序能正常运行
2. **版本号一致**：确保所有位置的版本号一致
3. **清理旧版本**：定期清理releases目录中的旧版本
4. **提交发布**：将发布文件提交到GitHub Releases供用户下载

## 相关文件

- `publish-quick.ps1` - 快速发布脚本（x64）
- `publish-all.ps1` - 全架构发布脚本
- `publish.bat` - 批处理发布脚本
- `publish/` - 发布配置目录
- `.github/workflows/release.yml` - GitHub Actions工作流
