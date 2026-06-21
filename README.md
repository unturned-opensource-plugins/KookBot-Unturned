# KookBot-Unturned

一个为Unturned服务器集成Kook（开黑啦）机器人功能的插件。

## 功能特性

- **聊天审核**：轻量聊天内容审核系统
  - 禁言管理（临时/永久）
  - 违禁词过滤
  - 发言频率限制
  - 违禁词自动禁言（可选）

- **Kook集成**：与Kook平台无缝集成
  - WebSocket连接管理
  - 卡片消息支持
  - 服务器指标监控

- **配置管理**：灵活的配置系统
  - 热重载配置
  - 运行时事件/指令开关

## 系统要求

- .NET Framework 4.8+
- Rocket.Unturned 插件框架

## 安装

1. 将编译后的DLL放入Unturned服务器的Plugins目录
2. 配置`KookBot-Unturned.configuration.xml`文件
3. 重启服务器

## 配置

编辑`KookBot-Unturned.configuration.xml`配置文件以自定义：
- 聊天审核规则
- Kook机器人令牌
- 服务器监控设置

## 开发

### 构建项目

```bash
dotnet build KookBot-Unturned.sln -c Release
```

### 运行测试

```bash
dotnet test KookBot-Unturned.sln -c Release
```

### 项目结构

- `ChatModerationManager.cs` / `MuteRegistry.cs` / `MessageDetectorRegistry.cs` - 聊天审核、禁言和检测器生命周期
- `Commands*.cs` / `KookAdminCommandHandler.cs` - KOOK 指令路由、管理指令和运维入口
- `Events*.cs` / `EventDeduplicationStore.cs` / `PvpEventRuntime.cs` / `KookSendQueue.cs` - 游戏事件转发、去重、PvP 降噪和 KOOK 发送队列
- `KookApi/` - KOOK HTTP API、WebSocket Gateway 和卡片消息
- `Monitoring/` - 服务器监控

## 许可证

详见 LICENSE.txt 文件

## 作者

Emqo
